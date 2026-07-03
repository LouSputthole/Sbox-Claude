# Progression & Upgrades

How to build leveled upgrade trees, currencies, and stat payoffs in modern s&box — host-authoritative, data-driven, and save-safe. Mined from 18 shipped games.

## What this IS / when you need it

A progression system is three loops that feed each other:
1. **Currency / XP** — a number that goes up (money, coins, XP, score, quota).
2. **Upgrades** — spend the currency to raise leveled *upgrade* values, each with a **cost curve** and a **cap**.
3. **Payoff** — gameplay reads the upgrade levels and changes behavior (faster mining, more spawns, bigger inventory).

You need this for any tycoon/idle/sim/roguelite. The hard parts are *authority* (who is allowed to spend), *the cost curve*, *where the payoff is read*, and *making it survive save/reload and host migration*. Get those four right and the rest is content.

## Canonical modern-s&box approach

### 1. Hold economy state as host-authoritative `[Sync]`

Put currency + every upgrade level on one component as `[Sync(SyncFlags.FromHost)]` auto-properties. Clients see read-only mirrors; only the host writes (repo/vault77.chop_the_forest: `Code/Player/PlayerProgression.cs:37`; repo/emg.everything_must_go: `Code/Shop/Shop.cs:22-30`).

```csharp
public sealed class ShopProgression : Component
{
    [Property, Sync( SyncFlags.FromHost )] public float Money { get; private set; }
    [Property, Sync( SyncFlags.FromHost )] public int Xp { get; private set; }
    [Property, Sync( SyncFlags.FromHost )] public int Level { get; private set; }
    [Sync] public int AdvertisingTier { get; set; }   // 0..Max
}
```

`SyncFlags.FromHost` means clients literally cannot write the value — any client-initiated change MUST round-trip an `[Rpc.Host]` (repo/vault77.chop_the_forest gotcha). Collections do **not** `[Sync]`: either use `NetList<T>`/`NetDictionary<K,V>` (repo/emg.everything_must_go: `Code/Shelving/Shelf.cs:48-54`; repo/thefancylads.restaurant_dev: `NetDictionary<UpgradeResource,int> ActiveUpgrades`) or serialize a `HashSet` to a CSV `[Sync] string` and rebuild on proxies (repo/enifun.shop_manager: `Code/Economy/PlayerProgression.cs:250`).

### 2. The canonical buy recipe (proxy → host → validate → spend → cap → save)

Every upgrade follows the same shape. This is the most-copied block across the corpus (repo/enifun.shop_manager: `Code/Shop/ShopManager.cs:208`):

```csharp
public bool TryBuyAdvertising()
{
    if ( IsProxy ) { RequestBuyAdvertisingOnHost(); return true; }   // client → host
    if ( AdvertisingTier >= MaxAdvertisingTier ) return false;       // cap

    var cost = GetAdvertisingCost();
    if ( !ShopFunds.Current.SpendMoney( cost, "Advertising" ) )      // re-validate funds host-side
        return false;

    AdvertisingTier++;                                              // mutate the [Sync] level
    SaveManager.MarkDirty();                                        // persist
    return true;
}

[Rpc.Host] private void RequestBuyAdvertisingOnHost() => TryBuyAdvertising();
```

Re-check the cap and the funds **on the host**, never trust the client number. With per-owner permissions, gate on the caller (repo/thefancylads.restaurant_dev: `Code/Common/Progression/Upgrades.cs:61` — `if ( restaurant.HasPermissions( Rpc.Caller ) )`).

### 3. The cost curve

Two idioms dominate:

**Geometric** — `BaseCost * pow(mult, currentLevel)`, returns a sentinel at max. The canonical idle/tycoon price (repo/master.digging_simulator: `Code/ShopTerminal.cs:49`):

```csharp
public int GetCost( UpgradeConfig c, int level )
    => level >= c.MaxLevel ? -1 : (int)(c.BaseCost * MathF.Pow( c.CostMultiplier, level ));
```

**Table** — a `static readonly float[]` of explicit per-tier costs (and a parallel effect array), `MaxTier` derived from `.Length` (repo/enifun.shop_manager: `Code/Shop/ShopManager.cs:175`; repo/vault77.chop_the_forest: `Code/Player/AxeUpgradeBalance.cs:21`):

```csharp
public static readonly float[] AdvertisingCosts      = { 500f, 1000f, 2000f, 4000f };
public static readonly float[] AdvertisingReductions = {  10f,   20f,   30f,   40f }; // % effect per tier
public float GetAdvertisingCost() =>
    AdvertisingTier >= MaxAdvertisingTier ? 0f : AdvertisingCosts[AdvertisingTier];
```

XP-to-level is usually a pure power curve solved both ways, with `AddXp` looping to handle multiple level-ups at once (repo/clearlyy.s_miner: `LevelingTable.cs:140` — `320 * level^2.55`; repo/playbtg.elevator: `ElevatorPlayer.Score.cs:111`; repo/enifun.shop_manager: `Code/Economy/PlayerProgression.cs:90`):

```csharp
void AddXp( int amount )
{
    Xp += amount;
    while ( Xp >= XpForNextLevel( Level ) ) { Xp -= XpForNextLevel( Level ); Level++; OnLevelUp(); }
}
```

### 4. Pull-based payoff (don't push)

Upgrades should **not** reach into gameplay components. Consuming systems *query* the current effect. Two ways:

**Per-upgrade getter** (simple, verbose) — `GetX()` reads the tier and returns a multiplier; gameplay calls `ShopManager.Current?.GetAdvertisingSpawnMultiplier()` (repo/enifun.shop_manager: `Code/Shop/ShopManager.cs:197`):

```csharp
public float GetAdvertisingSpawnMultiplier() =>
    AdvertisingTier <= 0 ? 1f : 1f - AdvertisingReductions[AdvertisingTier - 1] / 100f;
```

**Central effect aggregator** (cleanest, scales) — one `EffectsManager` that every system queries; upgrades just *declare* `UpgradeEffect{Type, Mode, ValuePerRank}` structs and never touch gameplay (repo/thefancylads.restaurant_dev: `Code/Common/Effects/EffectsManager.cs:30`):

```csharp
public float Get( RestaurantComponent r, EffectType type )
{
    float total = 0f;
    foreach ( var (upgrade, rank) in r.ActiveUpgrades )
        foreach ( var effect in upgrade.Effects )
            if ( effect.Type == type && effect.Mode != EffectMode.Flag )
                total += effect.ValuePerRank * rank;
    return IsMultiplicative( type ) ? 1f + total : total;   // additive vs multiplicative
}
```

Additive effects return the raw sum; multiplicative return `1 + sum`. The additive-vs-multiplicative split is a hand-maintained `switch` — easy to forget a new `EffectType` (same file `:92`, gotcha confirmed).

### 5. Define content as data, not code

For anything beyond a handful of upgrades, make each upgrade a `GameResource` so designers author asset files with zero code changes (repo/thefancylads.restaurant_dev: `Code/Common/Progression/UpgradeResource.cs:6`; repo/artisan.darkrpog: `Code/Jobs/JobDefinition.cs:1`; repo/playbtg.elevator: `ExperienceDefinition.cs`):

```csharp
[AssetType( Name = "Upgrade", Extension = "upgrade", Category = "Progression" )]
public class UpgradeResource : GameResource
{
    public string Id { get; set; } = "";
    public UpgradeCategoryResource Category { get; set; }
    public int LevelCount { get; set; } = 5;          // cap
    public int Tier { get; set; } = 1;                // gate: (Tier-1)*5 points in category
    public UpgradeEffect[] Effects { get; set; } = Array.Empty<UpgradeEffect>();
}
```

Enumerate generically with `ResourceLibrary.GetAll<UpgradeResource>()` and look up by string `Id`. **Never** cache an empty `GetAll()` result — an early caller (hotload race) can run before GameResources are indexed and permanently empty your catalog (repo/artisan.darkrpog gotcha). And **never send a GameResource ref over an RPC** — send its `Id` string and reconstruct host-side via `ResourceLibrary.GetAll<T>().FirstOrDefault(r => r.Id == id)` (repo/thefancylads.restaurant_dev: `Code/Common/Economy/RestaurantShop.cs:29`).

### 6. Persist it (and clamp on load)

Save a flat POCO via `FileSystem.Data.WriteJson` / `ReadJson` (repo/master.digging_simulator: `SaveManager.cs:80`; repo/facepunch.jumper: `JumperProgress.cs:17`), or the s&box cloud `Sandbox.Services.Stats` for leaderboard-backed numbers (repo/playbtg.elevator: `ElevatorPlayer.Score.cs:80`). **Always re-clamp every level on load** — `Math.Clamp(saved, 0, Max)` — and re-apply upgrade stats onto live components after restore (repo/master.digging_simulator `RestoreLevels()`; repo/clearlyy.s_miner: `StatsManager.cs:1041-1068`). Adding a balance-table row raises `Max`, so a row removal would otherwise leave a persisted level above cap (repo/vault77.chop_the_forest gotcha).

## Variations seen across games

- **Stat-modifier engine** — instead of summing effects per query, register layered modifiers (`Set` → `Add` → `Mult` by priority) per source into a `NetDictionary<Stat,float>`; recompute from a stored base. Best when ~100 stats and many sources (repo/facepunch.ss1: `ss1/Code/things/Player.cs:931,953`).
- **Roguelite "choose 1 of N"** — on level-up, queue weighted reward choices (unlock / upgrade / worker / cash) with per-kind caps + dedupe + a reroll cost; mirror pending choices to clients as parallel `NetList` columns (repo/emg.everything_must_go: `Code/Shop/Shop.cs:1095-1183`).
- **Reflection-driven perks** — every perk is a `[Status(maxLevel, reqLevel, weight, ...)]`-attributed class; `TypeLibrary.GetTypes<Status>()` + weighted sampling builds the choice list. Adding a perk = one file (repo/facepunch.ss1: `ss1/Code/StatusManager.cs:42`).
- **Prestige / rebirth / ascension** — higher meta-layers reset lower progress for permanent multipliers; `Reconcile…FromUpgrades` re-derives spent points on load (repo/clearlyy.s_miner: `StatsManager.cs:1127`). Rank tables recomputed from a running score each frame (repo/lavagame.multis_cases: `Code/Game/Core/RankSystem.cs:27`).
- **Idle/passive earners** — a per-tick income generator whose upgrade levels live on the *entity*, with **bucketed sync** (accumulate a delta, only write the `[Sync]` when it crosses a threshold) so a fractional value doesn't spam 60 packets/sec (repo/artisan.darkrpog: `Code/Items/MoneyPrinter.cs:49`).
- **Anti-farm XP** — per-source hourly cap + per-key cooldown so you can't grind the same victim (repo/artisan.darkrpog: `Code/Skills/RoleplaySkillService.cs:39`).
- **Escalating quota economy** — daily quota from a `.runset` resource, scaled by player count, with over-quota bonus + carry-over into the next day (repo/treehaven.sdiver: `Code/Gameplay/GameMode/ExpeditionMode.cs:85`).
- **Deterministic per-player rotation** — daily/weekly challenges seeded by an explicit FNV mix of `SteamId + periodIndex` so the set is stable across sessions but unique per player; progress = lifetime `Stat.Sum` − a snapshot taken at rollover (repo/Blind: `Code/Economy/ChallengeDef.cs:69`).

## Gotchas

- `SyncFlags.FromHost` = clients cannot write. Every client-driven spend MUST go through `[Rpc.Host]`, and the host must **re-validate and re-clamp** the incoming request — never trust the number (repo/vault77.chop_the_forest, repo/enifun.shop_manager).
- **Client-only saves are cheatable.** `FileSystem.Data` keyed by SteamId on the owner is fine for solo/co-op, trivially editable for a competitive economy (repo/clearlyy.s_miner, repo/stepdev.xtrem_road gotchas). For trust, keep state host-authoritative.
- **Collections can't `[Sync]` directly** — use `NetList`/`NetDictionary` (host-only replication; rebuild after host-side mutation) or a CSV-string `[Sync]` rebuilt on proxies.
- **Bump a save Version on schema change** and clamp/sanitize on load; bumping silently wipes incompatible saves by design (repo/emg.everything_must_go `CurrentVersion`; repo/master.digging_simulator). Dictionaries keyed by an enum break if you reorder the enum (repo/clearlyy.s_miner).
- **Don't cache an empty `GetAll()`** — guard against the hotload/early-tick race that runs before GameResources index (repo/artisan.darkrpog).
- **Additive vs multiplicative** must be declared per effect type and is easy to miss when adding a new one (repo/thefancylads.restaurant_dev).
- **`MathF` may be blocked in the access-listed sandbox.** Several of these games run unsandboxed and use `MathF.Pow`; if your project throws on `MathF`, use `System.MathF` carefully or precompute the curve into a table (cookbook-wide s&box gotcha — verify with `describe_type`).
- Host-migration survival: every must-survive number needs `FromHost` sync **and** a deterministic restore path (repo/playbtg.elevator: `ElevatorNetworkHelper.cs` + `RestoreExperienceOrder`).

## Seen in

- repo/vault77.chop_the_forest — `Code/Player/PlayerProgression.cs`, `AxeUpgradeBalance.cs` (FromHost economy + balance tables)
- repo/thefancylads.restaurant_dev (GASTROTOWN) — `Code/Common/Effects/EffectsManager.cs`, `Common/Progression/Upgrades.cs`, `UpgradeResource.cs` (central effect aggregator + GameResource upgrades) — *cleanest reference*
- repo/enifun.shop_manager — `Code/Shop/ShopManager.cs`, `Economy/PlayerProgression.cs` (canonical TryBuy recipe + XP tokens)
- repo/master.digging_simulator — `Code/ShopTerminal.cs`, `SaveManager.cs` (geometric cost formula + JSON save)
- repo/emg.everything_must_go — `Code/Shop/Shop.cs` (roguelite choose-1-of-N + NetList mirrors)
- repo/clearlyy.s_miner — `StatsManager.cs`, `LevelingTable.cs` (prestige/rebirth/ascension + power-curve XP)
- repo/facepunch.ss1 — `Code/things/Player.cs`, `StatusManager.cs` (layered stat-modifier engine + reflection perks)
- repo/artisan.darkrpog — `Code/Skills/RoleplaySkillService.cs`, `Items/MoneyPrinter.cs`, `Jobs/JobDefinition.cs` (anti-farm XP, idle earner, GameResource defs)
- repo/playbtg.elevator — `ElevatorPlayer.Score.cs`, `ExperienceManager.cs` (cloud-Stats XP + host-migration restore)
- repo/lavagame.multis_cases — `Code/Game/Core/RankSystem.cs` (prestige rank table + odds buffs)
- repo/treehaven.sdiver — `Code/Gameplay/GameMode/ExpeditionMode.cs`, `EquipmentResource.cs` (escalating quota + tiered GameResource upgrades)
- repo/stepdev.xtrem_road — `Code/Progression/PlayerProgression.cs`, `Inventory/PlayerInventory.cs` (level/prestige gating + vendor)
- repo/goders.natural_disaster_survival — `Code/globals/DataManager.cs`, `ui/ShopItem.cs` (cost-array upgrade tree + 3D look-to-buy shop)
- repo/facepunch.jumper — `Code/Player/JumperProgress.cs` (per-scene JSON progress + Achievements.Unlock)
- repo/yellowletter.terrys_crash_course — `LevelMedals.cs`, `LevelProgressionService.cs` (medal tiers + unlock gating over Stats)
- repo/Blind — `Code/Economy/ChallengeDef.cs`, `ChallengeService.cs` (deterministic FNV-seeded challenge/streak rotation)
- repo/namicry.gacha_crawler — `Code/Models/GameItem.cs` (level×rarity upgrade cost formulas)
- repo/simalami.15_puzzle_master — `LevelProgression.cs` (save-slot star-mask progression + resumable state)

## Corpus refresh (2026): more reference implementations

Net-new cross-game implementations from the latest mining pass. None duplicate the patterns above — they are *variations* and *standalone techniques* worth stealing.

### Per-source modifier stack keyed by the source object (cleaner than ss1)

`facepunch.ss2` is the strongest stat-modifier reference in the corpus. Same Set→Add→Mult idea as ss1, but modifiers are stored in a nested dict **keyed by the source object**, so removal/re-scale is one line and four unrelated item kinds (`Perk`, `Gun`, `Charm`, `Gem`) plug into one engine via a bare `IStatModifier` marker (ss2: `ss2/Code/things/player/Player.Stats.cs:20`):

```csharp
Dictionary<IStatModifier, Dictionary<PlayerStat, ModifierData>> _statModifiers;   // ~250 PlayerStat enum
public void Modify( IStatModifier caller, PlayerStat stat, float v, ModifierType type, float priority = 0 ) {
    _statModifiers[caller][stat] = new ModifierData( v, type, priority );   // overwrites same caller+stat slot
    RefreshProperty( stat );                                                // highest-priority Set, then +Add, then ×Mult, then clamp
}
public void RemoveModifiers( IStatModifier caller ) { /* drop the caller's dict, RefreshProperty each touched stat */ }
```

A concrete upgrade is ~10 lines and re-applies on every level-up — because each caller owns exactly one slot per stat, re-calling `Modify` naturally re-scales it (ss2: `perks/PerkBulletDamage.cs`):

```csharp
public override void Refresh() => Player.Modify( this, PlayerStat.BulletDamage, GetValue( Level ), ModifierType.Mult );
```

ss2 also carries the **balance lore in a code comment**: a long note in `RefreshProperty` argues additive `-35% + -55%` feeling wrong as `-90%` and proposes a logarithmic `base * 2^(mod/100)` instead — exactly the additive-vs-multiplicative decision a reader will hit (ss2: `Player.Stats.cs`).

### Switch-expression effect lookups (no stored numbers, no migration)

`lavagame.sandmoney_` stores **only the int level** in the save and resolves every effect through a `switch` expression, so a balance change never needs a save migration (sandmoney: `Code/UpgradeSystem.cs:34`):

```csharp
public float GasReduction => GasLevel switch { 5 => 0.55f, 4 => 0.45f, 3 => 0.30f, 2 => 0.15f, 1 => 0.05f, _ => 0f };
```

Its buy path is **transactional with rollback** — deduct first, run `applyEffect()` in a `try`, refund on exception — then commits with a *critical, non-throttled* save so an upgrade is never lost to the 10s save debounce (sandmoney: `TryApplyUpgradePurchase`):

```csharp
Owner.TakeMoney( cost );
try { applyEffect(); } catch { Owner.AddMoney( cost ); throw; }   // refund on failure
UpgradeRevision++; Network.Refresh(); SaveCritical();             // bypass the 10s throttle for purchases
```

### Bit-packed upgrade levels + data-asset stage ladders

`intercrusstudio.sneguborka` packs **16 tool slots × 4 bits each into 3 branch-major ulongs** (so `MaxLevelPerBranch ≤ 15`), and authors the ladders as `.upgrd` GameResources where a **missing/short stage list just means "branch maxed"** — a half-authored config never crashes the UI (sneguborka: `Player/PlayerToolUpgrades.cs`, `Config/ToolUpgradeConfig.cs`):

```csharp
// .upgrd resource: CooldownStages / RangeStages / PowerStages = List<{ int Level, int CostMoney, float Delta }>
public Stage NextStage( int branch, int tool ) {
    int lvl = GetLevel( branch, tool );           // unpack 4 bits
    var stages = Config.StagesFor( branch );
    return lvl < stages.Count ? stages[lvl] : null;   // null == maxed (short list = capped, no crash)
}
```

Bag capacity is a **separate progression axis** (one bit per owned tier) with `MaxSlots` lazily recomputed only when `OwnedBagBits` changes — a delta-cache instead of a per-frame scan. Pair this with the prestige notes below.

### Prestige variations: bracket currency + full-wipe-then-reapply

Two fresh prestige idioms beyond s_miner's rebirth:

- **Bracket-based meta-currency** (`sandmoney_`): a reset awards prestige coins for your *current* net-worth bracket only, not cumulatively, so repeated cheap resets earn nothing — `ComputeHeritageCoinsForNetWorth(nw)` returns 0 below $1T then 1/3/5/8…35 by power-of-ten (sandmoney: `PlayerTrader` Heritage region). `ResetProfileCore` zeroes ~40 fields + destroys bot objects, **then re-applies persistent perks and re-runs FTUE** so the new run starts with its purchased head-starts.
- **Re-grant the starter, don't recreate the object** (`sneguborka`): `WinterReset` wipes money/tools/upgrades/bags, re-derives the "first Cost=0, MaxTier=1" Spoon + T1 bag (mirroring spawn without a new GameObject), re-locks key-gated walls, and **clears the host-only grant-dedupe set** so consumables can be re-earned. Teleport-on-confirm is host-resolved via **reservoir sampling over spawn points** (uniform pick, no list alloc) and applied on the owning client with `Transform.ClearInterpolation()` so the Rigidbody controller doesn't visibly lerp across the map (sneguborka: `Player/PlayerPrestigeController.HostApply.cs`).

### "Tuning sheet as code" — the whole XP economy in one file

`despawn.murder` makes the entire XP formula a single editable `static class` of `const`s, applied as `BaseXP × (1 + Σ qualifying bonus fractions)` (murder: `Systems/Rounds/XpConfig.cs`):

```csharp
public static class XpConfig {
    public const int WinBase = 100, KillBase = 25;
    public const int RampageThreshold = 5;          // named gameplay threshold
    public const float FullLobbyBonus = 0.25f, RampageBonus = 0.5f;   // additive fractions, summed then ×Base
}
```

And it makes **prices tunable live without touching assets** by pulling them from a `Server|Replicated` ConVar mini-DSL (`Powerups = "radar=1,silent=2,knife=3"`) instead of the item, so a server owner re-balances the in-round store at runtime (murder: `Systems/Game/GameConVars.cs`, `EquipmentShop/EquipmentShopManager.cs`). The item itself is a string-key → behavior dispatch (`"radar" => new Radar{...}`).

### Backend-authoritative XP with optimistic local level-ups

When progression lives on a real REST backend (not `FileSystem.Data`), `despawn.murder` keeps the UI responsive by computing the level-up math **locally and immediately**, then reconciling against the authoritative response — an optimistic store with a debounced flush (murder: `Systems/Inventory/MurderDataStore.cs`, `API/ApiClient.cs`):

```csharp
LevelCalculator.AddExperience( LevelConfig.Profile, ref level, ref experience, gainedXp ); // local, instant bar move
OnProfileChanged?.Invoke(); MarkDirty();          // fire watchers, 5s debounce → push to server
// ApplyServerProfile(resp) later reconciles + CancelDirty(); round reports are fire-and-forget (`_ = ReportRoundToApi(json)`)
```

This is the reference for "responsive progression UI over a slow authoritative service" — far beyond the local-JSON save patterns above. Pair with a resilient `ApiClient` (JWT exchange + shared in-flight task + exp-backoff retry + typed `ApiResult<T>`).

### Leaderboard service as the source of truth for XP

`stellawisps.lumberyard` doesn't store XP in its local save at all — it **rehydrates XP from the Stats service on load** and double-writes on gain, so the cloud leaderboard *is* the authority (lumberyard: `LumberPlayer.cs:105,787`):

```csharp
public void AddExperience( int amount, string reason ) {
    RecentXpChange = $"+{amount} XP: {reason}";
    Sandbox.Services.Stats.Increment( "XP", amount );          // write
}
// on load: Xp = Stats.GetPlayerStats( Game.Ident, Connection.Local.SteamId ).Get( "XP" ).Sum;   // read back
```

It also shows **gear-gated progression encoded as two ints on data assets** — chopping checks `tool.ToolLevel < tree.AxeLevel → "too weak"`, so harder content simply requires a higher-tier tool, no tree structure needed (lumberyard: `Tool.cs:592`, `Inventory.cs:17`).

### Stat-threshold goals that auto-complete and unlock content

`facepunch.fair` gates progression purely by data flow — a `Goal` watches a stat and **completes itself the instant the stat crosses a threshold** (no polling), and each goal carries the GameObjects it unlocks, registered into a static cache the build menu queries (fair: `Park/Goals/*`, `Park/Progression/Stats/Stats.cs`):

```csharp
[Feature("Stat")] public string StatName; public float StatNeededToComplete;
void IStatEvents.OnStatChanged( string name, float value ) {
    if ( name == StatName && value >= StatNeededToComplete ) Complete();   // auto-complete, no Update loop
}
// each Goal: List<GameObject> Buildings → Goal.GetRequiredForBuilding(b) gates the build menu
```

### Achievements as decoupled event-listeners

`barrelproto.ragroll` (and `facepunch.jumper`) treat each achievement as a tiny self-contained subscriber that calls `Sandbox.Services.Achievements.Unlock("id")` on its trigger — adding one is adding a class to a list, never editing a central switch (ragroll: `Code/mode/achievements/AchievementsController.cs` + `implement/*.cs`):

```csharp
class ComboBeatAchievement : IAchievementCondition {
    public void Init() => Missions.OnMissionFinished += m => { if ( m.Mission.Id == "combo_beat" ) Services.Achievements.Unlock( "combo_beat" ); };
}
// gating off state: NickNPC reads Services.Achievements.All[..].IsUnlocked to decide whether to force the tutorial
```

The same file family shows a **combo-multiplier accumulator** (rising `1 + (count-1)/4` multiplier, grounded-timeout flush) — a progression-flavored score loop for skater/trick games (ragroll: `Code/mode/score/ModeScore.cs`).

### Zero-state soft progression walls

Two cheap "you must earn your way here" gates that store **no extra progression state**:

- **Category-gated unlocks** (`ss2`): a shop item declares `RequiredPurchases = N`, meaning "own N items in this category first" — derived live from inventory, no flags (ss2: `ProgressManager.cs`, `IsItemUnlocked`/`GetPurchasesNeeded`).
- **Collection-album multiplier** (`lavagame.multis_cases`): completing a case's non-gold item set grants `CollectionMultiplier += 0.05` to passive income — a non-pay progression sink layered on the rank ladder already covered above (multis_cases: `GameManager.cs` CollectionData). Its rank is earned by *gambling volume* (`RankScore = TotalCasesSpent + CumulativeSkinValue`), not by holding cash, and feeds `GetRarityBonus(rank)` back into the loot roll.

### Gotcha — static rate-limiters must use `RealTime.GlobalNow`

A `static readonly` per-`Connection` cooldown gate that guards your buy/upgrade/prestige RPCs must measure time with `RealTime.GlobalNow`, **not** `Time.Now`: `Time.Now` resets on every editor F5, so a static limiter that survives the hotload sees stale future timestamps and silently rate-limits every RPC for minutes (sneguborka: `Player/RpcRateLimiter.cs`).

### Read these games (updated pointer)

- **ss2** (`ss2/Code/things/player/Player.Stats.cs`, `perks/`, `ProgressManager.cs`) — *the* per-source Set/Add/Mult modifier stack + reflection perk draft + one-file meta-game (currency+shop+save+versioning). The cleanest engine in the corpus.
- **sandmoney_** (`Code/UpgradeSystem.cs`, `PlayerTrader` Heritage region) — switch-expression effects (no-migration balance) + transactional refund-on-exception buy + bracket-prestige + full-wipe-then-reapply.
- **sneguborka** (`Player/PlayerToolUpgrades.cs`, `PlayerPrestigeController.HostApply.cs`, `RpcRateLimiter.cs`) — bit-packed levels + `.upgrd` stage ladders + re-grant-the-starter prestige + the `RealTime.GlobalNow` rate-limiter gotcha.
- **despawn.murder** (`Systems/Rounds/XpConfig.cs`, `Systems/Inventory/MurderDataStore.cs`, `GameConVars.cs`) — tuning-sheet-as-code XP + ConVar-tunable prices + backend-authoritative XP with optimistic local level-ups.
- **lumberyard** (`LumberPlayer.cs`, `Tool.cs`, `Inventory.cs`) — leaderboard-service-as-XP-authority + two-int gear gating.
- **facepunch.fair** (`Park/Goals/*`, `Park/Progression/Stats/Stats.cs`) — stat-threshold goals that auto-complete and unlock buildings.
- **barrelproto.ragroll** (`Code/mode/achievements/`, `mode/score/ModeScore.cs`) — achievements as event-listeners + combo-multiplier accumulator.

---
**Verify live:** the installed SDK is authoritative — confirm `GameResource`, `[AssetType]`, `Sync`/`SyncFlags`, `Rpc.Host`, `NetList<T>`, `Sandbox.Services.Stats`, and `FileSystem.Data` with `mcp__sbox__describe_type` / `mcp__sbox__search_types` before relying on a signature. Reflection beats training data; the API shifts between versions.
**See also:** `sbox-api` (look up exact type/method signatures) and `sbox-build-feature` (the screenshot-driven build-and-iterate loop).
