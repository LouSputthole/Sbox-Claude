# Bullet-Heaven / Survivor-Roguelite Recipe

How to build a Vampire-Survivors-style survivor roguelite in modern s&box (GameObject/Component/Scene), distilled from Facepunch's two shipped titles: `facepunch.ss2` ("Sausage Survivors 2" — the larger, meta-progression-heavy reference: 300+ perks, data-driven spawn director, currency/shop/save, leaderboards) and `facepunch.ss1` ("Super Square" — the leaner predecessor: 113 perk classes, a spatial-hash collision broadphase, a vendored 2D-sprite engine, cloud-Stats-only persistence). Together they are a near-complete, code-grounded implementation of the genre.

## What defines the genre

A bullet-heaven survivor is a **single stat sheet wearing a swarm**. The player walks a top-down arena under an orthographic camera; the character **auto-attacks** while an escalating swarm closes in; killing things drops XP; each level-up presents a **weighted-random draft of N perks** (pick one — or a forced *curse* every few levels); chosen perks attach as **stat modifiers + event hooks** that snowball the build; the run ends when you **reach and kill the boss at a fixed time** (victory) or die. A meta loop (currency from quests/achievements → shop → unlocks → harder difficulty) wraps the run — or doesn't.

The core loop, in one line:
> survive an escalating swarm → gain XP → draft 1-of-N weighted perks (forced curse every Nth level) → perks stack onto a Set/Add/Mult stat engine + a perk event-bus → reach the timed boss and kill it = victory → (optional) bank currency → shop/unlock → repeat on higher difficulty (facepunch.ss2: summary).

The whole *build* of this genre is **one stat dictionary + one modifier stack + hundreds of tiny attribute-decorated classes that each call `Player.Modify(...)`**. Get that spine right and everything else (draft, director, meta) bolts on. Both games share the exact same spine; they differ on how much *meta* and *rendering* they layer over it — pick your posture up front:

- **facepunch.ss2 — meta-progression survivor.** A ~3,700-line `Player.cs`, ~250 `PlayerStat`s, 300+ `Perk`/`Curse` classes, gems/charms/guns as loadout items, a fully data-driven `EnemySpawnConfig` director, and a complete persistent meta-game (`ProgressManager`: coins, shop, gem upgrades, quests, achievements, unlocks) in one static class, plus Facepunch leaderboards. Use this when the run feeds a long-term economy/shop and you want builds drafted from a huge reflection-driven pool.
- **facepunch.ss1 — pure-run survivor.** No local save at all — settings/leaderboards ride `Sandbox.Services` only. Its net-new gold is **infrastructure for the swarm itself**: a uniform-grid spatial-hash broadphase (350 enemies + bullets + coins colliding with no physics engine) and a vendored 2D-sprite/billboard rendering pipeline. Use this when the run *is* the whole game and you need to render/collide hundreds of cheap agents.

The defining tension is the same in both: **the run simulation is host-authoritative and the perks are NOT networked as objects** — the host keeps the real perk dictionary and ships only identity→level plus pre-rendered display strings to proxies. Decide your authority model before you write a single perk.

## The system stack to compose

Each maps to a deeper system reference where one exists. Compose in this order — the stat engine and the draft are the genre; the rest is scaffolding.

1. **Stat-modifier engine** (`references/systems/progression-upgrades.md`) — the spine: a per-source Set/Add/Mult modifier stack with priority + clean per-source removal. THE defining system; build it first.
2. **Perk draft / upgrade pool** (`references/systems/gacha-loot.md` for the weighted draw; `progression-upgrades.md` for the per-level scaling) — reflection-built catalog + weighted draw-without-replacement + prerequisite/synergy gating. The level-up choice screen.
3. **Perk event-bus** (no dedicated ref — covered below) — a `IPlayerCallbacks`-style interface of `On*` hooks every perk/loadout-item can override, fanned out by the player. How reactive content stays open/closed.
4. **Spawn director** (`references/systems/spawning-waves.md`) — a data-driven, time-curve, crowd-aware weighted spawner instead of authored waves. Gives "feels handcrafted" pacing.
5. **Run / match flow** (`references/systems/round-match.md`) — `Lobby ↔ Playing` state, an `ElapsedTime` clock, a boss-arrival gate, win/lose + a cinematic outro.
6. **Swarm collision broadphase** (no dedicated ref — covered below) — a uniform-grid spatial hash so hundreds of agents collide without the physics engine (ss1's standout).
7. **Curse / risk-reward drafts** (no dedicated ref — covered below) — forced negative-modifier picks on a difficulty-scaled cadence; same machinery as perks, opposite sign.
8. **Meta-progression** (`references/systems/economy-currency.md` + `shop-vendor.md` + `save-persistence.md`) — currency, a shop catalog with category-gated unlocks, gem upgrades, and a dirty-flag autosave POCO. ss2 bundles all three into one `ProgressManager`.
9. **Leaderboards** (`references/systems/leaderboards-services.md`) — `Sandbox.Services` with a versioned, single-axis score encoding for victory/partial/death and per-run metadata.
10. **2D-sprite rendering** (no dedicated ref — covered below) — billboard-quad + atlas-offset sprite animation with a flash-tint shader (ss1; only if you go 2D-in-3D).

## Build order

Build the spine and one perk before any swarm. The whole genre is testable with a stationary player, a stat engine, and three perks before you ever spawn an enemy.

1. **The stat-modifier engine.** A `PlayerStat` enum, a stat dictionary, `Modify(source, stat, value, Set/Add/Mult, priority)`, `RemoveModifiers(source)`, and `RefreshProperty(stat)` (resolve highest-priority Set → sum Adds → multiply Mults → clamp). Do this first — every perk, gun, gem and curse plugs into it.
2. **One perk + the `IStatModifier` marker.** A `Perk` base implementing `IStatModifier` whose `Refresh()` calls `Player.Modify(this, ...)`. Prove a level-up re-scales it.
3. **The perk event-bus.** An `IPlayerCallbacks` interface + empty virtuals on the base + a `ForEachPerk(p => p.OnX(...))` fan-out. Now reactive perks need zero central wiring.
4. **The weighted draft.** Reflection-build the pool from `TypeLibrary.GetTypes<Perk>()`, filter by attribute axes (rarity/level/prereq/difficulty/curse), weighted reservoir draw without replacement, present N cards. Add banish/reroll.
5. **Player movement + auto-attack.** Stock `PlayerController` (or a top-down kinematic mover); fire on a timer toward the nearest enemy / aim. Auto-attack is just a perk-modifiable fire-rate stat.
6. **The swarm + collision.** Pooled enemy GameObjects; a uniform-grid broadphase if you want hundreds (ss1) or stock physics/triggers if your counts are modest.
7. **The spawn director.** A per-type `EnemySpawnConfig` with progress-curve weight + pop-cap + catch-up; sample the weighted distribution each tick. Make tables hot-reloadable.
8. **Run flow.** `GameState { Lobby, Playing }`, an `ElapsedTime` clock, boss spawn at a fixed time, `Victory()`/`GameOver()`, the slow-mo outro.
9. **Curses.** Same draft machinery, `IsCurse=true`, forced on a `level % N`-style cadence past a difficulty threshold.
10. **Meta-progression + leaderboards.** The `ProgressManager` POCO (coins/shop/upgrades/save) and the versioned single-axis leaderboard. Skip entirely for a pure-run game (ss1 has no save file).

## How the real games do each piece

### Stat-modifier engine — per-source Set/Add/Mult with priority
The central architecture in both games. One flat stat table (`Dictionary<PlayerStat,float>`, ~250 stats in ss2 / 90+ in ss1) seeded once, plus modifiers stored **keyed by their source object** so removal is clean. `RefreshProperty(stat)` recomputes one stat from its stored base: apply the highest-**priority** `Set`, then sum all `Add`, then multiply all `Mult`, then clamp.

```csharp
// Player.Stats.cs — modifiers keyed by source so RemoveModifiers(caller) is trivial.
Dictionary<IStatModifier, Dictionary<PlayerStat, ModifierData>> _statModifiers;
public void Modify( IStatModifier caller, PlayerStat stat, float value, ModifierType type, float priority = 0 ) {
    _statModifiers[caller][stat] = new ModifierData( value, type, priority );  // overwrites same caller+stat
    RefreshProperty( stat );   // recompute: highest-prio Set, then +Add, then *Mult, then clamp
}
```
(facepunch.ss2: ss2/Code/Player.Stats.cs `Modify`/`RefreshProperty`; ss1: ss1/Code/things/Player.cs `Modify`@931, `UpdateProperty`@953 — the identical Set→Add→Mult recompute.) `IStatModifier` is a **bare marker interface** implemented by `Perk`, `Gun`, `Charm`, `Gem` — the only thing they share, so all four item kinds plug into the same engine (ss2: ss2/Code/IStatModifier.cs). A concrete upgrade is ~3 lines re-called on each level-up so the single overwrite-per-caller slot naturally re-scales:

```csharp
// perks/PerkBulletDamage.cs — Refresh() runs on gain AND each level-up; GetValue(Level) is a pure formula.
public override void Refresh() => Player.Modify( this, PlayerStat.BulletDamage, GetValue( Level ), ModifierType.Mult );
```
(facepunch.ss2: ss2/Code/perks/PerkBulletDamage.cs; ss1: ss1/Code/status/DamageStatus.cs Refresh@—.) **Balance lives in a C# attribute + pure formulas, not a spreadsheet asset** — adding a perk is one file. ss2's `RefreshProperty` even carries a long in-code comment debating additive-vs-multiplicative stacking (Shotgun -35% + Pierce -55% feeling wrong as -90%) and a logarithmic `base * 2^(mod/100)` alternative — the exact balance decision you'll hit. **Gotcha:** clamp inside `RefreshProperty` (MaxHp cap, min bullet damage, dash-count floor) or stacked Mults go negative/absurd.

### Perk draft — reflection catalog + weighted draw + synergy gates
The level-up choice is a **weighted-rarity draw with prerequisite gating**, and the catalog is **reflection-driven, never hand-listed**: an attribute on each class decorates its rarity/level/curse/difficulty axes; `TypeLibrary.GetTypes<Perk>()` builds the pool. Add a file → it's in the game.

```csharp
// StatusManager.cs (ss1) — gate by every attribute axis, then weighted draw-without-replacement.
foreach ( var type in TypeLibrary.GetTypes<Status>() ) {
    var a = type.GetAttribute<StatusAttribute>(); if ( a == null ) continue;
    if ( player.Level < a.ReqLevel ) continue;                              // gate: min level
    if ( player.GetStatusLevel( type ) >= a.MaxLevel ) continue;           // gate: maxed
    if ( a.ReqStatuses.Length > 0 && a.ReqStatuses.All( x => !player.HasStatus( x ) ) ) continue; // prereq combo
    if ( (curses && !a.IsCurse) || (!curses && a.IsCurse) ) continue;      // curse vs perk split
    var weight = a.Weight; if ( player.GetStatusLevel( type ) > 0 ) weight += specialistAmount; // bias owned (synergy)
    valid.Add( (type, weight) );
}
// then pick numStatuses by rand in [0,totalWeight); remove each picked → no dupes in one offer
```
(facepunch.ss1: ss1/Code/StatusManager.cs `GetRandomStatuses`; facepunch.ss2: ss2/Code/PerkManager.cs `GetRandomPerks` + `IsPerkAllowed`/`IsPerkAllowedAsReward`.) ss2 adds ~40 hardcoded **synergy prerequisites** in `IsPerkAllowed` (e.g. `CursePiercing` only offered if you already pierce; crit perks require crit > 0), **per-pick reweighting** so owned perks snowball (`weight *= 1 + level * ExistingPerkChance`), rarity weights scaled by luck stats, **max-level-per-rarity** (Common 7 … Unique 1 — rarer = fewer but stronger levels), and in-place `BanishExistingPerkChoice`/`RerollSinglePerkChoice`. **The same code drives the level-up draft, the boss reward, and the curse draft** — one weighted picker, three callers. See `references/systems/gacha-loot.md` for the weighted-draw and rarity-bucket treatment.

### Perk event-bus — virtual `On*` hooks + fan-out (the single most reusable pattern)
The perk base implements a callbacks interface exposing dozens of lifecycle hooks; the player fans every game event out to all owned perks + loadout items. **Adding a reactive upgrade = override one method, no central dispatch wiring** — pure open/closed extension.

```csharp
// perks/Perk.cs — Perk : IStatModifier, IPlayerCallbacks; ~45 virtual hooks (ss2) / ~18 (ss1).
public virtual void OnKill( Enemy e ) { }
public virtual void OnHurt( float dmg ) { }
public virtual bool TryPreventDeath() => false;   // last-stand perks override this
// Player.cs fans events to every owned perk AND gun/gem/charm:
void ForEachPerk( Action<Perk> fn ) { foreach ( var p in Perks.Values ) fn( p ); }
// call site: ForEachPerk( p => p.OnKill( enemy ) );
```
(facepunch.ss2: ss2/Code/perks/Perk.cs + ss2/Code/IPlayerCallbacks.cs, `Player.ForEachPerk`/`ForEachLoadoutItem`; facepunch.ss1: ss1/Code/status/Status.cs + `Player.ForEachStatus`@1258.) ss1's hooks (`OnKill`, `OnHurt`, `OnReroll`, `OnGainExperience`, `OnDashStarted/Finished/Recharged`, `OnReload`, `OnGainShield`, plus `Update(dt)` and `Colliding`) let content like "take 20 dmg when you reroll" (`CurseRerollDmg.cs`) or "fire a laser at the cursor every 1-5s" (`SatelliteLaserStatus.cs`) need **zero changes to Player**.

### Spawn director — data-driven, time-curve, crowd-aware (not authored waves)
Neither game uses wave tables. Each enemy carries a declarative spawn config and the director samples a weighted distribution every tick, so pacing "feels handcrafted" without you authoring a single wave. Two flavours:

**ss2 — a declarative `EnemySpawnConfig` per type** with per-difficulty arrays: a base weight curve (`WeightMin@threshold → WeightMax@end` with easing), an early-spawn incentive, a catch-up bonus scaled by how few exist, a late-game multiplier, a **threat boost** (adds weight when total on-field threat is low → constant pressure), and a population cap that eases weight toward 0 at the cap. All tables are `static` and **hot-reloadable** via `[ConCmd("reload_spawn_configs")]` for live tuning (facepunch.ss2: ss2/Code/Manager.Spawning.cs).

**ss1 — a continuous if-ladder director** with curve-driven cadence and a self-limiting "special" valve:
```csharp
// Manager.cs (ss1) — spawn interval slows when crowded, ramps with run time t.
var spawnTime = Utils.Map( EnemyCount, 0, MAX_ENEMY_COUNT, 0.05f, 0.3f, QuadOut )   // crowd brake
              * Utils.Map( t, 0f, 80f, 1.5f, 1f ) * Utils.Map( t, 0f, 250f, 3f, 1f );
// each archetype: a t-gated chance that ramps, an eliteChance sub-roll, and a "special" chance
// SUPPRESSED by how many already exist — a self-limiting population valve:
float specialChance = Utils.Map( Scene.GetAll<SpitterSpecial>().Count(), 0, cap, 1, 0, ExpoOut );
```
(facepunch.ss1: ss1/Code/Manager.cs `HandleEnemySpawn`@360, `SpawnRandomEnemy`@401; the whole legacy summed-weight version is commented out at lines 588-751 — a real before/after of "if-ladder" vs "summed-weight" design.) Hard caps matter: ss1 uses `MAX_ENEMY_COUNT=350`. See `references/systems/spawning-waves.md` for the generic director skeleton.

### Run flow — clock, boss gate, slow-mo outro
`GameState { Lobby, Playing }` ([Sync]) on a singleton `Manager`; a synced `ElapsedTime` (a `TimeSince`) drives everything; the boss spawns when `ElapsedTime > BossArrivalTime` (~13-15 min). `Victory()`/`GameOver()` set synced flags then run a **cinematic outro** by ramping `Scene.TimeScale`.

```csharp
// Manager.cs (ss1) — TimeScale is the main expressive tool; all entities multiply their own dt by it.
// death outro ramps slow-mo back up over ~1.7s while zooming the ortho camera:
Scene.TimeScale = Utils.Map( TimeSinceGameOver, 0f, 1.7f, 0.02f, 0.25f, EasingType.SineIn );
// PauseWhileChoosing perk eases time → 0 while drafting; pause menu hard-sets TimeScale = 0.
```
(facepunch.ss1: ss1/Code/Manager.cs OnUpdate@246/279, GameOver@1129; facepunch.ss2: ss2/Code/Manager.cs `GameState` + `Manager.Instance` singleton, `Networking.CreateLobby` max 3 players + 12 spectators.) **Clean freeze pattern:** ss1 gates every entity's update behind `ShouldUpdateThings`/`ShouldUpdatePlayer` flags so one bool freezes the whole world. See `references/systems/round-match.md`.

### Swarm collision — uniform-grid spatial hash (ss1's standout)
To collide hundreds of agents without the physics engine, bucket the arena into a uniform grid; each `Thing` re-registers its cell only when it crosses a boundary; collision tests just the **3×3 neighborhood**, filtered by a per-entity `CollideWith` type list before the circle-overlap test.

```csharp
// Manager.cs / Thing.cs (ss1) — Dictionary<(int,int), List<Thing>> buckets; test 3x3 around self.
for ( int dx = -1; dx <= 1; dx++ ) for ( int dy = -1; dy <= 1; dy++ ) {
    if ( !ThingGridPositions.TryGetValue( (gx + dx, gy + dy), out var bucket ) ) continue;
    foreach ( var other in bucket )
        if ( CollideWith.Contains( other.GetType() ) && CircleOverlap( this, other ) )
            HandleCollision( other );
}
```
(facepunch.ss1: ss1/Code/Manager.cs `ThingGridPositions`/`GridSquare`@78, `Thing.UpdateGridPos`@95, `Enemy.CheckCollisions`@650.) The same grid powers boids-like enemy separation (read neighbors, push apart). This is the genre's answer to "350 enemies + bullets + coins at frame rate." If your counts are modest, stock `BoxCollider`+`ITriggerListener` is simpler — only reach for the hash when you've measured a physics bottleneck.

### Curses — negative perks on a difficulty-scaled cadence
Curses are **normal perks with `IsCurse=true`** that hook events to hurt you; on difficulty past a threshold, certain levels force a curse draft instead of a perk draft. Same machinery, opposite sign — a clean risk/reward + anti-snowball lever.

```csharp
// Player.cs (ss1) — past difficulty 6, shrinking-interval levels are cursed (forced negative draft).
bool IsLevelCursed( int level ) => Difficulty >= 6 && level % CurseInterval( Difficulty ) == 0;
// GenerateLevelUpChoices then calls GetRandomStatuses(..., curses: true) → only IsCurse perks.
// CurseRerollDmg/CurseReverseControls/CurseHurtLoseXp are just Statuses with On* hooks that bite.
```
(facepunch.ss1: ss1/Code/things/Player.cs `IsLevelCursed`@1507, `GenerateLevelUpChoices`@1458, ss1/Code/status/Curse*.cs; facepunch.ss2: 300+ `Curse*` classes, forced every 6 levels.) Because curses ride the same draw + event-bus, you add one with a single file — no new system.

### Meta-progression — one static POCO for currency + shop + save (ss2)
ss2 puts the entire meta-game in one static `ProgressManager`: a serialized POCO → `progress.json` via `FileSystem.Data`, with a **dirty-flag interval autosave**, a `StateVersion` int for O(1) UI change-detection, an in-`Load` schema migration, and a `ShopItemDef` catalog with **category-gated unlocks** (own N in a category to unlock the next — a soft wall with zero extra state).

```csharp
// ProgressManager.cs (ss2) — dirty flag + interval autosave; rewards save immediately.
public void AddCoins( int n ) { Data.Coins += n; _isDirty = true; }
public void Tick() { if ( _isDirty && _timeSinceLastSave > 5f ) Save(); }
void Save() { FileSystem.Data.WriteJson( "progress.json", Data ); _isDirty = false; StateVersion++; }
// ShopItemDef.RequiredPurchases = N → "own N items in this category first" gates the unlock.
```
(facepunch.ss2: ss2/Code/ProgressManager.cs — holds coins, owned shop items, equipped gems + upgrade levels, selected gun/charms, quest levels, unlocked+claimed achievements; `BuildGunItems`/`BuildCharmItems`/`BuildGemItems` assemble the catalog and **duplicate-ID-check at build**.) This bundles currency + shop + save + versioning the cookbook treats separately — see `references/systems/economy-currency.md`, `shop-vendor.md`, and `save-persistence.md`. **ss1 has no save file at all** — it rides `Sandbox.Services.Stats` for settings and leaderboards only (a cloud Stat doubles as a persisted preference via `Stats.LocalPlayer.Get(key).LastValue`). Pick: persistent meta (ss2) vs pure-run (ss1).

### Leaderboards — versioned, single-axis multi-outcome encoding (ss2)
A run has three outcomes (victory / reached-the-boss-but-died / early death) that must sort correctly on **one** numeric board. ss2 encodes all three on one axis with offsets, and **bakes a `LEADERBOARD_VERSION` into the stat name** so a balance change starts a fresh board.

```csharp
// Manager.Stats.cs (ss2) — one score carries three outcome types; UI decodes by range.
long GetScore() => IsVictory ? VICTORY_OFFSET - (long)ElapsedTime          // faster victory = higher
                 : ReachedBoss ? BOSS_DEFEAT_OFFSET + (long)(BossDamagePct * 100)
                 : (long)ElapsedTime;                                       // early death = raw time survived
// stat name embeds version + difficulty + player count → a balance change = a clean new board:
string StatName => $"ss2_score_v{LEADERBOARD_VERSION}_{difficulty}_{numPlayers}";
```
(facepunch.ss2: ss2/Code/Manager.Stats.cs `GetScore`/`GetStatString`; submit via `Sandbox.Services.Stats.SetValue`, read via `Services.Leaderboards.GetFromStat(name).SetAggregationMax().SetSortDescending()`. ss1 is simpler: `SetAggregationMin()` + `SetSortAscending()` for fastest-time-wins, `Leaderboards.Board2`, per-difficulty stat name — ss1/Code/ui/Panels/LeaderboardPanel.razor.) ss2 also filters bad scores client-side at display (`HiddenLeaderboardSteamIds` + value-range filters) and serializes per-run perks as a `Dictionary<int,int>` (identity→level) in the submission metadata so the detail panel can rehydrate perk icons. See `references/systems/leaderboards-services.md` and `references/systems/anti-cheat.md`.

### 2D-sprite rendering — billboard quads + atlas-offset shader (ss1, optional)
If you go 2D-in-3D (world on the XY plane, `Z = -y*10` for depth sorting), ss1 ships a complete vendored sprite engine: a billboard `SceneObject` on `models/sprite_quad_1_sided.vmdl` driven by texture-atlas offset/tiling uniforms (`g_vTiling`/`g_vOffset`) via a custom `shaders/sprite_2d.shader`, plus a **flash-color uniform** (`g_vFlashColor`/`g_flFlashAmount`) for hit feedback — white-alpha-0 normally, red on hurt — Forward/PingPong loop modes, per-frame animation-tag broadcast events, and attach points materialized as child Bone GameObjects (facepunch.ss1: ss1/Code/Sprite/SpriteComponent.cs, ss1/Code/shaders/sprite_2d.shader). It also demonstrates **versioned resource/component migration** (`[JsonUpgrader]`, `ResourceVersion`/`ComponentVersion`) — `Looping:bool` → `LoopMode:enum`, backfill a missing field. Lift this only if you want a 2D look; a 3D survivor can use stock `ModelRenderer`s.

## Networking — host-authoritative run, identity-keyed perk state
Both games run the simulation host-authoritative and **never network perks as objects**. This is the pattern to copy for any co-op survivor.

**Perks travel as identity→level + pre-rendered display strings.** The host keeps the real `Dictionary<int, Perk>`; proxies get a `[Sync] NetDictionary<int,int>` (typeIdentity→level). For the choice UI, the host **pre-renders** name/description/icon into parallel `[Sync] NetList<string>` so clients never run perk logic.

```csharp
// Player.Perks.cs (ss2) — wire format is the type identity, not the object.
[Sync] public NetDictionary<int,int> SyncPerks { get; set; }            // identity -> level
[Sync] public NetList<string> SyncCurrentPerkChoiceDisplayNames { get; set; }   // host pre-renders
// PerkManager wraps TypeDescription.Identity <-> TypeLibrary.GetTypeByIdent as the wire format.
```
(facepunch.ss2: ss2/Code/Player.Perks.cs, ss2/Code/PerkManager.cs `TypeToIdentity`/`IdentityToType`.) **Client-side registry hydration:** a client that only knows a perk by identity has never run that perk's static ctor, so its display metadata is missing — `Perk.EnsureRegistered(type)` does `TypeLibrary.GetType(type).Create<Perk>()` once **purely to trigger the static ctor** and fill the static display tables (ss2: ss2/Code/perks/Perk.cs). **Whitelist-synced stats:** of ~250 `PlayerStat`s, only a hand-curated `_syncedStats` list mirrors to proxies via `[Sync] NetDictionary<PlayerStat,float>`; everything else is host-authoritative and never sent (ss2: ss2/Code/Player.Stats.cs `GetUiStat`) — deliberate bandwidth control most s&box games miss.

## UI reactivity — hash-counters, not deep diffing
Both games drive Razor reactivity with plain int counters bumped on mutation, so panels rebuild only when something actually changed — cheap, explicit, no per-frame allocation.

```csharp
// ModifierHash++ on every Modify; StateVersion++ on every save; PerkChoiceHash++ on a new draft.
// Razor BuildHash() combines them so the panel rebuilds only on real change:
protected override int BuildHash() => System.HashCode.Combine( Player.ModifierHash, Manager.PerkChoiceHash );
```
(facepunch.ss2: ss2/Code/Player.Stats.cs `ModifierHash`, ss2/Code/ProgressManager.cs `StateVersion`, ss2/Code/ui/LeaderboardPanel.razor.) ss2 also ships an **attribute/regex-driven rich-text token system**: `[RichTextPanelAttribute(pattern)]` panels self-register and `RichText.razor` regex-matches a string to render live inline perk/item icons mid-sentence (`[perk:PerkBulletDamage:3]`, `[item:gem_gold]`, `[color:#ff0]…[/color]`) — same reflection-extensibility philosophy as the perk catalog, applied to UI (ss2: ss2/Code/ui/richtext/).

## Pitfalls (from the mined code)

- **Clamp inside `RefreshProperty`, not at call sites.** Stacked Mults send HP/damage/dash-count to absurd or negative values; the recompute is the only place that sees the final value (ss2: Player.Stats.cs; ss1: Player.cs UpdateProperty@953).
- **Decide additive-vs-multiplicative stacking up front.** ss2's own in-code comment flags Shotgun -35% + Pierce -55% reading as -90% feeling wrong; a `base * 2^(mod/100)` log curve is the alternative. Choose before you author 100 perks (ss2: Player.Stats.cs `RefreshProperty`).
- **Never network perks as objects.** Sync identity→level + host-pre-rendered display strings; proxies must never run perk logic or descriptions (ss2: Player.Perks.cs).
- **Trigger static ctors on clients before reading static perk metadata** — `Create<T>()` the type once or the description tables are empty on a proxy (ss2: Perk.cs `EnsureRegistered`).
- **Whitelist which stats sync** — mirroring all ~250 stats is needless bandwidth; only the UI-visible handful need `[Sync]` (ss2: Player.Stats.cs `_syncedStats`).
- **Cumulative rarity thresholds must be ascending, and weighted draw must remove-on-pick** — see `gacha-loot.md` for the shipped overlapping-threshold bug in a sibling game; the perk draw avoids dupes by removing each pick.
- **Cap the swarm and throttle drops.** ss1 hard-caps at 350 enemies and *banks un-spawned coin value as debt* when the field is saturated (`_coinDebt += value`), paying it back on the next coin — keeps loot fair without unbounded entities (ss1: Manager.cs `SpawnCoin`@839).
- **Gate the spawn director's "special" variants by how many already exist** — a population valve (`Map(count, 0, cap, 1, 0, ExpoOut)`) prevents elite/special floods (ss1: Manager.cs).
- **One global `Scene.TimeScale` knob only works if every entity multiplies its own `dt` by it** — slow-mo/pause/hitstop desync otherwise (ss1: Manager.cs OnUpdate).
- **Carry run-config across the menu→game scene load with `DontDestroyOnLoad`, then clear the flag** so the carrier dies with the run instead of leaking into the next (ss1: `DifficultyTracker` — sets `GameObjectFlags.DontDestroyOnLoad`, Manager clears it on consume).
- **Spatial hash, not physics, for hundreds of agents** — but only reach for it after measuring; stock triggers are simpler at modest counts (ss1: Manager.cs grid).
- **`MathF` exists in the game sandbox but NOT the bridge editor addon** — these games use `MathF`/custom `Utils.Map` at runtime; use `MathX`/`System.Math` for any editor-side code you generate via the bridge.

## Things NOT to copy

- **A hand-maintained "add a stat means edit 3 places" `CharacterStats` struct** (a sibling gacha game's anti-pattern) — these games avoid it by keying everything off the `PlayerStat` enum + dictionary; keep the dictionary, don't reintroduce parallel field lists.
- **Client-authoritative scores/currency** — ss2 keeps the run host-authoritative and submits via `Services.Stats`; never trust a client-reported score for a leaderboard.
- **Hardcoded display constants coupled to CSS** (strip-offset/slot-width pitfalls covered in `gacha-loot.md`) — keep simulation (the roll/draft) fully decoupled from any animation or layout number.

## Verify live

The installed SDK is authoritative — confirm signatures with `describe_type` / `search_types` reflection before relying on them, not this doc or training data: `Component`, `[Sync]` / `[Rpc.Host]` / `[Rpc.Broadcast]` / `IsProxy`, `Component.INetworkListener`, `Networking.CreateLobby`, `NetDictionary<,>` / `NetList<>`, `Game.TypeLibrary.GetTypes<T>()` / `TypeDescription.Identity` / `TypeLibrary.GetTypeByIdent` / `Create<T>()`, `Sandbox.Services.Stats.SetValue` / `Services.Leaderboards.GetFromStat` (+ `SetAggregationMin/Max`/`SetSortAscending/Descending`/`FilterByDay/Week/Month`/`SetFriendsOnly`), `FileSystem.Data.ReadJsonSafe`/`WriteJson`, `Scene.TimeScale` / `Time.Delta`, `GameObjectFlags.DontDestroyOnLoad`, and `[JsonUpgrader]` (resource/component migration). Note the s&box **sandbox restricts `MathF`** — these games' runtime math (`MathF`, custom `Utils.Map` with easing) is fine in-game but use `MathX`/`System.Math` in any editor-side bridge code. Stop play mode before scene edits; screenshot UI/visual changes and read the PNG.

Cross-links: see the **sbox-api** skill for authoritative type/method signatures, and the **sbox-build-feature** skill for the screenshot-driven build loop and the sandbox gotcha list (MathF restricted, Cloud assets ephemeral, head-bone case sensitivity).

## Which games to read

- **facepunch.ss2** ("Sausage Survivors 2") — the **primary, complete reference**. Read `Player.Stats.cs` (the modifier engine), `perks/Perk.cs` + `IPlayerCallbacks.cs` (event-bus), `PerkManager.cs` + `Player.Perks.cs` (reflection draft + identity-keyed sync), `Manager.Spawning.cs` (declarative spawn director), `ProgressManager.cs` (currency+shop+save in one), `Manager.Stats.cs` (versioned single-axis leaderboard). The biggest survivor codebase in the corpus.
- **facepunch.ss1** ("Super Square") — the **lean companion**. Read `things/Player.cs` (`Modify`/`UpdateProperty` — same engine, smaller), `status/Status.cs` + `StatusManager.cs` (event hooks + weighted draw with prereq-combo gates), `Manager.cs` (the spatial-hash broadphase + the if-ladder time-curve director + `TimeScale` choreography + coin-debt throttle), `Sprite/SpriteComponent.cs` (the optional 2D billboard/atlas/flash-shader pipeline). Best read for swarm *infrastructure* and the pure-run (no-save, cloud-Stats-only) posture.

For composing pieces: the **stat-modifier engine** and **perk event-bus** here are cross-genre (RPG, tycoon, card-battler) — see also `references/genres/gacha-crawler.md` for the weighted-roll/derived-stat treatment and `references/genres/deathmatch-arena.md` for the real-time combat exchange and `IDamageable` contract a 3D survivor's bullets will reuse.
