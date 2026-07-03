# Spawning Waves
Host-authoritative system that releases enemies/hazards/pickups in escalating timed bursts, capped by a live population count and rolled from a weighted table.

## What it IS / when you need it
A "wave spawner" is three small pieces that almost always ship together:
1. **A cadence gate** — *when* does the next thing spawn? (a self-rearming timer, usually escalating).
2. **A selection roll** — *what* spawns? (a weighted random table, often with anti-streak "pity").
3. **A population cap** — *how many at once?* (refuse/throttle the spawn when too many are live).

Use it for survival/horde games (Natural Disaster Survival, bullet-heaven SS1), per-round hazard directors (Blind), or any "shop customers / debris arrive on a curve" loop. The pattern is identical whether the "wave" is zombies, meteors, or restaurant patrons — only the prefab and the curve differ.

Golden rule across all 13 examples: **the host owns the spawn loop; clients only read replicated results.** Every spawner gates its tick with `if (!Networking.IsHost) return;` and the spawned object is `NetworkSpawn()`'d once so it replicates to everyone (goders.natural_disaster_survival: `disaster_manager.cs:103` OnStart `if(IsProxy)return`; suburbianites.blindloaded: `HazardManager.cs:298`).

## Canonical modern recipe

### 1. The cadence gate — `TimeUntil` self-rearming, curve-escalated
`TimeUntil` is the idiomatic s&box spawn timer: assign a float (seconds-from-now), poll the bool. When it has elapsed, fire and **re-assign** it — this is the whole loop.

```csharp
using Sandbox;

public sealed class WaveSpawner : Component
{
	[Property] public GameObject EnemyPrefab { get; set; }
	[Property] public Curve SpawnIntervalCurve { get; set; } // editor-authored: progress -> seconds
	[Property] public float RoundLength { get; set; } = 120f;

	private TimeUntil _nextSpawn;        // self-rearming gate
	private RealTimeSince _roundStart;

	protected override void OnStart()
	{
		_roundStart = 0;
		_nextSpawn = 0; // fire immediately on first tick
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost ) return;          // host-only loop
		if ( _nextSpawn ) return;                  // TimeUntil bool: true once elapsed

		SpawnOne();

		// escalate: as the round progresses, the curve returns a shorter interval
		float progress = ((float)_roundStart / RoundLength).Clamp( 0f, 1f );
		_nextSpawn = SpawnIntervalCurve.Evaluate( progress );
	}
}
```

This is exactly goders.natural_disaster_survival's wave loop: `GetTimeRatio()` (1 − timeLeft/maxTime) feeds `LightningSpawnCurve.Evaluate(ratio)` which is assigned to a `[Sync] TimeUntil timeBeforeNextWave`, and `OnFixedUpdate` fires the next wave only once `!timeBeforeNextWave` (`disaster_manager.cs:404`, `:411`). The curve is tuned in the inspector, so designers reshape difficulty with no code.

**Variant cadence (multiplicative time-curves instead of one Curve asset)** — SS1 computes its interval inline from several `Utils.Map(...)` ramps multiplied together, scaled by the *live* enemy count so a crowded arena slows spawns automatically (facepunch.ss1: `Manager.cs:360` `HandleEnemySpawn`):
```csharp
var spawnTime = Utils.Map( EnemyCount, 0, MAX_ENEMY_COUNT, 0.05f, 0.3f, EasingType.QuadOut )
              * Utils.Map( t, 0f, 80f, 1.5f, 1f )       // ramp up over first 80s
              * Utils.Map( t, 0f, 800f, 1.2f, 1f );      // long-tail ramp
if ( _timeSinceEnemySpawn > spawnTime ) { SpawnRandomEnemy(); _timeSinceEnemySpawn = 0; }
```

### 2. The selection roll — weighted table with anti-streak "pity"
Textbook weighted pick: sum weights, `roll = Random.Float()*sum`, walk the cumulative total. The reusable twist is **after picking, bump every loser's weight and reset the winner to 0**, so recently-spawned types get rarer and nothing starves (goders.natural_disaster_survival: `disaster_manager.cs:111`).

```csharp
Disaster Roll()
{
	int sum = 0;
	foreach ( var pair in Weights ) sum += pair.Value;

	float roll = Game.Random.Float() * sum;
	int acc = 0;
	Disaster picked = default;
	foreach ( var pair in Weights )
	{
		if ( pair.Value == 0 ) continue;          // weight 0 = permanently disabled
		acc += pair.Value;
		if ( roll <= acc ) { picked = pair.Key; break; }
	}

	foreach ( var d in AllDisasters )             // pity update
		Weights[d] = (d == picked) ? 0 : Weights.GetValueOrDefault( d, 0 ) + 1;
	return picked;
}
```
The same cumulative-weight roller appears engine-free in artisan.darkrpog (`LootboxRoller.cs:8`) and with per-entry spawn caps (`SpawnsLeft--`, RemoveAll when 0) in treehaven.sdiver (`LootRoller.cs:33`) — extract it as a pure helper; pass a seeded `Random` for replayable rolls.

### 3. The population cap — refuse/throttle when full
Count what's live and bail (or down-scale) before spawning. SS1 hard-caps `MAX_ENEMY_COUNT = 350`; cosmetic effects use a *probabilistic* cull — once a list passes a soft cap, increasing chance to skip the spawn (facepunch.ss1: `Manager.cs:1221`). enifun.shop_manager scales the concurrent customer cap off stocked-shelf count and player level via `[Property]` curves (`CustomerSpawner.cs:238`).

```csharp
if ( _live.Count >= MaxConcurrent ) return;             // hard cap
// or soft cap: if ( Utils.Map(_live.Count, soft, hard, 0f, 1f) > Game.Random.Float() ) return;
```
SS1 also tracks **spillover debt** — value (e.g. coins) that couldn't spawn because of the cap is folded into the next spawn instead of lost (`Manager.cs:839`).

### 4. The spawn itself — clone, configure, then `NetworkSpawn`
```csharp
void SpawnOne()
{
	var go = EnemyPrefab.Clone( PickSpawnPoint() );
	go.NetworkMode = NetworkMode.Object;
	var e = go.GetOrAddComponent<Enemy>();
	e.Configure( /* stats rolled this wave */ );   // configure BEFORE NetworkSpawn
	go.NetworkSpawn();                             // host-only; replicates to all clients
	_live.Add( go );
}
```
**Order is load-bearing**: configure the component the same frame you clone, *before* `NetworkSpawn()`, or proxies receive an unconfigured object (yellowletter.terrys_crash_course: `TurretComponent.cs:89` — "Configure must be called the same frame as Clone before NetworkSpawn"). To survive host migration, set `go.Network.SetOrphanedMode( NetworkOrphaned.Host )` (playbtg.elevator: `ExperienceManager.cs:235`).

## Notable variations across games
- **Per-round burst director** (no continuous timer): roll N hazard *slots* into `[Sync(FromHost)]` ints during prep (so the HUD can show a slot-machine preview), then `ClearAll()` + spawn the cloned/NetworkSpawned prefabs in `OnRoundStarted` (suburbianites.blindloaded: `HazardManager.cs:296`). Duplicate slots are **coalesced** (two Meteor slots = one volley) or they stack into an unfair burst.
- **State-machine driven**: a round FSM (`PRE_ROUND → ACTIVE_ROUND → POST_ROUND`) seeds each phase's duration into a `[Sync] CurrentRoundTime` and spawns the wave in the state-entry hook (goders `RoundManager.cs:164`; apl.sandboxwars `MiniGameManager.cs:460`).
- **Lazy / virtual spawning**: don't spawn the whole wave up front — precompute a deterministic map of *where* things will spawn, and only `Clone()+NetworkSpawn()` when the player gets near (master.digging_simulator ore reveal: `OreGenerator.cs:95`). Huge perf win for large fields.
- **Deck-draw (no-repeat) selection**: instead of weights, `RemoveAt()` from a shuffled list so a type never repeats until the deck is exhausted (luckygaming.doner_kiosk: `GameManager.cs:176`).
- **Spawn-rate as a global effect knob**: route the interval through a central `EffectsManager.Get(EffectType.SpawnRate)` so upgrades change cadence without touching the spawner (GASTROTOWN `EffectsManager.cs:30`).
- **Lifecycle cap with expiry** ("pool-lite"): register each spawned object with a `TimeUntil(LIFETIME)`; a host-only `OnTick` deletes expired ones and force-deletes the oldest when over `MAX` (goders `DebrisManager.cs:32`). Note it destroys, it does not truly re-pool.
- **Walkable-town pedestrian NPC** (the "spawned thing wanders the world" variant): a kinematic, host-authoritative crowd member that routes on a **sidewalk-grid graph** through an FSM (`Wandering → Following → Heading → Queuing → Leaving`), turning only at junctions and crossing only at junctions. The reusable mechanics: **boid separation** (`Separation()` sums a repulsion push from every nearby peer, read off the per-client `_all` registry — see networking pattern 20), a **stuck detector** (`TimeSince _sinceProgress`; if barely moved while it *should* be walking, re-route to a fresh `RandomSidewalkPoint()`), and **queue claim/poach with a grace lock** so rival vendors can steal a crowd but no more than the cap ever converge on one stop (`stop.Claim(this)` reserves a slot up front). The body is `CitizenAnimationHelper`-driven from movement velocity and `NetworkMode.Never` (cosmetic, recomputed locally). Verified against freddo.scoops `Code/CustomerNpc.cs` (FSM `:264`, `Separation()` `:571`, stuck-reroute `:234-252`, `Claim`/`Unclaim` `:327-347`, `CitizenAnimationHelper` from velocity `:105,:186`) + the sidewalk graph `Code/CityGrid.cs` (`NearestNode`/`RandomNeighbour`/`SidewalkPoint`/`ClampToStreets`). Use it for graveyard visitors, town crowds, shop walk-ins — anything that *populates* a world rather than attacking a player.

## Gotchas
- **`NetworkSpawn` is host-only.** A client calling it does nothing useful; route client requests through `[Rpc.Host]`. Editor-placed manager objects are NOT networked by default — their `[Sync]` fields silently won't replicate until the host calls `GameObject.NetworkSpawn()` once in OnStart (`if(Networking.IsHost && !Network.Active) GameObject.NetworkSpawn()`) — Blind `GameManager.cs` OnStart.
- **Configure before NetworkSpawn** (see recipe step 4) or proxies get a blank object for a frame.
- **`TimeUntil` must be re-armed every fire** or it only spawns once. Reading `if (myTimeUntil) return;` means "not elapsed yet"; the bool flips true *after* the deadline.
- **`static` + `[Sync]` weight tables are a footgun** — they work only because there's exactly one manager instance (goders `DisasterWeights`). Prefer an instance `NetDictionary`/synced field; duplicating the manager corrupts the shared state.
- **Population lists leak** if a spawned object is destroyed without being removed from your live list — every count read should prune invalid entries (`_live.RemoveAll(go => !go.IsValid())`) like apl.sandboxwars `LimitsSystem.cs`.
- **Host migration**: a promoted host has all host-only spawn arrays `null` and stale `[Sync]` state. Rebuild by scanning the scene (`Scene.GetAllComponents<Panel>()`) and destroying orphans *before* respawning, not by trusting your local list (Blind `ArenaManager.cs` RebuildGridFromScratch). Spawned objects need `SetOrphanedMode(NetworkOrphaned.Host)` to transfer instead of vanish.
- **Coalesce duplicate rolls** in slot-based directors or two identical picks double up unfairly (Blind `HazardManager.cs:333`).
- **`Curve`/time-curve cadence is incomplete without the asset** — the C# (`ratio → Curve.Evaluate → TimeUntil`) is portable, but the actual difficulty lives in the inspector-authored `.prefab`/`.scene` data.

## Seen in
- **goders.natural_disaster_survival** — `Code/disasters/disaster_manager.cs` (curve cadence `:404`, weighted+pity table `:111`), `Code/globals/RoundManager.cs` (round FSM `:164`), `Code/globals/DebrisManager.cs` (cap+expiry)
- **facepunch.ss1** — `ss1/Code/Manager.cs` (time-curve interval `:360`, prefab-clone factory + caps `:770`, spillover debt `:839`, probabilistic VFX cull `:1221`)
- **suburbianites.blindloaded** (Blind) — `Code/Hazards/HazardManager.cs` (per-round slot director `:296`), `Code/Arena/ArenaManager.cs` (per-round panel grid + host-migration rebuild)
- **enifun.shop_manager** — `Code/AI/CustomerSpawner.cs` (scaling caps + daily rush-hour event `:156`/`:238`)
- **emg.everything_must_go** — `Code/Shop/Shop.cs` (host-only customer spawn + day events)
- **playbtg.elevator** — `Code/Experiences/ExperienceManager.cs` (round-rotation prefab spawn + orphaned-mode `:174`/`:235`); `KingOfTheHillController.cs:143` (timed checkpoint spawn)
- **apl.sandboxwars** — `Code/MiniGameManager.cs` (phase-machine spawn `:460`), `Code/GameLoop/LimitsSystem.cs` (per-player population quota)
- **master.digging_simulator** — `OreGenerator.cs:95` (lazy/virtual spawning)
- **artisan.darkrpog** — `Code/Lootboxes/LootboxRoller.cs:8` (pure weighted roller) · **treehaven.sdiver** — `Code/Items/Treasure/LootRoller.cs:33` (weighted + per-entry caps)
- **luckygaming.doner_kiosk** — `Code/Game/GameManager.cs:176` (deck-draw no-repeat) · **GASTROTOWN** — `EffectsManager.cs:30` (spawn-rate via central effects)
- **yellowletter.terrys_crash_course** — `TurretComponent.cs:89` (clone→Configure→NetworkSpawn order)
- **freddo.scoops** — `Code/CustomerNpc.cs` (sidewalk-grid pedestrian FSM + boids + stuck-recovery + queue claim/poach), `Code/CityGrid.cs` (town topology graph) · **klavs.basebuilder** — `Code/Npcs/` (HL2 schedule/task NPC brain: `Npc.Schedule.cs`, `ScheduleBase`/`TaskBase`, layered senses/nav/speech)

---
Verify live: the SDK is the source of truth — `describe_type TimeUntil` / `describe_type Curve` / `search_types NetworkOrphaned`, and `describe_type GameObject` for `Clone`/`NetworkSpawn`/`NetworkMode` before relying on any signature above. See also the **sbox-api** skill (reflection lookups) and **sbox-build-feature** skill (screenshot-driven iteration once the spawner is wired).

## Corpus refresh (2026): more reference implementations

### SS2 — data-driven per-enemy `SpawnConfig` struct with 4 stacking bonuses (facepunch.ss2)

SS2's `Manager.Spawning.cs` is the most complete "dynamic difficulty director" in the corpus. Instead of a wave list or a single curve, **each enemy archetype carries its own declarative `EnemySpawnConfig`** and the director samples a weighted distribution every tick. The config encodes five independent weight adjustments, all per-difficulty `[easy, normal, hard]`:

1. **Base ramp** — `WeightMin@ProgressThreshold → WeightMax@ProgressEnd` with a named `EasingType`
2. **Early-spawn incentive** — bonus while spawned-count is low and time is < gate
3. **Catch-up bonus** — scaled by how few of that type currently exist (re-pressures rare types)
4. **Late-game multiplier** — ramps a type up *or* down after minute X
5. **Threat boost** — adds weight when total on-field "threat" is low, keeping pressure constant
6. **Pop-cap** — `MaxCountByDifficulty[]` with `PopCapMinRatio` that eases weight toward 0 as the cap approaches (not a hard refuse — a soft valve)

All tables are `static` and **hot-reloadable** at runtime via `[ConCmd("reload_spawn_configs")]` so a designer can balance without a recompile. This is the pattern behind "feels handcrafted without scripted waves."

```csharp
// Sketch: EnemySpawnConfig struct + weight sampler (mirrors facepunch.ss2 Manager.Spawning.cs)
public struct EnemySpawnConfig
{
    public float[] BaseWeightByDifficulty;    // [easy, normal, hard]
    public float ProgressThreshold;           // weight is 0 before this
    public EasingType RampEasing;
    public float[] MaxCountByDifficulty;
    public float PopCapMinRatio;              // weight eases toward 0 above this ratio
    public float CatchUpBonus;               // added weight when count is low
    public float LateGameMultiplierStart;    // minute at which LateGameMult kicks in
    public float LateGameMult;
}

// Host-only tick (called from OnFixedUpdate after IsHost check):
float SampleWeight( EnemySpawnConfig cfg, float progress, int difficulty, int liveCount )
{
    float w = MathX.Lerp( 0f, cfg.BaseWeightByDifficulty[difficulty], progress >= cfg.ProgressThreshold ? 1f : 0f );
    float capRatio = liveCount / cfg.MaxCountByDifficulty[difficulty];
    if ( capRatio >= 1f ) return 0f;
    if ( capRatio > cfg.PopCapMinRatio )
        w *= MathX.Remap( capRatio, cfg.PopCapMinRatio, 1f, 1f, 0f );
    if ( liveCount == 0 ) w += cfg.CatchUpBonus;
    // late-game and threat-boost omitted for brevity
    return w;
}
```

**Anti-pattern in SS2** (documented in the source): the entire legacy weighted-list director is *commented-out* in-file (Manager.cs lines 588–751) as a before/after of "if-ladder per type" vs "summed-weight sampler". The summed-weight form is harder to reason about per-type but trivially extensible — add a new `EnemySpawnConfig` without touching the director. Prefer it for games with 5+ enemy archetypes.

Source: `facepunch.ss2/ss2/Code/Manager.Spawning.cs` (config structs, weighted sampler) · `stageEvents/StageEvent.cs` (12 timed environmental modifiers layered on top of the director — wind force, lava puddles, darkness, miniboss — showing how to separate hazard/event spawning from enemy spawning).

---

### SS2 — stage events as a separate "director event" layer

`StageEvent.cs` + 12 subclasses (`StageEventLava`, `StageEventWind`, `StageEventDark`, `StageEventMiniboss`…) add timed **environmental modifiers** on top of the enemy director. This is a clean separation:

- The enemy director controls *what spawns when*
- Stage events control *what the arena does* (global wind force, icy friction, lava puddles, healing zones, darkness)

Use the same pattern to separate "enemy wave spawner" from "environmental hazard scheduler" so both are independently tunable. Stage events are short-lived host-authoritative components that `Destroy` themselves at expiry.

---

### DOOM Resurrection — difficulty-filtered entity table from WAD data (ataco.sdoomresurrection)

The DOOM port's `ThingGenerator.cs` shows a **data-driven entity factory** where spawn configuration lives in the level file, not code. Each "thing" in the WAD carries 3 skill-bit flags (`0x001/0x002/0x004` = easy/medium/hard) and a multiplayer-only flag (`0x0010`). The factory reads the flags, rejects entities for the current difficulty, then dispatches to a typed factory `ThingEntity.From<T>(pos, rot)`.

This is the "same map, different monster counts per skill" pattern — applicable whenever your game has difficulty levels that should change *which and how many* enemies appear without authoring separate waves per difficulty.

```csharp
// Pattern: per-difficulty spawn filter (mirrors ataco.sdoomresurrection ThingGenerator.cs)
bool ShouldSpawn( int thingFlags, Difficulty difficulty, bool isMultiplayer )
{
    int skillBit = difficulty switch { Difficulty.Easy => 0x001, Difficulty.Medium => 0x002, _ => 0x004 };
    if ( (thingFlags & skillBit) == 0 ) return false;          // not present on this skill
    if ( (thingFlags & 0x0010) != 0 && !isMultiplayer ) return false; // MP-only entity in solo
    return true;
}
```

**Also from this game: item respawn loop** — each pickup tracks `doesRespawn`, `awaitingRespawn`, and a `TimeSince respawnTime`. When the entity becomes invalid the host schedules a randomized 8–300s respawn (`respawnTime = Game.Random.Float( 8f, 300f )`), re-runs the factory, and plays a flash + sound. Clean deathmatch pickup respawn template.

Source: `ataco.sdoomresurrection/Code/doomwad/ThingGenerator.cs` (difficulty filter, entity factory) · `CheckItemRespawns()` (randomized pickup respawn loop).

---

### klavs.basebuilder — escalating respawn HP ladder for a zombie attacker

BaseBuilder uses a completely different kind of wave director: instead of spawning multiple enemies, one player *becomes* the zombie and respawns with escalating HP on each death. The ladder is a plain int array indexed by death count:

```csharp
// From klavs.basebuilder Code/BaseBuilder/BaseBuilderGameMode.cs
static readonly int[] ZombieHealthSteps = { 400, 800, 1500, 3000, 6000, 12000, 25000, 50000, 100000 };

int GetZombieHealth( int deaths ) =>
    ZombieHealthSteps[ MathX.Clamp( deaths, 0, ZombieHealthSteps.Length - 1 ) ];
```

Death handling runs on the main thread (`await GameTask.MainThread()` before mutating scene state) and on each death flips the killed human to zombie, awards `ZombieKillReward` money to the human attacker, and re-evaluates the win condition. The initial zombie is chosen deterministically by `(RoundNumber - 1) % players.Count` so every player eventually "starts as zombie."

This "escalating HP ladder per death" pattern is directly applicable to a graveyard game where a ghost/haunting gains power each time the player ignores it.

Source: `klavs.basebuilder/Code/BaseBuilder/BaseBuilderGameMode.cs` (ZombieHealthSteps, OnPlayerDied handler).

---

### SS1 — self-limiting population valve via inverse Utils.Map (facepunch.ss1)

Already covered in the canonical recipe (crowd-aware cadence), but one sub-pattern is worth calling out explicitly: the **"special variant suppressed by its own population"** valve. The Spitter's special variant has its spawn chance dynamically reduced based on how many are already alive:

```csharp
// Already in the cadence recipe but this per-variant soft cap is distinct
float specialChance = Utils.Map( Scene.GetAll<SpitterSpecial>().Count(), 0, cap, 1f, 0f, EasingType.ExpoOut );
if ( Game.Random.Float() < specialChance ) SpawnSpecial();
```

This is a **per-type soft cap via inverse map** (not a global hard cap). Use it when you want "at most a few elites but never exactly zero" — the probability eases toward 0 as population rises rather than hard-refusing. The `ExpoOut` easing makes the first few spawns likely and the last near-impossible without a hard refuse. Source: `facepunch.ss1/ss1/Code/Manager.cs` `:401` `SpawnRandomEnemy`.

---

### Anti-patterns flagged in these games

| Anti-pattern | Source | Fix |
|---|---|---|
| Legacy "if-ladder per enemy type" director | ss1 Manager.cs lines 588–751 (commented out) | Use summed-weight `EnemySpawnConfig` sampler — new types = new config, zero director edits |
| `static` weighted tables shared across instances | goders disaster_manager `DisasterWeights` | Use per-instance `NetDictionary` or synced fields; duplicating the manager corrupts shared state |
| Hard-refusing the spawn when cap hit (binary) | Multiple games | Consider ss2's `PopCapMinRatio` soft-cap easing — weight → 0 as cap approaches, not a boolean refuse |
| Authoring separate wave lists per difficulty | N/A | Use per-entity difficulty flags (ss1/DOOM pattern) or per-difficulty config arrays (ss2) |

---

**Read these games for spawning-wave patterns (updated):**
- `facepunch.ss1` — continuous time-curve director, crowd-aware cadence, per-type population valves, coin-debt spillover
- `facepunch.ss2` — data-driven `EnemySpawnConfig` struct, 4 stacking bonuses, per-difficulty tables, hot-reload via `[ConCmd]`, stage-event layer
- `goders.natural_disaster_survival` — Curve-property cadence, weighted+pity selection, cascade spawns (meteor shards), circle/sphere helpers
- `ataco.sdoomresurrection` — difficulty-bit entity filter from data, typed factory, randomized pickup respawn loop
- `klavs.basebuilder` — escalating HP ladder per-death, main-thread death handling, deterministic zombie rotation
- `suburbianites.blindloaded` (Blind) — per-round slot director, host-migration grid rebuild
- `freddo.scoops` — sidewalk-grid pedestrian FSM (crowd population, not combat waves)
