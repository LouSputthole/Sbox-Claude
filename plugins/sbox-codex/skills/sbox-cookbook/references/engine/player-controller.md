# Player Controller (modern s&box scene system)

Recipes for building, extending, and networking a player pawn: `CharacterController` movement, `MoveMode` arbitration, networked spawn/respawn, view-models, and death/ragdoll/spectator flow. Modern `GameObject`/`Component`/`Scene` API only — no legacy `Entity`/`Pawn`/`[Net]`.

## Mental model

Split the pawn into three responsibilities, each its own object:

- **Identity** — one `Player` component per `Connection`. Owns networked state (inventory, owned units, lifecycle) and has **no physics**. In sbox-grubs it is a `LocalComponent<Player>` holding `NetList<Grub>` + `ActiveGrub`, all `[Sync(SyncFlags.FromHost)]` (sbox-grubs: `Code/Systems/Pawn/Player.cs:9-29`). This cleanly supports one player driving many bodies (RTS/squad) or swapping bodies.
- **Body** — the controllable GameObject holding `Health`, the renderer, and the controller.
- **Mover** — kinematics (`BuildWishVelocity`/`Accelerate`/`ApplyFriction`/`Move`).

Two ticks, never crossed:
- `OnFixedUpdate` → movement + physics (deterministic fixed tick).
- `OnUpdate` → look angles, camera framing, animation (per-frame).

Authority: read input and mutate synced state **only when `!IsProxy`** (and the body is the active one). On a proxy, mutating synced state silently rolls back.

## Recipe: CharacterController movement loop

`CharacterController` is the built-in kinematic mover. It applies **no gravity** — you integrate it. Run the whole thing on the fixed tick; gate on `IsProxy` (sbox-scenestaging: `Code/ExampleComponents/PlayerController.cs:120-168`).

```csharp
protected override void OnFixedUpdate()
{
    if ( IsProxy ) return;            // only the owner drives this body

    BuildWishVelocity();
    var cc = GameObject.Components.Get<CharacterController>();

    if ( cc.IsOnGround && Input.Down( "Jump" ) )
        cc.Punch( Vector3.Up * 322.0f );   // impulse, NOT cc.Velocity = ...

    if ( cc.IsOnGround )
    {
        cc.Velocity = cc.Velocity.WithZ( 0 );
        cc.Accelerate( WishVelocity );      // full friction on ground
        cc.ApplyFriction( 4.0f );
    }
    else
    {
        cc.Velocity -= Gravity * Time.Delta * 0.5f;   // half-step gravity before Move
        cc.Accelerate( WishVelocity.ClampLength( 50 ) ); // clamp air wish
        cc.ApplyFriction( 0.1f );                       // near-zero air friction
    }

    cc.Move();                                          // exactly once per tick

    if ( !cc.IsOnGround )
        cc.Velocity -= Gravity * Time.Delta * 0.5f;     // second half-step after Move
    else
        cc.Velocity = cc.Velocity.WithZ( 0 );
}

public void BuildWishVelocity()
{
    var rot = EyeAngles.ToRotation();
    WishVelocity = (rot * Input.AnalogMove).WithZ( 0 );
    if ( !WishVelocity.IsNearZeroLength ) WishVelocity = WishVelocity.Normal;
    WishVelocity *= Input.Down( "Run" ) ? 320.0f : 110.0f;
}
```

Splitting gravity into two half-steps around `Move()` (symplectic integration) gives smoother jump arcs than a single application (sbox-scenestaging: `PlayerController.cs:153,162`). `BuildWishVelocity` rotates `Input.AnalogMove` by eye angles, then scales by walk/run speed (sbox-scenestaging: `PlayerController.cs:170-181`). Look/camera/animation belong in `OnUpdate`, not here.

## Recipe: extend locomotion with MoveMode (don't fork the controller)

The modern `PlayerController` extension point is to **add `MoveMode` components** — never subclass the controller. Each state (NoClip/Sit/Kneel/Spectate/Ladder/Swim) overrides `Score(PlayerController)` to bid; the controller activates the highest scorer each tick. Critically, the bid must read a `[Sync]` var on proxies so remote clients mirror the mode (dxrp: `game/code/Player/MoveModes/MoveModeNoClip.cs`).

```csharp
public class MoveModeNoClip : MoveMode
{
    [Sync] public bool IsNoclipping { get; set; }

    public override bool AllowGrounding => false;
    public override bool AllowFalling   => false;

    public override int Score( PlayerController controller )
    {
        if ( IsProxy )                                   // remote clients mirror the synced flag
            return IsNoclipping ? 100 : 0;

        if ( CanNoclip() && Input.Pressed( "Noclip" ) )  // owner toggles locally
            IsNoclipping = !IsNoclipping;

        return IsNoclipping ? 100 : 0;
    }

    public override void UpdateRigidBody( Rigidbody body )
    {
        if ( IsProxy ) return;
        body.Gravity = false;
        if ( body.PhysicsBody != null )
            body.PhysicsBody.BodyType = PhysicsBodyType.Keyframed;   // swap physics cleanly per-mode
    }

    public override Vector3 UpdateMove( Rotation eyes, Vector3 input ) { /* return wishVelocity */ }
    public override void AddVelocity() { /* Controller.Body.Velocity = smoothed; */ }

    public override void OnModeEnd( MoveMode next )
    {
        base.OnModeEnd( next );
        if ( Controller.Body.PhysicsBody != null )
            Controller.Body.PhysicsBody.BodyType = PhysicsBodyType.Dynamic;  // restore on exit
    }
}
```

The lifecycle hooks are: `Score`, `AllowGrounding`/`AllowFalling`, `UpdateMove`, `AddVelocity`, `UpdateRigidBody`, `OnModeBegin`/`OnModeEnd(next)` (dxrp: `MoveModeNoClip.cs:60,80,112,155,184,196`). `OnModeEnd` must undo whatever physics state the mode mutated (here, back to `Dynamic`) (dxrp: `MoveModeNoClip.cs:204-207`).

## Recipe: spawn a networked pawn (host-authoritative)

Spawn is **host-only**. `Clone()`/`new GameObject` alone is local — `NetworkSpawn` is what replicates, and `NetworkSpawn(owner)` is what hands a client authority (sandbox-plus-plus: `Code/GameLoop/GameManager.cs:60-104`).

```csharp
// 1. Per-connection data object — non-physics, stays bound to its connection
PlayerData EnsurePlayerData( Connection channel )
{
    var existing = PlayerData.For( channel );
    if ( existing.IsValid() ) return existing;          // double-spawn guard

    var go = new GameObject( true, $"PlayerInfo - {channel.DisplayName}" );
    go.AddComponent<PlayerData>();
    go.NetworkSpawn( channel );
    go.Network.SetOwnerTransfer( OwnerTransfer.Fixed );  // never reassign owner
    return go.Components.Get<PlayerData>();
}

// 2. Spawn the body — host only, guard against an existing pawn for this owner
void SpawnPlayer( PlayerData data )
{
    Assert.True( Networking.IsHost, "Client tried to SpawnPlayer" );  // loud in dev
    if ( Scene.GetAll<Player>().Any( x => x.Network.Owner == data.Network.Owner ) )
        return;

    var go = GameObject.Clone( "/prefabs/engine/player.prefab",
        new CloneConfig { StartEnabled = false, Transform = FindSpawnLocation() } );

    go.Components.Get<Player>( true ).PlayerData = data;  // wire components BEFORE it goes live
    go.NetworkSpawn( data.Network.Owner );                // replicate + grant client authority
}
```

`StartEnabled = false` lets you wire components before the object goes live (sandbox-plus-plus: `GameManager.cs:94`). `OwnerTransfer.Fixed` keeps the data bound to its connection (`GameManager.cs:69`). The `Assert.True(Networking.IsHost,...)` + owner-match guard are the spawn safety rails (`GameManager.cs:79-83`). Make **respawn** an `[Rpc.Host]` the client requests — never let a client self-spawn.

## Recipe: first-person view-model (render-tag isolation)

Stop the FP weapon clipping the world with a **second `CameraComponent`**: `ZNear≈1`, higher `Priority`, `ClearFlags = Depth|Stencil`, and `RenderTags` containing only `{viewmodel, light}`. On the **main** camera, add `viewmodel` to `RenderExcludeTags`. You need **both halves** or the weapon clips or double-renders (simple-weapon-base: `code/swb_base/Weapon.cs:415`).

Each frame: copy the main camera transform onto the VM camera, then apply additive offsets (simple-weapon-base: `code/swb_base/ViewModelHandler.cs:60-103`).

```csharp
protected override void OnUpdate()
{
    // Toggle via shadow mode, NOT Enabled, to avoid a one-frame enable-flicker
    ViewModelRenderer.RenderType = ShouldDraw
        ? ModelRenderer.ShadowRenderType.Off
        : ModelRenderer.ShadowRenderType.ShadowsOnly;

    Camera.WorldPosition = Scene.Camera.WorldPosition;   // follow the main camera
    Camera.WorldRotation = Scene.Camera.WorldRotation;
    WorldPosition = Camera.WorldPosition;
    WorldRotation = Camera.WorldRotation;

    finalVectorPos = finalVectorPos.LerpTo( targetVectorPos, animSpeed * RealTime.SmoothDelta );
    finalRot       = finalRot.SlerpTo( targetRot, animSpeed * RealTime.SmoothDelta );

    WorldRotation *= finalRot;                            // rotation FIRST
    // position is composed from the rotation basis — must come after rotation
    WorldPosition += finalVectorPos.z * WorldRotation.Up
                   + finalVectorPos.y * WorldRotation.Forward
                   + finalVectorPos.x * WorldRotation.Right;

    targetVectorPos = Vector3.Zero;                       // RESET targets each frame
    targetVectorRot = Vector3.Zero;
    HandleIdleAnimation(); HandleWalkAnimation(); HandleSwayAnimation();  // each ADDs its offset
}
```

The feel pattern: keep `targetVectorPos`/`targetVectorRot`, reset both to zero each frame, let each effect (breathing, bob, sway, iron-sights, sprint) **add** its contribution, then smooth toward the targets with `RealTime.SmoothDelta` (simple-weapon-base: `ViewModelHandler.cs:89,100,106,149`). `RealTime.SmoothDelta` (not `Time.Delta`) keeps it alive while time-scaled/paused. This additive-then-smooth shape is reusable for camera feel, recoil, and hand IK.

## Recipe: death → ragdoll + spectator

Decouple the corpse from the camera owner. Build the ragdoll in a **new** GameObject — `CopyFrom` the live `SkinnedModelRenderer`, bone-merge clothing (`BoneMergeTarget`), add `ModelPhysics` with `CopyBonesFrom`, tag it `ragdoll`, **then** apply the impulse. For death-cam, spawn a **separate** non-enabled Observer component (`NetworkSpawn` to the owner) that orbits the latest death target in `OnPreRender`, blocks respawn ~1s, and calls an `[Rpc.Host] RequestRespawn` on input or timeout (sandbox/garryware: `Code/Player/Player.cs:228`). This avoids reparenting the live player and gates respawn server-side.

Critical ordering: enable `ModelPhysics`, `await GameTask.Delay(...)` one frame, **then** apply the clamped impulse — applying it the same frame physics is enabled makes the ragdoll explode (garryware: `Code/Player/Player.cs:161-210`). For non-lethal "fake death", reuse the ragdoll recipe but don't destroy the pawn — alpha-fade it (save/restore tint) and blend the camera from the ragdoll's head bone back before respawning.

## Recipe: possession / input takeover via MoveMode.OnInput

To let an external script drive a player (vehicle, turret, Wirebox keyboard), don't fight the controller — tag the player (e.g. `lockedposition`), get its locked `MoveMode`, and subscribe `mode.OnInput += handler`. Read `Input.Down(bind)` in the handler, diff each button against its last value before re-emitting, and provide a self-release (`Input.EscapePressed`). On disable, unsubscribe and untag (wirebox: `Code/wirebox/components/WireKeyboardComponent.cs:60`). The subscribe-on-enter / unsubscribe-on-exit + escape-to-release lifecycle is the reusable part.

## Recipe: dress a controller-driven / code-spawned citizen at RUNTIME

Two traps converge when you try to clothe a citizen the built-in `PlayerController` drives, or one you `new`'d in code: **(a)** `Dresser.Randomize()/Apply()` **NREs on a code-built body** (there's no editor avatar to source from), leaving it nude; **(b)** the `PlayerController` **rebuilds the citizen body a frame or two into play**, dropping any editor-applied or too-early clothing (the pawn spawns naked). The robust fix for both: build a `ClothingContainer` in code from verified `.clothing` paths and `Apply` it onto the body's `SkinnedModelRenderer` — and for the *player* specifically, re-apply across a short `TimeUntil` window so the controller's late rebuild can't wipe it.

```csharp
void DressFromPaths( SkinnedModelRenderer body, IEnumerable<string> clothingPaths )
{
    var container = new ClothingContainer();
    foreach ( var path in clothingPaths )
    {
        try
        {
            var c = ResourceLibrary.Get<Clothing>( path );        // or Clothing.Load( path )
            if ( c is not null ) container.Clothing.Add( new ClothingContainer.ClothingEntry( c ) );
        }
        catch ( Exception e ) { Log.Warning( $"skip clothing '{path}': {e.Message}" ); } // nude < thrown spawn
    }
    container.Apply( body );                                       // synchronous overload, onto the renderer
}

// PLAYER: re-apply across a window because the controller rebuilds the body late.
TimeUntil _stopReapplying = 1.5f;
protected override void OnUpdate()
{
    if ( !_stopReapplying )
    {
        var body = GetComponent<PlayerController>()?.Renderer       // re-resolve each tick — the old one gets destroyed
                   ?? GameObject.Root.GetComponentInChildren<SkinnedModelRenderer>();
        if ( body.IsValid() ) DressFromPaths( body, _outfit );
    }
}
```

Guard EVERY step and wrap each load in try/catch so a missing asset can only log-and-skip — a nude citizen is an acceptable worst case, a thrown spawn is not. For a code-spawned crowd, call `DressFromPaths` once right after building the body (no re-apply window needed — only the *player's* controller does the late rebuild). The base `citizen_clothes` set is a mounted core addon, so its `.clothing` paths are always available. (Pattern proven in the Gravehold build — `World/KeeperOutfit.cs` player re-apply window, `World/VillagerSpawner.cs` code-spawned crowd; the engine-side `ClothingContainer.Apply(body)` idiom also appears in the networking `OnActive` join recipe.)

## Recipe: kinematic limp-drape carry on a built-in controller

You **cannot** `FixedJoint`/weld a ragdoll to the built-in `PlayerController` — it's kinematic and exposes **no shoulder `PhysicsBody`** to anchor a joint on. The reliable move is a **kinematic drape**: on grab, freeze the corpse's ragdoll (poseable, not simulating), `SetParent` it to a shoulder-bone anchor at a slung local rotation (≈ `(80,10,90)` — rolled across the shoulders, head/legs hanging, NOT a stiff horizontal T-pose), then each frame nudge that local rotation with a small **sine sway scaled by the carrier's planar speed** so it jiggles with the gait. Re-enable real ragdoll physics **only on DROP** (unfreeze + unparent). Hold-type is `None` (a slung body doesn't read as a hands-in-front cradle, so no hand-IK on the body — the parent + rotation + sway sell it). Drive the drape **every frame, not just on grab**, because the controller rebuilds the body at runtime (see the dressing recipe above) and would otherwise leave the pose stale. (Gravehold `Player/KeeperGrab.cs`: `AttachDraped`, `ApplyDrapeSway`, `UpdateBodyAnimation`.) For a *physics-held* carry instead (Half-Life gravity-gun: the thing should collide/jostle in-hand), use the network-correct grab/carry/drop recipe in `physics-traces-movement.md` — disable gravity, raise damping, `SmoothMove` the mass-centre toward an eye-forward hold point.

## Gotcha table

| Symptom | Cause | Fix |
|---|---|---|
| Movement jittery / frame-rate-dependent | `CharacterController` moved in `OnUpdate` | Move it in `OnFixedUpdate`; only camera/look/anim in `OnUpdate` (sbox-scenestaging: `PlayerController.cs:120`) |
| Pawn spawns naked despite editor clothing | The `PlayerController` rebuilds the body a frame or two into play and drops it | Build a `ClothingContainer` in code and `Apply` it across a short `TimeUntil` window, re-resolving `PlayerController.Renderer` each tick (Gravehold `KeeperOutfit.cs`) |
| `Dresser.Randomize()` throws / NREs | No editor avatar to source from on a code-built body | Don't use `Dresser` on code bodies; `ResourceLibrary.Get<Clothing>(path)` → `container.Clothing.Add(...)` → `container.Apply(body)`, each load in try/catch |
| Carried ragdoll floats / sits in a stiff T-pose | Tried to weld a ragdoll to the kinematic controller (no shoulder body) OR a one-shot pose went stale on body rebuild | Kinematic drape: freeze ragdoll, parent to a shoulder bone at a slung rotation, sway it EVERY frame; re-enable physics only on drop (Gravehold `KeeperGrab.cs`) |
| Every client drives every pawn | No proxy gate | `if ( IsProxy ) return;` before reading input / mutating synced state |
| Custom MoveMode invisible to remote clients | `Score()` reads a local-only toggle | Drive `Score()` from a `[Sync]` var (dxrp: `MoveModeNoClip.cs:62`) |
| Jump stomps horizontal momentum | Set `cc.Velocity` for the jump | Use `cc.Punch( Vector3.Up * f )` (impulse) (sbox-scenestaging: `PlayerController.cs:136`) |
| Weapon clips world / renders twice | Only one half of tag isolation set | VM cam `RenderTags` has `viewmodel` AND main cam `RenderExcludeTags` has `viewmodel` |
| One-frame view-model flicker | Toggling renderer `Enabled` | Toggle `RenderType = ShadowsOnly` instead (simple-weapon-base: `ViewModelHandler.cs:62`) |
| View-model offset corrupted | Position set before rotation | Set `WorldRotation` first, compose position from its basis (simple-weapon-base: `ViewModelHandler.cs:100-102`) |
| Object doesn't replicate | `Clone()`/`new GameObject` only | `NetworkSpawn(owner)` — bare `NetworkSpawn()` leaves it host-owned |
| Pawn detaches from its connection | Owner reassigned mid-session | `Network.SetOwnerTransfer(OwnerTransfer.Fixed)` (sandbox-plus-plus: `GameManager.cs:69`) |
| Clients respawn at will | Client self-spawns | Respawn is an `[Rpc.Host]` the host validates; `Assert.True(Networking.IsHost)` |
| Duplicate pawns per connection | No double-spawn guard | Check for an existing `Player` whose `Network.Owner` matches (sandbox-plus-plus: `GameManager.cs:82`) |
| Ragdoll explodes at spawn | Impulse applied same frame physics enabled | Enable `ModelPhysics`, `await GameTask.Delay(...)` one frame, then impulse (garryware) |
| `MathF`/`System.Math` missing in sandbox | Sandbox API restriction | Use `MathX.Clamp` / `Vector3` helpers for pitch clamp etc. |
| Timers freeze when paused | `Time.Delta` is 0 at `TimeScale=0` | Use `RealTime.SmoothDelta` / `TimeSince` for camera/UI/sway |

## Cross-cutting (apply to every pawn)

- **Authority is the #1 bug class.** Mutating `[Sync]` state on a proxy silently rolls back. Gate every mutator: `if (IsProxy) return;` (owner-authoritative) or `if (!Networking.IsHost) return;` (host-authoritative). Add `Assert.True(Networking.IsHost,...)` in dev so desync is loud.
- **`[Rpc.Host]` is callable by any client with forged args** — re-validate ownership/limits and rate-limit (cooldown keyed by `Rpc.CallerId`) inside the host body. Use `SyncFlags.FromHost` for anything authoritative (health/score), not plain `[Sync]`.
- **`Network.IsOwner` is false in solo editor playtests** (no lobby = no owner) — combine owner guards with a `LocalSimulation` property so systems still run solo.
- **Statics are wiped on hotload.** Prefer `LocalComponent<T>` (as sbox-grubs `Player` does) or `GameObjectSystem<T>`; null `Instance` in `OnDestroy` and `-=` static-event subscribers.
- **`Connection`/`GameObject` refs are not `[Sync]`-able** — sync a stable `Guid` and resolve via `Connection.All` / `Scene.Directory.FindByGuid`.

## Verify live

API names drift between SDK builds — reflection is authoritative. Before writing, confirm member names with `describe_type CharacterController` / `describe_type Sandbox.Movement.MoveMode` / `search_types PlayerController` and `get_method_signature` for `Punch`/`Accelerate`/`NetworkSpawn`. Visuals (view-model framing, ragdoll) verify with `screenshot_from`; input/possession/multiplayer behavior needs `execute_csharp` or a human playtest — the single-client bridge can't synthesize key presses.

See also: **sbox-api** (input/physics/networking primitives reference) and **sbox-build-feature** (the screenshot-driven iteration workflow this controller is built with).

## Corpus refresh (2026): more reference implementations

### Pattern: host-migration-safe active-fighter refs — use `[Sync] GameObject`, not private fields

A documented bug from `aethercore.versus` (`ArenaManager.cs`): when fighters were stored as private `PlayerController` fields, host migration reset them to `null` on the new host, and `CheckRoundOver` concluded both fighters died → infinite draw loop. Fix: promote the ref to a `[Sync] GameObject` with a private property wrapper so call-sites are unchanged:

```csharp
// aethercore.versus: ArenaManager.cs — host-migration-safe participant refs
[Sync] public GameObject ActivePlayer1Obj { get; set; }
private PlayerController activePlayer1 {
    get => ActivePlayer1Obj?.GetComponent<PlayerController>();
    set => ActivePlayer1Obj = value?.GameObject;
}
```

**Rule:** any host-only field that names a participant in a round FSM must be `[Sync]` — or it vanishes on host migration. Applies to spectator targets, match fighters, and round-win counters alike.

### Pattern: `[Rpc.Owner]` damage routing — let the victim's machine decide

`aethercore.versus` (`HealthComponent.cs`, `PlayerController.cs`) routes damage through `[Rpc.Owner]` so the mutation executes on the **victim's owner**, not the attacker. The reason is subtle: parry detection reads a private non-synced `parryWindowTimer` that is only correct on the owner's machine — on the attacker's proxy the timer is always 0 so parries would never fire.

```csharp
// Attacker calls: victim.TakeDamage(info)
// HealthComponent.TakeDamage immediately bounces to:
[Rpc.Owner]
public void TakeDamageRpc( float amount, Vector3 force )
{
    // runs on victim's owner — parryWindowTimer, i-frame timers, etc. are all valid here
    Controller.OnDamageReceived( amount, force );
}
```

Use `[Rpc.Owner]` (not `[Rpc.Host]`) whenever the mutation needs **private per-owner state** that a proxy or host cannot know. Guard amount/force server-side before calling if cheat-resistance matters.

### Pattern: disconnect grace to avoid false draws

`aethercore.versus` (`ArenaManager.cs`) waits `DisconnectGrace = 3f` seconds before awarding a win when exactly one fighter ref is null — filters transient sync nulls that can appear the frame a client drops. When **both** are null it bails (`return`) instead of declaring a draw, assuming a host-migration sync frame.

```csharp
// In CheckRoundOver() — called every OnUpdate on host
if ( activePlayer1 == null && activePlayer2 == null ) return; // host migration frame
if ( activePlayer1 == null && _disconnectGraceTimer <= 0f )
    { Player2Wins++; EnterState( MatchState.RoundEnded ); }
```

Pair with `INetworkListener.OnDisconnected(Connection)` to compare `player.GameObject.Root.Network.Owner == channel` for an immediate authoritative forfeit.

### Pattern: trauma/Perlin screenshake as a GameResource

`aethercore.versus` (`PlayerCamera.cs`, `CameraShakeProfile.cs`) makes camera shake a designer-tunable `.shake` asset. The accumulator is `shakeTrauma = MathX.Clamp( trauma + intensity / 30f, 0f, 1f )`; each frame `shake = trauma * trauma` (squared for a natural feel), three independent `Noise.Perlin( Time.Now * freq + seedOffset )` samples drive pitch/yaw/roll offsets. Trauma decays by `Time.Delta / decayRate`.

```csharp
// aethercore.versus: PlayerCamera.cs — drop-in trauma shake
float shakeTrauma;
public void AddShake( CameraShakeProfile p ) =>
    shakeTrauma = MathX.Clamp( shakeTrauma + p.Intensity / 30f, 0f, 1f );

protected override void OnUpdate()
{
    shakeTrauma = MathX.Max( 0f, shakeTrauma - Time.Delta / p.DecayTime );
    float shake = shakeTrauma * shakeTrauma;             // square for natural curve
    Camera.WorldRotation *= Rotation.From(
        Noise.Perlin( Time.Now * p.Frequency + 0f ) * shake * p.Intensity,
        Noise.Perlin( Time.Now * p.Frequency + 1f ) * shake * p.Intensity,
        p.IncludeRoll ? Noise.Perlin( Time.Now * p.Frequency + 2f ) * shake * p.Intensity : 0f );
}
```

Assign one `CameraShakeProfile` per event (`HitShake`, `GuardShake`, `ParryShake`) so designers can tune feel without code changes. `Noise.Perlin` requires no import — it is in the s&box core API. Use `MathX.Max`/`MathX.Clamp` — not `MathF`.

### Pattern: OTS "virtual aim point" camera framing

`aethercore.versus` (`PlayerCamera.cs`) avoids the common OTS problem (character glued to screen-edge) by always looking at an **aim point**: the lock-on target's head bone position when locked, or a virtual point `IdleAimDistance = 400` units ahead along the player's yaw otherwise. Wall-collision pull-in uses a `Scene.Trace.Ray(...).Radius(8).IgnoreGameObjectHierarchy(target)` probe from player to ideal camera position, shortening the arm on hit.

```csharp
Vector3 aimPoint = lockOnTarget.IsValid()
    ? lockOnTarget.GetBoneTransform("head").Position
    : EyePosition + EyeRotation.Forward * IdleAimDistance;

cam.WorldRotation = Rotation.LookAt( aimPoint - cam.WorldPosition );
// wall pull-in: shorten arm along -Forward if trace hits
```

Root-motion-driven movement (for attacks/dodges) feeds `Controller.Velocity = Model.RootMotion.Position.WithZ(0) / Time.Delta` so animation displacement drives the body — no hand-coded lunge constants.

### Pattern: `UseInputControls = false` to freeze a player (admin / UI modal)

`lowkeynetworks.newrp` (`AdminService.cs`) implements admin "freeze" as `controller.UseInputControls = false` with a velocity zero-out. It also instantiates noclip mode with `GetOrAddComponent<MoveModeNoClip>()` — a simpler alternative to the full `MoveMode.Score()` bid system for programmatic one-off mode switches:

```csharp
// lowkeynetworks.newrp: AdminService.cs
void FreezePlayer( PlayerController controller )
{
    controller.UseInputControls = false;
    if ( controller.Body.IsValid() ) controller.Body.Velocity = Vector3.Zero;
}

void EnableNoclip( PlayerController controller ) =>
    controller.GetOrAddComponent<MoveModeNoClip>().IsNoclipping = true;
```

`bublic.stone_by_stone` (`PlayerComponent.cs`) uses the same toggle (`UseInputControls = false`) to suppress player input while a shop/upgrade panel is open, then restores it on close. This is the idiomatic "lock movement while a UI modal is active" pattern.

### Pattern: viewmodel wall-clip pullback via forward trace

`bublic.stone_by_stone` (`FastSlotsComponent.cs`) prevents the held weapon clipping through walls with a short forward `Scene.Trace.Ray(...).Radius(ClipRadius)` from the camera. When it hits, `LocalPosition` is lerped backward along a configurable axis proportional to proximity:

```csharp
// bublic.stone_by_stone: FastSlotsComponent.cs
var tr = Scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + cam.WorldRotation.Forward * ClipCheckDist )
    .UsePhysicsWorld().Radius( ClipRadius ).Run();
if ( tr.Hit )
{
    float pullback = 1f - (tr.Fraction);            // 0 = at wall, 1 = full extension
    heldModel.LocalPosition = Vector3.Lerp( heldModel.LocalPosition,
        ClipPullbackAxis * -pullback * MaxPullback, Time.Delta * PullbackSpeed );
}
```

This is the additive-offset extension of the view-model recipe (combine with the existing `targetVectorPos` reset-and-add pattern).

### Anti-pattern: "first proxy player" scan for opponent resolution

`aethercore.versus` (`ArenaManager.cs`) documents a real multiplayer bug: code scanned the scene for the first proxy `PlayerController` and picked the wrong person when a spectator was present. Fix is `ArenaManager.GetOpponentOf(player)` reading the synced active-fighter refs. General lesson: **never rely on scene scan order** to identify "the other player" — always look up from authoritative synced state.

### Gotcha: stats `Flush()` is mandatory for leaderboard propagation

`aethercore.versus` (`PlayerStats.cs`) documents that without `Stats.Flush()` the s&box backend buffers increments and leaderboard changes can take minutes to appear. Always pair `Stats.Increment(key, amt)` with `Stats.Flush()`:

```csharp
Stats.Increment( key, amount );
Stats.Flush();   // omit this and leaderboard updates lag minutes, not seconds
```

Also maintain a `Dictionary<string,double> sessionCache` so UI reads update instantly without waiting for a backend round-trip.

### Gotcha: hot-reload does not reinitialize static fields - use properties for condition tables

`aethercore.versus` (`SkinUnlock.cs`) exposes unlock conditions as a **property** (fresh `new Dictionary` each call), not a static field, because s&box hot-reload does not re-init static fields. If a condition table is a static field you will test against stale values until you restart. This applies to any data-table or registry populated at type-init time.

---

**Read these games for player-controller / camera / movement patterns:**
- `aethercore.versus` - host-migration-safe FSM refs, `[Rpc.Owner]` damage routing, trauma/Perlin shake, OTS virtual-aim framing, disconnect grace, root-motion attacks
- `lowkeynetworks.newrp` - `UseInputControls` freeze/unfreeze, `GetOrAddComponent<MoveMode>()` programmatic mode switch, admin noclip
- `bublic.stone_by_stone` - viewmodel pullback trace, `UseInputControls` UI-modal lock, `PlayerController.IEvents` integration
- `ataco.sdoomresurrection` - hand-rolled `DoomController` (gravity/friction/step-up/slide without `CharacterController`); wall-crossing segment trigger as alternative to trigger volumes
