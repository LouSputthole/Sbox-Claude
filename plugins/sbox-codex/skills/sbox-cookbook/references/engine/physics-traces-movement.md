# Physics, Traces & Custom Movement

Querying the world with `Scene.Trace`, applying forces to Rigidbodies, and going kinematic to own your velocity when Source 2's solver fights you.

## Mental model

Three ways to move things, pick deliberately:

- **Rigidbody-driven** — leave `Body.MotionEnabled = true`, push it with `ApplyForce` / `ApplyForceAt`. Source 2 solves contacts, friction, stacking for you. Best for props, debris, ragdolls, anything you nudge.
- **Kinematic-integrated** — you own an authoritative `Vector3 _vel`, accumulate forces into it, and write `WorldPosition` yourself each tick. Use this the moment the solver fights you (vehicles, dashes, grapples, hand-rolled controllers). Source 2 **caps/damps velocity at contacts and silently eats applied drive force** — heavy/fast custom movement feels weightless or speed-capped until you go kinematic (sbox-vehicle-kit: VehicleBase.Wheels.cs:192-201).
- **CharacterController** — a swept-capsule mover that collide-and-slides for you but **applies NO gravity** — you integrate gravity yourself (sbox-grubs: GrubPlayerController.cs:46).

`Scene.Trace` is the universal world query underneath all of it: ground checks, interaction rays, suspension casts, pickup sweeps.

---

## Pattern: fluent Scene.Trace world query

Build every query fluently, chain filters, then `.Run()`. The result struct carries everything you need.

```csharp
var tr = Scene.Trace
    .Ray( start, end )                       // or .Sphere(r, a, b) / .Ray(a,b).Size(bbox) for a box sweep
    .IgnoreGameObject( GameObject )           // don't hit your own collider
    .WithoutTags( "player", "debris" )        // exclude
    .WithAnyTags( "solid", "npc", "glass" )   // include only these
    .UseHitboxes()                            // precise hitbox hits, not just bounds
    .Run();

if ( tr.Hit )
{
    // tr.GameObject, tr.Body, tr.Surface, tr.HitPosition, tr.Normal, tr.Distance, tr.Fraction
}
```

Interaction ray from the camera — the canonical "use" trace, self-ignored (sbox-scenestaging: PlayerUse.cs:30):

```csharp
var tr = Scene.Trace.Ray( camera.WorldPosition, camera.WorldPosition + camera.WorldRotation.Forward * 100 )
    .IgnoreGameObject( GameObject )
    .Run();

if ( tr.Hit && tr.GameObject.Components.Get<BaseInteractor>() is BaseInteractor interactable )
    interactable.OnUsed();
```

Ground check: trace a short ray `Vector3.Down`. Note a trace hits a **child** collider/hitbox, not the entity root — resolve the owner with `GetInAncestorsOrSelf<T>()`, not a self-only `Components.Get<T>()`.

---

## Pattern: applying forces to a real Rigidbody

Apply forces in `OnFixedUpdate`. `ApplyForce` pushes through the center of mass; `ApplyForceAt(tr.HitPosition, force)` pushes at a point and imparts torque/spin. Multiply by `Body.Mass` only when you want a **mass-independent** ("ignore mass") response so light and heavy props react identically. The sensor trace **must self-ignore** (offset start past the prop's bounds + `IgnoreGameObjectHierarchy`) or it pushes itself (wirebox: WireForcerComponent.cs:68):

```csharp
protected override void OnFixedUpdate()
{
    var offset = WorldRotation.Up * GetComponent<Rigidbody>().PhysicsBody.GetBounds().Size.z;
    var tr = Scene.Trace.Ray( WorldPosition + offset, WorldPosition + offset + WorldRotation.Up * Length )
        .UseHitboxes().WithAnyTags( "solid", "npc", "glass" ).WithoutTags( "debris", "player" )
        .IgnoreGameObjectHierarchy( GameObject ).Size( 2 ).Run();
    if ( !tr.Hit || !tr.Body.IsValid() ) return;

    var force = WorldRotation.Up * Force;
    if ( IgnoreMass ) force *= tr.Body.Mass;     // cancel mass → equal accel for light + heavy
    tr.Body.ApplyForce( force );                 // ApplyForceAt(tr.HitPosition, force) → spin
}
```

---

## Pattern: CharacterController with manual gravity (leapfrog)

CharacterController gives you no gravity. Apply it as **two half-steps** around `Move()` (leapfrog/Verlet) — a single full step lags the jump/fall arc. Move in `OnFixedUpdate`, never `OnUpdate` (sbox-grubs: GrubPlayerController.cs:46).

```csharp
protected override void OnFixedUpdate()
{
    if ( IsProxy ) return;

    if ( IsGrounded )
    {
        CharacterController.SetVelocity( CharacterController.Velocity.WithZ( 0f ) );
        CharacterController.Accelerate( GetWishVelocity() );
        CharacterController.ApplyFriction( 4f, 100f );
    }
    else
    {
        CharacterController.SetVelocity( Velocity - Gravity * Time.Delta * 0.5f );  // half BEFORE
        CharacterController.ApplyFriction( 0.1f );
    }

    CharacterController.Move();

    if ( !IsGrounded )
        CharacterController.SetVelocity( Velocity - Gravity * Time.Delta * 0.5f );  // other half AFTER
}
```

Jump: set the velocity, then **`ReleaseFromGround()`**, then trigger the animator — or the controller re-sticks to the floor the same tick and eats the jump (sbox-grubs: GrubPlayerController.cs:122-124):

```csharp
if ( Input.Pressed( "jump" ) )
{
    CharacterController.SetVelocity( new Vector3( Facing * 175f, 0f, 220f ) );
    CharacterController.ReleaseFromGround();   // unstick BEFORE Move() runs
    Animator.TriggerJump();
}
```

---

## Pattern: going kinematic to own your velocity

When the solver caps your drive force, take movement off Source 2's hands once and integrate by hand. Keep the Body for its collider geometry + collision queries only (sbox-vehicle-kit: VehicleBase.Wheels.cs:192-201).

```csharp
void SetupKinematicIfNeeded()
{
    if ( _kinematicReady || Body == null ) return;
    try { Body.MotionEnabled = false; } catch { /* property name drifts across SDKs */ }
    _vel = Vector3.Zero;
    _kinematicReady = true;
}

protected override void OnFixedUpdate()
{
    SetupKinematicIfNeeded();
    var dt = Time.Delta;
    if ( dt <= 0f ) return;                  // dt==0 while time-scaled/paused

    _vel += AccumulatedForces / VehicleMass; // Body.Mass reads 0 now → keep your own mass field
    WorldPosition += _vel * dt;              // move by hand
    Body.Velocity = _vel;                    // mirror back so debug overlays/readers stay consistent
}
```

---

## Pattern: suspension + collide-and-slide for a kinematic ground-follower

Per wheel/foot, raycast down and apply a Hooke's-law spring + damper into a single Z velocity change (sbox-vehicle-kit: VehicleBase.Wheels.cs:549-571):

```csharp
var hit = Scene.Trace.Ray( origin, origin + wsDown * (restLength + radius) )
    .IgnoreGameObject( GameObject ).Run();
if ( hit.Hit )
{
    var compression = 1f - MathX.Clamp( (hit.Distance - radius) / restLength, 0f, 1f );
    var spring = compression * Stiffness;
    var damper = (compression - prevCompression) / dt * Damping;
    totalSuspensionForce += spring + damper;   // caller sums → one body Z velocity change
}
```

**Walls are the trap:** when floor and walls are one `MapCollider`, a low box sweep keeps hitting the shared floor face and never sees the wall. Instead fire horizontal **feeler rays at mid-body height** (they physically cannot touch the floor), reject floor/ramp hits with `|Normal.z| > 0.7`, clamp the move, and slide the remainder by cancelling only the into-wall velocity component (sbox-vehicle-kit: VehicleBase.Wheels.cs:494-523):

```csharp
var tr = Scene.Trace.Ray( start, start + dir * rayLen )
    .IgnoreGameObjectHierarchy( GameObject ).Run();
if ( tr.Hit && MathF.Abs( tr.Normal.z ) <= 0.7f )    // a wall, not the floor
{
    var wallN = tr.Normal.WithZ( 0f ).Normal;
    var slide = remaining - wallN * Vector3.Dot( remaining, wallN );  // tangential remainder
    pos += slide;
    var into = Vector3.Dot( _vel, wallN );
    if ( into < 0f ) _vel -= wallN * into;            // kill only the into-wall component
}
```

---

## Pattern: network-correct grab / carry / drop

To carry a physics object across the network: trace for it, `TakeOwnership`, parent it, tag it, and **disable its Rigidbody** so it follows kinematically. Drive its transform in `OnPreRender` (render-rate, smooth). On drop, re-enable the Rigidbody, set throw velocity, unparent, `DropOwnership`. The non-obvious safety: a proxy still flagged carrying must auto-Drop or two clients fight over ownership (sbox-scenestaging: NetworkTest.cs:71).

```csharp
[Sync] GameObject Carrying { get; set; }

void TryPickup()
{
    var tr = Scene.Trace.WithoutTags( "player" ).Sphere( 16, eyePos, eyePos + fwd * 100 ).Run();
    if ( !tr.Hit || tr.Body.GameObject is not GameObject go || !go.Tags.Has( "pickup" ) ) return;

    go.Network.TakeOwnership();
    Carrying = go;
    Carrying.SetParent( GameObject, true );
    Carrying.Tags.Add( "carrying" );
    if ( Carrying.Components.Get<Rigidbody>( true ) is { IsValid: true } rb ) rb.Enabled = false;
}

protected override void OnPreRender()   // smooth render-rate follow, NOT physics
{
    if ( Carrying.IsValid() && !Carrying.IsProxy )
        Carrying.WorldPosition = HoldRelative.WorldPosition + HoldRelative.Parent.WorldRotation * new Vector3( 0, 0, 40 );
}

void UpdatePickup()
{
    if ( Carrying.IsValid() && Carrying.IsProxy ) { Drop(); return; }  // proxy still carrying → auto-drop
    // ...
}

void Drop()
{
    if ( Carrying.Components.Get<Rigidbody>( true ) is { IsValid: true } rb )
    { rb.Enabled = true; rb.Velocity = throwDir; }
    Carrying.SetParent( null, true );
    Carrying.Tags.Remove( "carrying" );
    Carrying.Network.DropOwnership();
    Carrying = null;
}
```

Read `tr.Surface.SoundCollection` for material-specific footsteps/impact audio off any trace.

---

## Pattern: multi-point spring-damper buoyancy (floating boats/props)

Float a Rigidbody on a water surface with a **grid of sample points over the hull**, each contributing a Hooke's-law spring (force ∝ depth below the wave surface) plus a damper (opposes that point's vertical velocity, kills oscillation). Sampling at several offset points — not one centre point — is what gives roll/pitch and a stable, non-bouncy float. Run it in `OnFixedUpdate`, gated `if (IsProxy) return;` (host/owner-authoritative physics).

```csharp
void ApplyBuoyancy()
{
    var ext = m_Collider.LocalBounds.Extents;
    float sx = ext.x * HullSpread, sy = ext.y * HullSpread;
    // 9 points: centre + 4 edges + 4 corners
    Vector3[] pts = { center, center+new Vector3(sx,0,0), center+new Vector3(-sx,0,0), /* …8… */ };

    float mass = m_Collider.Rigidbody.Mass;
    var angularVel = m_Collider.Rigidbody.AngularVelocity;
    foreach ( var local in pts )
    {
        var world = WorldTransform.PointToWorld( local );
        float depth = ( WaterManager.GetWaterHeightAt( world ) + SurfaceOffset ) - world.z;
        if ( depth <= 0f ) continue;                                  // point is above the surface

        float spring = depth * SpringStiffness * mass * AirVolume / pts.Length;   // scaled by remaining air
        var pointVel = m_Collider.Rigidbody.Velocity + Vector3.Cross( angularVel, world - WorldPosition );
        float damper = -pointVel.z * Damping * mass / pts.Length;     // per-point vertical damper
        m_Collider.Rigidbody.ApplyForceAt( world, Vector3.Up * (spring + damper) );
    }
}
```

Verified against pldr.duck_pond `Code/Water/Buoyancy/Buoyancy.cs`: the 9-point hull sample (`:149-172`), per-point `spring = depth * Stiffness * mass * AirVolume / pointCount` + velocity damper applied with `ApplyForceAt` (`:195-201`), quadratic **water resistance** `-0.5·ρ·v²·Cd·A·dir·submersion` (`:111-130`), submersion-scaled **angular drag** (`:134-145`), and **wave-transport** that nudges the hull along the wave's horizontal displacement (`:207-216`). The `[Sync] AirVolume` (`:25`) drains while submerged so a holed boat slowly **sinks** (`:99-107`). Global surface queries `WaterManager.GetWaterHeightAt` / `GetWaveDisplacementAt` are the seam any water system should expose (`Code/Water/WaterManager.cs`). The same 9-point design appears in treehaven.sdiver and stepdev.xtrem_road.

---

## Pattern: conveyor — scroll a material from `Collider.SurfaceVelocity`

A moving-belt look without animating geometry: set a `BoxCollider.SurfaceVelocity` (physics actually carries props along it), then drive the belt material's scroll attribute from that same velocity so the texture visibly matches the push.

```csharp
public sealed class Conveyor : Component
{
    [RequireComponent] private BoxCollider Collider { get; set; }
    [Property] private ModelRenderer Renderer { get; set; }

    protected override void OnFixedUpdate()
    {
        if ( Renderer.IsValid() && Renderer.SceneObject.IsValid() )
            Renderer.SceneObject.Attributes.Set( "TimeScale", Collider.SurfaceVelocity.x / 65f );
    }
}
```

Verbatim from stellawisps.lumberyard `Code/Tycoon/Conveyor.cs:14`. One source of truth (`SurfaceVelocity`) drives both the physics carry and the visual scroll, so they can never disagree. The belt material reads its `TimeScale` attribute to pan its UVs. Pair with trigger-zone "suckers" (a `BoxCollider` trigger that pulls items toward a sell/buy point) for a full belt economy (lumberyard `ItemSucker.cs`/`SellSucker.cs`).

---

## Gotcha table

| Gotcha | Why it bites | Fix |
| --- | --- | --- |
| CharacterController has no gravity | Character floats / never falls | Integrate gravity yourself as two half-steps around `Move()` (leapfrog), not one full step |
| Jump gets eaten the same tick | Controller re-sticks to floor after you set jump velocity | Call `ReleaseFromGround()` after `SetVelocity`, before `Move()` |
| Drive force feels weightless / speed-capped | Source 2 solver damps velocity at contacts | `Body.MotionEnabled = false` and integrate `_vel` by hand |
| `Body.Mass` reads 0 | Body is kinematic now | Keep your own mass field for F=ma; mirror `_vel → Body.Velocity` for debug readers |
| Box sweep never sees the wall | Floor + walls share one `MapCollider`; sweep keeps hitting the floor face | Horizontal feeler rays at body height; reject hits with `|Normal.z| > 0.7` |
| Sensor/forcer trace self-hits | Trace starts inside your own collider | Offset start past the prop's bounds AND `IgnoreGameObjectHierarchy(GameObject)` |
| `Body.MotionEnabled` throws | Property name has shifted across SDK builds | Wrap in try/catch; confirm the live name with `describe_type` first |
| Two clients fight over a held item | Proxy still thinks it's carrying | Auto-`Drop()` when `Carrying.IsProxy` (or `IsProxy && Carrying`) is true |
| Carried object jitters | Transform driven in `OnFixedUpdate` (or physics in `OnPreRender`) | Drive carried transform in `OnPreRender`; do force/integration in `OnFixedUpdate` |
| `force * Body.Mass` makes heavy props float | Mass cancellation applied unconditionally | Multiply by `Body.Mass` only for intentional mass-independent response |
| Movement jitters / `Time.Delta` is 0 | Moving in `OnUpdate` (frame-rate dependent), or game time-scaled to 0 | Move in `OnFixedUpdate`; guard `if (dt <= 0f) return;` and use `RealTime` for time-scaled motion |
| Mutating synced state silently rolls back | Wrote on a proxy/client | Gate mutators behind `if (IsProxy) return;` (owner-auth) or `if (!Networking.IsHost) return;` (host-auth) |
| Single-point buoyancy bobs/flips | One sample force gives no roll/pitch and oscillates | Sample a grid of hull points; add a per-point vertical **damper** alongside the spring (duck_pond `Buoyancy.cs:149`) |
| Boat oscillates forever | Spring with no damping | Per-point `-pointVel.z * Damping * mass / count`; scale resistance/angular-drag by submersion (duck_pond `:195`,`:134`) |
| Conveyor texture doesn't match the push | Visual scroll and physics carry computed separately | Drive both from one `Collider.SurfaceVelocity` — material `TimeScale` attribute + the collider's surface velocity (lumberyard `Conveyor.cs:14`) |

Verify live: reflection is authoritative for the installed SDK — confirm volatile members (`Body.MotionEnabled`, `CharacterController.ReleaseFromGround`, `SceneTraceResult` fields, `Rigidbody.ApplyForceAt`) with `describe_type` / `search_types` / `get_method_signature` before relying on a name, and wrap genuinely version-volatile calls in try/catch with a one-shot warning.

See also: **sbox-api** (look up exact signatures via reflection) and **sbox-build-feature** (the screenshot-driven build loop — note the bridge can't synthesize input, so verify movement/grab with `execute_csharp` or a human playtest).

---

## Corpus refresh (2026): more reference implementations

### Pattern: manual CCD via `IScenePhysicsEvents.PrePhysicsStep` (slamdunk.minigolf)

For small fast bodies (golf balls, projectiles, marbles) the built-in Rigidbody CCD is not enough. Implement `IScenePhysicsEvents.PrePhysicsStep` — it runs *after* `OnFixedUpdate` but *before* the solver, so you can detect a tunnel and redirect the body before the engine ever sees the penetration. Owner-only; proxies let the host's result replicate.

```csharp
// slamdunk.minigolf: Player/Ball.cs → PrePhysicsStep
public void PrePhysicsStep()
{
    if ( IsProxy || Rigidbody.Velocity.Length < 100f ) return;
    var start = WorldPosition;
    var tr = Scene.Trace.Sphere( 2f, start, start + Rigidbody.Velocity * Time.Delta )
        .WithTag( "entity" ).IgnoreGameObject( GameObject ).Run();
    if ( !tr.Hit ) return;
    WorldPosition = tr.HitPosition + tr.Normal * 2f;
    Rigidbody.Velocity = Vector3.Reflect( Rigidbody.Velocity, tr.Normal ) * 0.8f; // energy loss
}
```

Anti-pattern: running CCD inside `OnFixedUpdate` instead. The solver runs *after* `OnFixedUpdate`, so the body is already penetrating when you redirect it — you get a one-frame overlap pop. `PrePhysicsStep` intercepts before that.

---

### Pattern: runtime welded collision mesh from a subtree (slamdunk.minigolf)

A fast body tunnels/snags on seams between many separate convex colliders. Build one `ModelCollider` for the whole level from a `ModelBuilder` that ingests every `ModelRenderer`'s verts, welds duplicates with `worldPos.SnapToGrid(0.1f)`, then optionally stitches T-junctions (vertex on another triangle's edge — `|dist(p1,p2)-(dist(p1,p3)+dist(p2,p3))| < 0.01`). Make the result `Static = true` and `NetworkMode.Never` (each client builds its own; collision is deterministic). Pair with `Network.ClearInterpolation()` after any teleport so the ball doesn't lerp across the map.

```csharp
// slamdunk.minigolf: CollisionManager.cs (condensed)
var mb = new ModelBuilder();
var weldMap = new Dictionary<Vector3, int>();
var tris = new List<int>();
var verts = new List<Vector3>();
foreach ( var mr in hole.GetComponentsInChildren<ModelRenderer>() )
{
    foreach ( var v in mr.Model.GetVertices() )
    {
        var w = mr.WorldTransform.PointToWorld( v ).SnapToGrid( 0.1f );
        if ( !weldMap.TryGetValue( w, out int i ) ) { i = verts.Count; verts.Add(w); weldMap[w]=i; }
        tris.Add( i );
    }
}
mb.AddCollisionMesh( verts.ToArray(), tris.ToArray() );
var go = Scene.CreateObject(); go.Static = true;
go.NetworkMode = NetworkMode.Never;
go.AddComponent<ModelCollider>().Model = mb.Create();
```

---

### Pattern: `ModelBuilder.AddTraceMesh` — shoot-through procedural geometry (ataco.sdoomresurrection)

When procedural geometry must also be *traceable* (bullets, LOS, ground-checks) add a trace mesh alongside the render and collision meshes in the same `ModelBuilder`. One model, one GameObject, all three channels.

```csharp
// ataco.sdoomresurrection: DoomMap.cs (condensed)
var mb = new ModelBuilder();
mb.AddMesh( renderMesh );                              // visible
mb.AddCollisionMesh( triVerts, triIndices );           // physics bodies walk on it
mb.AddTraceMesh( tracePoints, traceIndices );          // Scene.Trace rays hit it
var mr = go.AddComponent<ModelRenderer>();
mr.Model = mb.Create();
go.AddComponent<ModelCollider>();                      // static; no Rigidbody
```

---

### Pattern: `ApplyImpulse` for a shot controller + `ICollisionListener` for impact audio (alcoholics.nice_putt_idiot)

Use `ApplyImpulse` (instantaneous momentum change, mass-aware) rather than `ApplyForce` (continuous) for a single-shot putt/kick/slingshot. Gate all input on `Rigidbody.Velocity.Length > threshold` so you can't re-hit a moving ball. Wire `ICollisionListener.OnCollisionStart` for impact sounds without a polling trace.

```csharp
// alcoholics.nice_putt_idiot: GolfBall.cs (condensed)
bool IsMoving => Rigidbody.Velocity.Length > MaxVelocityForPutt;

void Putt( Vector2 direction, float dragDistance, float maxDrag )
{
    if ( IsMoving ) return;
    var power = MinPower + MathX.Clamp( dragDistance / maxDrag, 0f, 1f ) * (MaxPower - MinPower);
    Rigidbody.ApplyImpulse( new Vector3( 0f, direction.x * power, direction.y * power ) );
    Client?.IncrementStrokes();
}

// ICollisionListener — no trace polling needed for audio
public void OnCollisionStart( Collision c )
    => Sound.Play( PuttSound, c.Contact.Point );
```

---

### Pattern: non-linear charge-power curve + stuck-ball watchdog (slamdunk.minigolf)

Shape shot power non-linearly so the low end is still useful. Track `TimeSince AlmostStill`; a ball creeping at 0.1–5 u/s for more than ~3 s is force-stopped with `Rigidbody.ClearForces()` + zero linear/angular velocity, preventing a slow roller from stalling a round. On respawn/teleport call `GameObject.Network.ClearInterpolation()` so the remote proxy doesn't visually lerp across the map.

```csharp
// slamdunk.minigolf: Ball.cs (condensed)
// Non-linear power: designer-intuitive, low-end responsive
float shaped = 2.78f * MathX.Pow( 2f * rawPower + 0.4f, 2f );
Rigidbody.ApplyForceAt( WorldPosition, dir * shaped * 9500f );

// Stuck watchdog in OnUpdate
if ( Rigidbody.Velocity.Length is > 0.1f and < 5f )
    _almostStillTime += Time.Delta;
else _almostStillTime = 0f;
if ( _almostStillTime > 3f )
{
    Rigidbody.Velocity = Vector3.Zero;
    Rigidbody.AngularVelocity = Vector3.Zero;
    Rigidbody.ClearForces();
    _almostStillTime = 0f;
}
```

Note: `MathX.Pow` — NOT `MathF.Pow` (which does not exist in the s&box sandbox).

---

### Pattern: mass-compensated jetpack thrust with ground-ray gate (master.digging_simulator)

A jetpack that feels consistent regardless of the player's physics mass multiplies thrust by `_rb.Mass` so the acceleration is mass-independent. Gate the thrust on a short downward ray finding no ground — this prevents draining fuel while standing still, and correctly re-engages as soon as the player leaves the floor.

```csharp
// master.digging_simulator: JetpackController.cs (condensed)
protected override void OnFixedUpdate()
{
    if ( !Input.Down( "jump" ) ) return;
    if ( _rb == null ) _rb = Components.GetInAncestorsOrSelf<Rigidbody>();
    // Only thrust when airborne
    var groundRay = Scene.Trace.Ray( WorldPosition, WorldPosition + Vector3.Down * 8f )
        .IgnoreGameObjectHierarchy( GameObject ).Run();
    if ( groundRay.Hit ) return;
    var force = Vector3.Up * ThrustAccel * _rb.Mass * Time.Delta; // mass-compensated
    _rb.ApplyForce( force );
    ConsumeBattery( Time.Delta );
}
```

---

### Pattern: two-range trace for aim feedback (master.digging_simulator)

Fire a long trace for a visual ghost cursor (green = in range, red = too far) and a short trace for the actual action. They share one call site but have different `WithoutTags` masks: the long trace can hit ore (show it), the short one excludes ore (dig behind it). This makes targeting readable without any UI distance calculation.

```csharp
// master.digging_simulator: DrillTool.cs (condensed)
var visualTr = Scene.Trace.Ray( ray ).WithTag( "terrain" ).Run(); // long range, any tag
var digTr    = Scene.Trace.Ray( ray ).WithoutTags( "ore", "player", "tool" ).Run(); // short range

if ( visualTr.Hit )
{
    _cursor.WorldPosition = visualTr.HitPosition;
    _cursor.Tint = (visualTr.Distance <= DigDistance) ? Color.Green : Color.Red;
}
if ( Input.Pressed( "attack1" ) && digTr.Hit && digTr.Distance <= DigDistance )
    zone.Dig( digTr.HitPosition, DigRadius );
```

---

### Pattern: boat self-righting torque + seat mount (pldr.duck_pond)

Apply a constant self-righting torque `Vector3.Cross(WorldRotation.Up, Vector3.Up) * Stability` so a physics boat can't capsize under waves or player movement. When a player mounts, disable their `Body` and collider, reparent to the seat, and decouple the camera (use the player's own eye angles in world space, not the boat's rotation) so pitch/roll don't cause seasickness. On dismount, teleport to an `ExitPoint` before re-enabling physics so they don't spawn inside the hull.

```csharp
// pldr.duck_pond: BoatController.cs (condensed)
protected override void OnFixedUpdate()
{
    if ( !Buoyancy.IsTouchingWater ) return;
    // self-right
    var uprightTorque = Vector3.Cross( WorldRotation.Up, Vector3.Up ) * Stability;
    Rigidbody.ApplyTorque( uprightTorque );
    // drive
    float speed = Rigidbody.Velocity.Length;
    float speedLimit = MathX.Min( 1f, TerminalSpeed / (speed + 0.001f) );
    Rigidbody.ApplyForceAt( BowPoint.WorldPosition,
        WorldRotation.Forward * ThrottleInput * ThrustForce * speedLimit );
}

void Mount( PlayerController player )
{
    player.Body.Enabled = false;
    player.ColliderObject.Enabled = false;
    player.SetParent( Seat, false );
    player.LocalTransform = Transform.Zero;
}
```

---

### Pattern: `MoveMode` to add swimming to the stock `PlayerController` (pldr.duck_pond)

Rather than hand-rolling a swimming controller, plug into s&box's `MoveMode` scoring system. Override `Score()` to win when the player is submerged past a threshold, and `UpdateRigidBody()` to zero gravity and add damping for the water feel. The swim mode activates against the real animated wave surface, not a flat trigger.

```csharp
// pldr.duck_pond: FixedSwim.cs
public sealed class MoveModeSwimFixed : MoveMode
{
    [Property] public int Priority { get; set; } = 10;
    [Property, Range(0,1)] public float SwimLevel { get; set; } = 0.7f;

    public override void UpdateRigidBody( Rigidbody b )
    {
        b.Gravity = false;
        b.LinearDamping = 3.3f;
        b.AngularDamping = 1f;
    }
    public override int Score( PlayerController c ) => WaterLevel > SwimLevel ? Priority : -100;
    public override void OnModeBegin() => Controller.IsSwimming = true;
    public override void OnModeEnd( MoveMode next )
    {
        Controller.IsSwimming = false;
        if ( Input.Down( "Jump" ) ) Controller.Jump( Vector3.Up * 300f ); // hop out
    }
}
```

Add this component alongside a `PlayerController`. `WaterLevel` must be computed each `OnFixedUpdate` from the actual wave surface (sample wave height at head position, then `Vector3.InverseLerp(surface, foot, head, true)`).

---

### Updated gotcha table entries (2026 additions)

| Gotcha | Why it bites | Fix |
| --- | --- | --- |
| Fast ball tunnels even with Rigidbody CCD | CCD inside `OnFixedUpdate` runs after penetration | Implement `IScenePhysicsEvents.PrePhysicsStep`; redirect before the solver sees the overlap |
| Ball slowly rolls forever, stalling a round | No idle-velocity floor | `TimeSince` watchdog: zero `Velocity`/`AngularVelocity` + `ClearForces()` after ~3 s at 0.1–5 u/s |
| Teleport visually lerps across the map | Network interpolation not flushed | Call `GameObject.Network.ClearInterpolation()` immediately after the teleport |
| Seams between course pieces catch a fast ball | Many separate convex colliders, T-junctions | Weld all verts via `SnapToGrid(0.1f)` into one `ModelBuilder.AddCollisionMesh()`, stitch T-junctions |
| Procedural mesh not hittable by rays | Trace mesh not added | `ModelBuilder.AddTraceMesh(pts, idx)` alongside `AddCollisionMesh` — one model, all three channels |
| Jetpack thrust feels different at different masses | Fixed force, not mass-compensated | `force = Up * accel * _rb.Mass * Time.Delta`; also gate on a short downward ray (no drain while grounded) |
| Boat capsizes under waves | No restoring force | `Rigidbody.ApplyTorque(Vector3.Cross(WorldRotation.Up, Vector3.Up) * Stability)` each fixed tick |
| Player view tilts with boat pitch/roll | Camera parented to boat | Decouple camera: use player's eye angles in *world* space, not the boat's rotation |
| `MathX.Pow` not found | Used `MathF.Pow` | `MathF` does not exist in the s&box sandbox; use `MathX.Pow` (and `MathX.Clamp`, etc.) throughout |

---

### Read these games for physics/trace/movement patterns

- `slamdunk.minigolf` — manual CCD (`IScenePhysicsEvents.PrePhysicsStep`), runtime welded collision mesh, charge-and-release `ApplyForceAt` with non-linear power, stuck-ball watchdog, `Network.ClearInterpolation` on teleport
- `alcoholics.nice_putt_idiot` — `ApplyImpulse` shot controller, `ICollisionListener` for impact audio, 2.5D orthographic follow camera on a physics body
- `pldr.duck_pond` — `MoveMode` swim integration, boat self-righting torque, seat mount (disable-player-physics + reparent), `SuctionPoint` attractor, decoupled camera on a vehicle
- `master.digging_simulator` — mass-compensated jetpack with ground-ray gate, two-range trace aim feedback
- `ataco.sdoomresurrection` — `ModelBuilder.AddTraceMesh` for shoot-through procedural geometry, moving extruded geometry by translating a GameObject (no mesh rebuild)
- Previously cited: `sbox-vehicle-kit` (kinematic vehicle, suspension, wall feelers), `sbox-grubs` (CharacterController + leapfrog gravity), `pldr.duck_pond` (buoyancy), `stellawisps.lumberyard` (conveyor)
