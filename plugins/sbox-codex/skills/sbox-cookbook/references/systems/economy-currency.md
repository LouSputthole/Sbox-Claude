# Economy & Currency

How to build money/coins/credits, shops, upgrades, lootboxes and persistence in modern s&box — host-authoritative where it matters, client-trusted where it's cheap.

## What it is / when you need it

Any game with a spendable resource: a wallet (`Money`/`Gold`/`coins`/`BP`), ways to **earn** it (sales, kills, pickups, quotas, passive income), ways to **spend** it (shops, upgrades, gambling, resurrection), and a way to **persist** it. The hard parts are: who is allowed to change the number (trust model), how upgrade costs scale, and how the balance survives a reload. Pick your trust model first — it dictates everything else.

## Canonical approach: host-authoritative wallet

The dominant pattern across the mined games. The balance lives in ONE place, only the host writes it, clients see a read-only replica. Used by chop_the_forest, shop_manager, everything_must_go, darkrpog, GASTROTOWN.

```csharp
public sealed class ShopFunds : Component
{
    public static ShopFunds Current { get; private set; }
    [Property] public float StartingMoney { get; set; } = 500f;

    // Host owns the value; clients get a read-only replicated mirror.
    [Sync( SyncFlags.FromHost )] public float Money { get; set; }

    protected override void OnStart()
    {
        Current = this;
        // Claim host ownership once. After this clients are IsProxy=true.
        if ( Networking.IsHost && !GameObject.Network.Active )
        {
            Money = StartingMoney;          // overwritten later by your save
            GameObject.NetworkSpawn();
        }
    }

    public void AddMoney( float amount, string reason = "" )
    {
        if ( IsProxy || amount <= 0f ) return;   // mutators no-op on clients
        Money += amount;
    }

    public bool SpendMoney( float amount )       // returns the affordability gate
    {
        if ( IsProxy || amount <= 0f ) return false;
        if ( Money < amount ) return false;
        Money -= amount;
        return true;
    }
}
```
(enifun.shop_manager: Code/Economy/ShopFunds.cs:18,36,86)

Three rules that make this safe:

1. **`[Sync(SyncFlags.FromHost)]`** means clients literally cannot write the field — the value only flows host→client. Use a `private set` if the property is on the player itself (artisan.darkrpog: Code/Player/Player.Roleplay.cs:9).
2. **Every mutator early-returns on a proxy.** Guard with `if (IsProxy) return;` (ShopFunds) or `if (!Networking.IsHost) return;` (darkrpog Code/Player/Player.Roleplay.cs:22). A client UI button must therefore call an `[Rpc.Host]` method, never `SpendMoney` directly.
3. **Validate-then-apply, never throw.** `TryTakeMoney` checks affordability + overflow *before* mutating and returns `bool`; mandatory costs use a separate `ForceSpend` that allows going negative (ShopFunds.cs:106). For richer outcomes return a result enum + a `DescribeFailure(enum)→string` mapping instead of bools (artisan.darkrpog: Code/Economy/Bank/BankingService.cs:13).

```csharp
// darkrpog — host gate + overflow guard + immediate save, returns bool
public bool TryTakeMoney( long amount )
{
    if ( !Networking.IsHost || amount < 0 || !CanAfford( amount ) ) return false;
    Money -= amount;
    SaveRoleplayData();
    return true;
}
```
(artisan.darkrpog: Code/Player/Player.Roleplay.cs:35; overflow-check on give at :28)

### Client-initiated change: the request → apply → confirm triad

When a client must drive a change (buy from a shop, pick a pet), it sends an `[Rpc.Host]` request; the host **re-validates the caller and re-clamps the number** (never trust the wire value), applies, and optionally `[Rpc.Owner]` confirms the authoritative result back.

```csharp
[Rpc.Host]
private void RequestBuy( string upgradeId )
{
    // re-validate identity AND re-derive cost host-side
    if ( Rpc.CallerId != Network.OwnerId ) return;
    int cost = ShopBalance.GetCost( upgradeId, CurrentLevel( upgradeId ) );
    if ( cost < 0 || !SpendMoney( cost ) ) return;     // host is the gate
    ApplyUpgrade( upgradeId );
}
```
(pattern from vault77.chop_the_forest: Code/Player/PlayerProgression.cs:922/941 RpcRequest/RpcConfirm)

### Shops & upgrades: the geometric cost curve

The canonical idle/tycoon pricing. A per-line config + `cost = BaseCost * mult^level`, returning a sentinel (`-1`) at max. Drop-in reusable.

```csharp
[Serializable]
public class UpgradeConfig
{
    [Property] public int BaseCost { get; set; } = 100;
    [Property] public float CostMultiplier { get; set; } = 1.5f;
    [Property] public int MaxLevel { get; set; } = 5;
    [Property] public float BaseStat { get; set; } = 50f;
    [Property] public float StatPerLevel { get; set; } = 15f;
}

public int GetCost( UpgradeConfig c, int level )
{
    if ( level >= c.MaxLevel ) return -1;                 // -1 == maxed
    return (int)(c.BaseCost * MathF.Pow( c.CostMultiplier, level ));
}
```
(master.digging_simulator: Code/ShopTerminal.cs:9,49,63). On buy, decrement money, increment level, then **re-derive the live stat onto the target component** (`drill.DigRadius = c.BaseStat + level*c.StatPerLevel`) — and re-apply ALL stats on load (ShopTerminal.cs:81). For larger trees, hold a `readonly struct[] Definitions` indexed by level with `Get(level)` that `Clamp`s to `[1,MaxLevel]` and `TryGetNext(cur, out def)` (vault77.chop_the_forest: Code/Player/AxeUpgradeBalance.cs:21,52); `MaxLevel` derives from `Definitions.Length` so adding a row "just works."

### Sending purchases over the wire: Ids, never resource refs

`GameResource` references do NOT round-trip across RPCs. Send the resource **Id (string)** + quantity, and reconstruct on the host via `ResourceLibrary`:

```csharp
[Rpc.Host]
private void TryPurchaseOrder( RestaurantComponent restaurant, Dictionary<string,int> orderData )
{
    foreach ( var (id, qty) in orderData )
    {
        var resource = ResourceLibrary.GetAll<DeliverableResource>()
            .FirstOrDefault( r => r.Id == id );
        if ( resource == null ) { Log.Warning( $"Unknown id {id}" ); continue; }
        order.SetQuantity( resource, qty );
    }
    if ( order.Cost() > restaurant.Money ) return;       // re-check host-side
    restaurant.Money -= order.Cost();
}
```
(thefancylads.restaurant_dev: Code/Common/Economy/RestaurantShop.cs:21,29)

## Variations seen across games

- **Two-tier "trust the client" economy** — client deducts its own balance optimistically, sends `[Rpc.Host]` with the declared cost, host rolls the outcome and `[Rpc.Broadcast(NetFlags.HostOnly)]`s authoritative state back; on any host validation failure the host MUST `SafeRefund(declaredCost)` (the client already debited). (lavagame.multis_cases: Code/Game/Core/GameManager.cs:1084-1243)
- **Cloud-stats-as-database** — no server DB at all; currency lives in `Sandbox.Services.Stats` keyed strings, mutated via `Stats.Increment` / `SetValue` then `Stats.Flush()`, rehydrated on spawn. Earned through `[Rpc.Owner]` because **the host cannot write another player's Steam stats** (facepunch Blind: Code/Player/Player.cs:786; playbtg.elevator: Code/Actors/ElevatorPlayer.Score.cs:43). Critical semantic: `SetValue` does NOT overwrite — `.Sum` accumulates every write; use Increment+`.Sum` for counts, SetValue+`.LastValue` for "current selection / claim flags."
- **Client-local JSON save** (co-op / non-competitive) — `FileSystem.Data.WriteJson/ReadJson("save.json")` of a POCO keyed by SteamId; trivially editable, fine for solo. Mark dirty on mutation, write on a `TimeSince` interval, force-save on `Game.IsClosing`/`OnDestroy` (master.digging_simulator: Code/SaveManager.cs:18; stepdev.xtrem_road: Code/Persistence/PlayerSaveService.cs:95,177; goders.natural_disaster_survival: Code/globals/DataManager.cs:94).
- **Signed/versioned anti-tamper save** — clamp every field on load (`ValidateAndSanitize`), then an FNV-1a signature over a **version-conditional** canonical payload so old saves still verify (vault77.chop_the_forest: Code/Player/LumberSteamProgressSave.cs:434,957). Deterrent only, not crypto.
- **Lootbox / gacha** — `RewardDefinition[]` with integer `Weight`; cumulative-weight roulette (roll `1..totalWeight`, walk a cursor). Free-case charges recharge over real time tracked as **Unix seconds** (survives reload), not `TimeSince` (vault77.chop_the_forest: Code/World/CaseRewardBalance.cs:87,140; emg: Code/Shop/Shop.cs:614). `System.Random.Shared` is fine for cosmetic rewards.
- **Provably-fair gambling** — for anything with economic value, do NOT use `System.Random`. Commit-reveal SHA256 (`System.Security.Cryptography` is whitelisted) with **rejection sampling** to kill modulo bias, kept as engine-agnostic POCO C# (vault77.chop_the_forest: Code/Gambling/ProvablyFairRng.cs:126,193).
- **Passive / idle income** — `PassiveIncome => slots.Sum(i => MathF.Sqrt(i.SellValue) * RATE)`; accrue `Unclaimed += income * Time.Delta` each frame, player claims manually (lavagame.multis_cases: Code/Game/Core/GameManager.cs:399). `Sqrt` compresses whale advantage; note offline time is NOT simulated unless you diff timestamps on load.
- **Escalating quota economy** — daily quota from a `.runset` `List<int>`, scaled by player count; split deposits into normal vs over-quota (bonus multiplier) and carry excess forward. Keep "progress toward quota" and "spendable bank" as **separate buckets** (treehaven.sdiver: Code/Gameplay/GameMode/ExpeditionMode.cs:85).
- **Capped inventory as currency** — `Dictionary<string,int>` + `int Money`; `AddResource` returns `false` when full so pickup logic stays in the item and capacity policy stays in the inventory; fire an `Action OnInventoryChanged` for UI (master.digging_simulator: Code/PlayerInventory.cs:15). Local `Action` is fine; add `[Sync]` for MP.
- **HTTP backend wallet** — use `Sandbox.Http.RequestStringAsync` / `CreateJsonContent`, NOT `System.Net.HttpClient` (whitelist-blocked). Non-host clients often have no direct internet, so relay through the host (vault77.chop_the_forest: Code/game/BackendClient/HttpBackendTransport.cs:27).
- **Payday-on-interval (RP salary).** A host-only ticker pays each player a salary keyed to their current job on a fixed cadence, instead of (or alongside) a harvest loop. Lives as a module that depends on the Player + Job modules; the interval ticker fires `TryEarn(salary)` per online player on the host (lowkeynetworks.newrp: `Code/modules/economy/EconomyModule.cs` + `EconomyTickerComponent.cs`). The clean part is the **separation**: the *what* (salary by job) is data on the Job, the *when* (interval) is the ticker, the *apply* is the wallet — three seams, no monolith.
- **Procedural OHLC / candle market (crypto-trading sim).** When the "currency" is a tradeable asset whose price moves on its own, drive it with a server-authoritative **candle engine**: a ring-buffer of OHLC candles (e.g. 3600), a look-ahead future-price queue, and **regime phases** (Natural / Squeeze / Spring / Hook / Resistance / Exhaustion) each with its own volatility / drift / shock / mean-revert params, plus a progressive mean-reversion attractor and event-driven trajectories with fake-pullbacks. Candle history syncs as a `[Sync] NetList<float>`, and a `RecoverFromHostMigration` rebuilds it on a host change (lavagame.sandmoney_: `Code/Core/MarketManager.cs`). Pair with exponential idle upgrade pricing `baseCost * growth^(level-1)` + tiered unlocks (`UpgradeSystem.cs`) and a peppered, clock-rollback-detecting daily-reward integrity hash (`Persistence/DailyRewardSecurity.cs`). Heavyweight — only reach for it if price *discovery* is the game.

## Gotchas

- **A `[Sync]` int with a public setter is exploitable** — a malicious client can write it. Only `SyncFlags.FromHost` (or host-side validation) protects it. playbtg.elevator's coin balance is public-set `[Sync]` and only the death-drop clamps it (Code/Actors/ElevatorPlayer.Score.cs).
- **Mutators silently no-op on proxies.** If a UI "Buy" button calls `SpendMoney` directly on a client, nothing happens and no error fires — route through `[Rpc.Host]`.
- **`NetworkSpawn()` once, host-only, guarded** by `!GameObject.Network.Active`; set starting money *before* it, then let the save overwrite.
- **Re-clamp on BOTH the host RPC and any owner-confirm** — never trust the incoming number, even your own confirm echo.
- **`NetList<T>`/`NetDictionary<K,V>` replicate from host only** — after a host-side mutation you must explicitly rebuild them, and client-side read APIs must fall back to the synced mirror (`Networking.IsHost ? liveList : syncedList`) or proxies read stale/empty (emg.everything_must_go: Code/Shop/Shelf.cs).
- **Trigger rewards double-fire** — a `PlayerController` exposes multiple colliders, so an `OnTriggerEnter` reward pays N times; debounce with a `HashSet<Guid>` of rewarded root Ids (lavagame.multis_cases: Code/Game/Obby/ObbyRewardButton.cs).
- **Steam `Stats.SetValue` accumulates via `.Sum`** — three `SetValue(1)` gives Sum=3. Ownership flags = write-once Increment (never decrement or you revoke a paid item); current-selection/claim flags = SetValue + `.LastValue` (facepunch Blind: Code/Player/Player.cs).
- **Geometric costs overflow int** fast — keep currency `int`/`long` but clamp (chop_the_forest clamps to 1_500_000_000) or use `long` (darkrpog).
- **Client-saved cooldown timestamps are editable** — a daily-lootbox cooldown compared against a client `DateTime.UtcNow` is trivially bypassed; gate server-side for anything that matters (clearlyy.s_miner: DailyLootbox.cs).
- **Don't lose materials on a failed craft/fusion** — generate the output into a copy first, consume ingredients only after success (namicry.gacha_crawler: Code/GameManager.cs:1913).

## Seen in

- **vault77.chop_the_forest** — host-authoritative `[Sync(FromHost)]` economy, data-driven balance tables, signed versioned save, free-case gacha, provably-fair gambling, HTTP backend.
- **enifun.shop_manager** — `ShopFunds` singleton wallet, checkout queue, `Sandbox.Services.Stats` leaderboard push.
- **emg.everything_must_go** — host-authoritative sim with `NetList`/`NetDictionary`, roguelite level-up rewards, slot-machine gacha, navmesh checkout queue.
- **master.digging_simulator** — capped inventory, formula-driven upgrade shop (geometric curve), JSON terrain-diff save.
- **artisan.darkrpog** — `long` wallet with overflow guards + result-enum bank service, daily-login streak.
- **thefancylads.restaurant_dev** (GASTROTOWN) — host-authoritative `Money`, Id-over-RPC procurement shop, customer-pays-on-eat income.
- **playbtg.elevator** — `[Sync]` coins with world pickups + spawner, confirm-gated 3D shop, cloud-stat XP/streak.
- **stepdev.xtrem_road** — multi-container inventory, tiered vendor, dirty-flag JSON autosave, services leaderboard.
- **lavagame.multis_cases** — idle passive income, host-validated case-battle/jackpot gambling, live CS2 case API.
- **clearlyy.s_miner** — daily lootbox, networked slot machine, prestige/rebirth/ascension tree.
- **goders.natural_disaster_survival** — client-local cash + meta-upgrade tree, look-at-to-buy 3D shop.
- **treehaven.sdiver** — escalating quota economy with carry-over + over-quota bonus.
- **namicry.gacha_crawler** — `[Flags]` crafting/fusion, buy/sell/upgrade/resurrection sinks.
- **lowkeynetworks.newrp** — module-based payday economy: `EconomyModule` + `EconomyTickerComponent` host-only salary-by-job on an interval.
- **lavagame.sandmoney_** — procedural OHLC/candle market with regime phases (`MarketManager.cs`), exponential idle upgrade pricing (`UpgradeSystem.cs`), peppered clock-rollback-safe daily-reward integrity (`DailyRewardSecurity.cs`), hash-verified anti-tamper save (`PlayerSaveStore.cs`).
- **dimmies.terryspapers** — dictionary-driven mood/economy progression with promotion + permadeath.
- **facepunch.Blind** — currency entirely on Steam Stats (Increment/Sum vs SetValue/LastValue).
- **vidya.terry_games** — derived money payout + money-particle burst + Steam achievements.

## Verify live

Reflection is authoritative for the installed SDK. Confirm members before you write code: `mcp__sbox__describe_type SyncFlags`, `mcp__sbox__search_types ResourceLibrary`, `mcp__sbox__describe_type GameObject` (for `NetworkSpawn`/`Network.Active`/`IsProxy`), and `mcp__sbox__describe_type Sandbox.Services.Stats`.

Cross-link: use **sbox-api** for exact type/member signatures, and **sbox-build-feature** for the screenshot-driven build-and-iterate loop when wiring the shop UI.

## Corpus refresh (2026): more reference implementations

Net-new patterns from a fresh pass over eight more economy games (incl. the official **facepunch.fair** park tycoon and **facepunch.ss2** survivor roguelite). All compose with the host-authoritative wallet above — these are the *variations* and *cleaner seams* worth lifting.

### Reason-tagged transactions = a self-instrumenting economy

facepunch.fair's money authority passes a `reason` string through every mutator and auto-generates a per-reason Steam stat key. New content (a ride, a shop) gets its own analytics + goal-eligible stat with **zero extra wiring**.

```csharp
public bool TakeMoney( int amount, string reason = "Other" ) {
    if ( Money < amount ) return false;                           // affordability gate
    Stats.Increment( "money_spent", amount );
    Stats.Increment( $"money_spent.{reason.ToIdentifier()}", amount ); // dynamic key
    Money -= amount; return true;
}
```
(facepunch.fair: `Park/ParkManager.cs`; `ToIdentifier()` normalizes names to safe keys in `Utils/StringExtensions.cs`). `Money/AdmissionFee/Rating/DailyIncome` are all `[Sync(SyncFlags.FromHost)]` with `private set`. enifun.shop_manager threads the same `reason` through `AddMoney/SpendMoney/ForceSpend` into a UI-only `RecentTransactions` ring buffer (NOT synced/saved). **Pattern: thread one `reason` arg → free analytics + a transaction log + goal triggers.**

### Derived running costs + a daily-upkeep tick (don't store what you can compute)

Tycoons drain money on a clock. facepunch.fair computes `DailyOperationalCosts` *live* (`building.Cost/5` per building + `2` per path + `guestTarget*5`) on `ITimeOfDayEvents.OnNewDay()`, deducts it plus staff `Wages`, then broadcasts a notification — costs are derived, never stored, so they can never desync from the world. Refunds round (`SnapToGrid(10)`) and pay back `Math.Min(cost*3/4, cost)` on demolish. enifun.shop_manager's `LoanManager` is the debt mirror: 4 data-defined tiers, one loan at a time, **daily auto-repayment via `ForceSpend` on day-rollover** (the spend path that's *allowed* to go negative). The clean rule across both: keep **earned state** saved, keep **rates/costs** in code/data and recompute.

### Centralize balance in a pure static class (the cleanest tuning sheet)

freddo.scoops puts every price/rate/formula in `Code/Econ.cs` — a `static class` of `const`s + pure functions, zero gameplay logic, retunable without touching systems.

```csharp
public static int FlavourCost( int i ) => 120 + (i - 1) * 130;            // unlock ramp
public static int VanCost( int owned ) => VanBaseCost + owned * VanCostPerOwned;
public static float TruckSellInterval( int lvl ) => MathF.Max( 3.5f, 8f - (lvl-1)*2f );
public static string Money( int amount ) => "$" + amount.ToString( "N0" );
```
(freddo.scoops: `Code/Econ.cs`). despawn.murder (`Systems/Rounds/XpConfig.cs`) and facepunch.ss2 do the same for XP/awards — `const` flat awards + additive bonus fractions, `Final = Base × (1 + Σ qualifying bonuses)`. **The whole economy is one editable file the non-coder can safely retune.** Pairs with the geometric-curve helper already above.

### Polymorphic currency: one product, multiple cost types

thefancylads.farm_land generalizes "what does this cost" into a strategy so a single product can cost "$5 **and** 3 wheat", and the same `Shop.TryPurchase` flow buys items *and* upgrades uniformly.

```csharp
public abstract class Currency {                 // Common/Economy/Currency.cs
    public abstract bool CanAfford( Player p, int qty );
    public abstract void Charge( Player p, int qty );
    public abstract string GetDisplay();
}
// CashCurrency (wallet) + ItemCurrency (consume N of an item)
// Product.Currencies is a LIST → Product.CanAfford = Currencies.All( c => c.CanAfford(p, qty) )
```
(farm_land: `Currency.cs`, `Shop.cs`, `IPurchasableResource.TryApplyPurchase`). Sell-side mirrors it: `ItemResource.SellPrice` is a category `switch` (crops/fish full, tools 50%, else 75%) and `Market.TrySellItems` re-verifies the seller actually owns the qty before crediting. **Reach for this the moment a second currency or a "costs items not cash" sink appears** — cheaper than bolting a parallel path on later.

### Dynamic price-elasticity demand curve (sim-driven income, not idle timers)

enifun.shop_manager's income isn't a passive generator — AI shoppers buy based on where the player's set price sits in a margin band. The demand curve is a tunable control-point table:

```csharp
// actualMargin = (price - wholesaleCostPerUnit) / wholesaleCostPerUnit
// <= MinMargin → 100% buy; >= MaxMargin → 0% buy; piecewise-lerp between
static (float t, float chance)[] BuyChanceCurve = { (0f,1f), (0.5f,0.6f), (1f,0f) };
```
(enifun.shop_manager: `Code/Economy/PriceManager.cs`). Rich customers use a separate `GetRichBuyChance` (100% until a hard ceiling). Prices persist **per-product-ID** (decoupled from shelves) and the whole table broadcasts to clients as one `[Sync] string SyncedPrices` shaped `"id:price,id:price"` — avoids a synced Dictionary. **This is the seam between `tycoon-idle` and a real management sim:** the income rate is an emergent function of a price you set, not a number you upgrade.

### The "currency is a physics object" economy (selling as a collision event)

stellawisps.lumberyard keeps almost nothing as an abstract counter until the final `Money` add: a chopped branch is a rigidbody you grab, drop on a conveyor (`SurfaceVelocity`), and trigger volumes transmute log→plank→paper→money (`PlankCutter` keyed by a `CutterType` enum). Selling is **decentralized across many tiny `Sell()` methods**, not one shop service:

```csharp
// Code/Trees/Wood.cs  — [Rpc.Owner]
public void Sell() { LumberPlayer.Local.AddMoney( Value ); GameObject.Destroy(); }
// fired three ways: SellZone.Interact (button, scans BoxCollider.Touching),
// SellPoint.OnCollisionStart (item lands on the pad), SellSucker (vacuum ApplyForce → pad)
```
Transmute multipliers live entirely on `.tree` GameResources (`PlankMultiplier=2`…), so designers tune the chain by editing assets. intercrusstudio.sneguborka does the same with terrain volume: reward = `carvedVolumeNormalized × cellAreaM² × tier.RewardPerM2 × prestigeMultiplier`, with a `MinRewardPerStamp` floor so a tiny carve still pays, and the **inventory slot is consumed first** (full bag = $0, forcing a deposit trip). **Lesson: "earn" and "sell" can be collision/volume events welded to physics — a different loop from click-building-counter-ticks.**

### Three host-only spend semantics, made auditable

intercrusstudio.sneguborka splits the wallet into three named mutators so the "never silently take progress away" invariant stays reviewable, and backs the reward path with **warn-once latches** so "I'm digging but money isn't moving" is observable instead of silent:

```csharp
// Player/PlayerWallet.cs — [Sync(SyncFlags.FromHost)] long Money
public void Grant( long a )      { if ( a <= 0 ) return; Money += a; }   // reward
public bool Charge( long a )     { /* host-side affordability even though UI greys */ }
public void SetMoney( long a )   { Money = a; }                         // prestige wipe ONLY
```
This is the same shape as shop_manager's `AddMoney/SpendMoney/ForceSpend` (above) but with an explicit prestige-only setter pulled out — a good discipline when you have a reset/prestige loop. Note `long`, not `int` (long sessions overflow int fast).

### Bit-packed upgrade levels (when many small ladders share a save)

sneguborka stores tool-upgrade levels packed into 3 `ulong`s — 16 tool slots × 4 bits each (so `MaxLevelPerBranch ≤ 15`), three branches mapped to visible stats (Cooldown→SPEED, Range→WIDTH, Power→POWER). Ladders are `.upgrd` GameResources (`{Level, CostMoney, Delta}` lists); a missing/short stage list reads as "branch maxed" so a half-authored config never crashes the UI. **Only reach for bit-packing if you have dozens of tiny ladders to persist** — otherwise the data-driven `UpgradeConfig` curve above is plenty.

### Transactional upgrade purchase with rollback

lavagame.sandmoney_ makes a purchase atomic: deduct first, run the effect in a `try`, **refund on exception**, then commit + force a critical (un-throttled) save so an upgrade is never lost to the save-interval timer.

```csharp
public bool TryApplyUpgradePurchase( int cost, Action applyEffect ) {
    if ( !Owner.DeductMoney( cost ) ) return false;
    try { applyEffect(); } catch { Owner.AddMoney( cost ); return false; }  // rollback
    UpgradeRevision++; Network.Refresh(); RequestCriticalSave(); return true;
}
```
(lavagame.sandmoney_: `UpgradeSystem.cs`; effect values are `switch` expressions on the stored int level, so balance edits never need a save migration — `GasReduction => GasLevel switch { 5 => 0.55f, ... }`). Same "consume only after success" rule as the gacha-fusion gotcha already noted.

### Meta-progression as ONE proven file (currency + shop + save + versioning)

facepunch.ss2 bundles what the cookbook treats separately into a single serialized POCO, which is the right altitude for a roguelite/arcade meta layer:

```csharp
// ProgressData → progress.json via FileSystem.Data.ReadJsonSafe<T>(path, fallback) / WriteJson
// holds: coins, owned shop items, equipped/upgraded gems, selected loadout, quest+achievement state
// dirty-flag + interval autosave: AddCoins sets _isDirty; Tick() saves if _timeSinceLastSave > 5s;
// reward collection saves immediately.
```
The shop is a `ShopItemDef` struct catalog assembled from partial-class builders and **duplicate-ID-checked at build** (`Log.Error`). **Category-gated unlocks with zero extra state:** `RequiredPurchases=N` means "own N items in this category first." Per-level gem upgrades read a `UpgradePrices[]` cost ladder. (facepunch.ss2: `ProgressData`/shop builders). For the upgrade *engine* under it — a per-source Set/Add/Mult stat-modifier stack — see **progression-upgrades**; that's the spine ss2's 300+ perks compose through (`Player.Modify(this, PlayerStat.X, val, ModifierType.Mult)`).

### In-round / consumable currency (clues, ammo, time)

despawn.murder proves the wallet doesn't have to be cash or persistent: **clues are the currency** (`Client.CluesCollected`), spent at a per-owner powerup store spawned to each player at round start. The standout is that **price comes from a ConVar, not the item**, so a server owner re-tunes the economy live without touching assets:

```csharp
[Rpc.Host] void PurchaseHost( string itemKey ) {       // EquipmentShopManager.cs
    if ( !PowerupEnabled || !Known(itemKey) || caller.Pawn is null ) return;
    var item = ItemComponentFactory.Make( itemKey );    // "radar" => new Radar{...}
    int price = GameConVars.GetPowerupPrice( itemKey, fallback );   // live, not baked
    if ( !item.CanPurchase(caller.Pawn) ) return;
    item.OnPurchase( caller.Pawn ); caller.CluesCollected -= price;
}
```
(despawn.murder: `Systems/EquipmentShop/`). The string-key→behavior switch (`ItemComponentFactory`) plus per-item classes is the same data-key→spawned-behavior dispatch as ss2's catalog. **ConVar-as-balance-DSL** (`"radar=1,silent=2,..."` parsed at purchase time) is a clean live-ops knob for any round game.

### Engine-as-renderer: hold the ledger off the s&box host entirely

sino.s_sino is the corpus's cleanest "server-as-truth" economy: **no money math in s&box at all.** Balance lives on an external Node/WebSocket backend as **cents in decimal strings** (never floats, never an int a gambler's bankroll could overflow); the client only displays a balance it was pushed.

```csharp
// Code/UI/BalanceHud.razor — the single client mirror
_subs.Add( mgr.On( "balance", msg => UpdateBalance( msg.balance ) ) ); // string cents
// balance_cache.txt (FileSystem.Data) seeds the HUD on boot so $0 doesn't flash —
// regex-validated ^\d+$, treated as COSMETIC; the server's first 'init' overwrites it.
```
This sidesteps the entire `[Sync]`/`[Rpc.Host]` re-validation discipline by simply not trusting the host with money. **Use it when money has real economic value (gambling, trading) or must survive across servers** — the save becomes a corrected cache, never the authority. (Contrast lavagame.sandmoney_, which keeps the same trading-sim genre fully host-authoritative + locally hash-signed; pick backend-truth only if you can run the server.) See **save-persistence** / leaderboards for the WebSocket reconnect + `Services.Auth.GetToken` plumbing.

### Cosmetic juice: the debounced value floater

A tiny reusable bit from stellawisps.lumberyard: instead of flickering a "+N" per sale, accumulate into a `RecentMoneyChange` and reset a `TimeSinceMoneyChange`; if >5s stale, zero the accumulator first so rapid sells stack into one growing "+1,234". The HUD hashes `(Money, RecentMoneyChange, TimeSinceMoneyChange > 5f)` — the `> 5f` bool-ifies the timer so the panel re-renders **once** when the floater should vanish, not every frame (`LumberPlayer.cs`, `MoneyHud.razor`). facepunch.fair's `MoneyEffect.Broadcast(pos, text, color)` is the worldspace version. Pairs with any `AddMoney` path for instant game-feel.

### Read these games (economy)

Already-cited above and still the deepest: **vault77.chop_the_forest** (signed save + provably-fair gambling), **lavagame.multis_cases** (two-tier trust + refund-on-fail). New this pass, in rough order of reusability:

- **enifun.shop_manager** — `ShopFunds` three-spend-path (canonical, top of this doc) + the `PriceManager` price-elasticity demand curve + day-seeded RNG + `LoanManager` debt sub-economy.
- **facepunch.fair** (official) — reason-tagged self-instrumenting transactions, derived daily-upkeep loop, two-phase grid placement that spends host-side, chunked buy-land zones.
- **facepunch.ss2** (official) — meta-progression as one POCO (currency+shop+save+versioning), category-gated unlocks, gem upgrade ladders; the stat-modifier engine lives in **progression-upgrades**.
- **thefancylads.farm_land** — polymorphic `Currency` (cash + items per product), uniform item/upgrade shop flow, category sell-price switch, daily-seeded barter vendor.
- **lavagame.sandmoney_** — double-for-cash/float-for-asset with NaN guards, transactional upgrade-with-rollback + critical-save, bracket-based prestige currency (already in "Seen in" for its candle market).
- **stellawisps.lumberyard** — physics-object resource economy, sell-as-collision-event, the debounced value floater.
- **intercrusstudio.sneguborka** — cleared-volume reward model, named three-mutator wallet (`Grant/Charge/SetMoney`) with warn-once latches, bit-packed upgrade levels, accelerating consolidated-payout deposit drain.
- **klibatocorp.phenodex** — time-compressed UTC-tick offline accrual (1 IRL day = N game-seconds), static-`IsOpen` vendor family, backend-authoritative breeding with local fallback.
- **despawn.murder** — in-round consumable currency (clues), ConVar-as-live-price DSL, data-key→spawned-behavior item factory.
- **sino.s_sino** — engine-as-renderer: ledger on an external WS backend in cents-strings, client is a corrected display cache.
