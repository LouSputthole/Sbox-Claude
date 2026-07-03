# AI Director / Adaptive Pacing
A Left-4-Dead-style "Director": the next spawn time is a **base interval × the product of N independent factor multipliers** (player-count, inactivity, milestone-proximity, time-pressure, discovery-rate…), clamped to a window, then scaled by a per-map penalty — each factor individually ConVar-gated so a server owner can tune or disable it live.

## What it IS / when you need it
A wave/spawn director answers "*when does the next thing appear?*" — but instead of a fixed timer or a single difficulty curve, it reads **live telemetry** and modulates pacing to keep tension where you want it. The defining structure is composable and genre-agnostic:

1. **A base interval** — the neutral cadence (seconds between spawns) when everything is "average".
2. **N factor multipliers** — each is an independent method returning a float around `1.0`. `< 1.0` speeds up (shorter interval), `> 1.0` slows down. They **multiply together**, so any one can dominate and they compose without coupling.
3. **A clamp** — `[min, max]` so no pathological combination starves or floods the spawn.
4. **A per-context penalty** — applied *after* the clamp (per-map, per-difficulty) for predictable per-arena tuning.
5. **Per-factor gates** — every factor is ConVar/`[Property]`-toggleable so it can be tuned, A/B'd, or disabled without a rebuild.

Use it for L4D-style horde pacing, survival/disaster intensity (Natural Disaster Survival), bullet-heaven enemy cadence (SS1), clue/loot drip in a deduction game (despawn.murder), restaurant rush-hour, or any "pull the player across the map / ramp the pressure" loop. This is the **adaptive** cousin of `spawning-waves` (fixed/scripted bursts) and `round-match` (phase timing); read those for the host-authority and cadence-gate primitives this builds on. For the *what-spawns* roll, see `gacha-loot`.

Golden rule: **the host owns the director loop; clients only read replicated results.** Every tick gates with `if (!Networking.IsHost) return;` and spawned objects are `NetworkSpawn()`'d once (same as `spawning-waves`). The director is pure logic — the *content* (prefabs, weights, curves, map knobs) lives in `[Property]` refs and `GameResource` assets.

## The system stack to compose
- **A host-only director component** (singleton-ish: one per scene) holding the base interval + a self-rearming `TimeUntil` gate.
- **One method per factor**, each reading live state and returning a multiplier near `1.0`.
- **A `GetNextSpawnTime()` aggregator** that multiplies enabled factors, clamps, applies the map penalty.
- **A weighted/anti-clustering placement picker** (`spawning-waves` step 2/4 + the anti-repeat queue below).
- **A ConVar/`[Property]` per factor** (gate + tuning constants) — pairs with `economy-currency`'s "balance DSL as ConVars" idea.
- *(optional)* a **map-analysis pass** at load that auto-sizes the arena and feeds a penalty.

### Build order
1. **Get a fixed-interval host spawner working first** (copy `spawning-waves` step 1+4: `TimeUntil` gate + clone→configure→`NetworkSpawn`). Verify it spawns on a flat cadence in play mode before adding any cleverness.
2. **Add ONE factor** (player-count is the easiest — `lerp(interval, faster, byPlayerCount)`) and confirm the interval visibly changes. Resist adding more until one works end-to-end.
3. **Wrap the interval in `GetNextSpawnTime()`**: `result = base; foreach enabled factor: result *= factor(); result = result.Clamp(min,max);`. Log each factor's value while tuning.
4. **Add the remaining factors one at a time**, each behind its own gate, each tested in isolation (disable the others). A factor that "ramps after a threshold" should `lerp` from `1.0`, not snap.
5. **Apply the per-map penalty AFTER the clamp** so the clamp window stays meaningful per arena.
6. **Replace random placement with anti-clustering weighted placement** (exclude the last N points; distance-weight away from players near a milestone) so the director feels intentional, not random.
7. **Expose every constant as a ConVar/`[Property]`** and tune live in a real session — the curve only feels right with players in it.

## The canonical aggregator (copy this shape)
Each factor is a small method around `1.0`; they multiply; the result is clamped, then penalized.

```csharp
public sealed class SpawnDirector : Component   // host-authoritative, one per scene
{
    [Property] public float BaseInterval { get; set; } = 8f;
    [Property] public float MinInterval  { get; set; } = 2f;
    [Property] public float MaxInterval  { get; set; } = 20f;
    [Property] public float MapPenalty   { get; set; } = 1f;   // per-arena, set at load

    private TimeUntil _next;

    protected override void OnFixedUpdate()
    {
        if ( !Networking.IsHost ) return;        // host owns the loop
        if ( _next ) return;                     // TimeUntil bool: true once elapsed
        SpawnOne();                              // clone→configure→NetworkSpawn (see spawning-waves)
        _next = GetNextSpawnTime();              // RE-ARM every fire, or it spawns once
    }

    float GetNextSpawnTime()
    {
        float t = BaseInterval;
        t *= PlayerCountFactor();   // more players -> < 1 (faster)
        t *= InactivityFactor();    // nothing happening -> < 1 (poke them)
        t *= ProximityFactor();     // someone near the goal -> < 1
        t *= TimePressureFactor();  // final stretch of round -> < 1
        t *= DiscoveryRateFactor(); // too slow -> <1, too fast -> >1 (homeostasis)
        t = t.Clamp( MinInterval, MaxInterval );
        return t * MapPenalty;      // penalty AFTER clamp, for predictable per-map feel
    }
}
```

This is the structure verified in **despawn.murder** (`Systems/Rounds/RoundDirector/`, a partial class across 6 files): factor methods in `RoundDirector.Multipliers.cs`, the aggregate in `RoundDirector.Spawning.cs::GetNextSpawnTime`. Each factor returns ~`1.0`, all multiply, the result is clamped to a ConVar window, then a per-map penalty is applied **after** the clamp. Every factor is individually ConVar-gated (`GameConVars.cs`). Their six real factors:

- **Player-count** — more players → faster (a busy lobby needs more to do).
- **Kill-inactivity** — ramps *after* a threshold of no kills, `lerp`ed in so it eases rather than snaps.
- **Milestone-proximity** — someone close to earning the revolver → faster (push the climax).
- **Time-pressure** — in the final ~40% of the round → faster.
- **Discovery-rate (homeostasis)** — tracks recent clue lifetimes; spawning too slowly → speed up, too fast → slow down. This is the self-correcting factor that keeps the world feeling "right" regardless of the others.
- **Milestone-cadence** — average time-to-milestone vs a target (e.g. 120s); behind target → faster.

### A factor method, concretely
A "ramp after a threshold" factor should lerp from `1.0`, not branch hard:

```csharp
// despawn.murder shape: nothing's happened for a while -> shorten the interval toward 0.5x
float InactivityFactor()
{
    if ( !ConVarEnabled( "director.inactivity" ) ) return 1f;
    float idle = TimeSinceLastKill;                       // RealTimeSince
    if ( idle < InactivityThreshold ) return 1f;          // below threshold: no effect
    float over = (idle - InactivityThreshold).Clamp( 0f, InactivityWindow );
    return MathX.Lerp( 1f, 0.5f, over / InactivityWindow ); // ease toward 2x faster
}
```

## How the real games do it (lift pieces from each)

### despawn.murder — the flagship multi-factor Director
`Systems/Rounds/RoundDirector/` (partial class, 6 files). Six independent factors → multiply → clamp → per-map penalty, every factor ConVar-gated. The clearest **adaptive-difficulty engine** in the corpus.
- **Aggregate**: `RoundDirector.Spawning.cs::GetNextSpawnTime`. **Factors**: `RoundDirector.Multipliers.cs`. **Gates/tuning**: `Systems/Game/GameConVars.cs`.
- **Per-map knobs as data**: `MapResource` (`.mapvote`, `Systems/MapVote/MapResource.cs`) carries `ClueSpawnMultiplier`/`ClueSpawnMultiplierMax`, **lerped by lobby size** — so the per-map penalty itself scales with player count. The director reads the resource for the current `scene.Source` at load.
- **Map-analysis pass**: `RoundDirector.MapAnalysis.cs` auto-classifies map size at load (samples avg distance between `LootSpawnPoint`s, blends 70% avg-distance + 30% bounding-box, normalizes by spawn-point density via `sqrt`, buckets Small/Medium/Large/Very-Large) and auto-adjusts spacing. Lift this when you want the director to self-tune to an unknown map.

### despawn.murder — anti-clustering weighted placement
`RoundDirector.Spawning.cs` + `Components/LootSpawnPoint.cs`. The *where* half of the director, and the part that makes it feel intentional rather than random:
- Weighted-random over designer-placed `LootSpawnPoint.SpawnWeight`.
- **Excludes the last ~2 chosen positions** (a small anti-repeat queue) so spawns don't pile in one spot.
- Skips occupied points (`CheckOccupancy()`).
- **Switches to distance-weighted scoring when a player nears the milestone** — prefers points *far* from those players, pulling them across the map for the climax.

```csharp
// anti-repeat queue + occupancy skip, then weighted pick (despawn.murder shape)
var candidates = AllSpawnPoints
    .Where( p => !_recent.Contains( p ) && !p.CheckOccupancy() )
    .ToList();
// near the milestone, bias AWAY from the close player instead of pure weight:
float WeightOf( LootSpawnPoint p ) => nearMilestone
    ? p.WorldPosition.Distance( milestonePlayer.WorldPosition )   // farther = heavier
    : p.SpawnWeight;
var chosen = WeightedPick( candidates, WeightOf );                // see spawning-waves / gacha-loot
_recent.Enqueue( chosen ); if ( _recent.Count > 2 ) _recent.Dequeue();
```

`LootSpawnPoint` (`Components/LootSpawnPoint.cs`) is the canonical "designer marks spawn nodes, system picks among them" asset — carries `SpawnWeight`, optional custom `Prefab`/`Model`, and `CheckOccupancy()`.

### facepunch.ss1 — multiplicative time-ramps × live population (no Director class needed)
`ss1/Code/Manager.cs::HandleEnemySpawn` (`:360`). The lightweight version of the same idea: instead of named factor methods, multiply several `Utils.Map(...)` ramps inline — and crucially scale by the **live enemy count** so a crowded arena throttles itself automatically:
```csharp
var spawnTime = Utils.Map( EnemyCount, 0, MAX_ENEMY_COUNT, 0.05f, 0.3f, EasingType.QuadOut ) // crowd-aware
              * Utils.Map( t, 0f, 80f,  1.5f, 1f )   // ramp up over first 80s
              * Utils.Map( t, 0f, 800f, 1.2f, 1f );  // long-tail ramp
if ( _timeSinceEnemySpawn > spawnTime ) { SpawnRandomEnemy(); _timeSinceEnemySpawn = 0; }
```
Lift this when you want adaptive cadence but don't need per-factor ConVars yet — the "live population is a multiplier" trick is the single most reusable factor. SS1 also caps hard (`MAX_ENEMY_COUNT = 350`), does a **probabilistic cull** for cosmetic spawns past a soft cap (`Manager.cs:1221`), and tracks **spillover debt** so value that couldn't spawn under the cap folds into the next spawn (`:839`).

### goders.natural_disaster_survival — intensity-curve director + inverse-scaling sub-events
`Code/disasters/disaster_manager.cs`. Curve-driven cadence keyed on round progress, with two director ideas worth stealing:
- **Cadence via a `[Property] Curve`**: `timeBeforeNextWave = LightningSpawnCurve.Evaluate( GetTimeRatio() )` into a `[Sync] TimeUntil` (`:404`). Designers reshape pacing in the inspector — no code. (This is the "single curve" baseline the multi-factor director generalizes.)
- **Sub-event odds that scale *inversely* with intensity**: `if ( randSubDisasterRoll <= .1f * (4 - Intensity) )` — rarer extras at high intensity so the screen doesn't saturate. A clean "as the main pressure rises, dial back the noise" factor.
- **Cascade-on-impact**: a high-intensity meteor spawns `Math.Round(Intensity*3)` shards on hit — the director's *intensity* becomes a spawn-count multiplier downstream, not just a cadence one.

### ataco.sdoomresurrection — continuous per-second probability director (the if-ladder variant)
`Code/.../Manager.cs::HandleEnemySpawn` (`:360`) + `SpawnRandomEnemy` (`:401`). No wave table at all: a continuous director rolls **escalating per-second probabilities keyed on run time `t` and difficulty**, curve-driven and crowd-aware. This is the design fork worth knowing — *summed-weight pick* (gacha-loot) vs *if-ladder of independent per-second rolls*: the ladder lets each enemy type ramp on its own schedule. Difficulty here is a cloud Stat carried across the menu→game scene load via a `DontDestroyOnLoad` config object, then consumed once.

## Gotchas
- **Re-arm the `TimeUntil` every fire**, or the director spawns exactly once. `if (_next) return;` means "not elapsed yet"; assign `_next = GetNextSpawnTime()` right after spawning.
- **Factors multiply — order doesn't matter, but magnitudes do.** Five factors each at `0.5` is `0.03×` (a flood); each at `1.5` is `7.6×` (a drought). The **clamp is load-bearing**, not optional — without `[min,max]` a bad combination softlocks pacing. Pick per-factor ranges deliberately (most should sit in `0.7–1.4`).
- **Apply the per-map/difficulty penalty AFTER the clamp** (despawn.murder does this explicitly) — clamping first then penalizing keeps the `[min,max]` window meaningful and the penalty predictable per arena.
- **Ramp factors should `lerp` from `1.0`, not snap.** A hard `if (idle > threshold) return 0.5f;` produces a visible cadence "pop"; lerp over a window instead.
- **Homeostasis factors need a measured signal, not a guess.** despawn.murder's discovery-rate factor tracks *actual recent clue lifetimes*; a self-correcting factor that reads a made-up number just fights the others. Measure the thing you're regulating.
- **Gate every factor.** Bugs in adaptive systems are hard to spot live. A per-factor ConVar/`[Property]` toggle lets you binary-search which factor is misbehaving by disabling them one at a time — and lets server owners tune without a build (despawn.murder ships ~60 ConVars for exactly this).
- **Host-only, and survive migration.** The whole loop is `if (!Networking.IsHost) return;`. A promoted host has `null` director state and a stale `[Sync] TimeUntil` (stored off the *old* host's clock) — re-arm `_next` against the new host's clock on becoming host, and rebuild any live-object lists by scanning the scene, not trusting a local list (see `spawning-waves` + `round-match` host-migration notes).
- **Anti-clustering needs the exclusion queue _and_ the occupancy check.** The "last N positions" queue stops *immediate* repeats; `CheckOccupancy()` stops spawning on top of a player. You want both — neither alone reads as "intentional placement".
- **Don't `[Sync]` the factor math.** Compute everything host-side; clients only see the `NetworkSpawn`'d results. Replicating intermediate multipliers is wasted bandwidth and a desync surface.
- **`Curve` cadence is incomplete without the asset** — the C# (`ratio → Curve.Evaluate → TimeUntil`) is portable, but the actual feel lives in the inspector-authored curve on the prefab/scene.

## Which games to read
- **despawn.murder** — `Systems/Rounds/RoundDirector/` (the 6-file partial Director): `RoundDirector.Spawning.cs` (aggregate `GetNextSpawnTime` + anti-clustering placement), `RoundDirector.Multipliers.cs` (the six factors), `RoundDirector.MapAnalysis.cs` (auto map-sizing). Plus `Systems/Game/GameConVars.cs` (per-factor gates), `Systems/MapVote/MapResource.cs` (`.mapvote` per-map `ClueSpawnMultiplier` lerped by lobby size), `Components/LootSpawnPoint.cs` (weighted spawn nodes). **The flagship — read this first.**
- **facepunch.ss1** — `ss1/Code/Manager.cs` (multiplicative time-ramps × live count `:360`; hard cap + probabilistic cull `:1221`; spillover debt `:839`). The no-Director-class lightweight version.
- **goders.natural_disaster_survival** — `Code/disasters/disaster_manager.cs` (single-curve cadence `:404`, inverse-intensity sub-events, cascade-on-impact); `Code/globals/RoundManager.cs` (intensity curve + round FSM). The intensity-driven baseline.
- **ataco.sdoomresurrection** — `Code/.../Manager.cs::HandleEnemySpawn` `:360` / `SpawnRandomEnemy` `:401` (continuous per-second-probability if-ladder director + per-difficulty cloud-Stat config). The if-ladder design fork.
- **Cross-reference**: `spawning-waves.md` (the fixed-cadence gate + clone→configure→`NetworkSpawn` + population cap this builds on), `gacha-loot.md` (the *what-spawns* weighted roll + pity), `round-match.md` (round phases that feed the time-pressure factor + host-migration timer).

---
Verify live: the SDK is the source of truth — `describe_type TimeUntil` / `describe_type Curve` / `search_types EasingType`, `describe_type GameObject` for `Clone`/`NetworkSpawn`/`NetworkMode`, and confirm `MathX.Lerp`/`MathX.Clamp` (NOT `System.MathF` — absent in the sandbox) before relying on any signature above. See also the **spawning-waves** and **gacha-loot** references, the **sbox-api** skill (reflection lookups), and the **sbox-build-feature** skill (screenshot-driven iteration once the director is wired).
