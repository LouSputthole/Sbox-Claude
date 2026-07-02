# Shopkeeper / Store-Management Recipe

How to build a shopkeeper game in modern s&box (GameObject/Component/Scene), distilled from three mined games: `enifun.shop_manager` and `emg.everything_must_go` (full supermarket-tycoon sims) and `luckygaming.doner_kiosk` (a horror game wearing a food-stall skin).

## What defines the genre

A shopkeeper game is a **service loop around a stocked surface and a paying customer**. The player acquires goods, presents them (stock a shelf / cook an order), and an AI customer evaluates and pays â€” money feeds back into more goods and upgrades. Two sub-shapes appear:

- **Tycoon/management** (`shop_manager`, `everything_must_go`): order wholesale â†’ stock shelves â†’ price goods â†’ run register â†’ AI customers browse/buy/queue/checkout â†’ profit â†’ upgrades/levels. Deeply systemic, host-authoritative co-op.
- **Judgment/job-sim** (`doner_kiosk`): a per-customer *correctness* call (serve the right order, or approve/deny the customer). The shop is a skin over a "did you make the right decision?" mechanic.

**Core loop:** `acquire stock â†’ place/prepare â†’ customer decides & pays â†’ bank money â†’ reinvest`. Everything else (placement, AI, save, upgrades, co-op) is scaffolding around that loop.

## The system stack to compose

Build these as separate components; each is a singleton-per-system or a per-actor FSM. References point to existing system docs.

| System | Role | Reference |
|---|---|---|
| Host-authoritative singleton economy | Money owner-of-truth, proxy-routed spends | `references/systems/economy-currency.md` |
| Product/data catalog | Item defs (id, cost, model, unlock, tags) | `references/systems/inventory.md` |
| Stock acquisition (supplier/wholesale) | Buyable goods, daily-rerolled stock | `references/systems/shop-vendor.md` |
| Shelf placement & grid stocking | Snap items into a stocking surface | `references/systems/building-placement.md` |
| Customer AI (activity-queue FSM) | Browse â†’ buy â†’ queue â†’ checkout â†’ leave | `references/systems/spawning-waves.md` |
| Cash register / checkout queue | Lane placement, scan, take payment | `references/systems/shop-vendor.md` |
| Worker AI + job queue (optional) | Idle automation, claimable jobs | â€” (below) |
| Save/load persistence | Versioned, whole-scene serialize | `references/systems/save-persistence.md` |
| Upgrade / level-up progression | Tiers, XP, unlock tokens | `references/systems/progression-upgrades.md` |
| Leaderboard stats push | Backend score | `references/systems/leaderboards-services.md` |
| Interaction layer | Raycast/`IPressable`, carry, hold-to-act | `references/systems/inventory.md` |

For the judgment sub-shape, swap "register/worker/upgrades" for **per-customer scoring** + **observation-gated reveal** (see "Judgment variant" below).

## The one idiom that makes co-op work

Every mined tycoon system is the same shape: a `Component` with a `static Current`, all simulation gated to the host, and every client mutation routed through `[Rpc.Host]`. Internalize this before writing anything else.

```csharp
public sealed class ShopFunds : Component
{
    public static ShopFunds Current { get; private set; }

    // Host owns the value; [Sync(FromHost)] replicates it read-only to clients.
    [Sync( SyncFlags.FromHost )] public float Money { get; set; }

    protected override void OnStart()
    {
        Current = this;
        // Host claims ownership so it's the sole mutator; clients become IsProxy.
        if ( Networking.IsHost && !GameObject.Network.Active )
            GameObject.NetworkSpawn();
    }

    public bool SpendMoney( float amount )
    {
        if ( IsProxy ) return false;          // proxies never mutate
        if ( Money < amount ) return false;
        Money -= amount;
        return true;
    }
}
```
(enifun.shop_manager: Code/Economy/ShopFunds.cs:36 OnStart NetworkSpawn, :86 SpendMoney)

A player-initiated action on a client routes to the host, which runs the *same* method:

```csharp
public void TryBuyUpgrade()
{
    if ( Networking.IsActive && !Networking.IsHost ) { RequestBuyOnHost(); return; }
    // ...host logic: SpendMoney, increment tier, save...
}

[Rpc.Host] void RequestBuyOnHost() => TryBuyUpgrade();
```
(enifun.shop_manager: Code/Shop/ShopManager.cs:208 TryBuyAdvertising â€” the canonical recipe, repeated ~12Ã—). EMG states the same rule as `if ( Scene.IsEditor || !Networking.IsHost ) return;` at the top of every AI `OnUpdate` (emg.everything_must_go: Code/Shop/Shop.cs:22-30).

**Collections don't `[Sync]`.** Flatten a cart/price-table/unlock-set to a CSV `[Sync] string` and rebuild on proxies when it changes (enifun.shop_manager: Code/Economy/PlayerProgression.cs:250 unlocks-as-CSV), or use `NetList<T>`/`NetDictionary<K,V>` and explicitly rebuild after a host mutation (emg.everything_must_go: Code/Shelving/Shelf.cs:48-54,232-248).

## Build order

Build single-player first; the host-authority idiom above makes it co-op "for free" because `IsProxy`/`HasPermission` short-circuit true offline.

1. **Economy singleton.** `ShopFunds.Current`, `[Sync(FromHost)] Money`, `SpendMoney`/`AddMoney`. Everything else reads/writes through it.
2. **Product catalog.** A static registry of `ProductDef` POCOs (id, category, wholesale cost, retail price, model path/Cloud ident, unlock gate, tags). Lookup by id; alias legacy ids so save data stays decoupled (emg.everything_must_go: Code/Items/ShelfableCatalog.cs:5-55).
3. **Interaction + carry.** Raycast the camera/mouse ray (`Scene.Trace.Ray(...).HitTriggers()`), walk up parents for an `IInteractable`/`IPressable`, pick up rigidbodies (take ownership, disable gravity, lerp to hold point). See step detail below.
4. **Shelf placement & stocking grid.** Derive a stocking surface from the model bounds, snap carried items into free cells.
5. **Supplier stock.** Buyable wholesale goods. Use **day-seeded RNG** so all clients roll identical stock without networking it.
6. **Customer AI.** Spawn â†’ pre-roll an activity queue â†’ FSM walks a `NavMeshAgent` through it â†’ buy decision via a clamped additive probability â†’ queue â†’ checkout â†’ leave.
7. **Register/checkout queue.** Slot-based lane, scan timer, take payment (player keypad or worker auto-ring), `AddMoney`, grant XP.
8. **Save/load.** Scan `Scene.GetAllComponents<T>()` into flat POCOs, write JSON via `FileSystem.Data`/`Storage`, gate load on Seed+Version, respawn through a factory.
9. **Progression + upgrades.** XP per sale â†’ levels â†’ unlock tokens / tier upgrades whose effects are *pulled* by the economy formulas.
10. **(Optional) Worker AI**, leaderboard push, day/night, traits/synergies.

## How the real games do each piece

### Customer AI â€” activity-queue FSM on a NavMeshAgent
Pre-roll a `Queue<Activity>` (Browse / OptionalBuy / one RequiredBuy), then run a switch-FSM that walks the agent to each target and executes it. The *buy?* decision is one clamped additive function summing independent influences (popularity + tags + archetype prefs + events + synergies + buffs + upgrades + price ratio), clamped to ~[0.08, 0.95].

```csharp
protected override void OnUpdate()
{
    if ( Scene.IsEditor || !Networking.IsHost ) return; // host-only sim
    switch ( State )
    {
        case CustomerState.Browsing:
            Agent.MoveTo( TargetShelf.FrontPoint );
            if ( (Agent.TargetPosition - WorldPosition).Length <= ArrivalDistance )
                State = CustomerState.Checkout;
            break;
        // ...
    }
}
```
(emg.everything_must_go: Code/Citizens/Customer.cs:96-126 queue build, :322-391 FSM; Code/Shop/Shop.cs:404-433 buy-chance stack. enifun.shop_manager: Code/AI/CustomerAI.cs:215 state switch, :1245 weighted shelf pick.) Animation has **no controller** â€” read `Agent.Velocity` per frame into `CitizenAnimationHelper.WithVelocity/WithLook`. Reserve a shelf's front spot via a static `Dictionary<Shelf,Customer>` so only one customer approaches at a time (enifun.shop_manager: Code/AI/CustomerAI.cs:855).

### Spawner â€” scaling caps + daily rush
Spawn on a `TimeUntil`, scale max-concurrent off *stocked shelves* and player level via `[Property]` curves, roll a once-per-day rush window, stop N hours before close. Each spawn clones a prefab, `NetworkSpawn()`s, then `Dresser.Randomize()` for a random outfit. Make every knob a `[Property]` with `[Range]`/`[Category]` for editor balancing (enifun.shop_manager: Code/AI/CustomerSpawner.cs:156,238,288).

### Supplier stock â€” day-seeded determinism
Re-roll per-product box counts each day with `new System.Random( day * 73856093 )`. Identical seed on every machine â‡’ identical roll â‡’ **stock never needs networking**; only player-caused consumption is broadcast.

```csharp
void RollStock()
{
    var rng = new System.Random( CurrentDay * 73856093 ); // same on host + all clients
    foreach ( var p in ProductDatabase.Products )
        _dailyStock[p.Id] = rng.NextDouble() < p.OutOfStockChance ? 0 : rng.Next(1, 8);
}
```
(enifun.shop_manager: Code/Economy/SupplierStock.cs:246 RollStock; "peek tomorrow" = `(day+1)*const` at :157.)

### Shelf placement â€” grid from model bounds
Derive the stocking surface from the renderer's `Model.Bounds` (column axis = longer side, inset edges, slice into columnsÃ—rows per level); find the first free cell; freeze placed rigidbodies (`Gravity=false, MotionEnabled=false, Sleeping=true`). One `Shelf` component then adapts to many models. Authored child `Stock Slot` GameObjects can override the procedural grid (emg.everything_must_go: Code/Shelving/Shelf.cs:66-128,412-471). For the *build/furniture* placement flow (ghost preview, grid snap, validity tint, `Scene.NavMesh.SetDirty()` after placing) see `references/systems/building-placement.md` (enifun.shop_manager: Code/Shop/ShopBuilder.cs:288,366,705).

### Interaction + carry
Raycast each frame, walk up parents for `IInteractable.CanInteract`, read a live `InteractLabel` for the HUD prompt. Carry takes network ownership and smooth-moves the body in `OnFixedUpdate`; drop tries every shelf's `TryStock` host-side.

```csharp
var tr = Scene.Trace.Ray( ray, Range ).HitTriggers().WithoutTags( "player" ).Run();
for ( var go = tr.GameObject; go.IsValid(); go = go.Parent )
    if ( go.Components.Get<IInteractable>() is { } it && it.CanInteract( this ) ) { it.Interact( this ); break; }
```
(emg.everything_must_go: Code/Player/ObjectCarrier.cs:46-109 carry+ownership; enifun.shop_manager: Code/Player/PlayerInteraction.cs:146 parent-walk.) Carry physics belong in `OnFixedUpdate`; take ownership *before* moving a proxy body or the move is ignored.

### Cash register â€” checkout queue
Keep an ordered queue; place lane slots either at fixed child anchors (`Queue0`/`MasterQueue`) or score 8 candidate directions by navmesh-validity so the line doesn't clip walls. Checkout progresses only while `IsStaffed()` (player seated or worker hired); on completion `AddMoney`, grant XP, fire a `static Action<float> OnTransactionComplete` seam for HUD/achievements (enifun.shop_manager: Code/Shop/CashRegister.cs:373,511,692; emg.everything_must_go: Code/Shop/CashRegister.cs:118-212 lane scoring).

### Worker AI â€” global job board (optional automation)
Don't let workers each scan the world. Shelves enqueue themselves into a **static FIFO `_taskQueue`** (+ `HashSet` dedupe) when low; idle workers claim the first task and stake **static reservation dictionaries** (`Dictionary<Shelf,Worker>`, `Dictionary<(Storage,int),Worker>`) so no two grab the same job/box/slot. EMG generalizes this to a polymorphic `ShopJob` base (`Restock`/`Cashier`) with `TryClaim/Release/IsStillNeeded`. Validate `owner.IsValid` on every read and `ClearAll()` before save-restore (enifun.shop_manager: Code/AI/RestockEmployeeAI.cs:63,212; emg.everything_must_go: Code/Shop/RestockJob.cs:1-97).

### Save/load â€” scan + factory respawn
Walk `Scene.GetAllComponents<Shelf/Shelfable/Container/Worker>()` into flat POCO structs (positions as X/Y/Z floats, yaw, inventory lists), serialize to JSON, and restore by feeding each record back through a central `ObjectFactory` that clones the prefab and re-networks it. Gate load on **Seed+Version** so a schema bump reseeds cleanly; run a JSON-node `SaveMigrator` *before* deserialize for in-place upgrades (emg.everything_must_go: Code/Shop/ProgressBootstrapper.cs:206-324; enifun.shop_manager: Code/Save/SaveMigrator.cs:23,33). Apply only after `AllSingletonsReady()`. Host-only â€” clients never load.

## Judgment variant (doner_kiosk)

If your shop is a skin over a decision, swap the tycoon back half for these:

- **Symmetric mistake scoring across two exit states.** Correctness lives at the two ways a customer can leave: `LeavingState.Enter()` (served) penalizes if `isAnomaly`; `SadLeavingState.Enter()` (rejected) penalizes if `!isAnomaly`. One boolean, two states, both â†’ `PlayerErrorAction()`. Portable to any approve/deny game (luckygaming.doner_kiosk: Code/npc/states/LeavingState.cs:10 + SadLeaving.cs:38).
- **Observation-gated reveal.** Anomaly behaviors are drop-in `SpecialModify : Component` modifiers gated on the customer's FSM state *and* whether the player is watching the CCTV. A head only rotates while unobserved; another only animates once watched; one toggles `Model.Enabled` so it's only (in)visible through the lens. This "behavior keyed to observation" loop is the genre's reusable heart (luckygaming.doner_kiosk: Code/npc/modify/SpecialModify.cs:1 base, FaceInCam.cs:15, Code/Game/VideoCamera.cs:34 CameraOnâ†’IsStartet, :74 CheckAnomaly). Note its CCTV blits via `DebugOverlay.Texture` â€” for production render the `Texture.CreateRenderTarget` feed into a Razor/world panel instead.
- **Hand-rolled per-actor FSM.** A `Dictionary<enum, State>` built once; `ChangeState` calls `Exit()`â†’swapâ†’`Enter()`; `OnUpdate` forwards to `current?.Update()`. States are reused (not per-entry) so reset per-visit data in `Enter()`, not in fields (luckygaming.doner_kiosk: Code/npc/Customer.cs:162,179).
- **Deck-draw spawn for guaranteed variety.** Load types from `Enum.GetValues` into a list; each spawn pulls a random index and `RemoveAt()`s it, so every type is seen once before any repeat â€” cleaner than naive `Random` for curated pacing (luckygaming.doner_kiosk: Code/Game/GameManager.cs:176).
- **Multi-step recipe via paired dictionaries.** Cooking is data, not branches: `_itemToBoardState` maps ingredientâ†’accepting board-state, `_placementActions` maps ingredientâ†’`Action<Board>`; a 1s hold-to-place (`TimeSince` â†’ 0..1 progress) gates the commit. Adding an ingredient = two dictionary entries (luckygaming.doner_kiosk: Code/Player/Player.cs:96, EntityBoard.cs:66).
- **In-engine TTS for NPC voice.** `Sandbox.Speech.Synthesizer().WithText(line).Play()` â†’ position the `SoundHandle` at the speaker â€” dynamic spoken dialog with zero recorded VO (luckygaming.doner_kiosk: Code/Game/Tts.cs:9).

## Pitfalls (from the mined code)

- Money mutators silently no-op on proxies â€” UI **must** route spends through `[Rpc.Host]`, never call `SpendMoney` on a client.
- `NetList`/`NetDictionary` replicate from host only; rebuild them explicitly after a host mutation or proxies read stale/empty.
- After placing/moving anything that affects pathing, call `Scene.NavMesh.SetDirty()`; spawn/despawn points may be off-mesh â€” snap with `Scene.NavMesh.GetClosestPoint`.
- Static reservation/queue/job state leaks across a save-load; `ClearAll()` before respawning, validate `owner.IsValid` on access.
- Bumping the save `CurrentVersion` wipes old saves (intentional) â€” pair it with a migration.
- Don't lean on global statics like `Player.ThisPlayer`/`GameSettings.*` (doner_kiosk does, baking in a single-local-player ceiling) â€” pass refs or resolve per-actor.
- Razor `PanelComponent.BuildHash()` must fold **every** value the markup reads, or the UI goes stale (emg.everything_must_go: Code/UI/CheckoutPanel.razor:155).

## Verify live

API surfaces drift between SDK versions â€” confirm before relying on a signature. Use `describe_type` / `search_types` reflection against the installed SDK as authoritative for: `Sandbox.Networking` (`IsHost`/`CreateLobby`), `[Sync]`/`SyncFlags`/`[Rpc.Host]`/`[Rpc.Broadcast]`, `NavMeshAgent` (`MoveTo`/`Velocity`/`TargetPosition`), `Scene.NavMesh`, `Scene.Trace.Ray(...).HitTriggers()`, `GameObject.NetworkSpawn`/`Network.TakeOwnership`, `CitizenAnimationHelper`, `Texture.CreateRenderTarget`/`CameraComponent.RenderToTexture`, `Sandbox.Speech.Synthesizer`, and `FileSystem.Data` / `Storage` JSON helpers.

Cross-links: see the `sbox-api` skill for authoritative type lookups, and the `sbox-build-feature` skill for the screenshot-driven build/iterate loop.

## Corpus refresh (2026): more reference implementations

Four additional games deepen the shopkeeper corpus: `thefancylads.restaurant_dev` (GASTROTOWN â€” restaurant tycoon with cooking + grid build-mode + REST backend), `emg.everything_must_go` (pawn-shop tycoon â€” the most complete host-authoritative shopkeeper in the corpus), `enifun.shop_manager` (grocery supermarket â€” price-elasticity demand, phone orders, 4-tier co-op permissions), `luckygaming.doner_kiosk` (already the basis of the Judgment section above). Techniques below are **net-new** vs the sections above.

### Charge â†’ act â†’ refund-on-fail rollback (thefancylads.restaurant_dev)

Every spend in GASTROTOWN follows one discipline: charge first, attempt the world change, refund if the world change fails. For replace operations (swap item A for B that costs less), charge only the _difference_. This is the cleanest "money can never desync from the world" recipe in the corpus.

```csharp
// Common/BuildMode/BuildModeServer.cs
bool TryCharge( RestaurantComponent r, float cost ) {
    if ( r.Money < cost ) { /* show toast to Rpc.Caller */ return false; }
    r.Money -= cost; return true;
}
void Refund( RestaurantComponent r, float cost ) => r.Money += cost;
bool TryChargeOrRefund( RestaurantComponent r, float delta ) {
    if ( delta > 0 ) return TryCharge( r, delta );
    if ( delta < 0 ) Refund( r, -delta );   // cheaper replace â†’ give money back
    return true;
}
// Usage: if (!TryCharge(...)) return; if (!grid.TryPlace(...)) { Refund(...); return; }
```

Anti-pattern: deducting money and then checking whether the action succeeds. Fix: always charge â†’ act â†’ refund-on-fail.

### Sync occupancy bits, not object graphs (thefancylads.restaurant_dev)

For grid placement the server needs full object references; clients only need to know "is this cell taken" to grey out invalid drop targets. Keep a heavy server-only map and a light synced shadow:

```csharp
// Common/Restaurants/RestaurantGrid.cs
private Dictionary<Vector2Int, GameObject> _surfaceCells = new();           // HOST ONLY
[Sync(SyncFlags.FromHost)]
private NetDictionary<Vector2Int, bool> _occupiedSurfaceCells { get; set; } // SYNCED: just occupancy
```

Clients call `IsSurfaceCellOccupied(cell)` for preview feedback; the host alone resolves what is there. Composable to any grid/tile placement game.

### Struct-of-Arrays (SoA) for replicating lists of structs (emg.everything_must_go)

`[Sync]` cannot replicate `List<CustomClass>`. The solution: keep the rich list host-side and mirror it as **N parallel `NetList<primitive>` columns**, zip them back on the client. The key robustness trick is clamping to `Math.Min(all column lengths)` before zipping â€” columns can arrive slightly out of step during replication.

```csharp
// Shop.CheckoutSync.cs
[Sync(SyncFlags.FromHost)] NetList<string>  SyncedCheckoutTypes    { get; set; }
[Sync(SyncFlags.FromHost)] NetList<float>   SyncedCheckoutPrices   { get; set; }
[Sync(SyncFlags.FromHost)] NetList<int>     SyncedCheckoutCounts   { get; set; }

IReadOnlyList<CheckoutLine> BuildSyncedCheckoutLines() {
    int n = Math.Min( SyncedCheckoutTypes.Count,
            Math.Min( SyncedCheckoutPrices.Count, SyncedCheckoutCounts.Count ) );
    var lines = new List<CheckoutLine>( n );
    for ( int i = 0; i < n; i++ )
        lines.Add( new CheckoutLine { Type = SyncedCheckoutTypes[i],
                                      UnitPrice = SyncedCheckoutPrices[i],
                                      Count = SyncedCheckoutCounts[i] } );
    return lines;
}
```

Use `Math.Min` (available in sandbox) â€” not `MathX.Min` (no overload for three args) and not `MathF` (doesn't exist). Applied to checkout lines, active buffs, and progression choices in EMG.

### Authority-forked read properties (emg.everything_must_go)

The cleanest discipline in the corpus for writing a single API that works identically on host and proxy: expose one public property that returns the host's rich object when you are the host and a hydrated mirror when you are a proxy.

```csharp
// Shop.cs
public IReadOnlyList<CheckoutLine> CheckoutLines =>
    Networking.IsHost ? _hostCheckoutLines : BuildSyncedCheckoutLines();

public IReadOnlyList<BuffState> ActiveBuffs =>
    Networking.IsHost ? _hostBuffs : BuildSyncedBuffs();
```

UI and other components call one API regardless of where they run. Apply uniformly to any host-only simulation that has a client-readable shadow. (EMG: `Shop.cs:96â€“124`, `CashRegister.cs:19â€“34`, `Shelf.cs:48â€“50`.)

### Queue hash for cheap Razor re-renders (emg.everything_must_go)

Instead of syncing the entire checkout queue to clients, sync one `int` hash that changes whenever the queue meaningfully changes. The Razor panel's `BuildHash()` folds it in, so the panel re-renders only when needed.

```csharp
// CashRegister.cs
int BuildQueueHash() {
    int h = 0;
    foreach ( var c in _queue ) h = HashCode.Combine( h, c.GetHashCode(), c.Total, c.ItemCount );
    return h;
}
[Sync(SyncFlags.FromHost)] int SyncedCheckoutQueueHash { get; set; }
// CheckoutPanel.razor  BuildHash(): return HashCode.Combine( base.BuildHash(), Register.SyncedCheckoutQueueHash );
```

### Three-spend-path currency pattern (enifun.shop_manager)

SHOP MANAGER distinguishes three semantically distinct spend paths â€” composable and worth codifying verbatim:

```csharp
// Code/Economy/ShopFunds.cs
void AddMoney( float amount, string reason = "" )    // income; rejects <=0
bool SpendMoney( float amount, string reason = "" )  // returns false if insufficient (gated)
void ForceSpend( float amount, string reason = "" )  // allows negative balance (salary, loan auto-repay)
bool CanAfford( float amount )                       // pure read â€” use in UI to grey out buttons
```

`ForceSpend` is deliberately designed to allow negative balances from system-triggered deductions (daily salaries, loan repayment). The save validator accepts negative money but clamps totals `>= 0`. Anti-pattern: using one `SpendMoney` for both player-initiated and system-triggered spends â€” the former should fail gracefully; the latter must succeed regardless.

### Price-elasticity demand curve via control-point array (enifun.shop_manager)

Instead of a flat buy-chance, derive purchase probability from where the player-set price falls within the product's wholesale-cost margin band. The curve is a tunable data table of `(margin_t, chance)` control points, linearly interpolated:

```csharp
// Code/Economy/PriceManager.cs
static (float t, float chance)[] BuyChanceCurve = {
    (0f, 1f), (0.25f, 0.99f), (0.50f, 0.98f), (0.75f, 0.85f), (1.0f, 0.20f)
};
// actualMargin = (price - wholesale) / wholesale
// t = InverseLerp(MinMargin, MaxMargin, actualMargin)  â†’ interpolate curve
```

Rich customers use `GetRichBuyChance` (100% below ceiling, ignores markup). Prices persist per-product-ID decoupled from shelf instance. Non-host pricing uses optimistic-local + `[Rpc.Host]` reconcile. The full price table syncs as one `[Sync] string` in `"id:price,id:price"` CSV form to avoid a synced Dictionary.

### Data-driven modifier engine via EffectsManager (thefancylads.restaurant_dev)

GASTROTOWN's upgrade tree flows entirely through one `EffectsManager.Get(owner, EffectType)` call that aggregates all active upgrades into a single multiplier or additive value. Every gameplay formula reads from it:

```csharp
// Common/Effects/EffectsManager.cs  (pattern, not verbatim)
// cook speed:    CookTime += delta * EffectsManager.Get(r, CookSpeedForMethod(method));
// spawn rate:    CustomerSpawnChance * Time.Delta * EffectsManager.Get(r, CustomerSpawnChance);
// patience:      Patience *= EffectsManager.Get(r, CustomerPatienceIncrease);
// meal value:    base * EffectsManager.Get(r, MealValueIncrease);
```

`UpgradeEffect` is a `struct { EffectType Type; EffectMode Mode(Add/Multiply/Flag); float ValuePerRank; }`. Multiplicative effects return `1 + sum`; additive return `sum`. `GetDescription(rank)` generates tooltip text from the same data â€” UI never drifts from the math. This is the model to copy for any upgrade/prestige tree.

### Dynamic pricing from recipe complexity (thefancylads.restaurant_dev)

Price is computed from content, not a static field. Each recipe step contributes ingredient cost + a complexity score (raw = 0.4, cooked = 0.8), then the whole thing is multiplied by upgrade and meal-type modifiers:

```csharp
// Common/Resources/MealResource.cs
float GetMealValue( RestaurantComponent r ) {
    float ingredientCost = 0f, complexityScore = 0f;
    foreach ( var step in Recipe.Steps.Values ) {
        ingredientCost += step.EffectiveCostPerMeal;
        complexityScore += step.AcceptedStates.HasFlag( CookedStateMask.Raw ) ? 0.4f : 0.8f;
        complexityScore *= stepBonusMultiplier;
    }
    return ingredientCost * complexityScore * valueMultiplier * mealTypeMultiplier;
}
```

Margins emerge from data; balancing is editing `.recipe`/`.upgrade` assets, not code. Pre-packaged items (no recipe) fall back to `Cost * 2`.

### INetworkVisible distance culling for crowded scenes (thefancylads.restaurant_dev)

In a social-hub map with many restaurants each spawning customers, implementing `INetworkVisible` on customers avoids sending updates for NPCs the client can't possibly see:

```csharp
// Common/Customers/Customer.cs
public bool IsVisibleToConnection( Connection connection ) {
    var agent = connection.GetPlayerObject()?.GetComponent<PlayerController>();
    if ( agent is null ) return true;
    return WorldPosition.Distance( agent.WorldPosition ) <= 1500f;
}
```

The `1500f` threshold is a `[Property]`. Apply to any NPC in a large world; the engine stops sending network updates beyond the distance. Verify `INetworkVisible` exists in your SDK version via `describe_type` before use.

### Data-driven catalogs as plain static C# (emg.everything_must_go)

EMG defines all items, shelves, buffs, and traits as `static class XCatalog { get { yield return new XDefinition{...}; ... } }` â€” no `GameResource`, no JSON. Each definition carries `Tags[]`, `UnlockLevel`, `UnlockRevenue`, `ModelIdent`, per-shelf `SurfaceLevelCapacityOverrides`, and a `TypeAliases` map so old/cut save strings normalize to current type names (forward-compat shim that complements the version gate). This is a lower-friction content pipeline for non-coders and matches how shipped games actually do it.

### Modal cursor UI borrow/restore (emg.everything_must_go)

Opening a mouse-driven panel (keypad, shop tablet) in an FPS game without leaking input state:

```csharp
// Code/UI/CheckoutPanel.razor
void OpenPanel() {
    _savedInputControls = Controller.UseInputControls;
    _savedCameraControls = Controller.UseCameraControls;
    Controller.UseInputControls = Controller.UseCameraControls = Controller.UseLookControls = false;
    Mouse.Visibility = true;
}
void ClosePanel() {   // also called from OnDisabled/Destroy
    Controller.UseInputControls = _savedInputControls;
    Controller.UseCameraControls = _savedCameraControls;
    Mouse.Visibility = false;
}
protected override void OnUpdate() { Controller.WishVelocity = Vector3.Zero; } // prevent drift
```

Anti-pattern: setting controls to `false` on open and hardcoding `true` on close â€” if the panel is destroyed while another modal is open, the restored state is wrong. Always save and restore.

### Per-player-plot foreign-object eviction (thefancylads.restaurant_dev)

In a social-hub world with separate per-player restaurants, prevent economy griefing by detecting any physics object that crosses into the wrong plot and force-teleporting it home:

```csharp
// Common/Restaurants/RestaurantArea.cs  (host-only trigger)
void OnTriggerEnter( Collider other ) {
    if ( !Networking.IsHost ) return;
    var grab = other.GameObject.GetComponentInParent<IGrabbable>();
    if ( grab is null || grab.Restaurant == this ) return;
    grab.GrabEndFromServer();
    other.GameObject.Network.TakeOwnership();   // reclaim before moving
    other.GameObject.WorldPosition = grab.Restaurant.HomePosition;
    // toast the offender
}
```

### Versioned JSON-node migration chain (enifun.shop_manager)

The best save-migration pattern in the corpus: migrations operate on raw `JsonObject` (not deserialized POCOs) and are stored in a `Dictionary<int, Action<JsonObject>>` keyed by the version they migrate *from*, looped until current:

```csharp
// Code/Save/SaveMigrator.cs
static Dictionary<int, Action<JsonObject>> _migrations = new() {
    { 1, v1 => { v1["newField"] = v1["oldField"]; v1.Remove("oldField"); } },
    { 2, v2 => { /* rename another field */ } },
    { 3, v3 => { /* add a new section */ } },
};
static JsonObject Migrate( JsonObject node, int fromVersion, int toVersion ) {
    for ( int v = fromVersion; v < toVersion; v++ )
        if ( _migrations.TryGetValue( v, out var fn ) ) fn( node );
    return node;
}
```

Anti-pattern: bumping `CurrentVersion` with no migration (`CanUseState` rejects old saves outright). That is a valid strategy during active development (EMG does it) but not appropriate for a shipped game. Use the migration chain for live games.

### Clothing blacklist sanitizer for Dresser.Randomize() (luckygaming.doner_kiosk)

After randomizing a Citizen NPC's outfit, strip items that carry semantic meaning (anomaly signatures, gore props) so normal customers never accidentally wear a horror tell:

```csharp
// Code/npc/Customer.cs
static readonly HashSet<string> _anomalyClothingBlacklist = new() {
    "skull_helmet", "axe_in_the_head_01", "mushroom_hat", /* â€¦ */
};
void DressNormal( CitizenAppearance dress ) {
    dress.Randomize();
    dress.Clothing.RemoveAll( e => _anomalyBlacklist.Contains( e.Clothing.ResourceName ) );
    dress.Apply();
}
```

Composable to any game that randomizes NPCs but reserves certain clothing for special roles.

### Hotload-safe singleton via IHotloadManaged (luckygaming.doner_kiosk)

The `[SkipHotload]` attribute on a static field survives code hot-reload but the assignment in `OnStart` does not re-run. Wrap in `IHotloadManaged` to handle both:

```csharp
// Code/Network/SingletonComponent.cs
public abstract class SingletonComponent<T> : Component, IHotloadManaged
    where T : SingletonComponent<T>
{
    [SkipHotload] public static T Instance { get; private set; }
    protected override void OnStart() => Instance = (T)this;
    void IHotloadManaged.Created( IReadOnlyDictionary<string,object> state ) {
        if ( state.TryGetValue( "active", out var v ) && (bool)v ) Instance = (T)this;
    }
    void IHotloadManaged.Destroyed( IDictionary<string,object> state ) =>
        state["active"] = ( Instance == this );
}
```

Use for long-lived manager singletons. The simpler `static Instance` re-assigned in `OnStart` (doner_kiosk's `GameManager`) works fine if hot-reload of running gameplay is not a concern.

### Read these games

The most complete shopkeeper references, in descending architectural completeness:

- `emg.everything_must_go` â€” pawn-shop tycoon; SoA net-sync, authority-forked reads, tag-synergy economy, job-queue workers, navmesh-aware checkout line, ghost placement (D:\sbox-lessons\mining-v2\games\emg.everything_must_go.md)
- `enifun.shop_manager` â€” grocery supermarket; price-elasticity demand curve, phone orders, 4-tier permissions, versioned migration chain, day-night clock, late-joiner hydration (D:\sbox-lessons\mining-v2\games\enifun.shop_manager.md)
- `thefancylads.restaurant_dev` â€” restaurant tycoon; chargeâ†’actâ†’refund rollback, occupancy-bit sync, EffectsManager modifier engine, `INetworkVisible` cull, `#if SERVER` compile-split, REST backend persistence (D:\sbox-lessons\mining-v2\games\thefancylads.restaurant_dev.md)
- `luckygaming.doner_kiosk` â€” anomaly-detection horror shopkeeper; judgment via FSM exit branch, observation-gated reveals, TTS voice, clothing blacklist, hotload-safe singleton (D:\sbox-lessons\mining-v2\games\luckygaming.doner_kiosk.md)
