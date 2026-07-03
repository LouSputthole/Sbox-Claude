# Physics-Sports Recipe (golf / climbing / skater)

How to build a **physics-locomotion sports game** — your avatar IS a physics body, not a `CharacterController` — in modern s&box (GameObject/Component/Scene), distilled from three mined games: `slamdunk.minigolf` (multiplayer charge-and-release minigolf), `alcoholics.nice_putt_idiot` (drag-back-release "Getting Over It" rage-climber), and `barrelproto.ragroll` (ragdoll trick-score skater). Each contributes a different slice; the genre is the **composition** of a physics-body verb + per-player turn/score + co-presence lobby.

## What defines the genre

A physics-sports game replaces the walking character with a **`Rigidbody` (or ragdoll) you act on indirectly** — you don't steer it, you *impart force/impulse* and then watch physics resolve. The whole game is the feel of that one verb plus a scoring frame around it. Three flavors share the stack:

- **Charge-and-release ball** (minigolf): a `Rigidbody` ball you flick by holding an input, charging power, releasing into an `ApplyForceAt`. Host-authoritative multi-round, stroke scoring. Failure is *strokes/out-of-bounds*.
- **Drag-back-release climber** (nice_putt): one side-on `Rigidbody` ball, aim by dragging a mouse vector back from the ball, release into an `ApplyImpulse`. Punishing fall-back, no checkpoints, time+stroke scoring. Failure is *positional* — you fall down the course.
- **Ragdoll trick-scorer** (ragroll): a physics ragdoll on a skate; the controller emits dumb physics deltas (`OnAirSpin`, `OnGrindChange`) that independent trick conditions integrate into a combo score. Failure is *a dropped combo*.

**Core loop (all three):** spawn a physics body → take input that imparts force/impulse → physics resolves while you can't act again until it settles → measure the outcome (strokes / height / combo) → record it (per-player score, `Sandbox.Services` leaderboard) → reset/retry. Co-presence (other players' bodies visible as translucent proxies) is common; *authoritative* shared simulation is rare — most of these are per-player runs sharing one world.

The defining engineering choice — and the thing the rest of the cookbook does **not** cover — is **avatar-as-`Rigidbody`**: manual continuous-collision against tunneling, "can't act while moving" input gates, stuck/out-of-bounds/water watchdogs, and force-vs-impulse tuning. None of the `player-controller` patterns apply; you are not using `CharacterController.Move()`.

## The system stack to compose

Pick from these. The first three are the genre spine; the rest are scoring, presence, and polish.

1. **Physics-body verb** — the one mechanic the whole game tests. A `Rigidbody` (`ApplyForceAt`/`ApplyImpulse`) or a ragdoll. Build on a bare `Rigidbody` + a custom component, **not** the stock `PlayerController`. Tune this longest — it's 80% of the game. See **references/systems/physics-traces.md** and **references/engine/physics-traces-movement** for trace/force APIs.
2. **Input → force/impulse mapping** — charge-on-hold (minigolf), drag-vector (nice_putt), or continuous physics ticks (ragroll). Two distinct input idioms below: an `InputAction` charge meter vs. screen-space mouse drag with **no input binding at all**.
3. **"Can't act while moving" gate** — every one of these games refuses input while the body is in motion (`Velocity.Length > threshold`). This is the load-bearing rule that makes a physics verb feel like a *shot*, not a joystick.
4. **Outcome + scoring** — stroke/par scorecard (minigolf), time+stroke run (nice_putt), or combo accumulator with multiplier + grounded-flush (ragroll). See **references/systems/round-match.md** and **references/systems/leaderboards-services.md**.
5. **Reset/respawn watchdogs** — stuck-body, out-of-bounds (`z < floor`), and water handling. A slow-rolling or fallen body must be force-stopped/respawned or the turn hangs forever.
6. **Per-player turn/run state** — either a host-authoritative `[Sync(FromHost)]` scorecard (minigolf) or a fully per-`Client` run with no global round (nice_putt). See **references/systems/round-match.md**.
7. **Co-presence lobby** — other players' bodies as dimmed translucent proxies + floating Steam nametags; presence without authoritative sync. A lighter cousin of `social-hub`.
8. **Follow camera** — third-person orbit (minigolf) or side-on orthographic that tracks only two axes (nice_putt). See **references/systems/camera.md**.

## Build order

1. **One physics body in an empty box.** A `Rigidbody` (or ragdoll) and nothing else. Get gravity, mass, damping, and bounce feeling right before any course exists.
2. **The verb + the "can't act while moving" gate.** Wire charge-and-release or drag-release to `ApplyForceAt`/`ApplyImpulse`, gated on `Velocity.Length <= threshold`. Tune the power curve until a shot *feels* like a shot. This is the game.
3. **Reset watchdogs.** Stuck-body force-stop, out-of-bounds respawn, water respawn — *before* you build a course, so testing the verb never softlocks.
4. **One course/hole/rail + the outcome trigger.** A single hole cup (`ITriggerListener`), finish line, or trick zone. Wire it to record one outcome (stroke / time / combo).
5. **Scoring + per-player state.** Stroke scorecard, run timer, or combo accumulator. Decide host-authoritative (minigolf) vs per-player (nice_putt) here.
6. **Co-presence + camera polish.** Proxy dimming + nametags, follow camera. Presence is `[Sync]` reads + a Scene query, no RPCs.
7. **Leaderboards + persistence.** `Sandbox.Services` last — it's plumbing, not feel.

## How the real games do it

### Charge-and-release shot with a non-linear power curve — slamdunk.minigolf `Player/Ball.cs`

The whole minigolf control scheme. `Input.Pressed("attack1")` zeroes power; `Input.Down` accumulates power **by looking up/down** (`Input.AnalogLook.pitch`), clamped 0–1; `Input.Released` fires. Power is shaped non-linearly before becoming a force, and `[Sync] ShotPower` lets other clients see your charge-up:

```csharp
if ( Input.Pressed("attack1") ) ShotPower = 0;
if ( Input.Down("attack1") )
    ShotPower = Math.Clamp( ShotPower + Input.AnalogLook.pitch * RealTime.Delta, 0f, 1f );
if ( Input.Released("attack1") ) Stroke( EyeAngles.yaw, ShotPower );

void Stroke( float yaw, float power )
{
    var dir = Rotation.From(0, yaw, 0).Forward;
    power = 2.78f * MathF.Pow( 2f*power + 0.4f, 2.0f );   // non-linear: small inputs stay gentle
    Rigidbody.ApplyForceAt( WorldPosition, dir * power * 9500 );
    ShotsTaken++;
}
```

Gotcha: `System.MathF` does **not** exist in the s&box sandbox, so the `MathF.Pow` above is illustrative — use `MathX` (e.g. `MathX.Clamp`/`MathX.Lerp`) or a precomputed designer `Curve`, and verify the exact math call with `describe_type` before writing it. The non-linear curve (they prototyped a designer `Curve.Evaluate` first, then hardcoded a `Pow`) is what makes fine putts controllable while big swings stay reachable. The aim arrow recolors green→red off the same power via `ColorConvert.HSLToRGB(120 - power²*120, …)`.

### Drag-back-release done entirely in Razor, NO input action — alcoholics.nice_putt_idiot `Code/UI/Hud.razor` + `Pawns/GolfBall.cs`

The opposite input idiom: the core mechanic has **zero `InputAction` bindings**. It's screen-space mouse handling on the HUD panel. The HUD projects the ball to screen (`Camera.PointToScreenPixels`), builds a drag vector from the mouse, draws the aim line immediate-mode each frame (`Scene.Camera.Hud.DrawLine`), and on mouse-up calls `Putt`:

```csharp
// GolfBall.Putt — normalized drag → impulse on the side-on Y/Z plane
var power = MinPower + Math.Min(dragDistance/maxDrag, 1f) * (MaxPower - MinPower);
Rigidbody.ApplyImpulse( new Vector3(0, direction.x*power, direction.y*power) );
Client?.IncrementStrokes();
```

This is the reusable recipe for any "slingshot / aim-from-the-object" control (pool, artillery, Angry-Birds). It needs no input config — just `MousePanelEvent` handlers + `Hud.DrawLine`. The camera is **orthographic and tracks only Y/Z** (side-on 2.5D), enabled only when `!IsProxy`.

### The "can't act while moving" gate — the load-bearing rule (both ball games)

Every physics-shot game refuses input while the body is in motion. This is what turns a `Rigidbody` into a *shot* instead of a steering toy:

```csharp
// nice_putt GolfBall.cs — gates ALL input
public bool IsMoving => Rigidbody.Velocity.Length > MaxVelocityForPutt;
// ...the HUD cancels an in-progress drag if golfball.IsMoving, and Putt() early-returns when moving.
```

minigolf does the same and *also* gates on UI state — `Ball.OnUpdate` reads `ScoreBoard.Instance.Visible` and suppresses shooting while the scoreboard is open. Pick a velocity threshold (`MaxVelocityForPutt`) and gate every input path through it.

### Manual continuous-collision against tunneling — slamdunk.minigolf `Player/Ball.cs` (`IScenePhysicsEvents.PrePhysicsStep`)

A small, fast `Rigidbody` ball **tunnels through thin walls** — built-in CCD alone wasn't enough. minigolf hand-rolls CCD in `PrePhysicsStep` (runs after `OnFixedUpdate`, before the solver): for a ball moving `>100 u/s`, sphere-trace this tick's path; on a hit, teleport to the impact point + a normal offset and reflect velocity with energy loss:

```csharp
void IScenePhysicsEvents.PrePhysicsStep()
{
    if ( IsProxy ) return;                              // only the owner simulates
    if ( Rigidbody.Velocity.Length < 100f ) return;
    var from = WorldPosition;
    var to   = from + Rigidbody.Velocity * Time.Delta;
    var tr = Scene.Trace.Sphere( Radius, from, to ).WithTag("entity").Run();
    if ( !tr.Hit ) return;
    WorldPosition = tr.HitPosition + tr.Normal * Radius;
    Rigidbody.Velocity = Vector3.Reflect( Rigidbody.Velocity, tr.Normal ) * 0.8f; // 0.8 = bounce loss
}
```

This pairs with welded collision (next) — fast bodies need *both* a seamless mesh and manual CCD. Guard on `IsProxy` so only the owner runs the simulation.

### Stuck / out-of-bounds / water watchdogs — slamdunk.minigolf `Player/Ball.cs`

A physics body will eventually creep, fall off the map, or land in water — each must be detected or the turn hangs. minigolf runs three watchdogs in `OnUpdate`:

```csharp
// 1. Stuck: creeping at 0.1–5 u/s for >3s → force-stop so a slow roll doesn't hold up the round
if ( vel > 0.1f && vel < 5f ) { if ( TimeSinceAlmostStill > 3f ) StopMovement(); }
else TimeSinceAlmostStill = 0;
// StopMovement(): Rigidbody.Velocity = 0; Rigidbody.AngularVelocity = 0; Rigidbody.ClearForces();

// 2. Out-of-bounds: below the world → respawn
if ( WorldPosition.z < -100f ) { StatManager.LogOutOfBounds(); Respawn(); }

// 3. Water: a [Sync] InWater flag scale-lerps the ball to 0 over ~1s, then Respawn().
```

Critical: **every respawn calls `GameObject.Network.ClearInterpolation()`** so the teleport doesn't visually lerp across the map. (nice_putt routes every respawn/load through a vignette fade so the hard cut is hidden — see Reusable patterns.)

### Runtime welded collision mesh for seamless fast-body rolling — slamdunk.minigolf `CollisionManager.cs`

The most novel file in the corpus. A fast ball **snags/tunnels on the seams between many separate convex colliders**. minigolf welds the *entire course* into one `ModelCollider`: walk every `ModelRenderer` (skip `flag/entity/water`-tagged), pull `model.GetVertices()/GetIndices()`, transform to world space, weld duplicate verts via `worldPos.SnapToGrid(0.1f)` into a `Dictionary<Vector3,int>`, and build one collision mesh:

```csharp
var verts = new Dictionary<Vector3,int>();   // weld by snapped position
foreach ( var v in worldVerts ) {
    var key = v.SnapToGrid(0.1f);
    if ( !verts.ContainsKey(key) ) verts[key] = verts.Count;
}
var collider = go.AddComponent<ModelCollider>();
collider.Model = new ModelBuilder().AddCollisionMesh( meshVerts, meshIndices ).Create();
go.GetComponent<ModelCollider>().Static = true;   // GO is NetworkMode.Never — each client rebuilds locally
```

`StitchTJunctions()` then splits triangles where a vertex lands on another triangle's edge (detected by `|dist(p1,p2) - (dist(p1,p3)+dist(p2,p3))| < 0.01`) so the ball can't fall through micro-cracks. The collider GO is `NetworkMode.Never` + `Static = true` — collision is deterministic, so each client builds its own and it needn't replicate. Note: `MeshCollider` does **not** exist in s&box — use `HullCollider`/`ModelCollider`.

### Trick scoring = controller emits dumb deltas, conditions own the balance — barrelproto.ragroll `Skate.cs` + score conditions

The cleanest data-driven trick pattern in the corpus, and the model for *any* score-attack physics game. The physics controller emits fine-grained events each tick (`OnAirSpin{Amount=±9}`, `OnGrindChange{Grinding,SideGrind}`, `OnAngleChange`, `OnFallChange`); each trick is an **independent `IScoreCondition` subscriber** that integrates those into a score with its own thresholds and naming:

```csharp
// AirSpinCondition — accumulates spin, escalates the trick name by magnitude
void OnAirSpin( ref OnAirSpin e ) {
    if ( MathF.Sign(e.Amount) != MathF.Sign(_accumSpin) ) return; // only the dominant direction
    _accumSpin += e.Amount;
    if ( MathF.Abs(_accumSpin) > 333 ) { /* started a spin trick */ }
    // _angleLevels {515,705,885,1065} → "360 → 540 → 720 → 900 → 1080", capped at 5000
}
```

The combo accumulator (`ModeScore.cs`) opens tricks into `ComboScore` entries, joins names with `" + "`, multiplies `1 + (count-1)/4`, and **flushes** (`EndCombo`) when grounded + no open trick + a 0.5s timer expires — or instantly on fall/respawn. Final `_score += (int)(multiplier*sum)`. The split — *controller emits deltas, conditions own all balance and naming* — means a new trick is one new subscriber class. Reuse the same shape three ways (scoring, tutorial detectors, achievements). See **references/genres/platformer-obstacle.md** (skater is adjacent) and the event-bus note below.

### Per-player run vs host-authoritative scorecard — the two scoring models

**Host-authoritative (minigolf `RoundManager.cs`):** one `INetworkListener` singleton drives the whole match over `[Sync(SyncFlags.FromHost)]` state so clients can never write it. Every mutator asserts authority:

```csharp
[Sync(SyncFlags.FromHost)] public NetList<ScorecardEntry> Scorecard { get; set; } = new();
[Sync(SyncFlags.FromHost)] public HoleDefinition CurrentHole { get; set; }
// every method: Assert.True( Networking.IsHost ); OnUpdate early-returns if (!Networking.IsHost)
```

It draws a balanced course (`OrderBy(_ => Guid.NewGuid()).Take(n)` per difficulty, syncing the *indices* not the prefabs), advances on "majority done → 30s countdown → DNF-penalty non-finishers," and self-resets the loop without a scene reload. See **references/genres/party-microgame.md** for the same director/clock pattern.

**Per-player, no global round (nice_putt `Pawns/Base/Client.cs`):** each `Client` owns its run — `TimeSince RunStartTime`, `int StrokeCount`, `bool HasFinishedCurrentRun`, with `ResetRun()`/`IncrementStrokes()`. The `GoalPoint` flips `HasFinishedCurrentRun` once and early-returns on re-entry (idempotent finish). No director, no shared timer — the multiplayer is pure co-presence. Choose this for a casual climber; choose host-authoritative for a competitive match.

### Co-presence proxies + nametags — alcoholics.nice_putt_idiot `Pawns/GolfBall.cs`

Other players are translucent ghosts, not authoritative bodies. In `OnStart`, if `IsProxy`: disable the camera, dim the model to 30% alpha, zero the highlight outline, and spawn a `WorldPanel` + nametag showing `avatar:@SteamId` + name that the proxy repositions every `OnUpdate`:

```csharp
if ( IsProxy ) {
    Camera.Enabled = false;
    Model.Tint = Model.Tint.WithAlpha( 0.3f );          // ghost
    var tag = new GameObject(); tag.SetParent( GameObject );
    tag.AddComponent<WorldPanel>(); /* renders avatar:@SteamId + DisplayName */
}
```

minigolf does the equivalent via the shared-presence idiom from `platformer-obstacle`: iterate `Scene.GetAllComponents<>()`, read each peer's `[Sync]` state + `avatar:SteamId`, draw them — **multiplayer presence with zero RPCs.**

### Live + auto-refreshing Services leaderboard with metadata — all three

The shared scoring backend. Write a stat on finish (carrying metadata columns), read a board with aggregation/sort/limit on a polled loop:

```csharp
// WRITE (nice_putt GoalPoint.cs, on finish) — extra columns ride along on the stat
Services.Stats.SetValue( "best-time", time, new Dictionary<string,object> {
    ["player_name"] = name, ["stroke_count"] = strokes, ["formatted_time"] = fmt } );

// READ (nice_putt Leaderboard.razor)
var board = Services.Leaderboards.GetFromStat("best-time");
board.SetAggregationMin();  // each player's best (lowest) time     [Max for high-score]
board.SetSortAscending();   // fastest first
board.MaxEntries = 5;
await board.Refresh();       // board.Entries -> DisplayName / Value / Rank
```

ragroll adds the weekly-board + avatar idiom (`board.FilterByWeek()`, `Texture.LoadAvatar(entry.SteamId, 32)`) on a 60s refresh loop, and wraps stat *writes* in `#if !DEBUG` so dev runs don't pollute the board. Poll with a diff-guard (`EntriesAreEqual` → only `StateHasChanged()` on real movement). See **references/systems/leaderboards-services.md**.

## Reusable standout patterns

- **Procedural aim-indicator as geometry, reshaped per frame** (minigolf `Player/BallArrow.cs`). The power arrow is a 7-vertex `Mesh` built once with `Model.Builder`, then *re-skinned every frame* by overwriting the vertex list (tapered body + arrowhead, length driven by power) and calling `_mesh.SetVertexBufferData(_vertices)` — no model asset. The template for any dynamic gameplay indicator; pairs with the immediate-mode `Hud.DrawLine` aim line from nice_putt.
- **Vignette transition as an async-action gate** (nice_putt `GolfBall.UpdateVignette`). A 4-phase enum (`None→FadingIn→ExecutingAction→FadingOut`) on `Time.Delta` lerps a `Vignette` post-process; `StartVignetteTransition(Action)` fades out, **runs the disruptive op at peak intensity** (respawn/load/clear hidden), then fades in. "Hide a hard cut behind a fade" with no coroutines — every teleport/reset routes through it.
- **Hold-to-confirm radial timer with input consumption** (nice_putt `RestartPrompt.razor`). Hold an action → fill a bar (`TimeSince`/`TimeUntil`/`Remap`) → fire once → `Input.ReleaseAction(action)` so the still-held key doesn't immediately re-trigger. Suppressed while `ball.IsInTransition`. The "hold to restart/quit/revive" UX everywhere.
- **Material tint by cloning the original slot** (ragroll `SkateCustomization.SetColor`). `renderer.Materials.GetOriginal(1).CreateCopy()` → `.Set("g_vColorTint", color)` → `.SetOverride(1, mat)`. The *correct* runtime per-instance recolor on a real `ModelRenderer` — and the documented workaround for the known MeshComponent/PolygonMesh tint gotcha (see **feedback_sbox_meshcomponent_material**).
- **Typed local event bus (`EventBrokerHandler`)** (ragroll). A per-controller pub/sub keyed by struct type, events passed `ref` (zero-alloc), one bus serving ~40 event types. The spine that lets "controller emits deltas, conditions consume them" stay decoupled. Lift it for any score-attack game; pairs with the trick-condition pattern.
- **Ping-corrected networked clock** (ragroll `HostClock.cs`). Host broadcasts a `[Sync]` timestamp every 0.4s; clients add `Connection.Host.Ping*0.001f` and only snap on `>0.1s` drift, advancing locally by `Time.Delta` between updates. A smooth shared game-time for any timed multiplayer mode.
- **Cosmetics gated on achievements, not currency** (minigolf `Cosmetics/`). A `GameResource` cosmetic base with `RequiresAchievement`/`AchievementIdent`/`IsUnlocked => Achievement.IsUnlocked` and a `Progress` % for UI bars, persisted to a local `FileSystem.Data` JSON. A progression path the rest of the cookbook doesn't cover. (`"avatar"` hat special-case pulls the player's actual Steam-worn hat off `Network.Owner.GetUserData("avatar")` — a zero-content cosmetic.)

## Pitfalls

- **Don't build the avatar on `PlayerController`.** These are `Rigidbody`/ragdoll bodies acted on by force/impulse — the `player-controller` patterns (`MoveMode`, `CharacterController.Move`) do not apply and will fight your physics.
- **Always gate input on "is the body moving."** Without a `Velocity.Length > threshold` gate, a shot game feels like a steering toy and a second shot mid-roll corrupts the turn.
- **Small fast bodies tunnel — built-in CCD is not enough.** Hand-roll a sphere-trace CCD in `PrePhysicsStep` *and* weld your collision into one seamless mesh; a multi-collider course snags balls on every seam.
- **`ClearInterpolation()` on every teleport/respawn**, or the body visibly lerps across the whole map. (Or hide the cut behind a fade like nice_putt.)
- **Watchdog every way a body can stall** — slow creep (force-stop), out-of-bounds Z (respawn), water (respawn). A missing watchdog hangs the turn forever.
- **`IsProxy`-guard the simulation.** Only the owner runs CCD / force application; proxies render off `[Sync]` state. minigolf guards `PrePhysicsStep` and the watchdogs with `if (IsProxy) return`.
- **Score is client-trusted in two of these games** (nice_putt + ragroll write straight to `Services.Stats` from the owner) — a spoofed client can submit arbitrary scores. Fine for casual; for competitive, validate host-side. nice_putt even ships an unguarded `DebugTeleport` gated only by two literal `SteamId`s — a real shipped backdoor; don't copy it.
- **`MathF`/`System.Math` may not exist in the sandbox** — use `MathX.Clamp` etc.; `MeshCollider` doesn't exist (use `HullCollider`/`ModelCollider`). Verify with `describe_type` before writing.
- **Versioned saves only.** nice_putt's positional `BinaryWriter` save has no version byte, so any field reorder silently corrupts old saves. Add a version header (minigolf-style cloud `Stats` avoids this entirely).

## Which games to read

- **slamdunk.minigolf** — the canonical physics-ball game. Read for: charge-and-release `ApplyForceAt` with a non-linear power curve (`Player/Ball.cs`), manual CCD (`PrePhysicsStep`), runtime welded collision + T-junction stitching (`CollisionManager.cs`), host-authoritative round/scorecard (`RoundManager.cs`), procedural aim-arrow mesh (`Player/BallArrow.cs`), and achievement-gated cosmetics.
- **alcoholics.nice_putt_idiot** — the casual climber and the *no-input-action* drag control. Read for: screen-space drag-release in Razor (`UI/Hud.razor`) → `ApplyImpulse` (`Pawns/GolfBall.cs`), side-on orthographic camera, per-player run state (`Pawns/Base/Client.cs`), co-presence proxies + nametags, vignette-gated respawns, hold-to-confirm restart, and the Services leaderboard panel.
- **barrelproto.ragroll** — the trick/score-attack model. Read for: controller-emits-deltas + `IScoreCondition` trick detection (`Skate.cs`), the combo accumulator with multiplier + grounded-flush (`ModeScore.cs`), the typed event bus (`EventBrokerHandler.cs`), the ping-corrected clock (`HostClock.cs`), weekly leaderboard with avatars (`GlobalScores.cs`), and runtime material recolor (`SkateCustomization`).
- **pldr.duck_pond** / **stepdev.xtrem_road** — *adjacent*, for the water side of physics sports (boat buoyancy, swim `MoveMode`, multi-point spring-damper buoyancy, wave-height queries). Read only if your sport involves water/floating; map those lessons to **references/genres/vehicles.md** + the water/buoyancy engine topics, not here.

## Verify live

API shifts between SDK builds — reflection is the source of truth, not this doc. Before coding, confirm signatures with the bridge: `describe_type` / `search_types` on `Rigidbody` (`ApplyForceAt`/`ApplyImpulse`/`Velocity`/`ClearForces`), `IScenePhysicsEvents` (the `PrePhysicsStep` hook), `Scene.Trace` (`.Sphere`/`.WithTag`), `ModelCollider` + `ModelBuilder.AddCollisionMesh`, `Component.ITriggerListener`, `SyncFlags` (`FromHost`), `NetList`, `Sandbox.Services.Stats`/`Leaderboards`, and `Vector3.Reflect`/`SnapToGrid`. Confirm `MathF` availability (sandbox often lacks it → `MathX`).

Cross-links: pair this with **sbox-api** (look up exact type signatures via reflection before writing) and **sbox-build-feature** (the screenshot-driven iteration loop to build and verify the physics feel in-editor — physics-feel especially needs the screenshot/playtest loop, not code-guessing). Compose scoring from **references/systems/round-match.md** + **references/systems/leaderboards-services.md**; the skater shares ground with **references/genres/platformer-obstacle.md**.
