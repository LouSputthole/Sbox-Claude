# Component & GameObject Lifecycle

Purpose: spawn timing, dependency wiring, persistent-vs-disposable object splits, scene-scoped services, simulation gating, and deterministic teardown for s&box `Component`/`GameObject`/`Scene` code.

## Mental model

A `Component` is a script on a `GameObject`; the `Scene` owns the tree. Hooks fire in order (override `protected override void On…`):

- `OnAwake` — once on create. Transform/owner set, but other systems and screen NOT settled. Do off-screen positioning + `Network.ClearInterpolation()` here.
- `OnStart` — once, after the batch's `OnAwake`s. Still before `Screen` size / peers settle — defer heavy work.
- `OnEnabled` / `OnDisabled` — every enable/disable toggle (incl. spawn/despawn).
- `OnUpdate` — per render frame (`Time.Delta`). UI, cosmetics, world-panel positioning.
- `OnFixedUpdate` — fixed physics tick. All movement/physics integration belongs here.
- `OnDestroy` — teardown: cancel async, unsubscribe delegates, null statics.

`OnUpdate`/`OnFixedUpdate`/`OnCollisionStart` run on every machine, but usually only the simulating one should mutate state — gate them (see #6).

## Patterns (recipes)

### 1. Hide-until-positioned to kill the one-frame origin flicker

A freshly spawned object renders at `(0,0,0)` for one frame before its real position is computed. Park it off-screen in `OnAwake`, then clear interpolation so the visual doesn't lerp from the origin.

```csharp
protected override void OnAwake()
{
    if ( !IsProxy ) // only the authority sets position; proxies receive it networked
    {
        WorldPosition = new Vector3( 0, 0, -999999 );
        Network.ClearInterpolation();
    }
    Owner = Components.GetInAncestors<IPlayerBase>( true );
}
```
(simple-weapon-base: code/swb_base/Weapon.cs:42) — note `GetInAncestors` to find the owning player. Call `Network.ClearInterpolation()` after ANY hard teleport, not just spawn. For fast networked projectiles also set `Network.Interpolation = false` so the visual doesn't lag the true position (simple-weapon-base: code/swb_base/bullets/BulletInfo.Physical.cs:56).

To dodge renderer-enable flicker, create renderers `ShadowsOnly`/`Off` and reveal a frame later via `await GameTask.DelayRealtime(1)` in `OnComponentEnabled` (simple-weapon-base: code/swb_player/PlayerBase.cs:67).

### 2. Guaranteed deps with `[RequireComponent]`; correct `FindMode` for lookups

`[RequireComponent]` makes the engine auto-add and guarantee the dependency — no null checks:

```csharp
public sealed class NavigationTargetWanderer : Component
{
    [RequireComponent] NavMeshAgent Agent { get; set; }
    [RequireComponent] Rigidbody Body { get; set; }

    protected override void OnEnabled() => Agent.MoveTo( _currentTarget ); // Agent is never null
}
```
(sbox-scenestaging: Code/ExampleComponents/NavigationTargetWanderer.cs:9)

`Components.Get<T>()` defaults to **self-only** and silently returns `null` when the component lives on a child or parent. Pick the directional `FindMode`:

```csharp
Components.Get<Gun>();                                          // self only (the silent-null trap)
Components.GetInAncestors<IPlayerBase>( includeDisabled: true ); // walk UP (gun → owning player)
Components.Get<Health>( FindMode.EnabledInSelfAndDescendants );  // walk DOWN, enabled only
Components.Get<Hitbox>( FindMode.EverythingInSelfAndDescendants );// any tagged child under a prefab
```
A trace hits a child collider/hitbox, not the entity root — resolve up with `GetInAncestorsOrSelf<IDamageable>()`.

### 3. Split persistent networked data from the disposable pawn

Stats and identity tied to the pawn vanish when the pawn is destroyed on death. Spawn a separate `PlayerData` component once per connection to hold `[Sync]`'d data, and re-spawn the pawn freely:

```csharp
var go = new GameObject( true, $"PlayerInfo - {channel.DisplayName}" );
var data = go.AddComponent<PlayerData>();
data.SteamId = (long)channel.SteamId;
go.NetworkSpawn( null );                       // replicate; null = no per-client owner
go.Network.SetOwnerTransfer( OwnerTransfer.Fixed );  // survives owner disconnect
```
The pawn is a separate prefab clone linking back via `[Sync(SyncFlags.FromHost)] public PlayerData PlayerData`; look it up with a static `PlayerData.For(connection)` / `PlayerData.All`. (garryware: Code/GameLoop/GameManager.cs:41-89). Note pawn spawn there: `Clone(..., new CloneConfig { StartEnabled = false, Transform = startLocation })` then `NetworkSpawn(owner)` — configure BEFORE spawning, assign authority via the `Connection`.

### 4. Belt-and-suspenders cleanup for transient (per-round) objects

Track by reference AND by tag so nothing leaks even if a reference is dropped:

```csharp
public GameObject CreateTemporaryObject( string name, Vector3 position, bool networked = false )
{
    var go = new GameObject( true, name )
    {
        WorldPosition = position,
        Flags = GameObjectFlags.NotSaved,                      // keep out of the saved scene
        NetworkMode = networked ? NetworkMode.Object : NetworkMode.Never
    };
    RegisterTemporaryObject( go );                             // push into a tracked list
    return go;
}

public void CleanupTemporaryObjects()
{
    foreach ( var go in Scene.GetAllObjects( true )
        .Where( x => x.Tags.Contains( "ware" ) && x.Tags.Contains( "removable" ) ).ToArray() )
        if ( go.IsValid() ) go.Destroy();                      // tag sweep

    foreach ( var go in _temporaryObjects.ToArray() )
        if ( go.IsValid() ) go.Destroy();                      // tracked list
    _temporaryObjects.Clear();
}
```
(garryware: Code/Ware/WareRoundSystem.cs:394-421). Choose `NetworkMode.Never` vs `.Object` per object based on whether clients need it.

### 5. Scene-scoped services via `GameObjectSystem<T>` with ordered hooks

For cross-cutting managers (cooldowns, watchdogs, API links) extend `GameObjectSystem<T>` instead of putting a `Component` on a `GameObject` — no deletable/duplicable prefab slot, a real per-scene singleton, and survives hotload (access via `T.Current`):

```csharp
public class Cooldown : GameObjectSystem<Cooldown>
{
    public Cooldown( Scene scene ) : base( scene )
    {
        Listen( Stage.FinishUpdate, 10, ProcessCooldowns, "ProcessCooldowns" );
    }
    private void ProcessCooldowns() { /* … */ }
}
```
(dxrp: game/code/Cooldown.cs:6). The middle arg is **priority** — it deterministically orders cross-system ticks ("system A before system B"). Subscribe to load too: `Listen( Stage.SceneLoaded, 100, Loaded, "Sentinel Start" )` and bail in editor with `if ( Scene.IsEditor ) return;` (dxrp: game/code/Sentinel/Sentinel.cs:18).

### 6. Simulation gating — `LocalSimulation || Network.IsOwner` (incl. collisions)

`Network.IsOwner` is `false` in a solo editor playtest (no lobby = no owner), so an `IsOwner`-only guard makes whole systems silently dead until multiplayer starts. Combine with a `[Property]` override:

```csharp
[Property] public bool LocalSimulation { get; set; } = true;
bool ShouldSimulate => LocalSimulation || Network.IsOwner;

public void OnCollisionStart( Collision collision )
{
    if ( !ShouldSimulate ) return;   // gate collisions too, not just OnUpdate/OnFixedUpdate
    var impact = collision.Contact.Speed.Length;
    // … apply crash damage
}
```
(sbox-vehicle-kit: Libraries/Vehicles.Maintenance/Code/Components/VehicleBase.Damage.cs:87-94). Gating crash damage on `Network.IsOwner` alone once broke the entire crash→repair loop in solo play.

### 7. Defer heavy init out of `OnStart` behind a readiness gate

`OnStart` runs before `Screen.Width/Height` and peers settle (you get the placeholder `1024x1024`). Set a flag and do the real work in `OnUpdate`, deferring while not ready, wrapped in try/catch that surfaces a UI error instead of throwing:

```csharp
protected override void OnStart() => _initCoreOnUpdate = true;

protected override void OnUpdate()
{
    if ( !StartCoreWhenReady() ) return;
    // … per-frame work once ready
}

private bool StartCoreWhenReady()
{
    if ( _initCoreOnUpdate )
    {
        if ( ShouldDeferInitialCore() ) return false;  // screen still placeholder → wait more frames
        _initCoreOnUpdate = false;
        InitCore();
    }
    return IsReady;
}
```
(sgba: Code/EmulatorComponent.cs:252)

### 8. Teardown discipline — cancel async, then unsubscribe, then clear

C# `Action` event subscribers survive content/hotload reloads and double-fire or leak. The reusable order is **cancel async first → unsubscribe delegates → clear state → fire unload events**:

```csharp
public void UnloadScript()
{
    CancelPlaybackOperation();                                   // 1. cancel running async loop

    if ( ActiveScript.OnChoiceSelected is not null )             // 2. detach every subscriber
        foreach ( var d in ActiveScript.OnChoiceSelected.GetInvocationList() )
            ActiveScript.OnChoiceSelected -= (Action<Script.Choice>)d;

    State.Clear();                                               // 3. clear / stop owned resources
    ActiveScript.OnUnload();                                     // 4. fire unload events
    OnScriptUnload?.Invoke( ActiveScript );
}
```
(SBox-Visual-Novel-Base: Libraries/VNBase/Code/Systems/Player/ScriptPlayer.cs:186-229). Mirror this in `OnDestroy`: null any static `Instance`, `-=` static-event subscribers, cancel a CTS for owned loops.

### 9. Partition a large pawn by concern

A pawn balloons into a thousand-line monster. Keep one file per concern with a central tick calling named helpers. **Modern (Scene) — preferred:** make each concern its own `Component` composed onto the pawn `GameObject` (composable, independently toggleable, testable). **Legacy (`Sandbox.Game`):** partial-class files (`Player.AFK.cs`, `Player.Roles.cs`, …) with a central `Simulate` calling `TickAFKSystem()`, `TickEntityHints()`, etc. (ttt-reborn: code/player/Player.cs:177 — *legacy `Simulate(Client)`/`IsClient`, shown for the per-feature-tick idea only; do NOT copy the `Client`/`Pawn` API*). Keep the named-per-feature-tick structure regardless of API.

## Gotcha table

| Gotcha | Fix |
|---|---|
| One-frame flicker at origin on spawn | Park off-screen in `OnAwake` + `Network.ClearInterpolation()`; clear after EVERY hard teleport |
| Renderer-enable flicker | Create `RenderType = ShadowsOnly/Off`, reveal after `await GameTask.DelayRealtime(1)` |
| Fast projectile visual lags true position | `Network.Interpolation = false` on the object |
| `Network.IsOwner` is false in solo playtest → system silently dead | Gate on `LocalSimulation \|\| Network.IsOwner`; expose `LocalSimulation` as a `[Property]` |
| `Components.Get<T>()` returns null for child/parent components | Use `GetInAncestors(OrSelf)` / `FindMode.EnabledInSelfAndDescendants` / `EverythingInSelfAndDescendants` |
| Stats/identity lost when pawn destroyed on death | Hold them on a separate `NetworkSpawn`'d `PlayerData` component, link via `[Sync(SyncFlags.FromHost)]` |
| Transient objects leak across rounds | Tag + tracked-list double cleanup; set `GameObjectFlags.NotSaved` |
| `OnStart` reads give placeholder `Screen` size (1024x1024) | Defer real init into `OnUpdate` behind a readiness gate; try/catch → UI error |
| `Action` subscribers double-fire / leak across hotload | Walk `GetInvocationList()` and `-=` each in teardown/`OnDestroy` |
| Singleton `Component` is deletable/duplicable, needs a prefab slot | Use `GameObjectSystem<T>` (per-scene singleton, `T.Current`, survives hotload) |
| Cross-system tick ordering nondeterministic | `Listen(stage, priority, …)` — the priority arg orders systems |
| In-flight async touches half-torn-down state | Teardown order: cancel async → unsubscribe → clear → unload events |
| `Clone()`/`new GameObject` not replicating | `NetworkSpawn` is required; configure (`CloneConfig.StartEnabled=false`) BEFORE, set `OwnerTransfer.Fixed` AFTER, spawn on one machine only |
| Movement jitter | Move/integrate physics in `OnFixedUpdate`, not `OnUpdate` |

## Verify live

SDK API drifts between builds — confirm against the installed SDK before writing: `describe_type` / `search_types` / `get_method_signature` on `Component`, `GameObject`, `GameObjectSystem`, `FindMode`, `GameObjectFlags`, `CloneConfig`, `OwnerTransfer`, `Stage`. Reflection is the source of truth, not memory.

Cross-links: networking/`[Sync]`/`[Rpc.*]`/`NetworkSpawn` authority rules live in the **sbox-api** reference; the screenshot-driven build loop and bridge gotchas live in the **sbox-build-feature** skill.

## Corpus refresh (2026): more reference implementations

### 10. Hotload-safe singleton via `IHotloadManaged`

`Singleton<T> : Component` alone loses its static `Instance` across a hotload. Implement `IHotloadManaged` and round-trip through the hotload state dictionary to rebind it:

```csharp
public class Singleton<T> : Component, IHotloadManaged where T : Singleton<T>
{
    public static T Instance { get; private set; }

    protected override void OnAwake()
    {
        if ( Instance != null && Instance != this ) { GameObject.Destroy(); return; }
        Instance = (T)this;
    }
    protected override void OnDestroy() => Instance = null;

    // IHotloadManaged — called by the engine around a hotload
    void IHotloadManaged.Destroyed( Dictionary<string,object> state )
        => state["IsActive"] = Instance == this;

    void IHotloadManaged.Created( Dictionary<string,object> state )
    {
        if ( state.TryGetValue( "IsActive", out var v ) && v is true )
            Instance = (T)this;
    }
}
```
(meteorlab.vehicle_tool_example: Code/Singleton.cs; identical pattern in alcoholics.nice_putt_idiot: Code/Components/Singleton.cs)

Without `IHotloadManaged`, `Instance` goes null after every code save, silently breaking every `Singleton<T>.Instance` caller until the scene reloads.

### 11. `OnEnabled`/`OnDisabled` for component registration

Use `OnEnabled`/`OnDisabled`/`OnDestroy` to maintain a controller's owned-component list during hot edits and runtime toggles:

```csharp
public class WheelCollider : Component
{
    [Property] public VehicleController Controller { get; set; }

    protected override void OnEnabled()  => Controller?.Wheels.Add( this );
    protected override void OnDisabled() => Controller?.Wheels.Remove( this );
    protected override void OnDestroy()  => Controller?.Wheels.Remove( this );
}
```
(meteorlab.vehicle_tool_example: Code/Vehicle/Wheel/WheelCollider.cs — implied by "registers/unregisters with its controller in OnEnabled/OnDisabled/OnDestroy")

This pattern generalises to any "parent holds a list of child subsystems" design. `OnDisabled` alone is not sufficient — `OnDestroy` fires when the component is removed entirely and `OnDisabled` may not precede it.

### 12. `StartEnabled=false` → configure → `NetworkSpawn` to prevent first-frame uninitialized state

Spawn networked objects with `StartEnabled = false` in the `CloneConfig`, configure all properties, then enable and network-spawn:

```csharp
// from facepunch.fair: AI/Guests/GuestManager.cs
var go = guestPrefab.Clone( new CloneConfig { StartEnabled = false, Transform = spawnPoint } );
var guest = go.Components.Get<Guest>();
guest.IsRich = Random.Float() < 0.04f;
go.Enabled = true;
go.NetworkSpawn();
```
(facepunch.fair: AI/Guests/GuestManager.cs; same principle in enifun.shop_manager: Code/AI/CustomerSpawner.cs — `Clone()` → `Dresser.Randomize()` → `NetworkSpawn()`)

Without `StartEnabled = false`, `OnEnabled`/`OnStart` fire before you have set the component's properties, and proxy clients receive one frame of uninitialized `[Sync]` values. This is the networked complement of the `OnAwake` off-screen-park pattern (recipe #1).

### 13. Proxy initialization bundle in `OnStart`

All proxy-side presentation setup belongs in `OnStart` under a single `if ( IsProxy )` block. Do not scatter it across multiple hooks:

```csharp
protected override void OnStart()
{
    if ( IsProxy )
    {
        // 1. Disable local-player-only components
        Components.Get<CameraComponent>().Enabled = false;

        // 2. Dim the model so proxies read as "other players"
        var renderer = Components.Get<SkinnedModelRenderer>();
        renderer.Tint = renderer.Tint.WithAlpha( 0.3f );

        // 3. Zero out cosmetic components that shouldn't pulse on proxies
        Components.Get<HighlightOutline>( FindMode.EverythingInSelfAndDescendants ).Width = 0;

        // 4. Spawn a floating WorldPanel nametag above the object
        var tag = Components.GetOrCreate<GolfBallTag>();
        tag.Client = Components.GetInAncestors<Client>( true );
    }
}
```
(alcoholics.nice_putt_idiot: Code/Pawns/GolfBall.cs — "proxy presentation in GolfBall.OnStart")

The nametag then positions itself each `OnUpdate` by reading `WorldPosition`. This is a composable proxy-presentation recipe: camera off + alpha dim + component zero + floating nametag.

### 14. Centralized `GameObjectSystem` tick — agents must NOT override `OnUpdate`

When simulating hundreds of agents, do not give each agent its own `OnUpdate`. Instead, inherit `GameObjectSystem` and iterate all agents once per frame from a single system:

```csharp
public class AgentTickSystem : GameObjectSystem
{
    public AgentTickSystem( Scene scene ) : base( scene )
    {
        Listen( Stage.StartUpdate,      0, TickUpdate,      "AgentUpdate" );
        Listen( Stage.StartFixedUpdate, 0, TickFixedUpdate, "AgentFixedUpdate" );
    }

    private void TickUpdate()
    {
        foreach ( var agent in Scene.GetAll<Agent>() )
        {
            if ( !agent.Active ) continue;
            agent.Controller?.Tick();   // movement + animation
            agent.Tick();
        }
    }

    private void TickFixedUpdate()
    {
        foreach ( var agent in Scene.GetAll<Agent>() )
        {
            if ( !agent.Active ) continue;
            agent.ActionController?.Tick();   // AI scoring
            agent.FixedTick();
        }
    }
}
```
(facepunch.fair: AI/AgentTickSystem.cs — "agents do NOT override OnUpdate themselves … one cache-friendly loop")

Anti-pattern: giving 500 `Agent` components their own `OnUpdate` creates 500 separate engine callbacks. The `GameObjectSystem` single-pass is O(n) over a contiguous list, skips inactive objects cheaply, and centralises a `Stopwatch` for timing. Stagger the first AI-scoring tick with `NextTick = Random.Float(0, FixedRate)` so 500 agents don't score on frame 1 simultaneously.

### 15. `[Sync, Change("MethodName")]` — callback on sync value change

Combine `[Sync]` with `[Change]` to fire a method whenever the synced property changes on any machine:

```csharp
[Sync, Change( "OnGameStateChanged" )]
public RagRollState GameState { get; set; }

private void OnGameStateChanged( RagRollState oldState, RagRollState newState )
{
    // runs on host AND all clients when GameState is updated
    Log.Info( $"State: {oldState} → {newState}" );
}
```
(barrelproto.ragroll: Code/mode/RollMode.cs — `[Sync, Change("OnGameStateChanged")] public RagRollState _gameState`)

The callback fires on the machine that received (or made) the change, so you can update UI, start coroutines, or reconfigure components without polling every frame. Pair with an `IsProxy` guard inside the callback if the reaction differs between host and clients.

### 16. `!IsValid()` guard in tick methods

Any `Component` or `GameObject` can become invalid mid-tick (destroyed during iteration, round ended, owner disconnected). Guard every tick method that accesses external references:

```csharp
// abstract RoundState base — despawn.murder: Systems/Rounds/RoundState.cs
protected override void OnFixedUpdate()
{
    if ( !IsValid )   return;   // component was destroyed
    if ( HasEnded )   return;   // round already over
    if ( IsProxy )    return;   // host-only logic
    OnTick();
}
```
(despawn.murder: Systems/Rounds/RoundState.cs — "Tick() early-returns on client, on HasEnded, on !IsValid")

`GameObject.IsValid()` and `Component.IsValid` are the safe null-equivalent checks for s&box objects. A raw `!= null` check does NOT catch a destroyed `GameObject`/`Component` — it still reports non-null while being invalid. Add the `!IsValid` guard at the top of any tick that dereferences synced references.

---

**Read these games** for additional lifecycle patterns: `meteorlab.vehicle_tool_example` (hotload-safe singleton, feature-toggle `[FeatureEnabled]`), `facepunch.fair` (centralized agent tick, `StartEnabled=false` spawn, interface-discovered save), `barrelproto.ragroll` (`[Sync,Change]`, `NetworkOrphaned.ClearOwner` host migration), `despawn.murder` (host-migration-safe `TimeUntil` re-arm, `!IsValid` guards), `alcoholics.nice_putt_idiot` (proxy setup in `OnStart`), `enifun.shop_manager` (`AllSingletonsReady()` deferred apply gate).
