# Physics, Traces & Custom Movement

<!-- reference-toc:start -->
## Contents

- [Mental model](#mental-model)
- [Pattern: fluent Scene.Trace world query](#pattern-fluent-scenetrace-world-query)
- [Pattern: applying forces to a real Rigidbody](#pattern-applying-forces-to-a-real-rigidbody)
- [Pattern: CharacterController with manual gravity (leapfrog)](#pattern-charactercontroller-with-manual-gravity-leapfrog)
- [Pattern: going kinematic to own your velocity](#pattern-going-kinematic-to-own-your-velocity)
- [Pattern: suspension + collide-and-slide for a kinematic ground-follower](#pattern-suspension--collide-and-slide-for-a-kinematic-ground-follower)
- [Pattern: network-correct grab / carry / drop](#pattern-network-correct-grab--carry--drop)
- [Pattern: multi-point spring-damper buoyancy (floating boats/props)](#pattern-multi-point-spring-damper-buoyancy-floating-boatsprops)
- [Pattern: conveyor — scroll a material from Collider.SurfaceVelocity](#pattern-conveyor--scroll-a-material-from-collidersurfacevelocity)
- [Gotcha table](#gotcha-table)
- [Corpus refresh (2026): more reference implementations](#corpus-refresh-2026-more-reference-implementations)
  - [Pattern: manual CCD via IScenePhysicsEvents.PrePhysicsStep (slamdunk.minigolf)](#pattern-manual-ccd-via-iscenephysicseventsprephysicsstep-slamdunkminigolf)
  - [Pattern: runtime welded collision mesh from a subtree (slamdunk.minigolf)](#pattern-runtime-welded-collision-mesh-from-a-subtree-slamdunkminigolf)
  - [Pattern: ModelBuilder.AddTraceMesh — shoot-through procedural geometry (ataco.sdoomresurrection)](#pattern-modelbuilderaddtracemesh--shoot-through-procedural-geometry-atacosdoomresurrection)
  - [Pattern: ApplyImpulse for a shot controller + ICollisionListener for impact audio (alcoholics.niceputtidiot)](#pattern-applyimpulse-for-a-shot-controller--icollisionlistener-for-impact-audio-alcoholicsniceputtidiot)
  - [Pattern: non-linear charge-power curve + stuck-ball watchdog (slamdunk.minigolf)](#pattern-non-linear-charge-power-curve--stuck-ball-watchdog-slamdunkminigolf)
  - [Pattern: mass-compensated jetpack thrust with ground-ray gate (master.diggingsimulator)](#pattern-mass-compensated-jetpack-thrust-with-ground-ray-gate-masterdiggingsimulator)
  - [Pattern: two-range trace for aim feedback (master.diggingsimulator)](#pattern-two-range-trace-for-aim-feedback-masterdiggingsimulator)
  - [Pattern: boat self-righting torque + seat mount (pldr.duckpond)](#pattern-boat-self-righting-torque--seat-mount-pldrduckpond)
  - [Pattern: MoveMode to add swimming to the stock PlayerController (pldr.duckpond)](#pattern-movemode-to-add-swimming-to-the-stock-playercontroller-pldrduckpond)
  - [Updated gotcha table entries (2026 additions)](#updated-gotcha-table-entries-2026-additions)
  - [Read these games for physics/trace/movement patterns](#read-these-games-for-physicstracemovement-patterns)
<!-- reference-toc:end -->

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

```text
function query_world(start, end, self):
  trace := begin a ray from start to end
  exclude self from the query
  exclude objects tagged player or debris
  require at least one of solid, npc, or glass
  request hitbox-level intersections
  result := run trace

  if result reports a hit:
    consume its object, physics body, surface, position, normal, distance, and fraction
```

Interaction ray from the camera — the canonical "use" trace, self-ignored (sbox-scenestaging: PlayerUse.cs:30):

```text
function try_use(camera, player):
  endpoint := camera position + camera forward direction * interaction range
  result := trace a ray from camera position to endpoint while ignoring player
  if result hit an object that exposes an interaction component:
    invoke that component's use action
```

Ground check: trace a short ray `Vector3.Down`. Note a trace hits a **child** collider/hitbox, not the entity root — resolve the owner with `GetInAncestorsOrSelf<T>()`, not a self-only `Components.Get<T>()`.

---

## Pattern: applying forces to a real Rigidbody

Apply forces in `OnFixedUpdate`. `ApplyForce` pushes through the center of mass; `ApplyForceAt(tr.HitPosition, force)` pushes at a point and imparts torque/spin. Multiply by `Body.Mass` only when you want a **mass-independent** ("ignore mass") response so light and heavy props react identically. The sensor trace **must self-ignore** (offset start past the prop's bounds + `IgnoreGameObjectHierarchy`) or it pushes itself (wirebox: WireForcerComponent.cs:68):

```text
on each fixed physics step:
  place the sensor origin beyond this object's bounds along its up axis
  sweep a narrow trace outward that:
    uses hitboxes, accepts solid/npc/glass, rejects debris/player, and ignores this hierarchy
  stop unless the trace hit a valid physics body

  force := configured magnitude along this object's up axis
  if equal acceleration across masses is desired, multiply force by target mass
  apply force through the center, or apply it at the hit position when torque is desired
```

---

## Pattern: CharacterController with manual gravity (leapfrog)

CharacterController gives you no gravity. Apply it as **two half-steps** around `Move()` (leapfrog/Verlet) — a single full step lags the jump/fall arc. Move in `OnFixedUpdate`, never `OnUpdate` (sbox-grubs: GrubPlayerController.cs:46).

```text
on each authoritative fixed physics step:
  if grounded:
    remove vertical velocity
    accelerate toward the requested movement velocity
    apply strong ground friction
  otherwise:
    subtract one half-step of gravity from velocity
    apply light air friction

  ask CharacterController to move

  if still airborne:
    subtract the second half-step of gravity
```

Jump: set the velocity, then **`ReleaseFromGround()`**, then trigger the animator — or the controller re-sticks to the floor the same tick and eats the jump (sbox-grubs: GrubPlayerController.cs:122-124):

```text
when jump is pressed:
  set the controller's horizontal and upward launch velocity
  release the controller from the ground before its next move
  trigger the jump presentation
```

---

## Pattern: going kinematic to own your velocity

When the solver caps your drive force, take movement off Source 2's hands once and integrate by hand. Keep the Body for its collider geometry + collision queries only (sbox-vehicle-kit: VehicleBase.Wheels.cs:192-201).

```text
once, when a physics body becomes available:
  disable solver-driven motion using the SDK member verified for the current build
  initialize the component-owned velocity and mark setup complete

on each fixed physics step:
  ensure kinematic setup has happened
  stop when the time step is zero or negative
  add accumulated acceleration using a component-owned mass value
  advance world position from the owned velocity
  mirror that velocity to the body for observers and debugging
```

---

## Pattern: suspension + collide-and-slide for a kinematic ground-follower

Per wheel/foot, raycast down and apply a Hooke's-law spring + damper into a single Z velocity change (sbox-vehicle-kit: VehicleBase.Wheels.cs:549-571):

```text
for each suspension sample:
  trace downward through rest length plus wheel radius while ignoring the vehicle
  if ground is found:
    normalize compression from hit distance and clamp it to the valid range
    spring contribution := compression * stiffness
    damping contribution := compression change per second * damping
    add both contributions to the body's accumulated suspension force
```

**Walls are the trap:** when floor and walls are one `MapCollider`, a low box sweep keeps hitting the shared floor face and never sees the wall. Instead fire horizontal **feeler rays at mid-body height** (they physically cannot touch the floor), reject floor/ramp hits with `|Normal.z| > 0.7`, clamp the move, and slide the remainder by cancelling only the into-wall velocity component (sbox-vehicle-kit: VehicleBase.Wheels.cs:494-523):

```text
cast a horizontal feeler from mid-body height while ignoring the mover hierarchy
if it hits a surface whose vertical normal magnitude is at most the floor threshold:
  flatten and normalize the surface normal
  project the remaining displacement onto the wall tangent and apply that slide
  measure velocity into the wall
  if velocity points into the wall, remove only that normal component
```

---

## Pattern: network-correct grab / carry / drop

To carry a physics object across the network: trace for it, `TakeOwnership`, parent it, tag it, and **disable its Rigidbody** so it follows kinematically. Drive its transform in `OnPreRender` (render-rate, smooth). On drop, re-enable the Rigidbody, set throw velocity, unparent, `DropOwnership`. The non-obvious safety: a proxy still flagged carrying must auto-Drop or two clients fight over ownership (sbox-scenestaging: NetworkTest.cs:71).

```text
synced state: carried object

on pickup request:
  sphere-trace from the eyes through carry range, excluding players
  require a hit body whose object carries the pickup tag
  take network ownership of that object
  store it as carried, parent it to the carrier while preserving world transform, and add a carrying tag
  disable its valid Rigidbody so parent motion, not physics, drives it

before rendering:
  when the carried object is valid and locally authoritative, place it at the hold transform

during carry maintenance:
  if the carried object has become a proxy, immediately run the drop path

on drop:
  re-enable any valid Rigidbody and assign the throw velocity
  unparent while preserving world transform, remove the carrying tag, and release ownership
  clear the synced carried reference
```

Read `tr.Surface.SoundCollection` for material-specific footsteps/impact audio off any trace.

---

## Pattern: multi-point spring-damper buoyancy (floating boats/props)

Float a Rigidbody on a water surface with a **grid of sample points over the hull**, each contributing a Hooke's-law spring (force ∝ depth below the wave surface) plus a damper (opposes that point's vertical velocity, kills oscillation). Sampling at several offset points — not one centre point — is what gives roll/pitch and a stable, non-bouncy float. Run it in `OnFixedUpdate`, gated `if (IsProxy) return;` (host/owner-authoritative physics).

```text
on each authoritative fixed physics step:
  derive a nine-point hull sample from the center, edges, and corners of local bounds
  read body mass and angular velocity
  for each local sample:
    transform it to world space and query water height there
    depth := water surface plus offset minus sample height
    skip samples above the surface
    spring := depth * stiffness * mass * remaining air / sample count
    point velocity := linear velocity + angular velocity crossed with the sample offset
    damper := negative vertical point velocity * damping * mass / sample count
    apply the combined upward force at that world-space sample
```

Verified against pldr.duck_pond `Code/Water/Buoyancy/Buoyancy.cs`: the 9-point hull sample (`:149-172`), per-point `spring = depth * Stiffness * mass * AirVolume / pointCount` + velocity damper applied with `ApplyForceAt` (`:195-201`), quadratic **water resistance** `-0.5·ρ·v²·Cd·A·dir·submersion` (`:111-130`), submersion-scaled **angular drag** (`:134-145`), and **wave-transport** that nudges the hull along the wave's horizontal displacement (`:207-216`). The `[Sync] AirVolume` (`:25`) drains while submerged so a holed boat slowly **sinks** (`:99-107`). Global surface queries `WaterManager.GetWaterHeightAt` / `GetWaveDisplacementAt` are the seam any water system should expose (`Code/Water/WaterManager.cs`). The same 9-point design appears in treehaven.sdiver and stepdev.xtrem_road.

---

## Pattern: conveyor — scroll a material from `Collider.SurfaceVelocity`

A moving-belt look without animating geometry: set a `BoxCollider.SurfaceVelocity` (physics actually carries props along it), then drive the belt material's scroll attribute from that same velocity so the texture visibly matches the push.

```text
component requirements:
  a box collider supplies surface velocity
  a model renderer supplies the belt material attributes

on each fixed physics step:
  if the renderer and its scene object are valid:
    set the material's TimeScale attribute from the collider's belt-axis surface velocity
```

The implementation cited at stellawisps.lumberyard `Code/Tycoon/Conveyor.cs:14` demonstrates the shared-input design. One source of truth (`SurfaceVelocity`) drives both the physics carry and the visual scroll, so they can never disagree. The belt material reads its `TimeScale` attribute to pan its UVs. Pair with trigger-zone "suckers" (a `BoxCollider` trigger that pulls items toward a sell/buy point) for a full belt economy (lumberyard `ItemSucker.cs`/`SellSucker.cs`).

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

```text
before each physics-solver step:
  stop on proxies or when speed is below the CCD threshold
  sphere-sweep from current position through this step's velocity displacement
  require the entity tag and ignore the moving object itself
  if the sweep hits:
    place the body just outside the surface by one sweep radius
    reflect velocity around the hit normal and multiply by an energy-retention factor
```

Anti-pattern: running CCD inside `OnFixedUpdate` instead. The solver runs *after* `OnFixedUpdate`, so the body is already penetrating when you redirect it — you get a one-frame overlap pop. `PrePhysicsStep` intercepts before that.

---

### Pattern: runtime welded collision mesh from a subtree (slamdunk.minigolf)

A fast body tunnels/snags on seams between many separate convex colliders. Build one `ModelCollider` for the whole level from a `ModelBuilder` that ingests every `ModelRenderer`'s verts, welds duplicates with `worldPos.SnapToGrid(0.1f)`, then optionally stitches T-junctions (vertex on another triangle's edge — `|dist(p1,p2)-(dist(p1,p3)+dist(p2,p3))| < 0.01`). Make the result `Static = true` and `NetworkMode.Never` (each client builds its own; collision is deterministic). Pair with `Network.ClearInterpolation()` after any teleport so the ball doesn't lerp across the map.

```text
create an empty model builder, quantized-position-to-index map, vertex list, and index list
for every descendant model renderer:
  for every source vertex:
    transform it to world space and snap it to the weld grid
    allocate one output index only when that quantized position is new
    append the resolved index to the triangle stream
add the welded vertices and indices as one collision mesh
create a static, never-networked scene object
assign the built model to its ModelCollider
```

---

### Pattern: `ModelBuilder.AddTraceMesh` — shoot-through procedural geometry (ataco.sdoomresurrection)

When procedural geometry must also be *traceable* (bullets, LOS, ground-checks) add a trace mesh alongside the render and collision meshes in the same `ModelBuilder`. One model, one GameObject, all three channels.

```text
create one model builder
add the visible render mesh
add triangle data for physical collision
add point/index data for Scene.Trace intersections
build the model and assign it to a renderer on the target object
add a static ModelCollider without a Rigidbody
```

---

### Pattern: `ApplyImpulse` for a shot controller + `ICollisionListener` for impact audio (alcoholics.nice_putt_idiot)

Use `ApplyImpulse` (instantaneous momentum change, mass-aware) rather than `ApplyForce` (continuous) for a single-shot putt/kick/slingshot. Gate all input on `Rigidbody.Velocity.Length > threshold` so you can't re-hit a moving ball. Wire `ICollisionListener.OnCollisionStart` for impact sounds without a polling trace.

```text
moving := body speed exceeds the allowed re-hit threshold

on putt request(direction, drag distance):
  stop if moving
  normalize drag distance and clamp it to zero through one
  interpolate shot power between designer minimum and maximum
  apply one impulse along the mapped play-plane direction
  increment the responsible player's stroke count

on collision start:
  play the impact sound at the first contact point
```

---

### Pattern: non-linear charge-power curve + stuck-ball watchdog (slamdunk.minigolf)

Shape shot power non-linearly so the low end is still useful. Track `TimeSince AlmostStill`; a ball creeping at 0.1–5 u/s for more than ~3 s is force-stopped with `Rigidbody.ClearForces()` + zero linear/angular velocity, preventing a slow roller from stalling a round. On respawn/teleport call `GameObject.Network.ClearInterpolation()` so the remote proxy doesn't visually lerp across the map.

```text
on shot release:
  transform raw charge with the configured nonlinear power curve
  apply the resulting force at the ball position along the aim direction

on each update:
  accumulate almost-still time only while speed lies between the creep thresholds
  otherwise reset that timer
  once the timer exceeds the watchdog duration:
    zero linear and angular velocity
    clear accumulated forces
    reset the watchdog
```

Note: `MathX.Pow` — NOT `MathF.Pow` (which does not exist in the s&box sandbox).

---

### Pattern: mass-compensated jetpack thrust with ground-ray gate (master.digging_simulator)

A jetpack that feels consistent regardless of the player's physics mass multiplies thrust by `_rb.Mass` so the acceleration is mass-independent. Gate the thrust on a short downward ray finding no ground — this prevents draining fuel while standing still, and correctly re-engages as soon as the player leaves the floor.

```text
on each fixed physics step:
  stop unless the jump control is held
  resolve the nearest ancestor Rigidbody when it is not cached
  trace a short distance downward while ignoring the player hierarchy
  stop when ground is detected
  compute upward thrust from desired acceleration, body mass, and fixed-step duration
  apply that force and consume battery for the same duration
```

---

### Pattern: two-range trace for aim feedback (master.digging_simulator)

Fire a long trace for a visual ghost cursor (green = in range, red = too far) and a short trace for the actual action. They share one call site but have different `WithoutTags` masks: the long trace can hit ore (show it), the short one excludes ore (dig behind it). This makes targeting readable without any UI distance calculation.

```text
visual hit := run the long terrain trace used for cursor feedback
action hit := run the action trace while excluding ore, player, and tool tags

if the visual trace hit:
  move the cursor to its hit position
  tint it valid when within action range, otherwise tint it out-of-range

if primary action was pressed and the action trace hit within range:
  dig the target zone at that position using the configured radius
```

---

### Pattern: boat self-righting torque + seat mount (pldr.duck_pond)

Apply a constant self-righting torque `Vector3.Cross(WorldRotation.Up, Vector3.Up) * Stability` so a physics boat can't capsize under waves or player movement. When a player mounts, disable their `Body` and collider, reparent to the seat, and decouple the camera (use the player's own eye angles in world space, not the boat's rotation) so pitch/roll don't cause seasickness. On dismount, teleport to an `ExitPoint` before re-enabling physics so they don't spawn inside the hull.

```text
on each fixed physics step while touching water:
  compute restoring torque from boat-up crossed with world-up, scaled by stability
  apply that torque
  derive a thrust limiter from terminal speed and current body speed
  apply forward throttle force at the bow so steering can create torque

on mount(player):
  disable the player's body and collider
  parent the player to the seat without retaining the previous local offset
  reset the player's local transform
```

---

### Pattern: `MoveMode` to add swimming to the stock `PlayerController` (pldr.duck_pond)

Rather than hand-rolling a swimming controller, plug into s&box's `MoveMode` scoring system. Override `Score()` to win when the player is submerged past a threshold, and `UpdateRigidBody()` to zero gravity and add damping for the water feel. The swim mode activates against the real animated wave surface, not a flat trigger.

```text
swim move mode configuration:
  expose a selection priority and a normalized submersion threshold

when updating the player's Rigidbody:
  disable gravity
  apply strong linear damping and moderate angular damping

when scoring this mode:
  return its priority above the submersion threshold; otherwise return a losing score

on mode begin, mark the controller as swimming
on mode end:
  clear the swimming flag
  if jump remains held, apply an upward exit jump
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
