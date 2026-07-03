# Shop / Vendor System

How to build an in-game shop where a player walks up to a kiosk, spends currency, and gets an upgrade, item, or stock delivery — in modern s&box (GameObject/Component/Scene).

## What it IS / when you need it

A shop-vendor is three loosely-coupled pieces that almost always ship together:

1. **A currency holder** — an `int`/`float` on the player or a manager, mutated through guarded `Spend`/`Add` methods.
2. **A catalog** — the list of buyable things (upgrades, items, skins), with a **cost curve** and an **effect** per entry.
3. **A vendor surface** — proximity detection + a "use" interaction that opens UI and runs the purchase.

You need it for tycoon/idle upgrades, roguelite reward shops, cosmetic stores, procurement (buy stock/ingredients), and sell-back sinks. Across the 27 mined games this is the single most-repeated system, so the patterns below are battle-tested.

## Canonical modern approach (recipe)

### 1. Currency holder with a guarded spend

Keep money in one place and never let callers touch the field directly — go through a method that returns `bool` so the caller knows if the purchase happened.

```csharp
public sealed class Wallet : Component
{
    [Sync] public int Money { get; set; }            // [Sync] only if multiplayer

    public bool TrySpend( int cost )
    {
        if ( cost < 0 || Money < cost ) return false;
        Money -= cost;
        return true;
    }
    public void Add( int amount ) => Money += amount;
}
```

In multiplayer, money is **host-authoritative**: a client check is advisory, the host re-validates (digging_simulator keeps a plain `int Money` for single-player; restaurant_dev's `Restaurant.Money` is `[Sync(SyncFlags.FromHost)]` and only the host decrements it — `RestaurantShop.cs:48-61`).

### 2. Catalog with a geometric cost curve

The canonical idle/tycoon price is `BaseCost * Multiplier^currentLevel`. Expose the tuning as a `[Serializable]` config so designers edit it in the inspector.

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

public int GetCost( int currentLevel, UpgradeConfig c )
    => currentLevel >= c.MaxLevel ? -1                       // -1 sentinel = maxed
     : (int)(c.BaseCost * MathF.Pow( c.CostMultiplier, currentLevel ));
```

Verbatim from digging_simulator (`ShopTerminal.cs:9` config, `:49-66` the `MathF.Pow` formula returning `-1` at max). Note `MathF` works in s&box (`Math.Pow` is also fine).

### 3. Buy: spend, increment, apply the effect to a live component

```csharp
public void BuyUpgrade( Wallet wallet, DrillTool drill )
{
    int cost = GetCost( LevelShovel, ShovelConfig );
    if ( cost < 0 || !wallet.TrySpend( cost ) ) return;      // maxed or broke

    LevelShovel++;
    drill.DigRadius = ShovelConfig.BaseStat + LevelShovel * ShovelConfig.StatPerLevel;
}
```

The upgrade writes the derived stat straight onto the relevant live component — `DigRadius`, `MaxItems`, `MaxBattery`, `JetpackLevel` (digging_simulator `ShopTerminal.cs:81-128`). **Consequence:** on load you must re-run every stat application, not just restore the level numbers (its `RestoreLevels()` re-applies all four, `ShopTerminal.cs:177-211`).

### 4. Vendor surface: proximity + use → open UI → purchase

Poll nearby vendors each frame and expose a bool the HUD reads; open on "use".

```csharp
protected override void OnUpdate()
{
    if ( IsProxy ) return;                                   // local player drives detection
    foreach ( var shop in Scene.GetAllComponents<ShopKiosk>() )
    {
        bool near = WorldPosition.WithZ(0).Distance( shop.WorldPosition.WithZ(0) ) <= shop.Radius;
        shop.PlayerNearby = near;
        if ( near && Input.Pressed( "Use" ) ) shop.Open( this );
    }
}
```

This 2D-distance proximity scan is xtrem_road's pattern (`PlayerInventory.cs:553,652` `IsNearVendor`/`GetNearbyRodShop`) and Blind's (`ShopKiosk.cs:70`). Detection runs on the **local non-proxy** player so it stays reliable in MP.

### 5. Confirm + purchase (with an Action hook for non-item buys)

```csharp
private void TryPurchase( ElevatorPlayer player )
{
    if ( player.Balance < Item.Cost ) return;
    player.RemoveCoins( Item.Cost );
    if ( Item.ShouldGiveEquipment )
        player.Components.Get<InventoryComponent>()?.GiveItem( Item );
    Item.OnPurchase?.Invoke( player );                       // upgrades/effects with no item
}
```

Verbatim from elevator (`ShopInteraction.cs:19-27`), gated behind a `ShopConfirmation.Open(item, callback)` razor dialog (`:16`). The `OnPurchase` `Action<Player>` on the definition lets one code path handle items *and* effect-only purchases.

## Notable variations

**Data-driven catalog as GameResource** — instead of C# configs, make each item a `[AssetType(Extension="shopitem")] GameResource` editable in the inspector; `ShopCatalog` scans `ResourceLibrary.GetAll<ShopItemResource>()` once and memoizes a `Dictionary<int,SkinDef>`, rebuilding only when the asset count changes (Blind `ShopItemResource.cs:21`, `ShopCatalog.cs:43`). Designer-authorable; the digging_simulator C# `UpgradeConfig` is simpler but not asset-editable.

**Host-authoritative procurement over RPC** — client calls a method that ships only resource **Ids + quantities** to `[Rpc.Host] TryPurchaseOrder`, which reconstructs from `ResourceLibrary.GetAll<T>().FirstOrDefault(r=>r.Id==id)`, re-checks `Cost() vs Money`, attempts physical delivery, *then* deducts (restaurant_dev `RestaurantShop.cs:21-69`). Never send `GameResource` refs over RPC — they don't round-trip.

**Multi-tier upgrade tables** — parallel `static readonly float[]` cost/effect arrays, one `TryBuyX` per line, effects read pull-based via getters (`GetPatienceMultiplier()`) so consumers query the manager rather than the upgrade pushing changes (shop_manager `ShopManager.cs:175,208`). Copy-pasteable but verbose (~12 near-identical blocks).

**3D "look at item to buy"** — a raycast from the eye with a `'shopitem'` tag, hovered `ShopItem` gets `OnHovered` (spin + tooltip); cost/purchase are `virtual` on a base `ShopItem` component, subclasses override (natural_disaster_survival `ShopItem.cs:58`, `PlayerStats.cs:182`). Variant: kiosk **trigger collider** + `box.Touching` instead of a raycast (multis_cases `Interactable.cs:14`, `PlayerInteractor.cs:40`).

**3D product display + confirm gate** — vendor picks N random definitions, pushes each into a `ShopDisplay` that swaps in the item's icon model/scale and fills a world-panel sign (elevator `ShopDisplay.cs:12`, `ShopController.cs:16`).

**Sell-back sink** — `SellAll`/`TryCreditSale` price items via a `Dictionary<string,int>` or a `GetSellPrice(rarity*suffix*mutation)` formula, then clear inventory and credit money (digging_simulator `ShopTerminal.cs:142`; xtrem_road `PlayerInventory.cs:652`).

**Currency on Steam Stats (no DB)** — persist currency in `Sandbox.Services.Stats` keyed strings; ownership is `Stats.Increment("owns_skin_{id}", 1)` read via `.Sum > 0`. The host **cannot** write a remote player's stats, so award through `[Rpc.Owner(NetFlags.HostOnly)]` (Blind `Player.cs:786,811`).

## Gotchas

- **`-1` is the maxed-out sentinel** from `GetCost`; callers must check `cost < 0` before `TrySpend`, or a maxed line "costs" -1 and silently grants money (digging_simulator `ShopTerminal.cs:63`).
- **Upgrades that write live-component stats must be fully re-applied on load** — restore the level *and* re-derive every stat, or loaded saves look upgraded but play un-upgraded (`ShopTerminal.cs:177`).
- **Never send `GameResource`/`Component` refs over RPCs.** Send string `Id`s and reconstruct host-side via `ResourceLibrary` (restaurant_dev `:39`). Store networked references as `[Sync] GameObject`, not typed fields (aethercore note).
- **Client-side purchase is trust-based.** elevator's `RemoveCoins` is `[Rpc.Owner]` with no host re-validation — fine for casual, exploitable competitively. For anything contested, re-check cost and decrement on the host.
- **Steam Stats accumulate.** `SetValue(1)` three times gives `.Sum == 3`; use `Increment` + `.Sum` for counts/ownership, but `SetValue` + `.LastValue` for "current selection". Never decrement an ownership stat to "refund" — it silently revokes a paid item (Blind).
- **Reserve catalog Id 0 for "default/none"** and filter it out of the catalog (Blind `ShopItemResource.cs:30`).
- **Two parallel price sources drift.** digging_simulator prices ore in `OrePrices` (on the shop) *and* `MoneyValue` (on the ore) — pick one source of truth before reusing (`ShopTerminal.cs` note).
- **Switch-on-string upgrade typing is brittle** vs an enum — a typo silently no-ops (`ShopTerminal.cs:55`).
- **Clamp tiers/levels on load** (`Math.Clamp(saved, 0, Max)`) — a schema change or corrupt save otherwise indexes past the cost table (shop_manager).
- **Negative price as a sentinel** for unbuyable/gacha-only items: respect `Price > 0` checks before treating it as purchasable (xtrem_road's Dark Rod, `Price = -1`).

## Seen in

- `master.digging_simulator` — `Code/ShopTerminal.cs` (geometric upgrade tree + SellAll; single-player, plain `int Money`) **[verified]**
- `thefancylads.restaurant_dev` (GASTROTOWN) — `Code/Common/Economy/RestaurantShop.cs` (host-authoritative procurement over RPC, ids-not-refs) **[verified]**
- `suburbianites.blindloaded` (Blind) — `Code/Economy/ShopItemResource.cs`, `ShopCatalog.cs`, `ShopKiosk.cs` (GameResource catalog + Stats currency) **[verified]**
- `playbtg.elevator` — `Code/Interaction/Interactables/ShopInteraction.cs`, `Inventory/ShopDisplay.cs` (3D product display + confirm gate + `OnPurchase` Action) **[verified]**
- `enifun.shop_manager` — `Code/Shop/ShopManager.cs` (multi-tier upgrade tables, pull-based effect getters)
- `emg.everything_must_go` — `Code/Shop/Shop.cs` (roguelite reward shop, slot-machine gacha, cash-register checkout queue)
- `stepdev.xtrem_road` — `Code/Inventory/PlayerInventory.cs`, `Code/Fishing/RodType.cs` (proximity vendor + tiered rod ladder + sell-back)
- `goders.natural_disaster_survival` — `Code/ui/ShopItem.cs`, `shop_items/*` (raycast "look-at-to-buy", virtual cost/purchase)
- `lavagame.multis_cases` — `Code/Game/Core/Interactable.cs`, `Player/PlayerInteractor.cs` (trigger-collider use-prompt stations)
- `namicry.gacha_crawler` — `Code/GameManager.cs` (buy/sell/upgrade with consumable stacking)
- `artisan.darkrpog` — `Code/Lootboxes/LootboxRoller.cs` (engine-free weighted loot table for shop restock)

## Verify live

The reflected SDK is authoritative — confirm types/signatures before coding: `mcp__sbox__search_types "GameResource"`, `mcp__sbox__describe_type "Sandbox.Services.Stats"`, `mcp__sbox__describe_type "ResourceLibrary"`. API drifts between SDK versions; reflection beats memory.

Cross-links: see the **sbox-api** skill for resolving exact type/member signatures, and the **sbox-build-feature** skill for the screenshot-driven build loop when wiring the kiosk UI.

## Corpus refresh (2026): more reference implementations

A second mining pass (4 new games + deeper re-reads) surfaced shop variations the original recipe didn't cover: a **player-set dynamic-price demand curve**, the **spawn-and-charge world vendor** with a per-player ownership cap, **category-gated unlock walls**, **reason-tagged transactions**, and the **shop-as-scene-unlocker**. All net-new below.

### Player-set price → buy-probability demand curve (supermarket sim)

In a stocking sim the *player* sets the retail price and demand falls out of a tunable curve, instead of a fixed cost per item. `PriceManager` maps the price's position in the product's margin band through a control-point array, so the curve is a data table designers edit. (enifun.shop_manager `Code/Economy/PriceManager.cs`.)

```csharp
// actualMargin = (price - wholesaleCostPerUnit) / wholesaleCostPerUnit
// <= MinMargin → 100% buy; >= MaxMargin → 0%; piecewise-lerp between the control points:
static (float t, float chance)[] BuyChanceCurve =
    { (0,1f), (0.25f,0.99f), (0.50f,0.98f), (0.75f,0.85f), (1.0f,0.20f) };
// Rich customers ignore markup (GetRichBuyChance = 100% until the ceiling).
```

Two more lift-able bits from the same game: a third spend path **`ForceSpend`** that *allows* a negative balance (salaries, loan auto-repay, firing penalties) alongside the gated `SpendMoney`/`CanAfford` (`Code/Economy/ShopFunds.cs`), and broadcasting the whole price table to clients as **one `[Sync] string` of `"id:price,id:price"`** rather than a synced `Dictionary` — non-host edits are optimistic-local then reconciled by an `[Rpc.Host]`.

### Spawn-and-charge world vendor with a per-player cap

The DarkRP market is a `static` class whose `[Rpc.Host] Purchase(itemId)` spawns a physical prop in front of the buyer. The full guard chain — and especially the **per-player ownership cap enforced by counting still-valid spawned objects** — is the reusable recipe. (lowkeynetworks.newrp `Code/content/market/MarketService.cs`.)

```csharp
[Rpc.Host] public static void Purchase(string itemId) {
  var player = FindCaller(); var item = ItemRegistry.Get(itemId);
  if (item == null || !item.Spawnable) return;
  if (item.Price > 0 && player.Data.Money < item.Price) return;
  if (item.MaxPerPlayer > 0 && GetOwnedCount(player, item.Id) >= item.MaxPerPlayer) return; // cap
  if (item.Price > 0 && !player.Data.TakeMoney(item.Price)) return;                          // charge
  var go = SpawnPurchasedItem(player, item);                                                 // eye-trace 220u
  if (!go.IsValid()) { if (item.Price > 0) player.Data.AddMoney(item.Price); return; }       // refund on fail
  Track(player, item.Id, go);                                                                // per-(steam:id) list
}
// GetOwnedCount prunes dead refs first — destroyed props free the cap automatically:
//   list.RemoveAll(x => !x.IsValid()); return list.Count;
```

Pair it with that game's **clamp-in-the-setter wallet** so money can never go negative from a bug: `public int Money { get => _money; private set => _money = Math.Max(0, value); }`, with `TakeMoney`/`AddMoney` write-through-saving on every mutation (`Code/modules/player/PlayerData.cs`). Note this is a *stronger* per-player-cap pattern than the existing doc's refund discipline — the cap is derived from live world state, never a stored counter that can drift.

### Category-gated unlock wall (own N first) + per-level upgrade ladder

A roguelite meta-shop catalog as a plain `struct` registry, with a zero-extra-state progression wall: an item is locked until you own `RequiredPurchases` items in its category. (facepunch.ss2 `Code/ProgressManager.cs`.)

```csharp
struct ShopItemDef { string Id; Category Category; int Price; int[] UpgradePrices;
                     int RequiredPurchases; /* own N in this category to unlock */ ... }
bool IsItemUnlocked(ShopItemDef d) =>
    OwnedInCategory(d.Category) >= d.RequiredPurchases;          // soft wall, no new flags
// Per-level upgrade economy reuses UpgradePrices[] as a cost ladder:
void UpgradeGem(string id) { int cost = def.UpgradePrices[GemUpgradeLevels[id]];
    if (TrySpendCoins(cost)) GemUpgradeLevels[id]++; }
```

The catalog is assembled from several partial-class builders (`BuildGunItems`/`BuildCharmItems`/`BuildGemItems`) and **duplicate-Id-checked at build** (`Log.Error` if two defs share an Id) — a cheap guard against the copy-paste catalog bug. `PrefabPath` is a computed `switch` on `category+id` (convention `prefabs/{guns|charms|gems}/{Id}.prefab`), so items need no per-entry prefab reference.

### Reason-tagged transactions (self-instrumenting economy) + vendor-as-building

A money authority where every spend carries a `reason` string that auto-generates a stat key, so the economy instruments itself for free. (facepunch.fair `Park/ParkManager.cs`.)

```csharp
public bool TakeMoney( int amount, string reason = "Other" ) {
    Assert.True( Networking.IsHost );
    if ( Money < amount ) return false;                                  // affordability gate
    Stats.Increment( "money_spent", amount );
    Stats.Increment( $"money_spent.{reason.ToIdentifier()}", amount );   // dynamic per-reason key
    Money -= amount; return true;
}
```

Its **vendor is just a building** — `Shop : Building` holds `List<GameObject> Items`; `OnUse(guest)` clones `Game.Random.FromList(Items)` into the guest's inventory and bumps `Uses` (`Park/Buildings/Shop.cs`). And admission re-checks affordability *at the door* (`GuestManager.RegisterGuest` re-tests the fee, which may have risen while the guest walked in) — a good "re-validate at the point of sale, not at intent" reminder for any priced gate.

### Shop entry that doubles as a scene-object unlocker

A tablet shop where buying flips a scene object on and sets a capability flag — no inventory item, no spawn. Cheapest possible "functional upgrade" wiring. (luckygaming.doner_kiosk `Code/UI/shop/ShopUI.razor`.)

```csharp
void BuyItem( ShopItem item ) {
    if ( GameSettings.PlayerMoneyCount < item.Price || item.IsPurchased ) return;
    GameSettings.PlayerMoneyCount -= item.Price; item.IsPurchased = true;
    Scene.Directory.FindByName( item.Id ).FirstOrDefault().Enabled = true;   // reveal the prop
    if ( item.Id == "flashlight" ) Player.ThisPlayer.HaveFlashlight = true;   // flip a capability
}
```

Items are an inline `List<ShopItem>` (Id, bilingual Name/Desc, Price, emoji Icon, `IsPurchased`). A neat companion idea: a purchase can **remove a chore step** rather than add a thing — its `auto_grinder` upgrade makes the meat-slicing `DecreaseMeat()` early-return (`if (Player.ThisPlayer.HaveAutoGrinder) return;`), `Code/Game/MeatComponent.cs`.

### In-round powerup store (currency = a gameplay resource, price from ConVar)

A round-game shop where the currency is **clues you collected this round**, the store prefab is `NetworkSpawn`'d per-owner at round start, and the price is read from a server **ConVar at purchase time, not baked into the item** — so an owner re-tunes the economy live. (despawn.murder `Systems/EquipmentShop/EquipmentShopManager.cs`.)

```csharp
[Rpc.Host] public void PurchaseHost( string itemKey ) {
    if ( !GameConVars.IsPowerupEnabled(itemKey) ) return;
    var item = ItemComponentFactory.Create(itemKey);   // "radar" => new Radar{...}  string-key → behavior
    if ( item == null || caller.Pawn is not {} pawn || !item.CanPurchase(pawn) ) return; // per-item precondition
    int price = GameConVars.GetPowerupPrice(itemKey, fallback);   // ConVar, re-tunable, clamped 1–5
    if ( caller.CluesCollected < price ) return;
    item.OnPurchase(pawn); caller.CluesCollected -= price;        // act, THEN deduct
}
```

Two takeaways beyond the existing "ids-not-refs over RPC" rule: (1) the **`item.CanPurchase(pawn)` precondition gate** (don't sell a thing the buyer can't use) layered on top of the affordability check, and (2) **`ItemComponentFactory` string-key → spawned-behavior dispatch** (`Systems/EquipmentShop/ItemComponentFactory.cs`) — each catalog key maps to a small component that subclasses `EquipmentShopPurchasableComponentItem<T>` and just attaches its networked component on purchase.

### Read these games

- **enifun.shop_manager** (`Code/Economy/PriceManager.cs`, `ShopFunds.cs`) — player-set **dynamic-price demand curve** + the `ForceSpend` (negative-allowed) third spend path + price-table-as-`[Sync] string`.
- **lowkeynetworks.newrp** (`Code/content/market/MarketService.cs`, `modules/player/PlayerData.cs`) — **spawn-and-charge world vendor** with a per-player cap counted from live objects + refund-on-spawn-fail + clamp-in-setter wallet.
- **facepunch.ss2** (`Code/ProgressManager.cs`) — `ShopItemDef` struct catalog with **category-gated `RequiredPurchases` unlocks** + per-level `UpgradePrices[]` ladder + duplicate-Id build check.
- **facepunch.fair** (`Park/ParkManager.cs`, `Park/Buildings/Shop.cs`) — **reason-tagged self-instrumenting transactions** + vendor-as-building + re-validate-at-the-door.
- **luckygaming.doner_kiosk** (`Code/UI/shop/ShopUI.razor`, `Code/Game/MeatComponent.cs`) — **shop entry as a scene-object unlocker** + capability-flag flip + a purchase that removes a chore.
- **despawn.murder** (`Systems/EquipmentShop/EquipmentShopManager.cs`, `ItemComponentFactory.cs`) — in-round powerup store, **price-from-ConVar**, `CanPurchase` precondition gate, string-key→behavior factory.
