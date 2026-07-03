# Idle / Offline Income Systems

How to build passive-income generators, idle-tick earners, and offline/disconnect-safe reward delivery in modern s&box (GameObject/Component/Scene).

## What this is and when you need it

An **idle system** keeps producing value while the player is busy or away: money printers, worker drones, passive case-cracking income, tycoon shops. Two distinct concerns hide under "idle/offline":

1. **Idle production while connected** — a host-authoritative component ticks on a cadence and accumulates a stored value the player collects. (`MoneyPrinter`, `multis_cases` worker slots.)
2. **Offline / disconnect durability** — surviving a relog or server restart: persistent IDs for placed earners, and at-least-once delivery of rewards earned while the player was gone.

Almost every s&box "idle" implementation in the corpus simulates only **live frames** — none replay elapsed wall-clock time on load (see Gotchas). If you want true "earned while logged off," you build it yourself from a saved timestamp.

## Canonical approach

### 1. The idle-tick generator (host-authoritative, capped store)

A `Component` whose production runs **only on the host**, on a `NextPrintAt` cadence, accumulating into a `[Sync]` store the player later collects. This is the cleanest reusable shape (artisan.darkrpog: `Code/Items/MoneyPrinter.cs:371`).

```csharp
public sealed class IdleGenerator : Component
{
    [Sync( SyncFlags.FromHost )] public int   StoredMoney  { get; set; }
    [Sync( SyncFlags.FromHost )] public float NextPrintAt  { get; set; }
    [Property] public int   MoneyPerTick   { get; set; } = 10;
    [Property] public int   MaxStored      { get; set; } = 1000;
    [Property] public float TickInterval   { get; set; } = 5f;

    protected override void OnFixedUpdate()
    {
        if ( !Networking.IsHost ) return;            // host owns all production
        if ( Time.Now < NextPrintAt ) return;        // not time yet

        Produce();
        NextPrintAt = Time.Now + TickInterval;       // schedule next tick
    }

    void Produce()
    {
        if ( StoredMoney >= MaxStored ) return;                       // capacity clamp
        int amt = Math.Min( MoneyPerTick, MaxStored - StoredMoney );
        StoredMoney += amt;
    }
}
```

Production is gated `if ( !Networking.IsHost ) return;` and the schedule lives in a synced float, so clients only ever *read* `StoredMoney` and `SecondsUntilNextPrint` for UI (`MoneyPrinter.cs:373` host gate, `:388` time check, `:1045` capacity clamp). The store is **capped** so an idle earner can't run away while unattended — collection empties it.

A read-only countdown for the HUD is derived, never networked separately:

```csharp
public float SecondsUntilNextPrint =>
    NextPrintAt <= 0f ? 0f : Math.Max( 0f, NextPrintAt - Time.Now );
```

(MoneyPrinter.cs:160)

### 2. Upgrade levels live on the earner, not the player

Each upgrade line is an integer `[Sync]` level on the component, and the live stat is **derived** from the level via a geometric cost curve. Storing levels on the entity is what makes them persist with a world-placed object (artisan.darkrpog: `MoneyPrinter.cs:82-95`; master.digging_simulator: `ShopTerminal.cs:49`).

```csharp
[Sync( SyncFlags.FromHost )] public int SpeedLevel { get; set; }
[Sync( SyncFlags.FromHost )] public int YieldLevel { get; set; }

// canonical idle/tycoon pricing: BaseCost * mult^level
int GetCost( int baseCost, float mult, int level ) =>
    (int)(baseCost * MathF.Pow( mult, level ));
```

### 3. Live-frame accrual variant (no per-instance component)

When income is a single global rate (assigned workers, staked items), accumulate into an unclaimed bucket each frame and let the player claim manually (lavagame.multis_cases: `GameManager.cs:399`).

```csharp
protected override void OnUpdate()
{
    if ( WorkerSlots.Count == 0 ) return;
    float income = PassiveIncome;            // see formula below
    UnclaimedBalance += income * Time.Delta; // accrue this frame only
}

public float PassiveIncome =>
    WorkerSlots.Sum( i => MathF.Sqrt( i.SellValue ) * WORKER_RATE ) * PassiveMultiplier;
```

The `MathF.Sqrt(value)` compresses whale advantage — a deliberate balance choice (`GameManager.cs:66`). `ClaimPassiveIncome()` moves `UnclaimedBalance` into `Balance`. **Note:** `MathF` is available here but is *blocked in the sandbox whitelist for some games* — verify with `describe_type` before relying on it; `System.Math` is always safe.

### 4. Offline durability part A — persistent identity for placed earners

`GameObject.Id` is **not** durable across a server restart. A world-placed printer needs its own stable id to be re-owned and re-loaded (artisan.darkrpog: `Code/Persistence/World/PersistentWorldEntity.cs:6`).

```csharp
public sealed class PersistentWorldEntity : Component
{
    [Property, Sync( SyncFlags.FromHost )] public Guid   PersistentId  { get; set; }
    [Property, Sync( SyncFlags.FromHost )] public long   OwnerSteamId  { get; set; }

    public static PersistentWorldEntity Ensure( GameObject go, long owner )
    {
        var e = go.Components.GetOrCreate<PersistentWorldEntity>();
        if ( e.PersistentId == Guid.Empty ) e.PersistentId = Guid.NewGuid();
        e.OwnerSteamId = owner;
        return e;
    }
}
```

### 5. Offline durability part B — at-least-once reward delivery (ACK pattern)

If a reward resolves while the recipient is offline, persist the payload to host disk and re-deliver on reconnect — keep it in the pending map **until the client ACKs**, so a second disconnect can't lose it (lavagame.multis_cases: `GameManager.cs:174`, `:210`).

```csharp
const string PendingFile = "pending_wins.json";
static Dictionary<ulong, string> _pending;

static void SavePending() =>
    FileSystem.Data.WriteAllText( PendingFile,
        System.Text.Json.JsonSerializer.Serialize( _pending ) );

// host calls on player connect:
public static void TryGrantPending( ulong steamId, GameManager host )
{
    if ( !_pending.TryGetValue( steamId, out var payload ) ) return;
    host.GrantWin( steamId, payload );   // re-send; KEEP entry until ACK
}

[Rpc.Host]                               // client acks once it has the item
public void AckWin( ulong steamId )
{
    if ( _pending.Remove( steamId ) ) SavePending();
}
```

`FileSystem.Data` is the host-side persistent store. ACK-before-delete = at-least-once delivery; the client dedupes by a unique item id (`GameManager.cs:215`).

## Variations seen across games

- **Risk/heat sink** — `MoneyPrinter` adds `Heat += perTick` in `Produce()`, catches fire over a threshold, then explodes after a grace window, forcing the player to come collect (artisan.darkrpog: `MoneyPrinter.cs:1055`, `:1062`). Turns a passive earner into an active-attention loop.
- **Bucketed sync for rapid floats** — heat cools every fixed tick, but the `[Sync]` write is deferred until the accumulated delta crosses `HeatCoolFlushThreshold` (0.25), so it doesn't spam a packet 60×/sec (artisan.darkrpog: `MoneyPrinter.cs:47-52`). Reuse this for any fast-changing networked float.
- **Synced-timer phase machine** — instead of `async` delays, drive idle phases off a `[Sync] TimeUntil` + enum so a mid-game joiner reads the synced timer and stays in phase (vidya.terry_games: `RedLightGreenLight.cs:164`, `ColorShuffle.cs:66`). Host-only; sync **both** the enum and the `TimeUntil`.
- **Worker-drone automation** — autonomous host-only FSM workers pull from a static job queue with claim-locking instead of a fixed rate (enifun.shop_manager: `RestockEmployeeAI.cs:212`; emg.everything_must_go: `Worker.cs:94`, `Shop.cs:275`). All reservation/queue state is `static`, so it must be cleared on save-load before respawning workers.
- **Spawner scaling + daily event** — spawn rate/cap scale off progression with a once-per-day rush-hour multiplier rolled against `Game.Random` (enifun.shop_manager: `CustomerSpawner.cs:238`).
- **Live external data with TTL cache** — fetch a loot catalog over `Http` and cache to `FileSystem.Data` with a 7-day TTL so offline/rate-limited starts still work (lavagame.multis_cases: `Cs2CaseApiBuilder.cs:11`).
- **Floating "+$" UI** — a `LastBalanceDelta` + sequence-int pair drives gain animations without an RPC; clients detect the changed sequence and play locally (lavagame.multis_cases: `GameManager.cs:683`). Same no-RPC effect-replication trick as vault77's hit sequences.

## Gotchas

- **No game simulates true offline elapsed-time.** `multis_cases` accrues only live frames (`UnclaimedBalance += income * Time.Delta`) and `MoneyPrinter` only ticks while the host runs — closing the game forfeits idle gains between sessions (lavagame.multis_cases: `GameManager.cs:399`). For real "earned while away," save a `DateTimeOffset LastSeen`, then on load credit `(Now - LastSeen)` × rate yourself (and clamp it).
- **All production must be host-gated.** Every generator starts with `if ( !Networking.IsHost ) return;`. Clients only read the `[Sync(FromHost)]` store. Forget this and every client double-prints (MoneyPrinter.cs:373).
- **`GameObject.Id` is volatile across restarts** — use a `Guid PersistentId` layer for anything world-placed (artisan.darkrpog: `PersistentWorldEntity.cs`). Tombstones/pending logs grow unbounded; compact them.
- **Cap the store.** An uncapped accumulator overflows balance and breaks economy pacing; `Produce()` clamps to `MaxStored` (MoneyPrinter.cs:1045).
- **ACK before delete** on pending rewards, and dedupe on the client by a unique id — otherwise a relog mid-delivery either loses or double-grants the reward (multis_cases: `GameManager.cs:215`).
- **Static queue/reservation state leaks.** Worker-AI job boards keyed on `static` dictionaries must be `ClearAll`'d on save-load and validate `owner.IsValid` on every read (enifun.shop_manager: `RestockEmployeeAI.cs`).
- **Bucket fast `[Sync]` writes.** A fractional value changing every fixed tick will flood the network unless you accumulate the delta and only flush past a threshold (MoneyPrinter.cs:47).

## Seen in

- **artisan.darkrpog** — `Code/Items/MoneyPrinter.cs` (idle-tick earner: host gate, cadence, capped store, upgrade levels, heat/risk, bucketed sync); `Code/Persistence/World/PersistentWorldEntity.cs` (durable Guid identity for placed earners); `Code/Skills/RoleplaySkillService.cs` (passive playtime XP tick + anti-farm).
- **lavagame.multis_cases** — `Code/Game/Core/GameManager.cs` (live-frame passive income accrual + `ClaimPassiveIncome`; disconnect-safe pending-win JSON + ACK; floating-+$ delta/seq UI); `Code/Game/Economy/Cs2CaseApiBuilder.cs` (Http fetch + TTL file cache).
- **master.digging_simulator** — `ShopTerminal.cs` (geometric `BaseCost*mult^level` upgrade curve, stat re-derived onto live components).
- **enifun.shop_manager** — `Code/AI/RestockEmployeeAI.cs`, `Code/AI/CustomerSpawner.cs` (worker-drone automation + scaling spawner with daily rush-hour).
- **emg.everything_must_go** — `Code/Shop/Shop.cs`, `Code/Citizens/Worker.cs`, `Code/Shop/RestockJob.cs` (polymorphic job queue with claim-locking).
- **vidya.terry_games** — `RedLightGreenLight.cs`, `ColorShuffle.cs` (synced `TimeUntil`+enum phase machine for late-joiner-safe idle timing).

---

**Verify live:** API shifts between SDK versions — confirm `[Sync(SyncFlags.FromHost)]`, `Networking.IsHost`, `FileSystem.Data`, `Time.Now`/`Time.Delta`, and `[Rpc.Host]` against the installed SDK with `describe_type` / `search_types` reflection (authoritative) before building.

**See also:** the `sbox-api` skill for resolving exact type/method signatures, and the `sbox-build-feature` skill for the screenshot-driven iteration loop when wiring an idle generator into a scene.

---

## Corpus refresh (2026): more reference implementations

### A. UTC-tick scheduling — the correct way to do true offline accrual (klibatocorp.phenodex)

The existing corpus note says "no game simulates true offline elapsed-time" and tells you to build it yourself. `klibatocorp.phenodex` is now the canonical reference: it stores **UTC tick deltas** (`DateTime.UtcNow.Ticks`), not `Time.Delta` accumulators, so growth and bills advance correctly across sessions regardless of how long the server was offline.

```csharp
// phenodex/Code/Plant.cs
[Sync(SyncFlags.FromHost)] public long PlantedAtTicks  { get; set; }
[Sync(SyncFlags.FromHost)] public long PhaseStartAtTicks { get; set; }

// Works correctly after a server restart — no Time.Delta involved
public double SecondsSincePhaseStart()
    => TimeSpan.FromTicks( DateTime.UtcNow.Ticks - PhaseStartAtTicks ).TotalSeconds;

// Apply a single DEV_TIME_SCALE knob for QA
public const double DEV_TIME_SCALE = 1.0;   // raise to e.g. 45 for ~25s cycles
```

Bills schedule the same way: `NextBillTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(BillIntervalSec).Ticks`, fired whenever `DateTime.UtcNow.Ticks >= NextBillTicks` — survives any number of restarts unchanged (phenodex: `Player.cs:ChargeMonthlyBill`).

**Anti-pattern (old):** `UnclaimedBalance += income * Time.Delta` — only accrues while the host runs; closing the game forfeits all idle gains.
**Fix:** store `LastSeenTicks`; on load credit `(DateTime.UtcNow.Ticks - LastSeenTicks)` ticks × rate (clamp to your cap).

### B. Dual-faucet offline reconciliation with penalty + fuel bounds (lavagame.sandmoney_)

`lavagame.sandmoney_` is the best reference for reconciling **two independently bounded offline income streams** from one `LastSeenUnix` timestamp on load:

```csharp
// sandmoney_/Code/InfrastructureManager.cs (condensed)
void SimulateOfflineEarnings( double offlineSec )
{
    offlineSec = MathX.Clamp( offlineSec, 0, 86400 );   // hard 24-h cap
    if ( offlineSec <= 300 ) return;                     // 5-min floor — skip trivial sessions

    foreach ( var machine in OwnedMachines )
    {
        int cycles = (int)(offlineSec / machine.CycleSec);
        double earn = cycles * machine.RewardPerCycle * 0.50;  // 50% offline penalty
        double remainder = offlineSec % machine.CycleSec;
        machine.CycleStartedAt = DateTime.UtcNow - TimeSpan.FromSeconds( remainder ); // resume mid-cycle
        ApplyEarnings( earn );
    }
}

// sandmoney_/Code/Player/PlayerTrader.cs (condensed)
void ApplyOfflineEarnings( double offlineSec )
{
    foreach ( var bot in ActiveBots )
    {
        double earnSec = Math.Min( offlineSec, bot.FuelSeconds );  // fuel-bounded
        double earn = bot.GetOfflineEarningsPerMin( bot.Tier, bot.Quality ) * (earnSec / 60.0);
        bot.FuelSeconds -= earnSec;
        if ( bot.FuelSeconds <= 0 ) bot.SetEnabled( false );       // disables when dry
        ApplyEarnings( earn );
    }
}
```

Key techniques:
- **50% offline penalty** — makes online play consistently more rewarding; disincentivises AFK.
- **Fuel-bounded passive income** — bots can't earn indefinitely offline; they self-disable when fuel runs out, creating a sink economy (refuel/calibrate loop).
- **Resume mid-cycle** — back-dates `CycleStartedAt` by the remainder so the progress bar picks up where it left off rather than restarting from 0.
- **5-minute floor** + **once-per-load report** (`_showOfflineReport`) — avoids spammy "+$3 while you were gone" toasts on quick reconnects.

This game also has the prestige layer (see section C) — both patterns compose on the same `LastSeenUnix` field.

### C. Bracket-based prestige currency + full-wipe reset (lavagame.sandmoney_)

The first full prestige implementation in the corpus. Key differentiators over a simple "reset for multiplier":

```csharp
// sandmoney_/Code/Player/PlayerTrader.cs (condensed)
long ComputeHeritageCoinsForNetWorth( double nw )
{
    // Bracket table: < $1T → 0; each power-of-ten above earns more coins
    // Returns coins for CURRENT bracket only (not cumulative) —
    // discourages repeated cheap resets for incremental gains
    if ( nw < 1e12 ) return 0;
    if ( nw < 1e13 ) return 1;
    if ( nw < 1e14 ) return 3;
    // ...up to 35 at the highest bracket
    return 35;
}

void ResetProfileCore()
{
    // "NUCLEAR RESET" — zero everything twice for safety
    Money = 0; CoinsHeld = 0;
    foreach ( var bot in ActiveBots ) bot.GameObject.Destroy();
    ActiveBots.Clear();
    Upgrades = new UpgradeData();
    Infrastructure = new InfrastructureData();
    Missions = new MissionData();
    // Re-apply permanent Heritage perks AFTER wipe
    foreach ( var bonus in Heritage.PurchasedBonuses ) ApplyHeritageBonus( bonus );
    RunFtueBootstrap();  // re-run first-time-user flow for the new run
}
```

Heritage shop uses `CmdBuyHeritageBonus(id)` with tiered prerequisites (a "Capital I" perk must be bought before "Capital II"); some bonuses grant starting currency, others unlock tiers — a clean meta-currency-spent-on-permanent-multipliers pattern composable into any idle game.

Anti-pattern: awarding cumulative prestige coins (total earned across resets). This encourages "reset as fast as possible." The bracket approach (coins for your *current* bracket only) requires meaningful progression before each reset.

### D. Prestige-wipe with re-grant and teleport (intercrusstudio.sneguborka)

`intercrusstudio.sneguborka` shows the **networking details** of a prestige reset that the sandmoney_ single-player case hides:

```csharp
// sneguborka/Code/Player/PlayerPrestigeController.cs (condensed)
[Rpc.Host]
void WinterReset( Connection caller )
{
    // 1. Wipe wallet/tools/upgrades/inventory on host
    Wallet.SetMoney( 0 );           // SetMoney is the prestige-wipe-only path
    ToolUpgrades.Clear();
    Inventory.Clear();
    WintersSurvived++;

    // 2. Re-grant starter kit (mirrors spawn flow without recreating the GameObject)
    GrantStarterSpoon();            // first tool at Cost=0, MaxTier=1
    GrantStarterBag();

    // 3. Zero key-gated state and clear host-only dedupe sets
    GoldenKeys = 0;
    _grantedKeysThisWinter.Clear(); // so keys can be re-earned

    // 4. Teleport: reservoir-sample a spawn point, apply on owning client
    var spawn = ResolveSpawnTransform();
    Rpc.FilterInclude( caller );
    ApplyTeleport( spawn );         // clears Rigidbody interpolation
    PlayerController.EyeAngles = spawn.Rotation.Angles();
}
```

Key points:
- `SetMoney(long)` is the **prestige-wipe-only** mutator — distinct from `Grant` (rewards) and `Charge` (purchases), so the "never take progress away" invariant stays auditable everywhere except here.
- Re-granting the starter kit mirrors the spawn flow **without re-creating the GameObject** — cheaper and avoids a race between object creation and the Sync replication.
- Warn-once diagnostic latches (`_warnedNullOriginator`, `_warnedNoEconomyResolve`, etc.) re-arm on prestige so a silent reward-path failure is still observable post-reset.
- Use `RealTime.GlobalNow` (not `Time.Now`) in the prestige RPC rate-limiter — `Time.Now` resets on editor F5 and a static limiter would silently block every RPC for minutes after a reload (sneguborka: `RpcRateLimiter.cs`).

### E. Autonomous hired-unit idle layer without true offline earnings (freddo.scoops)

`freddo.scoops` shows the simplest "earn while not playing" design — hired drivers that produce income with no player input, with no offline catch-up at all:

```csharp
// scoops/Code/IceCreamTruck.cs (shape)
public sealed class IceCreamTruck : Component
{
    [Sync] public int Level { get; set; } = 1;       // per-truck upgrade
    public bool InfiniteStock => true;               // drivers never run out

    protected override void OnUpdate()
    {
        if ( !Networking.IsHost ) return;
        switch ( _state )
        {
            case State.Driving:
                if ( _driveTimer > TruckDriveTime ) _state = State.Serving;
                break;
            case State.Serving:
                if ( _serveTimer > TruckVendTime )
                {
                    OwnerEmpire.Earn( EarningsPerCycle );  // pay owner, no input needed
                    _state = State.Driving;
                    _driveTimer = 0;
                }
                break;
        }
    }
}
```

Capped at `MaxDrivers = 6`. Each truck carries its own `[Sync] Level` — upgrading the truck (not the player) speeds its serve interval. Not true offline-earnings, but the design intent is identical: buy units that earn for you. Use this shape when offline replay isn't needed (e.g. short-session arcade tycoon).

### F. NaN-guarded money mutators + dynamic per-item shop inflation (itacho.fill_the_void)

`itacho.fill_the_void` contributes two production-quality patterns for idle economy robustness not covered elsewhere:

**1. NaN/Inf-guarded money at every boundary** (GameState.cs):
```csharp
// fill_the_void/Code/Components/Game/GameState.cs (condensed)
float NormalizeMoneyValue( float v )
    => float.IsFinite( v ) ? MathX.Max( 0f, MathF.Round( v ) ) : 0f;

float NormalizeMoneyDelta( float d )
    => float.IsFinite( d ) ? MathX.Max( 0f, d ) : 0f;

public void AddMoney( float amount )
{
    var d = NormalizeMoneyDelta( amount );
    if ( d <= 0 ) return;
    Money = NormalizeMoneyValue( Money + d );
    // ...achievements, events
}

public bool SpendMoney( float amount )
{
    var d = NormalizeMoneyDelta( amount );
    if ( NormalizeMoneyValue( Money ) < d ) return false;   // affordability gate returns bool
    Money = NormalizeMoneyValue( Money - d );
    return true;
}
```

Note: `MathF` is used here (`MathF.Round`) — verify with `describe_type` in your project since `MathF` may not be whitelisted in all s&box SDK versions. `MathX.Max` is always safe.

**Anti-pattern:** `Math.Max(0, NaN)` returns `NaN` — `JsonSerializer` then throws and **silently kills the save**. Normalize before write *and* clamp on load.

**2. Per-item exponential price inflation persisted to save** (GameState.cs):
```csharp
// fill_the_void/Code/Components/Game/GameState.cs (condensed)
public float GetCurrentShopItemPrice( string itemId, float basePrice,
                                      float multiplierPerPurchase = 1.5f )
{
    var count = GetShopItemPurchaseCount( itemId );   // from a persisted Dictionary<string,int>
    var scaled = basePrice * MathF.Pow( multiplierPerPurchase, count );
    return float.IsFinite( scaled ) ? scaled : basePrice;   // guard the Pow result too
}
```

`_purchasedShopItemCounts` is persisted in the save so prices survive a relog. This is the tycoon standard price curve — `basePrice * mult^purchaseCount` — applied per item-id rather than per upgrade level (suits a shop where each machine type is bought multiple times).

---

### Updated "read these games" pointer

For idle/offline systems, read these games in this order:

1. **artisan.darkrpog** — canonical host-authoritative tick generator, heat/risk, bucketed sync, persistent Guid identity (the baseline; already in the main section).
2. **lavagame.sandmoney_** — `Code/InfrastructureManager.cs` + `Code/Player/PlayerTrader.cs`: the only game in the corpus with **true offline elapsed-time reconciliation** (dual-faucet, 50% penalty, fuel bounds, mid-cycle resume) AND a full **prestige/Heritage reset** with bracket meta-currency and a Heritage shop.
3. **klibatocorp.phenodex** — `Code/Plant.cs` + `Code/Player.cs`: UTC-tick delta scheduling (the correct primitive), `DEV_TIME_SCALE` single-knob time compression, bill scheduling that survives server restarts.
4. **intercrusstudio.sneguborka** — `Code/Player/PlayerPrestigeController.cs` + `Code/Player/PlayerWallet.cs`: networked prestige wipe with semantic `SetMoney`/`Grant`/`Charge` split, re-grant starter kit without re-creating objects, teleport to reservoir-sampled spawn, `RealTime.GlobalNow` rate-limiter anti-footgun.
5. **itacho.fill_the_void** — `Code/Components/Game/GameState.cs`: NaN-guarded money mutators, `SpendMoney→bool`, per-item exponential price inflation persisted to save, forward-compatible `record` save schema.
6. **freddo.scoops** — `Code/IceCreamTruck.cs`: simplest idle-agent shape (hired driver FSM, capped count, per-unit level) for games that don't need offline catch-up.
