# Combat & Weapons

<!-- reference-toc:start -->
## Contents

- [Mental model](#mental-model)
- [Pick an architecture (commit to ONE spine)](#pick-an-architecture-commit-to-one-spine)
- [Recipes](#recipes)
  - [1. Shot cooldown with TimeUntil + AddShootDelay](#1-shot-cooldown-with-timeuntil--addshootdelay)
  - [2. Fire a hitscan shot (modern fluent trace + broadcast effects)](#2-fire-a-hitscan-shot-modern-fluent-trace--broadcast-effects)
  - [3. Physical (travel-time) projectile as a per-step integrating mover](#3-physical-travel-time-projectile-as-a-per-step-integrating-mover)
  - [4. Radial explosion: sphere query → falloff → per-body knockback → networked damage](#4-radial-explosion-sphere-query--falloff--per-body-knockback--networked-damage)
  - [5. Ballistic arc prediction (aim reticle / trajectory line / AI targeting)](#5-ballistic-arc-prediction-aim-reticle--trajectory-line--ai-targeting)
  - [6. Cancellable reload/channel/build — capture-then-compare the token](#6-cancellable-reloadchannelbuild--capture-then-compare-the-token)
- [Gotcha table](#gotcha-table)
- [Corpus refresh (2026): more reference implementations](#corpus-refresh-2026-more-reference-implementations)
  - [A. Anim-event damage windows instead of timers (aethercore.versus)](#a-anim-event-damage-windows-instead-of-timers-aethercoreversus)
  - [B. [Rpc.Owner] damage routing preserves private timers (aethercore.versus)](#b-rpcowner-damage-routing-preserves-private-timers-aethercoreversus)
  - [C. Penetrating hitscan — IEnumerable (ataco.sdoomresurrection)](#c-penetrating-hitscan--ienumerable-atacosdoomresurrection)
  - [D. Frame-table weapon state machine without an animgraph (ataco.sdoomresurrection)](#d-frame-table-weapon-state-machine-without-an-animgraph-atacosdoomresurrection)
  - [E. Combo cancel windows from a WeaponDefinition GameResource (aethercore.versus)](#e-combo-cancel-windows-from-a-weapondefinition-gameresource-aethercoreversus)
  - [F. Floating damage numbers via static pub/sub + PointToScreenPixels (aethercore.versus)](#f-floating-damage-numbers-via-static-pubsub--pointtoscreenpixels-aethercoreversus)
  - [G. Per-recipient outline via ghost-clone + Rpc.FilterInclude (despawn.murder)](#g-per-recipient-outline-via-ghost-clone--rpcfilterinclude-despawnmurder)
  - [H. [Rpc.Host] purchase re-validation — price from ConVar, not the item (despawn.murder)](#h-rpchost-purchase-re-validation--price-from-convar-not-the-item-despawnmurder)
  - [Updated "read these games" pointer](#updated-read-these-games-pointer)
<!-- reference-toc:end -->

Build weapons, projectiles, explosions, ballistic previews, and cancellable combat actions in s&box using the modern Component + `Scene.Trace` API.

## Mental model

A weapon is a `Component` that runs an **input pump** every frame and an authoritative **fire path** on the owner. Keep three jobs separate so the same weapon is drivable by a human OR an AI:

- `Can*Attack()` — am I *allowed* (ammo, not reloading, cooldown elapsed)?
- `Wants*Attack()` — did the controller *ask* (input down, or AI flag set)?
- `*Attack()` — actually *do* it (trace, damage, effects).

The pump just wires them: `if ( CanPrimaryAttack() && WantsPrimaryAttack() ) PrimaryAttack();` (sandbox: `Code/Game/Weapon/BaseWeapon/BaseWeapon.cs:129`). An AI never touches input — it overrides `Wants*` to return a decision, and the rest is identical.

Authority: the fire trace + damage run only on the owner (`if ( IsProxy ) return;`); cosmetic muzzle flash / tracers / decals run for everyone via `[Rpc.Broadcast]`. See the gotcha table — getting this wrong means "only I see my own bullets" or silent rollback.

## Pick an architecture (commit to ONE spine)

| Architecture | Shape | Best for | Source |
|---|---|---|---|
| **Deep base-class hierarchy** | `BaseCarryable → BaseWeapon → BaseBulletWeapon → GlockWeapon`; base owns pump/ammo/cooldown + a record-struct config, concrete overrides `PrimaryAttack`/`GetPrimaryFireRate` | curated, code-authored set sharing lots of logic | sandbox: `BaseWeapon.cs:116` |
| **Data-driven enum + delegates** | one `Weapon` component, `FiringType` switch (Instant/Charged/Cursor/Continuous) in `OnUpdate`, designer-set `[Property]` Cooldown/MaxUses + `OnFire`/`OnFireFinished` delegate properties | LARGE arsenal designers tune in-editor without new classes | data-driven pattern |
| **Strategy as a child Component** | `abstract class BulletInfo : Component { Shoot(...); }`; attach `HitScanBulletInfo` or `PhysicalBulletInfo` | polymorphic, designer-selectable behavior where each variant has real distinct logic | simple-weapon-base: `bullets/BulletInfo.Base.cs:5` |

The strategy-Component is the modern idiomatic choice when each variant is substantial; prefer it over an enum+switch. Default it safely: `Components.Create<HitScanBulletInfo>()` if none is attached. Don't mix all three.

## Recipes

### 1. Shot cooldown with `TimeUntil` + `AddShootDelay`

`TimeUntil` is a struct that counts down in real seconds; assign a float and it's "now + that". Put the check in `Can*Attack`, push it forward after firing (sandbox: `BaseWeapon.cs:21,171`).

```text
state: time remaining until the next permitted shot

to add a firing delay, reset that countdown from the requested duration

primary attack is allowed only when:
  an owned weapon has ammunition
  no reload is in progress
  the shot countdown has elapsed

when primary attack begins:
  reset the countdown from the configured fire interval
  execute the authoritative firing behavior
```

Keep per-weapon tunables (fire rate, aim-cone, spread, recoil) in a `record struct` config so a concrete weapon overrides them trivially.

### 2. Fire a hitscan shot (modern fluent trace + broadcast effects)

Run the trace authoritatively, apply damage, THEN broadcast cosmetics (sandbox: `BaseBulletWeapon.cs:81`; simple-weapon-base: `BulletInfo.HitScan.cs:11`).

```text
authoritative hitscan flow:
  perturb the aim direction by the configured horizontal and vertical spread
  trace to weapon range while:
    ignoring the shooter hierarchy
    using the named bullet collision rules
    excluding controller-only objects
    applying bullet radius and hitbox precision
  resolve IDamageable from the hit object's ancestors
  if found, deliver bullet damage with attacker, weapon, hitbox, position, force, and damage context
  broadcast cosmetic shot results using the trace endpoint, hit flag, normal, object, and surface
```

```text
unreliable broadcast shot-effects handler:
  return immediately on a dedicated server
  render muzzle flash and tracer
  when a hit occurred, create the appropriate impact decal and sound from surface data
```

`WithCollisionRules`+`UseHitboxes` confirmed at sandbox `BaseBulletWeapon.cs:83-86`; the `[Rpc.Broadcast( NetFlags.Unreliable )]` + `Application.IsDedicatedServer` early-out at simple-weapon-base `BulletInfo.HitScan.cs:59-62`.

### 3. Physical (travel-time) projectile as a per-step integrating mover

Spawn a networked GameObject carrying a mover; integrate in **`OnFixedUpdate`** (deterministic, frame-rate-independent) and trace each segment from last position to new (simple-weapon-base: `bullets/PhysicalBullet.Mover.cs:34`). Wrap it behind the same `BulletInfo.Shoot` strategy interface as hitscan so weapon code is identical.

```text
on each fixed physics step:
  stop on proxies, after impact, or without an owner
  reduce velocity by drag, then add gravity for this step
  compute the segment from current position through the resulting displacement
  trace that segment while ignoring the owner
  if it hits:
    resolve impact, snap to the hit position, and mark the projectile complete
  otherwise advance to the segment endpoint
```

### 4. Radial explosion: sphere query → falloff → per-body knockback → networked damage

One helper ties it together: query `Scene.FindInPhysics(new Sphere(...))`, inverse-square falloff, knockback down the **correct path per body type**, damage via `Health.TakeDamage`, world destruction host-only (sbox-grubs: `Code/Helpers/ExplosionHelper.cs:18`).

```text
for each physics object found inside the blast sphere:
  resolve Health from the object or its ancestors; skip objects without it
  calculate clamped quadratic distance falloff
  if the target resolves to a CharacterController:
    punch it away from the blast and release it from the ground
  otherwise, if it owns a Rigidbody:
    apply a mass-scaled impulse at the body position
  deliver explosion damage scaled by falloff with attacker and origin context

under an RPC filter that includes only the host:
  apply any terrain carve or persistent world scorch
```

`CharacterController.Punch` + `ReleaseFromGround` vs `Rigidbody.ApplyImpulseAt` and the `Rpc.FilterInclude(c => c.IsHost)` host-only carve are all at `ExplosionHelper.cs:41-66`.

### 5. Ballistic arc prediction (aim reticle / trajectory line / AI targeting)

Sample the flight path in N segments, `Scene.Trace.Ray` each, stop at first hit, return the segment list (sbox-grubs: `Code/Helpers/ArcSegment.cs:64`). Two modes:

- **`RunTo`** — cubic bezier toward a control point: cheap, smooth preview, not physically faithful.
- **`RunTowards`** — integrates the *real* motion so the preview matches the live projectile.

```text
initialize preview velocity and position from the launch inputs
repeat up to the configured segment count:
  remember the current position as the segment start
  apply the same wind and gravity increments used by the live projectile
  integrate the next preview position
  trace between segment endpoints using projectile radius while ignoring the shooter
  append the segment
  if the trace hits, clamp the endpoint to the hit, record its normal, and stop
```

`RunTowardsWithBounces` reflects off surfaces and damps `activeForce *= 0.66f` per bounce, treating a near-vertical hit (`Vector3.GetAngle(hitNormal, Vector3.Up) < 45`) as a stop (`ArcSegment.cs:114`). **Use the exact same gravity/wind/drag constants as the live projectile or the preview lies.**

### 6. Cancellable reload/channel/build — capture-then-compare the token

Model long interruptible actions as `async`. A new action cancels the old via a stored `CancellationTokenSource`; the critical idiom is to **capture your token at entry and only fire the "finished" callback if you still own it** — otherwise a superseded reload emits a spurious "finished" on the wrong instance (sandbox-plus-plus: `Code/Game/Weapon/BaseWeapon/BaseWeapon.Reloading.cs:95`).

```text
on reload start:
  stop unless reload is currently allowed
  cancel any previous reload
  create and store a new cancellation source; mark reload active
  await the reload routine with that source's token
  during cleanup, clear active state only if the stored source is still this source
  dispose the local source

reload routine:
  capture the currently stored source
  while the clip needs ammunition and cancellation was not requested:
    await the next reload interval using the cancellation token
    transfer the appropriate ammunition
  during cleanup, emit the reload-finished presentation only if the captured source is still current
```

`CancelReload()` just guards-and-cancels: `if ( reloadToken?.IsCancellationRequested == false ) reloadToken.Cancel();` (`BaseWeapon.Reloading.cs:46`). Generalizes to any overlapping cancellable action, not just reloads.

## Gotcha table

| Gotcha | Fix |
|---|---|
| Overlapping async reloads fire a stale "finished" callback on the wrong instance | Capture `var mySource = reloadToken` at entry; in `finally` only finalize `if ( reloadToken == mySource )` (`BaseWeapon.Reloading.cs:98,146`) |
| Knockback silently does nothing | Kinematic `CharacterController` needs `.Punch` (+ often `.ReleaseFromGround`); physics bodies need `Rigidbody.ApplyImpulseAt` — different APIs per body type (`ExplosionHelper.cs:53,64`) |
| Headshots / limb damage don't register | Add `.UseHitboxes()` to the shot trace, else hits land on the coarse physics body |
| Aim-preview arc doesn't match where the shot lands | Reuse the *exact* gravity/wind/drag constants the live projectile integrates; keep them in one shared place (`ArcSegment.cs`) |
| Only the shooter (or only the host) sees muzzle flash/tracers/decals | Put cosmetics in a `[Rpc.Broadcast]` method, not the authoritative fire path |
| Terrain carve/scorch desyncs across clients | Run world destruction host-only inside `using ( Rpc.FilterInclude( c => c.IsHost ) )` (`ExplosionHelper.cs:41`) |
| `Components.Get<Health>()` on a trace hit returns null | A trace hits a child collider/hitbox, not the entity root → use `FindMode.EverythingInSelfAndAncestors` / `GetInAncestorsOrSelf<IDamageable>()` |
| Projectile ballistics jitter / differ across clients | Integrate in `OnFixedUpdate`, not `OnUpdate` (frame-rate dependent) |
| Designers can't retune what bullets hit without a code change | Use a named collision ruleset (`.WithCollisionRules("bullet")`) instead of hardcoded tag checks |
| Effect/RPC body wastes cycles or null-crashes on a dedicated server | Early-return on `Application.IsDedicatedServer` in broadcast bodies (`BulletInfo.HitScan.cs:62`) |
| `new GameObject`/`Clone()` projectile is invisible to other clients | Call `NetworkSpawn()` after configuring it; spawn on exactly one machine (`if ( IsProxy ) return;`) |
| Synced mutator on a proxy silently rolls back | Gate every authoritative mutator behind `if ( IsProxy ) return;` (owner) or `if ( !Networking.IsHost ) return;` (host) |
| `Network.IsOwner` is false in solo editor playtest (no lobby) → firing disabled | The bridge is single-client and can't synthesize keypresses; verify fire/cooldown/reload with `execute_csharp` or a human playtest, visuals with `screenshot_from` |

**Verify live:** API names drift between SDK builds — confirm against the installed SDK with `describe_type`/`search_types`/`get_method_signature` (reflection is authoritative) before writing, e.g. `describe_type SceneTrace`, `describe_type CharacterController`, `search_types BulletInfo`. Wrap genuinely volatile calls in try/catch with a safe fallback.

Cross-links: see the **$sbox-api** skill for reflection-verified type/method signatures, and **$sbox-build-feature** for the screenshot-driven iteration loop that proves a weapon actually fires in-scene.

## Corpus refresh (2026): more reference implementations

### A. Anim-event damage windows instead of timers (aethercore.versus)

Opening a hit trigger based on elapsed time is fragile — if a flinch or guard-break interrupts the swing before `hit_end` fires, the trigger stays active and grants free hits. The correct pattern is to open a **collider trigger on the `hit_start` anim event** and close it on `hit_end`, then re-validate attacker state inside `OnTriggerEnter`.

`versus/Code/WeaponDamage.cs` — `Component.ITriggerListener`; `OnAttackHitStart` enables the collider, `OnAttackHitEnd` disables it. Inside `OnTriggerEnter` it dedupes with a `HashSet<GameObject>` (one hit per target root per swing) and refuses damage if the attacker's state flags are wrong:

```text
subscribe the weapon to generic animation events
on hit-start, clear the per-swing hit set and enable the damage trigger with current attack values
on hit-end, disable the damage trigger

when another collider enters during the window:
  resolve its root object
  reject roots already damaged by this swing
  re-check that the attack remains valid and is not guard-broken or parrying
  reject friendly targets
  record the root and deliver damage through an ancestor IDamageable
```

Anti-pattern: using `TimeUntil hitWindowEnd > 0` as the gate — timer doesn't know the swing was interrupted.

### B. `[Rpc.Owner]` damage routing preserves private timers (aethercore.versus)

When melee combat depends on non-synced private state (parry window timer, i-frame flag, guard meter) that is only correct on the victim's owning machine, routing damage through `[Rpc.Owner]` ensures the victim's own authoritative logic runs the outcome. On the attacker's proxy the victim's timer is always stale.

`versus/Code/HealthComponent.cs` + `PlayerController.cs`:

```text
public damage entry point:
  forward the damage record to an owner-targeted RPC

on the victim owner's machine:
  if the private parry timer is active and the attack angle is valid, resolve a parry and stop
  if guarding, let the shield absorb the damage and stop
  otherwise subtract health and emit the damage-received event
```

Anti-pattern: reading `parryWindowTimer` or an i-frame flag on the attacker's machine — proxy values are always 0/stale.

### C. Penetrating hitscan — `IEnumerable<SceneTraceResult>` (ataco.sdoomresurrection)

The standard `.Run()` returns only the first hit. For weapons that pierce multiple targets (SSG pellets, energy beams, chain-lightning), call `.RunAll()` which returns `IEnumerable<SceneTraceResult>` sorted by distance. Apply damage to each; break on the first solid (non-passthrough) surface.

`sdoomresurrection/Code/weapon/Weapon.cs`:

```text
run an all-results ray trace through weapon range:
  include damageable and blocking tag classes
  apply the configured trace radius

visit results in distance order:
  resolve the first IDamageable from the hit object or its ancestors and apply bullet damage
  stop traversal when the hit carries the blocking tag
```

Spread is applied per-pellet as `Rotation.FromYaw( rand * spread.x ) * Rotation.FromPitch( rand * spread.y )` applied to the base aim ray — cleaner than `WithAimCone` when you need asymmetric X/Y spread.

### D. Frame-table weapon state machine without an animgraph (ataco.sdoomresurrection)

When a weapon must match a sprite-sheet or HUD animation frame-by-frame (retro FPS, 2D sidebar weapon), drive state with a `switch(State)` tic counter instead of an animation graph. Each case sets the sprite, queues the next frame timer, and fires side-effects.

`sdoomresurrection/Code/weapon/DoomShotgun.cs` demonstrates the frame-table state machine:

```text
synced state: ready, fire, flash, reload, or empty
state: countdown until the next frame transition

on each authoritative fixed step after the countdown elapses:
  choose behavior from the current state
  for fire:
    select the firing sprite, emit muzzle presentation, and fire the pellet pattern
    arm the frame countdown from the state's tic duration
    advance to flash
  for flash:
    select the follow-up sprite
    arm its tic duration
    advance to reload
  define the remaining states with the same explicit transition pattern
```

Anti-pattern: using `Task.DelaySeconds` chains for frame pacing — they accumulate uncancellable continuations if the weapon is dropped mid-sequence.

### E. Combo cancel windows from a `WeaponDefinition` GameResource (aethercore.versus)

Hardcoding attack durations makes re-timing animations break the cancel system. Store combo durations and cancel windows as **normalized fractions** in a `[GameResource]` so re-exporting animations never requires code changes.

`versus/Code/Data/WeaponDefinition.cs` (key fields):

```text
WeaponDefinition game resource fields:
  attack duration in seconds for each combo index
  attack damage for each combo index
  normalized cancel-window start for each combo index

at runtime:
  absolute cancel start := normalized start * duration for the active combo index
  once elapsed attack time reaches that value, allow buffered chain or dodge cancellation
```

Input buffering: pressing the next attack during a swing sets `attackBuffer = bufferDuration`; the cancel window polls `attackBuffer > 0` and consumes it. Dodge-cancel has higher priority than combo-chain.

### F. Floating damage numbers via static pub/sub + `PointToScreenPixels` (aethercore.versus)

Decouple floating combat text from any specific HUD component. A static queue accepts world-space events; the HUD's `OnUpdate` projects and fades them.

`versus/Code/CombatEvents.cs` + `uicodes/PlayerHud.razor`:

```text
shared combat-event store:
  each damage popup records world position, amount, category, and real-time age
  an add operation appends a new record with age zero

during HUD update:
  visit the current popup records
  project each world position through the scene camera into screen pixels
  offset its screen position and fade its opacity as age increases
  remove records after the presentation lifetime
```

Anti-pattern: passing a UI reference into combat code — creates circular dependencies and breaks when the HUD is rebuilt.

### G. Per-recipient outline via ghost-clone + `Rpc.FilterInclude` (despawn.murder)

To show a wallhack/radar outline only to specific players (radar buyer + dead spectators) without revealing it to others, clone the target's `SkinnedModelRenderer` into a tagged ghost and `NetworkSpawn` it with a restricted audience. The real model is untouched.

`murder/Code/Systems/EquipmentShop/Items/Radar.cs` (`RadarOutlineFactory`):

```text
to create a recipient-scoped outline:
  clone the target renderer object into a separate ghost
  tag it for cleanup and attach an outline-only presentation component
  network-spawn the ghost
  filter the broadcast audience to the buyer and eligible spectators
  tell only that audience to enable the ghost
```

Clean up by tag on radar expiry: `Scene.GetAllObjects().Where(o => o.Tags.Has("outline")).ToList().ForEach(o => o.Destroy())`.

Anti-pattern: recoloring the real player's renderer — visible to everyone.

### H. `[Rpc.Host]` purchase re-validation — price from ConVar, not the item (despawn.murder)

Never trust the client's claimed item price. Re-validate the full purchase server-side; read the price from a server ConVar so live rebalancing requires no asset rebuild.

`murder/Code/Systems/EquipmentShop/EquipmentShopManager.cs`:

```text
host RPC purchase flow(item key):
  identify the RPC caller
  reject unknown or disabled items
  resolve the caller's pawn and re-run the item's eligibility check
  read authoritative price from the server ConVar using a safe fallback
  reject insufficient currency
  debit the authoritative balance
  apply the purchase to the pawn
```

Anti-pattern: `price = item.Price` (client-authored field) — a cheater can call the RPC without paying.

---

### Updated "read these games" pointer

For weapon combat, hitscan, projectiles, melee, and combos, the most instructive codebases are:

| Game | Strength |
|---|---|
| `sandbox` / `simple-weapon-base` | Canonical base-class hierarchy, hitscan trace, physical projectile, cancellable reload |
| `sbox-grubs` | Radial explosion, ballistic arc prediction |
| `aethercore.versus` | Full melee kernel: anim-event damage windows, combo cancel windows (normalized GameResource), input buffering, `[Rpc.Owner]` damage routing, damage popups |
| `ataco.sdoomresurrection` | Penetrating hitscan (`RunAll`), frame-table weapon FSM, `IDamageable` via `GetAll` |
| `despawn.murder` | Per-recipient outlines (ghost clone + `Rpc.FilterInclude`), host re-validated shop purchases |
