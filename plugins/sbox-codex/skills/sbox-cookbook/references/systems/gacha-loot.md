# Gacha / Loot Tables in modern s&box

How to build weighted random loot — drop tables, lootboxes, CS:GO-style case spins, daily/timed cases, pity — host-authoritative and reusable across drops/shops/spawns.

## What it IS / when you need it

A **gacha-loot** system is *weighted random selection* over a reward table plus the machinery
around it: where the table lives (code vs `GameResource` asset), how the roll is made
deterministic/host-authoritative, and how the result is presented (instant grant vs spinning
reveal). Use it for chest/treasure drops, lootboxes & case-opening, slot machines, daily free
cases, fishing/mining rarity tiers, ARPG affix rolls, even "pick a random timer/decoy". The
selection primitive is identical everywhere — only the table source and presentation differ.
It's the single most-repeated system in the 27 mined games (35 instances), all reducible to
the same ~8-line weighted pick.

## The canonical weighted pick (copy this)

The textbook cumulative-weight roulette. Filter to valid entries, sum weights, roll into
`[0,total)`, walk the cumulative sum, return the first entry the roll lands in. Verified
nearly identical in three independent games:

```csharp
// Pure, engine-free, deterministic if you pass a seeded Random.
public static T Roll<T>( IReadOnlyList<T> entries, Func<T, float> weightOf, Random rng = null )
{
    rng ??= Random.Shared;
    var valid = entries.Where( e => weightOf( e ) > 0f ).ToArray();
    if ( valid.Length == 0 ) return default;

    float total = valid.Sum( weightOf );
    float roll = (float)(rng.NextDouble() * total);   // [0, total)
    float cumulative = 0f;
    foreach ( var e in valid )
    {
        cumulative += weightOf( e );
        if ( roll <= cumulative ) return e;           // landed in this bucket
    }
    return valid[^1];                                  // float-rounding fallback
}
```

This is exactly `LootboxRoller.Roll` (artisan.darkrpog: Code/Lootboxes/LootboxRoller.cs:8)
— note it returns `reward.Clone()` so callers can't mutate the catalog. The int-weight
variant subtracts instead of accumulating: `roll = rng.Next(0,total); foreach(...) { roll -=
w; if (roll < 0) return item; }` (lavagame.multis_cases:
Code/Game/Economy/ItemDefinition.cs:117). The `Game.Random.Float(0,total)` walk is the same
shape (treehaven.sdiver: Code/Items/Treasure/LootRoller.cs:33).

Two RNG choices: `Random.Shared` / `new Random(seed)` (pure, seedable, replayable) vs s&box's
`Game.Random` (a shared `Sandbox.Random`). Use a **seeded** `Random` whenever the result must
be reproducible or host-validated; `Game.Random` is fine for cosmetic local picks.

### Where the table lives — prefer a GameResource

Make the table a `GameResource` subclass so designers author content as asset files, no
recompile:

```csharp
[GameResource( "Loot Table", "loot", "A weighted drop table" )]
public class LootTableResource : GameResource
{
    public List<LootEntry> Entries { get; set; } = new();
}

public class LootEntry
{
    public GameObject WorldPrefab { get; set; }   // designer drops a prefab ref
    public float Weight { get; set; } = 1f;
    public int MaxSpawns { get; set; } = 99;      // optional per-entry cap
}
```

Read them generically with `ResourceLibrary.GetAll<LootTableResource>()` (treehaven.sdiver:
Code/Definitions/LootTableResource.cs:14; artisan.darkrpog: Code/Lootboxes/
LootboxCaseDefinition.cs). **Never cache an empty `GetAll()`** — a hotload/chat tick can run
before resources are indexed and permanently empty your table (artisan.darkrpog: Code/Jobs/
JobDefinition.cs:118).

### Host-authoritative grant (the multiplayer recipe)

Roll **on the host**, replicate the open-state with `[Sync, Change]` so late-joiners are
correct. This treasure chest is the cleanest end-to-end example:

```csharp
public sealed class TreasureContainer : Component, IInteractable
{
    [Property] public LootTableResource LootTable { get; set; }
    [Property] public GameObject SpawnPointParent { get; set; }

    [Sync, Change( nameof( OnOpenedStateChanged ) )]
    public bool IsOpened { get; set; }

    [Rpc.Broadcast]
    public void OnInteracted( GameObject interactor )
    {
        if ( IsOpened ) return;
        if ( !IsProxy )                 // host only: roll + mutate Sync state
        {
            IsOpened = true;
            SpawnLoot();
        }
    }

    void SpawnLoot()
    {
        var roller = new LootRoller( LootTable );           // per-container, caps are local
        var points = SpawnPointParent.Children
            .OrderBy( _ => Game.Random.Float() ).ToList();   // shuffle so 1 drop isn't always slot 0
        int n = Game.Random.Int( 1, points.Count );
        for ( int i = 0; i < n; i++ )
        {
            var prefab = roller.RollItem();
            if ( prefab is null ) continue;
            var loot = prefab.Clone( points[i].WorldPosition, points[i].WorldRotation );
            loot.NetworkSpawn();                             // host spawns, replicates to all
            var rb = loot.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndDescendants );
            if ( rb != null ) rb.MotionEnabled = false;      // freeze in place
        }
    }

    // OnOpenedStateChanged / OnStart re-apply the "opened" visual + strip the
    // "interactable" tag on EVERY client (incl. late joiners).
}
```

Verified at treehaven.sdiver: Code/Items/Treasure/TreasureContainer.cs:14 (`[Sync, Change]`),
:33 (`[Rpc.Broadcast]` + `IsProxy` host gate), :60 (`Clone`/`NetworkSpawn`/freeze), :17
(`OnStart` late-joiner re-apply). The `LootRoller` is a plain C# class, instantiated **per
roll** so per-entry `SpawnsLeft` caps are per-container, not global
(Code/Items/Treasure/LootRoller.cs:8).

### Decide first, animate second (CS:GO-style spin)

For a spinning-strip reveal, **roll the real winner up front**, then place it at a fixed index
in a strip of weighted decoys and ease the scroll toward a precomputed pixel offset. The
animation is pure cosmetic and can't change the outcome.

```csharp
// 1. Decide (host-authoritative if it has value)
var winner = ItemGenerator.RollLootboxRarity();   // cumulative-threshold roll
// 2. Build a long filler strip, drop the winner at a constant index
strip[WinIndex] = winner;                          // namicry uses 85; stepdev uses 34
// 3. Animate offset with ease-out cubic so it decelerates onto WinIndex
float t = (elapsed / duration).Clamp( 0, 1 );
float eased = 1f - (1f - t) * (1f - t) * (1f - t);  // 1-(1-t)^3, no MathF needed
scrollPx = MathX.Lerp( 0, targetOffsetPx, eased );
```

Pattern at namicry.gacha_crawler: Code/GameManager.cs:2181 (StartLootbox places result at
index 85), :2307 (UpdateLootboxSpin `1-(1-t)^3` easing); stepdev.xtrem_road:
Code/Fishing/ChestPrizeDatabase.cs:60 (BuildStrip, winner at WinIndex=34), :73
(GetScrollTarget pixel offset). **The strip geometry (index, slot-width, viewport) is coupled
to the Razor panel CSS** — keep the roll fully decoupled from the animation so a CSS tweak
can't change odds.

## Notable variations

- **Cumulative-threshold rarity (no per-item weights):** one `rng.NextDouble()` vs ascending
  cutoffs (Divine 0.15% → Common 59.75%), then pick uniformly within the chosen rarity tier.
  Good when you have rarity buckets rather than per-item weights (namicry.gacha_crawler:
  Code/Data/ItemGenerator.cs:936 `RollLootboxRarity`; stepdev.xtrem_road:
  Code/Fishing/FishType.cs:255 `RollCatch`, where higher rod tier shifts the whole curve).
- **Rank/tier odds buff:** rebuild the weight array multiplying rare-tier entries by a
  `1.0–1.5x` rank bonus before rolling (lavagame.multis_cases:
  Code/Game/Economy/ItemDefinition.cs:136 `RollWinner(rng, PlayerRank)`).
- **Newcomer pity:** first-time flags boost Mythic/Legendary, and the roll returns *which* pity
  flag it consumed so the caller can burn it (namicry.gacha_crawler:
  Code/Data/ItemGenerator.cs:953 `RollLootboxRarityWithPity` → tuple).
- **Per-entry spawn caps:** `RemoveAll(SpawnsLeft==0)` before summing, decrement the winner
  (treehaven.sdiver: Code/Items/Treasure/LootRoller.cs:33).
- **Expected-value preview:** weighted-average payout to show EV / size battle pots
  (lavagame.multis_cases: Code/Game/Economy/ItemDefinition.cs:109 `GetExpectedValue`).
- **Timed free case + charge recharge:** `MaxCharges=3`, regen `1` per `1800s`, tracked as a
  persisted **Unix-seconds** stamp (not `TimeSince`) so it survives reload; reconstruct charges
  on load (vault77.chop_the_forest: Code/World/CaseRewardBalance.cs:87, Code/Player/
  PlayerProgression.cs:549). Daily-cooldown variant compares `DateTime.UtcNow` to a saved
  `LastDailyLootboxUtc` (clearlyy.s_miner: DailyLootbox.cs:212, :41).
- **Host-authoritative gambling (case-battle/jackpot):** client deducts optimistically, sends
  `[Rpc.Host]` with a declared cost, host re-validates + rolls, `[Rpc.Broadcast]`es the result;
  on any validation failure the host must `SafeRefund(declaredCost)` (lavagame.multis_cases:
  Code/Game/Core/GameManager.cs:1084, Code/Game/Gambling/JackpotManager.cs:50).
- **Live external catalog:** build the table at runtime from a public JSON API, cached to
  `FileSystem.Data` with a TTL so offline starts work (lavagame.multis_cases:
  Code/Game/Economy/Cs2CaseApiBuilder.cs:11).
- **Virtual/lazy spawning:** pre-compute a `Dictionary<Vector3Int,int>` of ore-index per voxel
  with a seeded `Random`, spawn nothing, and only `Clone()+NetworkSpawn()` when revealed —
  huge perf win (master.digging_simulator: OreGenerator.cs:49, :95).

## Gotchas

- **Ascending thresholds, in order.** A cumulative roll only works if cutoffs ascend. namicry's
  `RollRarity` (Code/Data/ItemGenerator.cs:912) checks Mythic at `< 0.6f` *before* Legendary at
  `< 0.12f`, so Legendary/Rare are **unreachable** — a real shipped bug. Copy
  `RollLootboxRarity`'s ordering (:936), not `RollRarity`'s.
- **Roll host-side for anything with value.** Client rolls are trivially editable. Vault77's
  free cosmetic case happily uses `Random.Shared`, but its gambling subsystem forbids it.
  Cooldowns/charges compared against client-saved timestamps are also editable — fine for
  free QoL, not for economy.
- **Never refund-forget.** In optimistic-deduct flows the client already spent; every host
  validation-failure branch must refund (lavagame `SafeRefund`).
- **Don't send `GameResource` refs over RPC.** They don't round-trip; send a string Id and
  rebuild via `ResourceLibrary.GetAll<T>().FirstOrDefault(r => r.Id == id)` on the host
  (GASTROTOWN: Code/Common/Economy/RestaurantShop.cs:29). Same for prefabs — send
  `PrefabFile.ResourcePath` (treehaven.sdiver: Code/Definitions/LootTableResource.cs).
- **Don't `[Sync]` the held/decoy visuals or the entries list.** Rebuild them locally from the
  synced state (treehaven late-joiner re-apply; namicry's chat entries list is not synced so
  late joiners get no history).
- **Decouple roll from UI geometry.** Hardcoded WinIndex / slot-width / center-offset are tied
  to Razor CSS; change the CSS and the winner stops landing centered.
- **Instantiate stateful rollers per use.** A `LootRoller` with `SpawnsLeft` caps is per-roll
  state, not a shared singleton.
- **Negative-price sentinels.** Some games mark gacha-only items with `Price = -1` and gate by
  `Price > 0` — respect the sentinel (stepdev.xtrem_road: Code/Fishing/RodType.cs:50).
- **Static lookups build at type-load.** Per-rarity/zone dictionaries built in a static ctor
  won't see content added at runtime (stepdev.xtrem_road: Code/Fishing/FishType.cs).

## Seen in (open the real code)

- **artisan.darkrpog** — `LootboxRoller.Roll` clean weighted pick + `.Clone()`; lootboxes as
  `GameResource` defs (Code/Lootboxes/).
- **lavagame.multis_cases** — int-weight roll, EV math, rank-buffed odds, host-authoritative
  case-battle/jackpot, live CS2 API catalog (Code/Game/Economy/, Code/Game/Gambling/).
- **treehaven.sdiver** — `LootRoller` with per-entry caps + `[Sync,Change]` host-authoritative
  `TreasureContainer` (Code/Items/Treasure/).
- **namicry.gacha_crawler** — cumulative-threshold rarity + newcomer pity + CS:GO spin
  (Code/Data/ItemGenerator.cs, Code/GameManager.cs).
- **stepdev.xtrem_road** — rod-tier/zone rarity curve + CS:GO case strip (Code/Fishing/).
- **vault77.chop_the_forest** — timed free case + charge recharge (Code/World/
  CaseRewardBalance.cs, Code/Player/PlayerProgression.cs).
- **clearlyy.s_miner** — daily/cooldown lootbox + data-driven block/loot config
  (DailyLootbox.cs, BlockConfigs.cs).
- **master.digging_simulator** — virtual/lazy depth-weighted ore spawning (OreGenerator.cs).
- **emg.everything_must_go** — slot-machine `ChooseWeighted` primitive (Code/Shop/).
- **Blind / suburbianites.blindloaded** — weighted item roll + double-or-bust gamble RPC
  (Code/Items/ItemDef.cs, Code/Player/Player.cs).
- **vidya.terry_games** — drop-in weighted-pick over a timer table
  (Code/Logic/Gamemodes/RLGL/RedLightGreenLight.cs).

---

Verify live: the installed SDK is authoritative — confirm `[Sync]`/`[Rpc.Broadcast]`/`[Rpc.Host]`,
`GameResource`, `ResourceLibrary.GetAll<T>`, `GameObject.Clone`/`NetworkSpawn`, `Game.Random`,
`Random.Shared`, and easing helpers (`MathX`, `MathF`) with `describe_type` / `search_types`
reflection before relying on a signature. For the build loop and API lookups, cross-link the
`sbox-api` and `sbox-build-feature` skills.

## Corpus refresh (2026): more reference implementations

Four additional games surface techniques absent from the section above. All code is modern
s&box (GameObject/Component, `[Sync]`/`[Rpc]`, `OnUpdate`).

### Reflection-driven catalog — no hand-list (facepunch.ss2)

The perk pool in Sausage Survivors 2 is never maintained as a list. Every class decorated with
`[Perk(Rarity, ...)]` is automatically included. Add a file → it's in the pool.

```csharp
// PerkManager.cs — build the pool once from reflection
static List<TypeDescription> _pool;
static void BuildPool()
{
    _pool = TypeLibrary.GetTypes<Perk>()
        .Where( t => !t.IsAbstract && t.HasAttribute<PerkAttribute>() )
        .ToList();
}

// Weighted reservoir draw without replacement
public List<Perk> GetRandomPerks( Player player, int count, PerkRarity rarity )
{
    var candidates = _pool
        .Where( t => PassesSynergyGate( t, player ) )
        .Select( t => (type: t, weight: GetWeightForRarity( t.GetAttribute<PerkAttribute>().Rarity )) )
        .ToList();
    // per-pick reweighting: already-owned perks get weight *= (1 + level * ExistingPerkChance)
    foreach ( var owned in player.OwnedPerks )
        AdjustWeight( candidates, owned.GetType(), 1f + owned.Level * ExistingPerkChance );

    return ReservoirDraw( candidates, count );   // removes winner from candidates each pick
}
```

Game: facepunch.ss2 — `Code/PerkManager.cs`, `Code/perks/Perk.cs`.

**Anti-pattern fixed here:** a hand-maintained `List<Type>` in a central file → merges conflict and
authors forget to register. The attribute approach is zero-maintenance.

**Per-pick reweighting (snowball / anti-snowball):** multiply the candidate weight by a bias factor
after each draw. ss2 uses it to let owned perks snowball (`weight × (1 + level × chance)`), but the
same hook can *reduce* weight after each pick to push variety.

### Synergy/prerequisite gates (facepunch.ss2)

Before offering a perk, ss2 checks ~40 hardcoded rules so a pick is only shown when it can do
something useful:

```csharp
// PerkManager.cs IsPerkAllowed — called as the candidate filter above
bool PassesSynergyGate( TypeDescription t, Player p )
{
    var attr = t.GetAttribute<PerkAttribute>();
    if ( attr.MinDifficulty > CurrentDifficulty ) return false;
    if ( attr.RequiresPiercing && !p.CanPierce() ) return false;
    if ( attr.RequiresCrit && p.CritChance <= 0f ) return false;
    if ( attr.MaxLevel > 0 && p.GetPerkLevel(t) >= attr.MaxLevel ) return false;
    // ... ~40 more rules
    return true;
}
```

Apply this any time a loot table should only offer items the player can use — e.g., only offer
weapon drops matching the player's class, only offer upgrade perks for abilities the player owns.

### Client-side static-registry hydration (facepunch.ss2)

Clients connected mid-session receive perk identities (ints) over `[Sync] NetDictionary`, not
object instances. Static display dicts are populated by a static ctor — but that ctor never ran on
the client for perks they haven't encountered:

```csharp
// Perk.cs — called before displaying a perk by identity
public static void EnsureRegistered( Type type )
{
    if ( _registered.Contains( type ) ) return;
    // Instantiating the type triggers its static ctor, which calls Register(...)
    TypeLibrary.GetType( type ).Create<Perk>();
    _registered.Add( type );
}
```

Lesson: `TypeDescription.Create<T>()` purely to trigger a static ctor is the correct way to hydrate
type-keyed static data on a client that never ran the game-object lifecycle for that type.

### Live cloud stats feeding back into drop tables (facepunch.ss2)

The `PerkRandomFavorite` perk biases the draft toward what this player historically chooses:

```csharp
// Player.Perks.cs — called once per run to personalize the pool
void DetermineFavoritePerks()
{
    _favorites = _pool
        .OrderByDescending( t =>
            Sandbox.Services.Stats.LocalPlayer.Get( $"perkChosen_{t.Identity}" )
            - Sandbox.Services.Stats.LocalPlayer.Get( $"perkIgnored_{t.Identity}" ) )
        .Take( 10 )
        .ToHashSet();
}
// During GetRandomPerks: if _favorites.Contains(type) weight *= FavoriteBonus;
```

Pattern: `Services.Stats.LocalPlayer.Get(key)` — the read-only per-player stat store — is
queryable at runtime and can personalize any weighted draw. No server round-trip needed.

### Wear/float as a post-roll value modifier (lavagame.multis_cases)

After the rarity roll selects an item, a second `WearFloat` roll (0 = Factory New → 1 =
Battle-Scarred) drives a `WearMultiplier` (1.35× to 0.70×) that scales `SellValue`:

```csharp
// InventoryItem construction after RollWinner
item.WearFloat = rng.NextSingle();   // uniform [0,1)
item.SellValue = item.BaseValue * GetWearMultiplier( item.WearFloat );

static float GetWearMultiplier( float wear ) => wear switch {
    < 0.07f  => 1.35f,  // Factory New
    < 0.15f  => 1.15f,  // Minimal Wear
    < 0.38f  => 1.00f,  // Field-Tested
    < 0.45f  => 0.85f,  // Well-Worn
    _        => 0.70f,  // Battle-Scarred
};
```

Game: lavagame.multis_cases — `Code/Game/Economy/ItemDefinition.cs`. Generalizable to any
"condition" axis on a drop (durability, freshness, edition, quality grade).

### Collection album — completing a set gives a persistent multiplier (lavagame.multis_cases)

Filling all non-gold items in a case grants a permanent `CollectionMultiplier += 0.05f` on passive
income — a non-monetary, non-pity progression sink that rewards broad rolling over cherry-picking:

```csharp
// GameManager — called after any item grant
void CheckCollectionCompletion( string caseName, string itemName )
{
    _collection[caseName].Add( itemName );
    var caseDef = _cases[caseName];
    bool complete = caseDef.PossibleDrops
        .Where( i => i.Rarity != ItemRarity.Gold )
        .All( i => _collection[caseName].Contains( i.Name ) );
    if ( complete ) CollectionMultiplier += 0.05f;
}
```

Game: lavagame.multis_cases — `Code/Game/Core/GameManager.cs` + `CollectionData`.

### Jackpot pending-win-until-ACK (lavagame.multis_cases)

When the jackpot host selects a winner but the winner disconnects before receiving their prize,
the payout is persisted locally and re-applied on next join:

```csharp
// JackpotManager.cs — host side, after finalizing the jackpot
[Rpc.Broadcast]
void BroadcastJackpotResult( string winnerSteamId, ItemDefinition[] wonItems )
{
    if ( Connection.Local.SteamId.ToString() != winnerSteamId ) return;
    if ( !GrantItems( wonItems ) )
    {
        // Disconnecting before grant — persist to local file
        FileSystem.Data.WriteAllText( "mc_jackpot_pending.json",
            JsonSerializer.Serialize( wonItems ) );
    }
}

// GameManager.OnStart — always re-check for a pending win
void TryGrantPendingJackpotWin()
{
    if ( !FileSystem.Data.FileExists( "mc_jackpot_pending.json" ) ) return;
    var items = FileSystem.Data.ReadJson<ItemDefinition[]>( "mc_jackpot_pending.json" );
    if ( GrantItems( items ) )
        FileSystem.Data.DeleteFile( "mc_jackpot_pending.json" );
}
```

Game: lavagame.multis_cases — `Code/Game/Gambling/JackpotManager.cs`, `Code/Game/Core/GameManager.cs`.

### Per-source layered rarity curves (namicry.gacha_crawler)

The same generator serves three different roll contexts, each with its own probability shape:

```csharp
// ItemGenerator.cs — three separate roll functions, same enum return
public static ItemRarity RollLootboxRarity()    // shop boxes: Divine 0.15%, Mythic 0.25%, Legendary 2% ...
public static ItemRarity RollDungeonLootRarity() // no Mythic; Legendary capped at 0.5%; min-rarity floor
public static ItemRarity RollRarity()            // world drops: wider common band

// ⚠ ANTI-PATTERN in the same file — RollRarity checks Mythic BEFORE Legendary:
//   if (roll < 0.006f) return Mythic;
//   if (roll < 0.012f) return Legendary;   ← unreachable (Mythic already catches [0, 0.006))
// Fix: always check from rarest DOWN in ascending threshold order.
// RollLootboxRarity at :936 gets it right — copy that, not RollRarity.
```

Game: namicry.gacha_crawler — `Code/Data/ItemGenerator.cs:912` (buggy world roll) vs `:936` (correct
lootbox roll). The ascending-threshold bug is already noted in Gotchas above; the layering pattern
is new.

### Class-weighted generation — biasing drops toward the player's build (namicry.gacha_crawler)

After rarity is decided, the item type is skewed 70% toward what the player's class can use:

```csharp
// ItemGenerator.GenerateRandomItemForClass
ItemType PickItemType( PlayerClass cls, Random rng )
{
    if ( rng.NextDouble() < 0.70 )
    {
        // 70%: preferred armor or a weapon the class can wield
        return rng.NextDouble() < 0.5
            ? ClassData.GetPreferredArmor( cls )
            : ClassData.GetUsableWeapon( cls, rng );
    }
    return AllItemTypes[ rng.Next( AllItemTypes.Length ) ];  // 30%: any type
}
```

Game: namicry.gacha_crawler — `Code/Data/ItemGenerator.cs`. Useful for any class/build-system where
off-class drops feel like wasted rolls.

### Ingredient-modified procedural roll (namicry.gacha_crawler)

Crafting ingredients carry `[Flags] CraftIngredientEffect` that modify how `ItemGenerator.GenerateCraftedItem`
runs — a clean way to let crafting inputs control the loot outcome rather than a fixed recipe:

```csharp
// CraftIngredientData.cs
[Flags] enum CraftIngredientEffect { None=0, ClassMatch=1, TierGuarantee=2, PropertyReroll=4 }

// ItemGenerator.GenerateCraftedItem
public static Item GenerateCraftedItem( int level, ItemRarity rarity, PlayerClass cls,
                                         CraftIngredientEffect effects )
{
    var item = GenerateBase( level, rarity );
    if ( effects.HasFlag( CraftIngredientEffect.ClassMatch ) )
        item.Type = ClassData.GetPreferredArmor( cls );
    if ( effects.HasFlag( CraftIngredientEffect.TierGuarantee ) )
        item.Tier = MaxTier;
    if ( effects.HasFlag( CraftIngredientEffect.PropertyReroll ) )
        RegenerateItemProperties( item );
    return item;
}
```

Game: namicry.gacha_crawler — `Code/Data/CraftIngredientData.cs`, `Code/Data/ItemGenerator.cs`.

### "Server-as-truth" anti-pattern (sino.s_sino)

sino.s_sino (a casino game) carries **no gacha math in the s&box layer at all**. Every roll, every
balance change, every payout lives on an external Node/WebSocket server. The s&box client only
renders what the server pushes. Balance is held as **cents strings** (never floats), cached locally
purely for instant boot display, and always overwritten by the server's first `init` message.

The lesson is not to copy this architecture blindly, but to understand when it applies:
- If your gacha involves real or cross-session stakes (leaderboard-ranked inventory, PvP case
  battles), rolling on the s&box host with `[Rpc.Host]` is the minimum; an external server removes
  the trust problem entirely.
- For cosmetic-only or single-player gacha, `Random.Shared` on the local client is fine.
- The explicit pattern from this game: `balance_cache.txt` (cosmetic local cache, regex-validated)
  + server's first `init` overwrites it. Use this if you ever show a balance before a server
  connection is established.

Game: sino.s_sino — `Code/UI/BalanceHud.razor`, `Code/Core/WebSocketManager.cs`.

---

### Updated "read these games" pointer

For gacha / loot / weighted-rarity systems, open the real code in this order:

1. **artisan.darkrpog** — clean baseline `LootboxRoller.Roll` + `GameResource` defs
2. **lavagame.multis_cases** — EV normalization, rank-biased odds, wear/float modifier, collection
   album, jackpot pending-win-ACK, host-authoritative case-battle
3. **namicry.gacha_crawler** — layered per-source curves, pity, class-bias, ingredient-flags,
   spin animation in C# not CSS; also the ascending-threshold anti-pattern at `:912`
4. **facepunch.ss2** — reflection-driven catalog, synergy gates, per-pick reweighting, cloud-stats
   personalization, static-registry hydration on clients
5. **treehaven.sdiver** — `[Sync,Change]` host-authoritative `TreasureContainer`, per-entry caps
6. **stepdev.xtrem_road** — rod-tier/zone rarity curve, CS:GO strip with `WinIndex=34`
7. **vault77.chop_the_forest** — timed free case, charge recharge persisted as Unix seconds
8. **clearlyy.s_miner** — daily/cooldown lootbox
9. **master.digging_simulator** — virtual/lazy depth-weighted ore spawning
10. **sino.s_sino** — "server-as-truth" contrast case (no gacha math in s&box layer)
