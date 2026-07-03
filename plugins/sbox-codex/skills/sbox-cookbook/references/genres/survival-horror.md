# Survival-Horror Recipe

How to build a survival-horror game in modern s&box (GameObject/Component/Scene), distilled from three mined games: `goders.natural_disaster_survival` (a round-based "survive the environmental threat" party game), `mishmaps.backrooms` (a near-pure-atmosphere flickering-maze), and `treehaven.sdiver` (a networked co-op underwater extraction run in the Lethal Company mold).

## What defines the genre

Survival-horror is a **resource-pressure loop under an escalating, mostly-environmental threat in a hostile space**. The player is rarely the aggressor; they manage depleting vitals (HP, air, stamina, light) while an *external* force — a disaster wave, a pressure gradient, the dark itself, or a stalking creature — escalates until they either extract/survive or die. Three sub-shapes appear, and a real game usually fuses two of them:

- **Atmosphere-first** (`backrooms`): the threat *is* the place. Almost zero gameplay code — the shipped zip is one ~125-line `NeonFlickerLight` FSM that sells the dread (backrooms/Code/LightFlicker.cs:52). Dread comes from sound + light + level geometry, not mechanics. This is the cheapest, highest-leverage layer and you build it first.
- **Round/wave survival** (`natural_disaster_survival`): a host-authoritative state machine runs `PRE → ACTIVE → POST` rounds; an escalating, weighted-random environmental hazard (disaster waves) tries to kill everyone; survivors bank currency for persistent meta-upgrades (NDS: Code/globals/RoundManager.cs:72, Code/disasters/disaster_manager.cs:111).
- **Co-op extraction** (`sdiver`): a team descends into a foggy hostile zone, grabs loot under a vitals clock (air/pressure/stamina), and must haul it back to hit an escalating daily quota or the run ends. Vitals depletion + dynamic fog + creatures supply the horror; the quota supplies the compulsion (sdiver: Code/Gameplay/GameMode/ExpeditionMode.cs:85, Code/Player/DiverState.cs:120).

**Core loop:** `enter the space → manage depleting vitals (HP/air/stamina/light) → the threat escalates (wave / pressure / dark / creature) → survive-or-extract → bank reward → (re)descend harder`. Horror is *pacing of pressure*: the threat must outrun the player's ability to replenish.

## The system stack to compose

Build these as separate Components. References point to existing system docs.

| System | Role | Reference |
|---|---|---|
| Atmosphere FSM (light/fog/sound) | Flicker, dynamic fog, ambient dread — the cheap horror | — (below) |
| Vitals / survival meters | HP + air + stamina + light, depletion + damage-over-time | `references/systems/progression-upgrades.md` (stat seam) |
| Round / phase state machine | `[Sync]` host FSM: prep → active → summary → loop | `references/systems/round-match.md` |
| Escalating threat spawner | Weighted-random + curve-driven cadence that ramps | `references/systems/spawning-waves.md` |
| Threat actor (hazard or creature) | Physics-force disaster *or* a stalking enemy AI | `references/systems/spawning-waves.md` |
| Raycast interactor (hold-to-use) | Eye-trace + outline + hold bar for pickups/terminals | `references/systems/inventory.md` |
| Slot inventory + pickups | Host-authoritative grab with race-lock | `references/systems/inventory.md` |
| Quota / economy + carry-over | Escalating target, over-quota bonus, persistent bank | `references/systems/economy-currency.md` |
| Persistent meta-upgrades | Save/load upgrade tree spent between runs | `references/systems/save-persistence.md`, `progression-upgrades.md` |
| Dual-body life-state (spectate) | Living → ragdoll → spectator on death | — (below) |
| Reconnect / sleeping-body | Disconnect leaves a body; reconnect restores gear | `references/systems/save-persistence.md` |
| Stats / leaderboard push | Survivals, deaths, quota → backend | `references/systems/leaderboards-services.md` |

## The authority idiom that makes it work

**Host-authoritative simulation; clients are pure readers of `[Sync]` state.** Every threat tick, vitals decrement, phase transition, and spawn runs inside a single `if (!Networking.IsHost) return;` gate at the top of `OnUpdate`/`OnFixedUpdate`. Clients never simulate the threat — they read synced enums/scalars and render. This is the discipline that keeps a dozen horror systems auditable.

```csharp
protected override void OnUpdate()
{
    if ( !Networking.IsHost ) return;        // host-only simulation; clients read [Sync]
    TickRoundTimer();
    switch ( roundState )                    // [Sync] RoundState enum
    {
        case RoundState.PRE_ROUND:    if ( CurrentRoundTime <= 0 ) SetRoundState( RoundState.ACTIVE_ROUND ); break;
        case RoundState.ACTIVE_ROUND: /* all-dead early-exit + timeout → POST */ break;
    }
}
```
(NDS: Code/globals/RoundManager.cs:72-120 the gate + switch; :164 `SetRoundState` runs entry hooks. The same gate guards spawning at Code/disasters/disaster_manager.cs and the sdiver mode tick at Code/Gameplay/GameMode/GameManager.cs.) **Per-player local-only logic is the mirror image:** vitals/post-processing in sdiver run only for `Diver.Local` and proxies early-return (sdiver: Code/Player/DiverState.cs — all visual/vital code is owner-only).

## Build order

Atmosphere first (it's cheap and it *is* the genre), then vitals, then the threat, then the loop, then networking depth.

1. **Atmosphere.** Flickering light FSM + dynamic fog + ambient sound. A lit-correctly empty maze is already scary. Pure client-local visual code, no networking (backrooms/Code/LightFlicker.cs).
2. **Vitals meters.** HP + whatever else fits your fiction (air, stamina, light, sanity). Deplete per-second; apply damage-over-time when a threshold is crossed (sdiver pressure: Code/Player/DiverState.cs:120). Owner-local.
3. **Round/phase FSM.** `[Sync]` host-authoritative enum + countdown timer; entry hooks per state. The skeleton of prep → active → summary (NDS: RoundManager.cs:72; sdiver mode switchboard).
4. **The threat.** Pick a shape: a physics-force *hazard* (tornado/flood) or a stalking *creature* AI. Spawn it from the host on an **escalating** cadence (next step).
5. **Escalating spawner.** Weighted-random pick (with anti-streak pity) + a designer `Curve` evaluated against round progress → a `[Sync] TimeUntil` self-rearming clock that fires faster as the round wears on (NDS: disaster_manager.cs:111 + :404).
6. **Interactor + inventory.** Eye-trace hold-to-use interactor (outline + hold bar) and a host-authoritative slot inventory with a tag-as-mutex pickup lock (sdiver: Code/Interaction/PlayerInteractor.cs, Code/Items/PlayerToolbar.cs).
7. **Reward loop.** Quota/currency with carry-over + over-quota bonus (sdiver: ExpeditionMode.cs:85), or survive-cash + a 3D shop of persistent meta-upgrades saved to disk (NDS: DataManager.cs, ShopItem.cs).
8. **Death → spectate.** Dual-body life-state: on death spawn a networked ragdoll, swap to a free-fly spectator camera, count the death (NDS: NetworkPlayer.cs).
9. **Networking depth.** Reconnect-into-sleeping-body, stats/leaderboard push, chat.

## How the real games do each piece

### Atmosphere — a per-instance flicker FSM, no networking
The whole of Backrooms' gameplay code is one sealed Component running a 3-state machine (`Off`/`On`/`Burst`) in `OnUpdate`, timed by a `RealTimeSince stateTimer` compared against a `Game.Random`-seeded `nextAction` threshold. `Burst` is the "dying neon" effect — it rapidly toggles `Light.Enabled` for a random count before returning to a steady state. Every duration is an inspector `[Property]` min/max pair so designers retune the whole feel.

```csharp
case State.Burst:
    if ( stateTimer > nextAction ) {
        stateTimer = 0;
        Light.Enabled = !Light.Enabled;          // the dying-fluorescent flicker
        if ( ++burstCount >= burstTarget ) SwitchState( RandomState() );
        else nextAction = Game.Random.Float( BurstMinSpeed, BurstMaxSpeed );
    }
    break;
```
(backrooms/Code/LightFlicker.cs:52 OnUpdate FSM, :91 `RandomState`, :96 `SwitchState`.) **The light reference is auto-wired in three escalating fallbacks** — `[Property]` first, then `Components.Get<PointLight>()`, then `GetComponentInChildren<PointLight>()`, then `Log.Error` (backrooms/Code/LightFlicker.cs:28-45). A great default for any "effect needs a sibling component" recipe. **Gotcha:** this is pure local visual code — in multiplayer each client flickers on its own `Game.Random` sequence, so lights will NOT match across clients. Fine for ambience, wrong for anything gameplay-relevant (e.g. a light that gates a creature). For dynamic *fog* horror, sample depth/distance-indexed `Curve`s on a config `GameResource` to drive `CubemapFog` distance+tint each frame (sdiver: Code/Player/DiverState.cs — fog/light/saturation all curve-driven by depth).

### Vitals — owner-local depletion + grace-then-DoT
Survival meters deplete only for the local owner; proxies skip the whole block. The canonical pattern is *cross a threshold → start a grace timer → after grace, apply damage-over-time via `DamageInfo`*. sdiver's pressure system: depth past your `PressureRating` stat starts a warning, then after a 3s grace bleeds HP/sec.

```csharp
CurrentDepth = Math.Max( 0, (surfaceZ - Diver.WorldPosition.z) / 39.37f );   // inches→meters
bool over = CurrentDepth > currentPressureRating;
if ( over && !wasOverPressureLimit ) { wasOverPressureLimit = true; timeSinceOverLimit = 0f; Diver.Effects?.SetPressureWarning( true ); }
// ...after a grace period, apply ~10 dmg/sec via a DamageInfo on Diver.
```
(sdiver: Code/Player/DiverState.cs:120 `UpdatePressureState`; air depletes per-second below a depth threshold unless `AirBubbleCount > 0`; stamina governs sprint and feeds back into air drain.) **Gotcha:** engine distances are inches, survival gameplay is usually meters — sdiver constantly `/39.37f`; mixing the two is the easiest bug to ship. **Composable stats:** sdiver resolves each final vital as `(base + Σflat) * Πmult` from three cached layers (base, equipment tier, timed buffs), with one `OnStatsChanged` event re-reading consumers (Code/Player/DiverData.cs:48) — adopt this if upgrades/buffs modify survival caps.

### Round / phase FSM — host enum + entry hooks
A `[Sync]` enum + a single `[Sync]` countdown float, ticked only on the host. Transitions live in `SetRoundState`'s switch (load map, spawn the wave, tally survivors), NOT in a `[Change]` handler — so design the synced fields to be self-sufficient for late-joiners (they read already-synced state rather than replaying entry logic).

```csharp
[Sync] public RoundState roundState { get; set; } = RoundState.PRE_ROUND;
[Sync] public float CurrentRoundTime { get; set; } = MAX_ROUND_TIME;
// ACTIVE_ROUND early-exits to POST when every ActivePlayers entry is dead:
bool allDead = true;
foreach ( var p in ActivePlayers ) if ( p.IsValid() && p.State == NetworkPlayer.LifeState.Living ) { allDead = false; break; }
```
(NDS: Code/globals/RoundManager.cs:32-55 fields/enum, :86-120 the switch incl. all-dead exit, :164 `SetRoundState`.) **Swappable modes:** sdiver keeps this cleaner — a singleton `GameManager` owns all `[Sync]` run state but zero rules, and `Components.Create<>`'s a `BaseGameMode` subclass it forwards ticks to (`Preparation → Diving → Summary → loop/GameOver`). Adding a mode = new Component + enum case (sdiver: Code/Gameplay/GameMode/ExpeditionMode.cs:172 `AdvancePhase`). **Gotcha:** editor single-player is special-cased so a 1-player round doesn't instantly end (NDS: RoundManager.cs:107).

### The threat — physics-force hazard OR stalking creature
**Hazard shape** (tornado): a `DisasterComponent` + `Component.ITriggerListener` with a `HullCollider` catch volume. `OnTriggerEnter` adds caught bodies to a `[Sync] NetList<GameObject>`; every `OnFixedUpdate`, `SwirlObjects()` applies three `ApplyForce` vectors per Rigidbody (tangential spin, radial pull, upward lift), each scaled by `Curve.Evaluate` of distance/height and a per-tag multiplier; players also take curve-scaled HP drain.

```csharp
void Component.ITriggerListener.OnTriggerEnter( Collider other ) { /* UnlockBody + BreakJoints, add Rigidbody to affectedObjects, hand to DebrisManager */ }
// each FixedUpdate, per caught body: spin + pull + lift, force-based (never transform-set) for network-safe physics
```
(NDS: Code/disasters/TornadoComponent.cs:155 `SwirlObjects`, :218 `OnTriggerEnter`.) **Gotcha:** force magnitudes are gigantic and hand-tuned per tag (`player`/`ragdoll`/`prop`) — expect to retune; relies on a tag taxonomy defined elsewhere. **Creature shape** (sdiver): an abstract `EnemyBase : Component` driven by an `EnemyResource` data definition, with `MovingEnemy`/`PatrolEnemy`/`StaticEnemy` subclasses and an animator flag for attacks.

```csharp
public abstract class EnemyBase : Component {
    [Property] public EnemyResource Resource { get; set; }
    public virtual void OnAttackPlayer() => Renderer?.Set( "IsAttacking", true );
}
```
(sdiver: Code/Enemies/BaseEnemy.cs:6.) For a heavier creature brain (chase/missile/melee/death states), the deathmatch-arena recipe's `IEnumerator`-as-state-machine monster AI is the reusable pattern (see `references/genres/deathmatch-arena.md`). **Cap your hazards:** NDS hard-limits live disasters and funnels broken props into a `DebrisManager` that expires them (two index-aligned `NetList`s + `TimeUntil`) so physics load can't blow up (NDS: Code/globals/DebrisManager.cs).

### Escalating spawner — pity-weighted pick + curve-driven cadence
Two reusable kernels. **(1) Anti-streak pity table:** roll over a `NetDictionary<T,int>` of weights; after picking, increment *every other* option's weight by 1 and zero the chosen one — self-balancing variety with no permanent exclusion (weight 0 = manual disable).

```csharp
float roll = Game.Random.Float() * sum;  int acc = 0;
foreach ( var pair in DisasterWeights ) { if ( pair.Value == 0 ) continue; acc += pair.Value; if ( roll <= acc ) { picked = pair.Key; break; } }
foreach ( var d in availableList )                              // pity: starve nothing, repeat nothing
    DisasterWeights[d] = (d != picked) ? DisasterWeights.GetValueOrDefault(d,0) + 1 : 0;
```
(NDS: Code/disasters/disaster_manager.cs:111-160.) **(2) Curve → self-rearming clock:** feed round-progress (`1 - timeLeft/max`) into a designer `[Property] Curve`, store the result in a `[Sync] TimeUntil`, and fire+re-arm when it elapses — pacing escalates as the round wears on, tuned in the inspector without code.

```csharp
float ratio = 1f - (CurrentRoundTime / MAX_ROUND_TIME);
timeBeforeNextWave = LightningSpawnCurve.Evaluate( ratio );      // [Sync] TimeUntil
// in OnFixedUpdate: if ( !timeBeforeNextWave ) { SpawnWave(); /* re-arm above */ }
```
(NDS: disaster_manager.cs:404 `HandleLightningWave`, :411 the `TimeUntil` assignment; RoundManager.cs:302 `IntensityCurve` picks per-round intensity.) See `references/systems/spawning-waves.md`.

### Interactor + inventory — hold-to-use eye-trace + tag-as-mutex pickup
The interactor (one per local owner) raycasts from screen-center each frame `WithAnyTags("interactable","ragdoll")`, toggles a `HighlightOutline`, shows a prompt, and drives a hold progress bar; on completion `[Rpc.Broadcast]`s `OnInteract` to every `IInteractable` on the object (sdiver: Code/Interaction/PlayerInteractor.cs, Code/Interaction/InteractableObject.cs). Pickup is a host request/confirm handshake with a **tag-as-mutex** to dedupe simultaneous grabs in one tick:

```csharp
[Rpc.Host] void HostRequestPickup( Guid guid, int slot ) {
    var obj = /* find */;  if ( obj.Tags.Has( "picked_up" ) ) return;   // already claimed this tick
    obj.Tags.Add( "picked_up" );                                        // lock before end-of-frame Destroy
    obj.Destroy();  BroadcastConfirmPickup( guid, slot );               // writes item into everyone's slot dict
}
```
(sdiver: Code/Items/PlayerToolbar.cs:300.) **Gotcha:** the slot dict itself is NOT `[Sync]` — it's replicated by broadcasting every mutation RPC, so reconnecting clients must have it hand-restored (see below). `OnInteract` runs on *all* clients (`[Rpc.Broadcast]`) — terminals must re-check `Network.IsOwner`/`IsProxy` before owner/host-only work.

### Reward loop — escalating quota with carry-over, or survive-cash + meta-shop
**Extraction quota** (sdiver): daily target comes from a `RunSettingsResource.DailyQuotas` list indexed by day (past the last entry = run won), scaled by player count. Deposits split into normal vs. excess; the part above quota gets an `OverQuotaBonusMultiplier`, and excess carries into the next day.

```csharp
if ( Manager.ScoreGainedThisDay + value > Manager.DailyTargetScore ) {
    int normal = Manager.DailyTargetScore - Manager.ScoreGainedThisDay;
    int excess = value - normal;
    finalValue = normal + (int)Math.Round( excess * Manager.OverQuotaBonusMultiplier );
}
```
(sdiver: Code/Gameplay/GameMode/ExpeditionMode.cs:85.) **Gotcha:** keep two buckets — `ScoreGainedThisDay` (progress toward today) vs `TotalScore` (the spendable bank credited net-of-carryover at Summary); forgetting the carry-over subtraction double-counts. **Survive-and-upgrade** (NDS): survivors earn cash scaled by disaster intensity, spent at a 3D "look at the item to buy" shop (`ShopItem` base with virtual `GetCashCost`/`Purchase`, hovered via a trigger-only eye-trace) on **persistent** Health/Stamina/Jump levels saved with `FileSystem.Data.WriteJson` and mirrored to cloud `Services.Stats` (NDS: Code/globals/DataManager.cs, Code/ui/ShopItem.cs:58). **Gotcha:** NDS persistence is client-local with no server validation — a co-op/non-competitive economy, trivially editable; fine for survival-horror, wrong for ranked.

### Death → spectate, and reconnect into a sleeping body
A `[Sync,Change("OnStateChanged")] LifeState` enum flips which of two child GameObjects is enabled (a `PlayerController` body vs. a free-fly spectator) and which `CameraComponent.IsMainCamera` is true; on `Living → Spectator` it spawns a networked ragdoll and reports a death. `OnStart` manually re-invokes the change handler with the current value so join-in-progress clients initialize correctly (NDS: Code/player/NetworkPlayer.cs:29 `OnStateChanged`). For disconnects, don't delete a mid-run player — `TakeOwnership` of their body, mark it sleeping, and on reconnect snapshot its non-`[Sync]` inventory/equipment and replay it to a fresh body via `[Rpc.Owner]` restore calls (sdiver: Code/Managers/CustomNetworkManager.cs). See `references/systems/save-persistence.md`.

## Pitfalls (from the mined code)

- **Gate ALL threat/vitals/timer simulation behind one `if (!Networking.IsHost) return;`** (or `if (IsProxy) return;` for owner-local vitals). Clients must be pure readers of `[Sync]` state, or the threat desyncs (NDS: RoundManager.cs:79; sdiver DiverState owner-only).
- **Atmosphere FSMs are local-only and won't match across clients** — each runs its own `Game.Random`. Acceptable for flicker/fog ambience; if a light/fog *gates gameplay*, drive it from synced state instead (backrooms/Code/LightFlicker.cs gotcha).
- **State-entry side effects live in the FSM switch, not a `[Change]` handler** — design synced fields so a late-joiner reading them is correct without replaying entry logic, and have `OnStart` re-apply the current state (NDS: RoundManager.cs:164; NetworkPlayer.cs OnStart re-invoke).
- **Move threat physics with `ApplyForce`, never transform-sets** — force-based motion stays network-safe; transform-setting fights the physics sync (NDS tornado; sdiver carry spring).
- **Cap live hazards/debris** — hard-limit concurrent threats and expire broken props (`TimeUntil` GC), or physics load snowballs and tanks the frame (NDS: DebrisManager.cs).
- **Inventory/equipment dicts are NOT `[Sync]`** — they're replicated by broadcasting each mutation, so they MUST be hand-restored on reconnect via owner RPCs or returning players lose gear (sdiver: CustomNetworkManager.cs + PlayerToolbar.cs).
- **Tag-as-mutex before the end-of-frame Destroy** to dedupe simultaneous pickups; without it, N grab RPCs in one tick all succeed (sdiver: PlayerToolbar.cs:307).
- **Inches vs. meters** — engine distance is inches; survival vitals (depth/pressure) are usually meters. Convert consistently (`/39.37f`) or pressure/air math is silently wrong (sdiver: DiverState.cs:115,123).
- **Client-local JSON persistence has no server validation** — fine for co-op survival meta-upgrades, exploitable for anything competitive (NDS: DataManager.cs gotcha).
- **`Game.Random.Int(min, max)` is inclusive-max in s&box** — `Int(0,2)` yields 0/1/2; copying that into an exclusive-max RNG silently drops a state (backrooms/Code/LightFlicker.cs:93).
- **`MathF` does not exist in the bridge editor addon** (it exists in the *game* sandbox) — use `MathX`/`System.Math` for editor-side code.

## Verify live

API surfaces drift between SDK versions — confirm before relying on a signature. Use `describe_type` / `search_types` reflection against the installed SDK as authoritative for: `[Sync]`/`SyncFlags`/`[Change]`/`Networking.IsHost`/`IsProxy`, `[Rpc.Host]`/`[Rpc.Broadcast]`/`[Rpc.Owner]` (+ `Rpc.Caller`), `NetList<T>`/`NetDictionary<K,V>`, `Component.ITriggerListener` (`OnTriggerEnter`/`Exit`) and `Component.INetworkListener`, `TimeUntil`/`TimeSince`/`RealTimeSince`, `Curve`/`Curve.Evaluate`, `Rigidbody.ApplyForce`/`ApplyForceAt`/`MotionEnabled`, `HullCollider`/`BoxCollider.IsTrigger`, `Scene.Trace.Ray`/`.WithAnyTags`/`.IgnoreGameObjectHierarchy`/`.Run`, `CubemapFog`/`DirectionalLight`/`PointLight.Enabled`, `DamageInfo` (`.FromBullet`/`.WithAttacker`), `Sandbox.Services.Stats`/`Leaderboards`, `FileSystem.Data.WriteJson`/`ReadJson`, `GameResource`/`[AssetType]`/`ResourceLibrary.GetAll<T>()`, and `Scene.GetSystem<T>()`/`GameObjectSystem`.

Cross-links: see the `sbox-api` skill for authoritative type lookups, and the `sbox-build-feature` skill for the screenshot-driven build/iterate loop.

## Corpus refresh (2026): more reference implementations

Net-new techniques mined from the primary survival-horror games (`goders.natural_disaster_survival`, `mishmaps.backrooms`, `treehaven.sdiver`, `ataco.sdoomresurrection`) plus two cross-genre games (`despawn.murder`, `facepunch.ss2`) whose systems compose cleanly into horror loops. Anything already documented above is not repeated here.

### Sanity / vitals — frame-rate-independent discrete ticks

sdiver uses a `TimeSince` gate instead of `Time.Delta` multiplication for vitals like air depletion:

```csharp
// sdiver: Code/Player/DiverState.cs — flat per-second drain, FPS-independent
private TimeSince timeSinceLastAirTick;
void TickVitals()
{
    if ( !IsProxy && timeSinceLastAirTick >= 1f )
    {
        timeSinceLastAirTick = 0;
        CurrentAir -= AirDrainPerSecond;   // flat 1-unit/sec, same at 30 or 300 fps
        if ( CurrentAir <= 0 ) ApplyDamage( pressureDamagePerSec );
    }
}
```

**Why:** `Time.Delta` × rate gives floating-point drift at variable fps; a `TimeSince >= 1f` gate fires exactly once per second regardless of frame rate — sanity bars stay deterministic. Contrast to the grace-then-DoT pattern already shown: both can coexist (discrete ticks for air, continuous DoT multiplied by `Time.Delta` for pressure).

### Disaster / threat cascading — sub-disasters and meteor shards

NDS spawns secondary hazards mid-round whose odds scale *inversely* with primary intensity, delayed to mid-round:

```csharp
// NDS: Code/disasters/disaster_manager.cs — sub-disaster spawn
if ( Game.Random.Float() <= 0.1f * (4 - Intensity) )   // rarer as intensity grows
{
    await Task.DelaySeconds( Global.RoundManager.CurrentRoundTime / 2f );
    SpawnFlood( subIntensity );
}
```

Meteor shards self-spawn on impact (`Intensity >= 4f → MathX.RoundToInt(Intensity*3)` shards) and maintain a fixed-size fire pool — oldest evicted when `fireList.Count >= MaxFire` (`fireList[0].DestroyGameObject()`). **Anti-pattern:** without the cap, one high-intensity meteor wave snowballs into hundreds of fire objects and tanks frame time. Always bound cascading spawns with a hard cap, and evict by age (not by random removal) to keep the visual uniform.

### Homing hazard — orbital laser that targets nearest LOS player

NDS `LaserComponent` tracks per-object melt state in a `[Sync] NetDictionary<GameObject, LaserObj>` keyed by GameObject. Each tick it finds the nearest line-of-sight player, slerps aim toward them, and drains `LaserObj.health` while scaling `WorldScale = originalScale * health` so objects visually melt. At `health < 0.9` joints are broken and debris cleanup is scheduled; at `~0` the object deletes and a fire spawns:

```csharp
// NDS: Code/disasters/LaserComponent.cs (summary)
[Sync] public NetDictionary<GameObject, LaserObj> BurningObjects { get; set; } = new();
void AdjustAimDir()
{
    // trigger overlap → find nearest player with a clear LOS trace
    // WorldRotation = Rotation.Slerp( WorldRotation, targetRot, Time.Delta * turnSpeed );
}
```

**Reuse:** `NetDictionary<GameObject, T>` as "per-object state attached by a separate authority component" avoids putting melt data on the target itself — the hazard owns its own bookkeeping. Combine with `ApplyForce` (never transform-set) and a `[Sync] TimeUntil` cleanup gate.

### Async death with validity re-check (avoid the destroyed-during-await crash)

sdiver's death flow: broadcast death instantly, `await Task.Delay(3000)` for ragdoll spectacle, *then* switch to spectator camera — but only after re-validating:

```csharp
// sdiver: Code/Gameplay/Diver.cs — SetIncapacitate
async void SetIncapacitate()
{
    BroadcastDeath();                              // instant network effect
    await Task.Delay( 3000 );                      // let ragdoll play out
    if ( !GameObject.IsValid() || !Scene.IsValid() ) return;  // guard re-check
    SwitchToSpectatorCamera();
}
```

**Anti-pattern:** skipping the `IsValid()` re-check after `await` is the #1 crash source in async gameplay code — the GameObject can be destroyed (disconnect, scene change, round end) during the delay window. Always re-check both `GameObject.IsValid()` AND `Scene.IsValid()` before touching anything after an `await`.

### Coroutine-based monster AI (IEnumerator as per-actor state machine)

`ataco.sdoomresurrection` drives all 50+ Doom monster types with `IEnumerator`-per-state methods advanced by `MoveNext()` each animation tic — a net-new pattern not seen elsewhere in the corpus:

```csharp
// sdoomresurrection: Code/entities/monsters/Monster.cs (condensed)
IEnumerator currentState;
void SetState( IEnumerator newState ) { currentState = newState; }
void OnAnimationTick()                // called each Doom tic (~1/35s)
{
    if ( currentState != null && !currentState.MoveNext() )
        SetState( StateIdle() );
}
IEnumerator StateSee()
{
    while ( true )
    {
        NewChaseDir();
        yield return null;             // one tic per move step
    }
}
IEnumerator StateMissile()
{
    SpawnProjectile();
    yield return null; yield return null;   // brief windup ticks
    SetState( StateSee() );
}
```

**Why it works:** each "actor state" is an `IEnumerator` you swap in via `SetState()`; `MoveNext()` advances it one step per tick. Multi-step sequences (windup → fire → recover) are just `yield return null` chains with no explicit timer state. This is lighter than a full `IEnumerator`-based coroutine system (no coroutine scheduler needed) and avoids `switch`-on-enum spaghetti for per-state logic. Use it for sprite-based or simple AI enemies where the state machine has many small phases.

### Runtime procedural mesh with walkable collision (ModelBuilder pattern)

sdoomresurrection generates Doom's BSP sectors as live s&box geometry with full trace/collision — no `.vmdl` assets at all:

```csharp
// sdoomresurrection: Code/entities/DoomMap.cs (condensed)
var mesh = new Mesh();
mesh.CreateVertexBuffer( verts.Length, MeshLayout, verts );
var mb = new ModelBuilder();
mb.AddMesh( mesh );                        // render mesh
mb.AddCollisionMesh( triVerts, triIdx );   // physics hull
mb.AddTraceMesh( traceVerts, traceIdx );   // raytrace surface
go.GetOrAddComponent<ModelRenderer>().Model  = mb.Create();
go.GetOrAddComponent<ModelCollider>().Model  = mb.Create();
```

**Note:** `MeshCollider` does NOT exist in s&box — use `ModelCollider` fed a `ModelBuilder`-built model, or `HullCollider` for convex shapes. `AddCollisionMesh` + `AddTraceMesh` on the same builder gives you both physics and `Scene.Trace` in one model. This is the canonical recipe for any dungeon/cave/sector generator that must also be shootable and walkable.

### Host-migration-safe round timer re-arm

`despawn.murder`'s `RoundManager` explicitly handles the clock discontinuity when the host changes:

```csharp
// despawn.murder: Systems/Rounds/RoundManager.cs — ValidateStateAfterMigration
void ValidateStateAfterMigration()
{
    float remaining = State.TimeLeft.Relative;           // seconds left on old host's clock
    State.TimeLeft = MathX.Max( remaining, 0 );          // re-arm against new host's Time.Now
    if ( State is PostRoundState )
        TransitionTo<WaitingRoundState>( _ => { } );     // stale post-round → fresh round
}
```

`TimeUntil` stores an absolute epoch offset from the **old** host's `Time.Now`; after migration it holds the wrong epoch. The fix: read `.Relative` (remaining seconds from old context), clamp to zero, write back — this re-anchors the timer to the new host's clock. **Anti-pattern:** using `TimeUntil` directly across a host migration without re-arming will either instantly expire (negative remaining) or run far too long (large positive epoch offset). Always re-arm in `INetworkListener.OnBecameHost`.

### AI Director for adaptive spawn pacing (composed multipliers)

`despawn.murder`'s `RoundDirector` computes next-spawn time as a base interval multiplied by several independent factors. Each factor returns a float near `1.0`:

```csharp
// despawn.murder: Systems/Rounds/RoundDirector/RoundDirector.Multipliers.cs (condensed)
float GetNextSpawnTime()
{
    float t = DirectorBaseInterval;
    t *= PlayerCountMultiplier();       // more players → faster
    t *= KillInactivityMultiplier();    // nobody dying → faster
    t *= MilestoneProximityMultiplier();// someone near objective → faster
    t *= TimePressureMultiplier();      // final 40% of round → faster
    t *= DiscoveryRateMultiplier();     // found too few recently → faster
    t = MathX.Clamp( t, MinInterval, MaxInterval );
    t *= PerMapPenalty;                 // applied AFTER clamp for predictability
    return t;
}
```

**Why composed multipliers beat a single curve:** each axis is independently tunable; disabling one (`return 1f`) has no side effects. Per-map tuning goes in `PerMapPenalty` (or a `MapResource`-style asset component) applied after the clamp so map balance doesn't fight the global range. This pattern directly extends NDS's `IntensityCurve` approach and works for any survival-horror wave cadence — not just clues.

### Bad-luck protection (pity tickets) for role/threat assignment

`despawn.murder` persists pity ticket counts across sessions:

```csharp
// despawn.murder: Systems/MurdererTickets/MurdererTicketManager.cs (condensed)
Dictionary<ulong, int> tickets;   // SteamId → ticket count, loaded from FileSystem.Data JSON
ulong PickMurderer( List<ulong> candidates )
{
    float total = candidates.Sum( id => MathX.Max( 1, tickets[id] ) );
    float roll  = Game.Random.Float() * total;
    // cumulative-weight selection → winner
    foreach ( var id in candidates ) { tickets[id]--; }   // winner loses tickets
    foreach ( var id in candidates ) { if (id!=winner) tickets[id]++; }
    Save();
    return winner;
}
```

**Survival horror application:** use the same pattern to ensure the same player isn't always targeted by the disaster first or always assigned the "it" role in asymmetric horror. The `MathX.Max(1, ...)` floor means even a brand-new player has a baseline chance. Save to `FileSystem.Data` JSON for persistence across sessions — but only store SteamIds, not personal data.

### Per-recipient outline via ghost clone (wallhack for specific clients only)

`despawn.murder`'s radar item shows an outline only to the buyer and dead spectators — no global recolor:

```csharp
// despawn.murder: Radar.cs (condensed)
void CreateOutlineForTarget( Diver target )
{
    var ghost = target.GetComponent<SkinnedModelRenderer>().GameObject.Clone();
    ghost.Tags.Add( "outline_ghost" );
    ghost.GetOrAddComponent<HighlightOutline>().Color = outlineColor;
    // send ONLY to the buyer and spectators:
    using var filter = Rpc.FilterInclude( buyerConnection, spectatorConnections );
    BroadcastSpawnGhost( ghost.Id );
}
```

**Key technique:** clone the target's renderer into a tagged ghost, apply `HighlightOutline`, then use `Rpc.FilterInclude(...)` so only those specific connections receive the spawn broadcast. Use `Rpc.FilterInclude` + a ghost clone whenever "show this visual hint to only N clients" — never recolor the real object which all clients see. Time-based alpha fade + `Tags`-based cleanup on round end.

### First-round grace period using persisted round count

NDS grants a longer pre-round timer the very first time a new player joins, without a flag:

```csharp
// NDS: Code/globals/RoundManager.cs
CurrentRoundTime = PersistentData.Instance.RoundsPlayed > 0
    ? MAX_PRE_ROUND_TIME       // 15s — returning player
    : STARTING_PRE_ROUND_TIME; // 45s — first ever session
```

`RoundsPlayed` is a persistent integer incremented every POST_ROUND. This avoids "I just joined and the round started before I loaded" for new players with zero additional flag state. The same trick applies to any "first-session tutorial grace window."

### Parallel-array networking as NetDictionary workaround

NDS `DebrisManager` stores pending debris deletions as **two parallel NetLists** (objects + `TimeUntil` expiry) instead of a `NetDictionary<GameObject, TimeUntil>`, with the dict version commented out above the lists:

```csharp
// NDS: Code/globals/DebrisManager.cs
[Sync] public NetList<GameObject> DebrisDestructionListA { get; set; } = new();
[Sync] public NetList<TimeUntil>  DebrisDestructionListB { get; set; } = new();
// Former attempt: NetDictionary<GameObject,TimeUntil> DebrisDict — had wire-format issues
```

**Anti-pattern + fix:** `NetDictionary<GameObject, V>` can misbehave when the `GameObject` key is destroyed mid-frame or changes ownership — the dictionary key becomes stale before the network message arrives. Two index-aligned `NetList`s are safe because `NetList` entries are positional, not keyed. If a `NetDictionary` key type gives you ghost entries or silent drops, switch to parallel `NetList`s and manage the indices manually.

### Priority-gated transient HUD announcements

NDS `RoundUI` drops low-priority messages while a more important one is active:

```csharp
// NDS: Code/ui/RoundUI.cs — broadcastMessage is [Rpc.Broadcast(NetFlags.HostOnly)]
[Rpc.Broadcast( NetFlags.HostOnly )]
void BroadcastMessage( string msg, int priority )
{
    if ( priority < messagePriority ) return;   // silent drop
    messagePriority = priority;
    messageText     = msg;
    // 3-step tween: fade in → hold → fade out
}
```

`BuildHash() => HashCode.Combine( DisplayTime, RoundTitle, centralOpacity, messageText )` makes Razor rebuild only when any of those change. **Pattern:** assign integer priority levels (1=flavor, 5=round-end, 10=all-dead); any banner with lower priority than the one currently showing is silently dropped. This prevents "everyone is dead!" from being stomped by a simultaneous disaster text.

### SoundStream — streaming raw PCM audio positionally

`ataco.sdoomresurrection` decodes DMX sound lumps to `short[]` and plays them via `SoundStream`:

```csharp
// sdoomresurrection: Code/doomwad/SoundLoader.cs (condensed)
short[] samples = DecodeDmxToSigned16( lumpBytes );       // unsigned 8-bit → signed 16-bit
var stream = new SoundStream( sampleRate, channels: 1 );
stream.WriteData( samples );
var snd = stream.Play();
snd.Position = worldPosition;                              // positional 3D audio
// schedule disposal: await Task.Delay((int)(samples.Length / sampleRate * 1000));
```

**Why it matters for survival-horror:** you can generate or decode *any* audio format (procedural groans, decoded monster sounds, custom synthesizer output) and play it positionally without a `.vsnd` asset. The `SoundStream` push pattern is the only way to play raw PCM in s&box. Note: dispose the `SoundStream` after the clip finishes — use `Task.Delay` scheduled from the caller (or `TimeSince` polling) since there is no completion callback.

### Tween library as GameObjectSystem (reusable juice layer)

NDS ships `Braxnet.TweenManager` — a ~340-line `GameObjectSystem` with a sequential+parallel chaining API:

```csharp
// NDS: Code/globals/TweenManager.cs — usage example
var tween = TweenManager.CreateTween();
tween.AddFloat( t => myPanel.Opacity = t, from: 0f, to: 1f, duration: 0.3f )
     .AddPosition( go, to: targetPos, duration: 0.5f ).Parallel
     .SetEasing( Sandbox.Utility.Easing.EaseOut );
await tween.Wait();   // TaskCompletionSource-backed, auto-skips if !GameObject.IsValid()
```

`.Parallel` marks the next step as concurrent with the previous one (they start at the same time); without it steps run in sequence. `await tween.Wait()` blocks the calling async method. Auto-skips invalid objects so a round-end destroy doesn't throw. **Drop this in instead of per-effect lerp loops** — use it for HUD fades, damage flash, disaster despawn dissolve, "you survived" slide-in.

### Per-map disaster tuning via a scene component

NDS `MapComponent` is a `[Property]`-only component attached to each map's root object:

```csharp
// NDS: Code/map/MapComponent.cs
public class MapComponent : Component
{
    [Property] public float StartingFloodHeight { get; set; }
    [Property] public float FinalFloodHeight    { get; set; }
    [Property] public float MinVolcanoAngle     { get; set; }
    [Property] public float MaxVolcanoAngle     { get; set; }
}
```

`MapManager` reads it on scene load and pushes values into `DisasterManager`. **Pattern:** put all per-map balance knobs (spawn arcs, flood range, intensity multiplier) on a single component in the scene, not in code or in a separate asset file. `MapComponent` has zero methods — it is pure data. This is the cheapest way to make each map feel hand-tuned without branching on map identity in the disaster code.

---

### Read these games

Primary survival-horror corpus: `goders.natural_disaster_survival` (round/wave/disaster director, NDS anti-streak picker, TweenManager), `mishmaps.backrooms` (atmosphere FSM, RealTimeSince cosmetic timing), `treehaven.sdiver` (co-op extraction, vitals discrete ticks, reconnect-sleeping-body, mode-as-strategy), `ataco.sdoomresurrection` (IEnumerator monster AI, ModelBuilder collision mesh, SoundStream PCM, billboard SceneCustomObject).

Cross-genre techniques that compose into horror: `despawn.murder` (AI Director multipliers, pity tickets, host-migration timer re-arm, per-recipient ghost outlines), `facepunch.ss2` (per-source stat modifier stack, reflection-driven upgrade catalog).
