# Tycoon-Idle Genre Recipe

How to build a harvest -> sell -> upgrade -> prestige tycoon/idle game in modern s&box (GameObject/Component/Scene), distilled from three shipped titles.

## What defines the genre

A tycoon-idle game is a **numbers-go-up economy with a tight harvest loop wrapped around data-driven progression**. The player does one cheap repeated action (chop / mine / dig), converts the output into a currency, spends currency on **geometric-cost upgrades** that make the action faster, and periodically hits a **prestige/reset** that trades current progress for a permanent multiplier. Everything else (gacha cases, gambling, leaderboards, station UIs) is bolted onto that spine.

The core loop, verbatim from a shipped game's own summary:
> swing an axe at trees -> fill a weight-capped backpack -> sell logs / mill them into planks -> spend Money on tiered upgrade trees -> unlock map gates -> fast-travel -> prestige reset for permanent multipliers (chop_the_forest: summary).

Three reference games, three networking postures — pick yours up front because it dictates everything:
- **chop_the_forest** — host-authoritative multiplayer. Economy lives on the host; clients replicate. Use this if real value (leaderboards, a backend) is at stake.
- **s_miner** — client-authoritative ("Roblox hub"). Each client owns its own save; `[Sync]` only for headline numbers, `[Rpc.Broadcast]` for shared events. Easy, but trivially cheatable.
- **digging_simulator** — single-player. Zero networking discipline (no `[Sync]`, no `[Rpc]`); plain C# fields. Great single-player recipes, needs a networking pass before MP (digging_simulator: summary).

## The system stack to compose

Compose these in order. Each maps to a deeper system reference where one exists.

1. **Harvest interaction** (`references/systems/spawning-waves.md` for spawn cadence) — the repeated action: a trace/proximity query that damages a world resource and emits drops.
2. **Capped inventory / carry** (`references/systems/inventory.md`) — a weight- or count-capped backpack that gates the action when full.
3. **Currency + economy core** (`references/systems/economy-currency.md`) — one component holding all currencies; the single place money is minted/spent.
4. **Data-driven upgrade tables** (`references/systems/progression-upgrades.md`) — `readonly struct[]` balance tables with geometric cost curves, indexed by level.
5. **Shop / vendor** (`references/systems/shop-vendor.md`) — buy upgrades, sell carry, applies stats to live components.
6. **Prestige / rebirth** (`references/systems/progression-upgrades.md`) — reset lower progress for a permanent multiplier.
7. **Signed, sanitizing save** (`references/systems/save-persistence.md`) — clamp-everything load, versioned schema, anti-tamper.
8. **Gacha / loot case** (`references/systems/gacha-loot.md`) — timed free case with weighted rolls (optional).
9. **Leaderboards** (`references/systems/leaderboards-services.md`) — `Sandbox.Services.Stats` with season schedule (optional, MP).
10. **Idle/offline accrual** (`references/systems/idle-offline.md`) — recharge/earn over real (Unix) time across sessions (optional).

## Build order

Build the loop before the meta. Vertical-slice order:

**1. Harvest target.** A `HarvestableResource` Component with a synced hit counter. On enough hits, deplete and spawn a pickup. For a forest of thousands, do NOT scan the scene every swing — bucket instances into a static spatial grid and query only nearby cells.

```csharp
public sealed class HarvestableResource : Component
{
    static readonly Dictionary<ResourceGridCell, List<HarvestableResource>> Grid = new();
    const float CellSize = 512f;

    [Sync(SyncFlags.FromHost)] public float HitsTaken { get; private set; }
    [Sync(SyncFlags.FromHost)] public bool IsDepleted { get; private set; }

    // Query only the cells overlapping the AABB, not AllActiveResources.
    public static void GetActiveResourcesNear(Scene scene, Vector3 pos, float radius, List<HarvestableResource> results)
    {
        results.Clear();
        var min = Cell(pos - new Vector3(radius, radius, 0));
        var max = Cell(pos + new Vector3(radius, radius, 0));
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
            if (Grid.TryGetValue(new ResourceGridCell(x, y), out var cell))
                foreach (var r in cell)
                    if (r.IsValid() && r.WorldPosition.DistanceSquared(pos) <= radius * radius)
                        results.Add(r);
    }
}
```
(chop_the_forest: Code/World/HarvestableResource.cs:10 grid field, :163 GetActiveResourcesNear, :47 Sync hit state.) Cell is captured at OnStart from WorldPosition — never move a registered resource or it desyncs its bucket.

**2. Economy component.** ONE component owns every currency and level. In MP, make them `[Sync(SyncFlags.FromHost)]` with private setters and guard every mutator so only the host writes.

```csharp
public sealed class PlayerProgression : Component
{
    [Sync(SyncFlags.FromHost)] public int Money { get; private set; }
    [Sync(SyncFlags.FromHost)] public int AxeLevel { get; private set; } = 1;
    [Sync(SyncFlags.FromHost)] public int PrestigeLevel { get; private set; }

    public void AddMoney(int amount)
    {
        if (Networking.IsActive && !Networking.IsHost) return; // host-only writes
        Money = Math.Clamp(Money + amount, 0, 1_500_000_000); // stay inside int.MaxValue
    }
}
```
(chop_the_forest: Code/Player/PlayerProgression.cs:37 the `[Sync(FromHost)]` currency block.) `FromHost` means clients *cannot* write — a client-initiated change must round-trip an `[Rpc.Host]` (see Request->Apply->Confirm below). Keep money as `int`/`long` but clamp; geometric costs blow past `int.MaxValue` fast.

**3. Balance tables.** Each tunable system is a static class with a `readonly struct[]` indexed by level + a clamping `Get`. Designers tune one array; `MaxLevel` derives from `.Length`.

```csharp
public readonly struct AxeUpgradeDefinition
{
    public AxeUpgradeDefinition(int level, float power, int coinCost, int plankCost)
        { Level = level; Power = power; CoinCost = coinCost; PlankCost = plankCost; }
    public int Level { get; } public float Power { get; }
    public int CoinCost { get; } public int PlankCost { get; }
}

public static class AxeUpgradeBalance
{
    static readonly AxeUpgradeDefinition[] Definitions =
    {
        new(1, 1.0f, 0, 0), new(2, 1.45f, 30, 0), new(3, 1.55f, 90, 0),
        new(4, 1.9f, 220, 0), /* ... costs grow geometrically ... */ new(25, 65.0f, 55_000_000, 7000)
    };
    public static int MaxLevel => Definitions.Length;
    public static AxeUpgradeDefinition Get(int level) => Definitions[Math.Clamp(level, 1, MaxLevel) - 1];
    public static bool TryGetNext(int current, out AxeUpgradeDefinition def)
    { def = Get(current + 1); return current < MaxLevel; }
}
```
(chop_the_forest: Code/Player/AxeUpgradeBalance.cs:21 Definitions, :50 MaxLevel from Length, :52 Get/clamp.) `AxeUpgradeBalance`, `SawUpgradeBalance`, `BackpackUpgradeBalance`, `PrestigeBalance`, `ZoneUnlockBalance` all share this exact shape. The economy reads them via computed props: `AxePower => AxeUpgradeBalance.Get(AxeLevel).Power`. Adding a row "just works" but invalidates persisted levels above the new max — your save loader MUST clamp on load.

**4. Shop / vendor.** If you don't need the multiplayer ceremony, the single-player vendor pattern is the cleanest geometric-cost shop: a `[Serializable]` config per upgrade line, `BaseCost * mult^level`, then write the derived stat onto the live component.

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

public int GetCost(int level, UpgradeConfig c)            // -1 == maxed
    => level >= c.MaxLevel ? -1 : (int)(c.BaseCost * MathF.Pow(c.CostMultiplier, level));
```
(digging_simulator: Code/ShopTerminal.cs:9 UpgradeConfig, :49 GetCost geometric formula, :81 BuyUpgrade writes `drill.DigRadius = BaseStat + level * StatPerLevel` onto the live component.) Because buying mutates live components, your save loader must **re-apply every upgrade stat on load** (`RestoreLevels`). Note: this is a desktop game so `MathF.Pow` works — but in the s&box **sandbox** `MathF` is restricted; prefer `(float)Math.Pow(...)` if your code runs sandboxed (see sbox-build-feature gotchas).

**5. Save + sanitize.** Persist per-account JSON, clamp every field on load, then re-apply stats. For MP value, sign it.

```csharp
// Local per-Steam-ID JSON. Save() clamps everything, then Sign().
FileSystem.Data.WriteJson($"corp_progress/steam_{steamId}.json", save);
// Load: verify signature -> ValidateAndSanitize (clamp every field, clamp levels to MaxLevel)
//       -> version-migrate -> re-apply stats -> rewrite sanitized file back.
```
(chop_the_forest: Code/Player/LumberSteamProgressSave.cs:434 ValidateAndSanitize, :957 FNV-1a signature.) The signature payload is **version-conditional**: `BuildCanonicalPayload` appends a field only `if (save.Version >= N)`, so a save signed under v3 still verifies after the code reaches v8 — never insert new fields unconditionally. FNV-1a is anti-tamper deterrent, NOT cryptographic. For a paranoid single-DTO variant with rotating multi-file backups + heuristic loss-recovery, see s_miner: Code/StatsManager.cs:1737 SaveStatsToDisk, :1855 rotation, :1422 TryRecoverFromSafetyBackupIfProgressLost. Full treatment in `references/systems/save-persistence.md`.

**6. Prestige.** A reset method: bank a permanent multiplier from current totals, zero the run-level currencies/levels, bump `PrestigeLevel`, save. The multiplier feeds back into harvest power. (chop_the_forest: PrestigeBalance table, same struct[] shape as the axe table.) Multi-tier resets (rebirth/ascension that reset prestige) are just stacked layers — see s_miner: Code/StatsManager.cs:500 prestige/ascension const tables. Details in `references/systems/progression-upgrades.md`.

## Two networking patterns you will need (MP only)

**Request -> Apply -> Confirm** for any state a non-host client must change. Client applies optimistically, calls `[Rpc.Host]` Request which re-checks caller + re-clamps, host applies, `[Rpc.Owner]` Confirm echoes the host's authoritative numbers back. Clamp at all three layers — the host never trusts the wire.

```csharp
[Rpc.Host]
void RpcRequestSelectPet(string petId)
{
    if (Rpc.CallerId != Network.OwnerId) return;   // re-validate caller
    petId = SanitizePetId(petId);                  // re-clamp argument
    SelectedPetId = petId;                          // host applies (writes the [Sync] prop)
    RpcConfirmPet(petId);
}

[Rpc.Owner] void RpcConfirmPet(string petId) => ApplyConfirmedPet(petId);
```
(chop_the_forest: Code/Player/PlayerProgression.cs:922 RpcRequestApplyBackendProfileState, :941 RpcConfirm.)

**No-RPC event replication** for one-shot effects (hit shake, chop sound). Host increments a `[Sync(FromHost)]` int; each client caches an `_observed` copy and plays the effect locally when they differ. Cheap, ordered, survives late-join — but suppress it for ~0.35s after spawn so remote clients don't replay stale effects.

```csharp
[Sync(SyncFlags.FromHost)] int HitEffectSequence { get; set; }
int _observedHitEffectSequence = -1;

protected override void OnUpdate()
{
    if (HitEffectSequence != _observedHitEffectSequence)
    {
        _observedHitEffectSequence = HitEffectSequence;
        if (TimeSinceSpawn > 0.35f) PlayHitFeedback();   // suppress stale replays on join
    }
}
```
(chop_the_forest: Code/World/HarvestableResource.cs:49 sync sequence ints, :150 observed-copy init.) In s_miner the inverse idiom appears: an `[Rpc.Broadcast]` method that immediately `if (!Networking.IsHost) return;` — a broadcast runs on every peer, the guard makes only the host act, which re-broadcasts the result. That's how it routes client->host commands without a dedicated server RPC (s_miner: Code/MineReset.cs:241 CastVoteYes).

## Standout patterns worth copying

- **Spatial hash grid** for O(nearby) world queries instead of scene-wide scans + a single round-robin update runner to cap per-frame cost (chop_the_forest: HarvestableResource.cs:10, :163).
- **Virtual entities** — compute a dense procedural distribution as a seeded `Dictionary<coord,index>` with NO GameObjects; instantiate the real prefab only when the player digs near it and the voxel is still solid (digging_simulator: Code/OreGenerator.cs:49 GenerateVirtualOres, :95 RevealOresInSphere). Massive perf win for "millions of potential things".
- **Diff-based save** — persist only voxels that differ from the deterministic seeded default + the seed, regenerate the default on load and replay the diff. Tiny saves for an arbitrarily large destructible world (digging_simulator: Code/DiggableZone.cs:261 GetTerrainChanges).
- **One bool-returning resource gate** drives every consumer: `ConsumeBattery(amount)->bool` is the single chokepoint for drill cost-per-dig, jetpack cost-per-second, and triggers the death/respawn penalty when empty (digging_simulator: Code/PlayerResources.cs:29).
- **Pure power-law XP curve**, trivially liftable: `xpForLevel = 320 * level^2.55`, sync the derived level for nameplates (s_miner: Code/LevelingTable.cs:140, StatsManager.cs:468 `[Sync] NetworkedLevel`).
- **Recharge over Unix time, not `TimeSince`** — store last-recharge as Unix seconds so timed free cases / idle accrual survive save/reload and session boundaries (chop_the_forest: PlayerProgression `FreeCaseLastRechargeUnix`; see `references/systems/idle-offline.md`).

## Verify live

s&box's API shifts between SDK versions — reflection is the source of truth, not this doc or training data. Before writing against an unfamiliar type, confirm it: `describe_type` / `search_types` for `Component`, `Sandbox.Networking`, `Sandbox.Services.Stats`, and the `[Sync]` / `[Rpc.Host]` / `[Rpc.Owner]` / `[Rpc.Broadcast]` attributes; `Scene.Trace` for the harvest trace. Stop play mode before scene edits; screenshot visual changes and read the PNG.

Cross-links: see the **sbox-api** skill for authoritative type/method signatures, and the **sbox-build-feature** skill for the screenshot-driven build loop and the sandbox gotcha list (MathF restricted, head-bone case sensitivity, Cloud assets ephemeral).

## Corpus refresh (2026): more reference implementations

Eight more shipped tycoon/idle games mined since the original three. The three above stay the backbone (chop_the_forest = host-auth, s_miner = client-auth hub, digging_simulator = single-player). Below is **only net-new** material — pick the variant matching your authority posture and economy shape.

### Authority + offline: pick your posture, then your time model

The corpus now spans four authority models, not three. **farm_land is owner-authoritative** (listen-server, each player owns their farm GameObject via `NetworkSpawn(client.Connection)`; every mutator is `[Rpc.Owner]` re-checking `FarmLand.Occupant == player.Client`, with explicit `// todo: host validation` comments). It is the pragmatic middle ground between host-auth and client-auth — copy its *structure* but know the trust boundary is weaker than chop_the_forest's. **sneguborka is the gold-standard host-auth save**: owner writes its own `Game.Cookies`, host re-applies through a re-validation pipeline (see below).

Two offline-accrual idioms beyond chop_the_forest's Unix-recharge:
- **UTC-tick deltas EVERYWHERE, never accumulators** (phenodex). Every timer is `[Sync(FromHost)] long XStartedAtTicks`; `SecondsSince() = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - started).TotalSeconds`. Growth, water decay, and rent bills all derive from ticks, so they advance correctly across save/reload with zero catch-up code. One `DEV_TIME_SCALE` const compresses the whole clock for testing (phenodex: `Cultivation/Plant.cs`, `Player.cs` `NextBillTicks`).
- **One tick model, real-time + offline via a `simulate` flag** (farm_land). The *same* `Crop.UpdateStage(bool simulate)` runs live and during catch-up; `simulate` only gates side-effects (particles/sounds/logs/event-posts). Eliminates the classic "offline math drifts from online math" bug. Clamp iterations to **remaining work, not elapsed time**: `stagesToSimulate = Math.Min(elapsedStages, crop.StageCount - crop.CurrentStage)` so a week-away player doesn't spin a runaway loop (farm_land: `Persistence/FarmStateManager.cs`).

```csharp
// sandmoney_: InfrastructureManager.SimulateOfflineEarnings — bounded, penalized, mid-cycle resume
var offline = Math.Clamp(NowUnix - LastSeenUnix, 0, 86400);   // 24h cap
if (offline <= 300) return;                                   // 5-min floor: no "+$3 while away" spam
var cycles = (int)(offline / cycleSeconds);
Earn(cycles * revenuePerCycle * 0.5);                         // 50% offline penalty
CycleStartedAt = NowUnix - (offline % cycleSeconds);          // re-seed so the bar resumes mid-cycle
```
(sandmoney_: `Core/InfrastructureManager.cs`; bots use a **fuel-bounded** variant — earn for `min(offline, FuelSeconds)`, burn that fuel, disable if dry, so passive income is never infinite.)

### Save/persistence: five patterns the original doc doesn't have

The original covers `ValidateAndSanitize` + FNV signature. These are net-new shapes:

1. **Interface-discovered, ordered, versioned save** ★ (fair — the most reusable save system in the corpus). Savers implement `ISaveDataProperty { PropertyName; int PropertyOrder; WriteValue/ReadValue(Scene) }`; `PersistenceManager.FindProperties()` discovers them three ways (scene singleton Components, `GameObjectSystems`, and plain classes via `TypeLibrary.GetTypes<ISaveDataProperty>().Create()`), then `DistinctBy(PropertyName).OrderBy(PropertyOrder)`. Each section is try/caught so one corrupt blob can't nuke the save. Versioning is brutally simple — `if (saved != CurrentSaveVersion) { DeleteFile(path); return false; }` (delete-on-mismatch, no migration). A `SpawnedPrefabSaveData<TComponent,TSaveData>` base saves *every placed prefab instance* grouped by source path and respawns on load (fair: `Persistence/PersistenceManager.cs`, `ISaveDataProperty.cs`).
2. **Polymorphic per-entity blob via `JsonElement` + a string `Type` discriminator** (farm_land). A heterogeneous building list round-trips without a discriminated-union serializer: `GridBuildingData { string BuildingType; ...; JsonElement BuildingSpecificData = JsonSerializer.SerializeToElement(plotData); }`; on load `data.BuildingSpecificData.Deserialize<GridFarmPlotData>()`. Each building type self-registers a handler (`GridSaveHandlerRegistry.Register(new GridFarmPlotSaveHandler())`) so adding a buildable is one class + one line, save loop untouched (farm_land: `Persistence/SaveHandlers/GridSaveHandlers.cs`).
3. **Save-light "derive on load" + skip-empty** (farm_land). Don't persist what you can recompute: `StatTracker` challenges write `Data = null` and re-derive progress from the canonical `Statistics` store; zero-progress challenges are skipped entirely; completed ones store only `{Type, IsCompleted}`. Big save-size + migration-resilience win.
4. **Multi-slot save with live screenshot thumbnails + sidecar meta** ★ (fill_the_void — the best save UX in the corpus). Each slot is three files: `slot_N.json` (full state), `slot_N.meta.json` (tiny `{Timestamp,Money,...}` so the menu lists slots *without* parsing the save), and `slot_N.thumb.png` captured at save time via `using var bmp = new Bitmap(256,144); camera.RenderToBitmap(bmp); FileSystem.Data.WriteAllBytes(thumb, bmp.ToPng().ToArray());`. Scene handoff is a **static pending-load channel**: `StartLoadGameFromSlot` sets `pendingLoadJson`, loads the scene, then `GameState.OnStart` calls `ConsumePendingLoadJson()` (fill_the_void: `Code/Components/Game/MainMenuSlotService.cs`).
5. **Forward-compatible save with NO version int** (fill_the_void). The positional `record SaveData` is v1; every field added later is `{ get; init; } = default;` so old JSON deserializes with null/zero and new code tolerates it. Contrast with fair's version-int + delete-on-mismatch; both are valid, this one is migration-free.

**Tamper resistance (two flavors, both because `System.Security.Cryptography.*` is whitelist-blocked in s&box):**
- **FNV envelope + pepper + SteamId** (sandmoney_). `Hash = FNV-1a(json + localSteamId + "secret_pepper")`; recompute on load. SteamId in the hash means a save copied to another account fails (and is *not* `.bak`-eligible). A hash mismatch is **accepted** (so adding a defaulted field doesn't wipe saves) but flagged → schedule a re-save; only a *parse* failure falls back to `.bak`. Critical footgun documented: `Math.Max(0, NaN)` returns NaN → `JsonSerializer` throws → save silently dies, so NaN-sanitize every float/double before write (sandmoney_: `Persistence/PlayerSaveHasher.cs`).
- **Hand-rolled SipHash-2-4 128-bit HMAC + constant-time compare** (sneguborka — when FNV isn't enough). Hand-rolls `KeyedHash128` (two SipHash invocations, different key halves) and a `FixedTimeEquals` because `CryptographicOperations.FixedTimeEquals` is also blocked. **Sign the base64 payload** (exactly what crosses the wire) to avoid unicode-normalization drift. Full host-apply pipeline: size-cap *before* any parse (megabyte-JSON memory-DoS defense) → single-shot `_loadApplied` replay guard (blocks load→buy→load money re-apply) → envelope parse + version → HMAC verify → inner DTO parse + version → field clamps (`MaxLegitMoney`, owned-tool whitelist, bag-tier domain). Plus two **sentinel-save guards** that fix dated real bugs: reject an all-zero save (a fresh joiner mustn't wipe the just-granted starter tool) and reject owned-tools-but-empty-hotbar (two `[Sync]` fields can replicate a frame apart) (sneguborka: `Player/PlayerPersistence.HostApply.cs`).

### Economy: variants beyond "one Money int, clamp it"

- **Reason-tagged transactions that auto-generate stat keys** (fair). The economy self-instruments: `TakeMoney(amount, reason="Other")` does `Stats.Increment($"money_spent.{reason.ToIdentifier()}", amount)` so new content gets its own analytics/goal-eligible stat with zero wiring; the same `$"building.used.{Title.ToIdentifier()}"` pattern is everywhere. Daily upkeep is *derived not stored*: `OnNewDay()` deducts `building.Cost/5` per building + per-path + staff wages (fair: `Park/ParkManager.cs`, `Stats.cs`).

```csharp
// fair: ParkManager.TakeMoney — affordability gate + dual-key self-instrumenting spend
public bool TakeMoney(int amount, string reason = "Other") {
    Assert.True(Networking.IsHost);
    if (Money < amount) return false;
    Stats.Increment("money_spent", amount);
    Stats.Increment($"money_spent.{reason.ToIdentifier()}", amount); // per-reason key, auto-created
    Money -= amount; return true;
}
```

- **`double` for cash, `float` for the volatile asset** (sandmoney_). Money must survive to ~1e28 (top prestige rank) so it's `double` everywhere; the small quantized traded coin stays `float`. Every credit/debit is **NaN/Infinity-guarded at the mutation site** (early-return) — the discipline that stops one bad multiply poisoning a `[Sync]` and corrupting the save. `long Money` (sneguborka) is the cheaper middle option for a long single session.
- **Money mutators that return `bool` + normalize at the boundary** (fill_the_void). `SpendMoney(amount) → bool` (affordability gate) lets callers branch (slots, shop, quest-cancel penalty); `Normalize` rejects NaN/Inf, floors at 0, and `MathF.Round`s to whole coins on every delta (fill_the_void: `Code/Components/Game/GameState.cs`).
- **The resource is a rigidbody, not a counter** (lumberyard). A chopped log is a real physics object the entire pipeline: grab it, drop it on a conveyor whose `BoxCollider.SurfaceVelocity` pushes it, a trigger volume transmutes it to the next tier, a sell zone reads `Collider.Touching` and destroys-for-money. **"Sell" is a collision event, not a UI action** (`SellPoint.OnCollisionStart`, a `SellSucker` that `ApplyForce`s loose wood toward a pad). Balance lives entirely in `TreeResource` float multipliers (`PlankMultiplier=2`...) — designers retune by editing `.tree` assets (lumberyard: `Code/Trees/PlankCutter.cs`, `Conveyor.cs`, `Wood.cs`).
- **Multiplier-stacking pricing as the whole model** (scoops): `pay = base * (hungry?2:1) * HotZone.BonusAt(pos)/*3x*/ + favouriteBonus + perfectScoopBonus`. **Dynamic per-item inflation** (fill_the_void): `price = basePrice * MathF.Pow(1.5f, purchaseCountSoFar)` with the per-item count persisted, so each repeat buy is ~50% dearer.
- **`NumberFormatter.ToFormattedString()`** (sandmoney_) — short-scale big-number suffixes (`k,M,B,T,Qa,Qi,Sx,Sp,Oc,No,Dc`) with trailing-zero trim, an extension on `double/long/float`. Copy-paste reusable for any idle UI past a million.
- **Debounced "+N / -N" value floater** (lumberyard). `AddMoney` accumulates `RecentMoneyChange` and resets `TimeSinceMoneyChange`; if >5s since the last change the accumulator resets first, so rapid sells stack into one growing "+1,234" popup. The HUD hashes `(Money, RecentMoneyChange, TimeSinceMoneyChange > 5f)` — note the bool-ifying so it re-renders once when the floater should vanish (lumberyard: `LumberPlayer.cs`, `MoneyHud.razor`).

### Prestige: bracket-currency + full-wipe-then-reapply

The original covers "bank a multiplier, zero the run." Two sharper templates:
- **Bracket-based (not cumulative) prestige currency** (sandmoney_). `ComputeHeritageCoinsForNetWorth(nw)` returns 0 below $1T then 1/3/5/8…35 by power-of-ten bracket — you earn coins for your *current* bracket only, which discourages repeated cheap resets. `ResetProfileCore` wipes ~40 fields + destroys bot GameObjects, then **re-applies persistent perks and re-runs the FTUE bootstrap** so the new run starts with its purchased head-starts. A `CmdBuyHeritageBonus(id)` meta-shop with tiered prereqs spends the currency (sandmoney_: `Player/PlayerTrader.cs` Heritage region).
- **Wipe + re-grant starter + re-lock gates + teleport** (sneguborka). `WinterReset` wipes money/tools/upgrades/bags, re-grants the starter Spoon + T1 bag (re-deriving the "first Cost=0" item without recreating the GameObject), re-locks key-gated walls, clears the host-only grant-dedupe set so keys re-earn, and **teleports on confirm** via reservoir sampling over `SpawnPoint`s with `Transform.ClearInterpolation()` so the Rigidbody controller doesn't visually lerp across the map (sneguborka: `Player/PlayerPrestigeController.HostApply.cs`).

### Networking idioms the original doc lacks

- **Keep mutable state on the HOST-owned object, not the client-owned one** ★ (scoops — non-obvious, bites everyone). A player's van is *client-owned* so it drives responsively, but s&box can't reliably let the host write a client-owned object's `[Sync]` fields at runtime. So the van's upgrade `Level` lives on the host-owned `Empire` and the van reads through: `public int Level => OwnerEmpire?.VanLevel ?? 1;` (scoops: `IceCreamVan.cs`, `Empire.cs`).
- **`NetworkMode.Never` for procedurally-built deterministic visuals** ★ (scoops, lumberyard, phenodex, sneguborka — pervasive). If the visual is networked, the host's mesh replicates into a late-joiner's snapshot AND that client *also* runs `BuildVisuals()` → everything double-renders. Rule: sync the *data* that drives the visuals, build the geometry locally under a `NetworkMode.Never` child, network only the root transform.
- **Host-migration recovery from `[Sync]` state** ★ (sandmoney_). A host-only sim (the market) corrupts on host migration because the new host inherits only replicated values, not private fields. Each component caches `_wasProxy` and in `OnFixedUpdate` detects the proxy→authority flip (`if (_wasProxy && !IsProxy) RecoverFromHostMigration();`), rebuilds private state from the synced ring buffer, and `Network.Refresh()`es; also wired from `INetworkListener.OnBecameHost`. Cheaper cousin: **idempotent host-spawned singletons** (scoops) — count what already exists before topping up (`if (HotZone.All.Count >= N) _spawned = true;`) so a migrated host doesn't double-spawn shared world objects.
- **`RealTime.GlobalNow`, not `Time.Now`, for a static rate-limiter** ★ (sneguborka — silent F5 trap). A `static` per-`Connection` cooldown gate must use `RealTime.GlobalNow`; `Time.Now` resets each editor F5, so a static limiter surviving the hotload sees stale future-timestamps and silently rate-limits *every* RPC for minutes. Drop a `if (_since < MinInterval) return; _since = ...;` guard on every economy RPC — bridge-built RPCs never have one (sneguborka: `Player/RpcRateLimiter.cs`).
- **Re-validate the world, not just the caller** (scoops). `RpcSellOnFoot` re-checks server-side distance to the target NPC (`npc.WorldPosition.Distance(self) > 260f → reject`) so a client can't sell across the map; `RpcUpgrade` re-checks `v.OwnerId == emp.OwnerId`. The client RPC is a *request*; the host re-derives everything.
- **List<int> doesn't survive s&box's JSON save round-trip** (scoops — concrete gotcha). Only scalar properties persist. Store collections as **comma-separated strings** and hand-encode/decode (`TruckTunesCsv`, `ToCsv/FromCsv`); bitmask unlocks (`Flavours.Owns/Grant` via `mask | 1<<i`) are even cheaper to sync and persist. farm_land hits the same wall crossing RPCs and re-serializes each `GridBuildingData` to a `string[]` before the wire.

### Building/placement: grid + region connectivity + two-phase ghost

The original doesn't cover placement at all; four games converge on the same shape.
- **Two-phase placement with a shader-attribute ghost** ★ (fair, phenodex, lumberyard, farm_land). Client owns a ghost clone and validates locally; host re-validates + spends in `[Rpc.Host]`. Tint the ghost via a shader attribute, **not** by swapping materials: `SceneObject.Attributes.Set("Ghost", overlapping ? 2 : 1)` (1=green valid, 2=red invalid) in fair; lumberyard feeds `Attributes.Set("WrongBuildLerp", overlapping ? 1 : 0)`. Strip the phantom to inert: farm_land clones the prefab, sets `NetworkMode.Never`, and **removes every component except `ModelRenderer`/`BuildingPhantom`**. `SnapToGrid` + 0–3 quarter-turn rotation, and **swap X/Y extents when rotation is odd** before the AABB overlap test (lumberyard: `Code/Tycoon/TycoonMain.cs`; fair: `Park/Buildings/BuildingPlacer.cs`; farm_land: `Common/Building/BuildingPlacer.cs`).
- **Networked spatial grid** (farm_land): `[Sync] NetDictionary<Vector2Int, GridCell>`; world↔grid via `WorldRotation.Inverse` so the grid honors the plot's rotation. Persist **relative** `LocalPosition` + int rotation per building so the plot is portable (lumberyard `BuildData`, farm_land).
- **Flood-fill region connectivity for O(1) reachability** ★ (fair — big perf/correctness win for grid AI). `DirtyRegions()` flood-fills walkable cells from the entrance; every disconnected island gets a `RegionId`, so `IsWalkable(a,b)` is an O(1) `a.RegionId == b.RegionId` check and agents never even *start* an A* to an unreachable ride. Debounced invalidation: edits set dirty neighbors, recalc after 3 stable frames and bump `NavigationVersion++` so agents detect stale routes cheaply. A* is **pooled** (open/closed sets as fields, rented result lists) for zero per-path GC (fair: `Park/GridManager.Regions.cs`, `AI/GridNavigation.cs`).
- **Plot-ownership lifecycle** (lumberyard). Buy an unowned plot (`Tycoon.Network.Owner == null → TakeOwnership() + LoadData()`), and on owner-disconnect clone a fresh unowned `tycoonbase.prefab` and drop ownership so the plot recycles for the next player. Loose world resources use the inverse: spawn world-owned (`Network.DropOwnership()` on tree + children), a chop `AssignOwnership(Rpc.Caller)`s the falling piece — the corpus's clearest "neutral resource → claimed on touch" recipe (lumberyard: `Code/Tycoon/...`, `OwnerTransfer.Takeover`).

### Data-driven engines that cut across systems

- **String-keyed stat-modifier bus** ★ (farm_land — the single most reusable idea across genres). `Buff` is a `GameResource` whose `Effects` is `Dictionary<string, {float Value; Multiply|Add|Set}>`. Gameplay asks `BuffManager.GetModifier("farming.mutation.chance")`, which folds every active+passive matching effect; callers do `baseValue * GetModifier(key)`. A documented dotted-key namespace (`farming.yield.{type}`, `economy.market.{itemId}.sellprice`, `fishing.level.bonus`) means designers author buffs as assets and code reads them by string — any genre (tycoon/RPG/survival) can adopt it (farm_land: `Common/Players/Buffs/Effect.cs`, `BuffManager.cs`).

```csharp
// farm_land: BuffManager.GetModifier — fold Multiply/Add/Set across all matching effects
public float GetModifier(string key) {
    float r = 1f;
    foreach (var e in AllActiveAndPassiveEffects.Where(e => e.Key == key))
        r = e.OperationType switch { Multiply => r * e.Value, Add => r + e.Value, Set => e.Value, _ => r };
    return r;   // callers: baseValue * GetModifier("fishing.level.bonus")
}
```

- **Effect lookups as `switch` expressions, not stored numbers** (sandmoney_). `GasReduction => GasLevel switch { 5 => 0.55f, ... }`; the save stores only the int level, so a balance change never needs a migration. Pairs with a **transactional purchase with rollback**: deduct first, run `applyEffect()` in a try, `catch { AddMoney(cost); }` to refund, then a **critical (throttle-bypassing) save** so an upgrade is never lost to the 10s save interval (sandmoney_: `UpgradeSystem.cs`).
- **Deterministic daily-rotation from a day-seeded RNG** (farm_land). `new Random((int)(DateTime.UtcNow - epoch).TotalDays)` picks the day's barter order; every client/server independently computes the *same* rotation with zero networking, cached per day. Pair with a per-player daily stock counter that resets on date change and saves. The clean way to do "daily shop / wheel" without server state (farm_land: `Common/Economy/MushroomDealer.cs`).
- **Event-driven FTUE with guarded `TryAdvance(from, to)`** (phenodex). No "Next" button — an 11-step enum persisted on the player, advanced only by `TryAdvance` calls sprinkled at the exact gameplay moments (BuySeed → BuyFirstPot, placement commit → PlantSeed). The guard makes each transition idempotent and reorder-safe (phenodex: `UI/TutorialManager.cs`).
- **Named-rules singleton + service-locator** (fill_the_void). All components find managers via `scene.Directory.FindByName("GameRules").FirstOrDefault()?.Components.Get<GameConfig>()` with a one-lookup `FindGameData(out config, out state)` helper, decoupling ~30 components from hard references and surviving scene reloads. The config self-sanitizes every frame (`NormalizeRuntimeValues()` clamps designer typos) and bumps a `ChangeVersion` only on a real `record struct` snapshot diff (fill_the_void: `GameRuntimeContext`, `GameConfig`).

### Genetics / collection meta (if your tycoon breeds things)

phenodex is the definitive `genetics-breeding` reference and net-new to this doc:
- **Value-type genome + Box-Muller gaussian inheritance + generation variance reduction** ★. The heritable unit is a `struct StrainGenome : IEquatable` (lives in dicts, hashes, serializes). Crossing is per-stat gaussian recombination where `varianceMult = Math.Max(0.20f, 1f - childGen * 0.10f)` shrinks the stddev as a line is self-crossed deeper — one line turns breeding into a long-term IBL-stabilization game (F1 wild → F8 tight). A `mean*0.06` term injects spread even when parents are identical so self-crossing still explores (phenodex: `Cultivation/StrainGenome.cs`, `Breeding.cs`).
- **Identity-by-bucket-hash to bound a combinatorial library** ★. The genome hash is computed only from `(Lineage, MutationType, Species, IsAutoflower)` — *not* stat values — so infinite phenotype rolls collapse into ~1-3 named buckets per lineage; stat variance instead drives a per-bucket "best-of" score. Clean separation of *identity* vs *quality*.
- **Deterministic procedural naming shared across players**. `ProceduralNamer` seeds a noun pool by an FNV-1a hash of the canonical lineage, so two players who roll the same cross see the same name; the stat-tier adjective is applied at *display time only* so it doesn't pollute identity.

farm_land's lighter take: mutations are an `ICropMutation` registry (`GiantMutation`, `GoldMutation` with `BaseChance`, `CalculateChance`, `GetYieldMultiplier`); on final stage `TryMutate` shuffles the registry and rolls `chance * buff("farming.mutation.chance")`; the mutation id is `[Sync, Change("OnMutationUpdated")]` so the model swaps on all clients.

### Crowds + populations (when "spawning-waves" isn't combat)

- **Centralized single-pass tick for hundreds of agents** ★ (fair). A `GameObjectSystem` iterates `Scene.GetAll<Agent>()` **once** on Update (movement/anim) and FixedUpdate (AI/needs); agents do **not** override `OnUpdate` themselves. One cache-friendly loop, cheap `!Active` skips, built-in `Stopwatch` timing. Staggers first ticks (`NextTick = Random.Float(0, rate)`) and scores at a fixed 0.25s. The corpus's best "simulate a crowd" pattern — teaches "don't put OnUpdate on 500 components" (fair: `AI/AgentTickSystem.cs`).
- **Utility-AI scorer on top of behavior trees** (fair). Each tick, score every `AgentAction` child component and switch to the highest; actions are components → fully data-authorable on a prefab, no enum/switch. Allocation-free need ranking via a reused `(Need,float)[16]` scratch buffer + insertion sort (not LINQ) to survive 500+ agents (fair: `AI/ActionSystem/AgentActionController.cs`).
- **Reserve-on-intent crowd caps** ★ (scoops). A serving stop has a short serve line (`MaxQueue=2`) AND a wider committed-crowd cap (`MaxChasing=7`); a customer must `Claim(this)` (reserve a slot in a `HashSet`) the *moment it decides* to come, before it arrives, so no more than the cap ever converge — prevents "the whole map mobs one van." Plus a 5s grace lock before a closer van can poach a committed customer, and a stuck-detector that re-roots a route after 2.5s of no progress (scoops: `CustomerNpc.cs`).
- **Rate-limited crowd spawn** (scoops). `GameDirector` keeps ~70 NPCs alive but spawns one every 0.15s rather than all at once, because spawning dozens of clothed citizens in a single frame stalls the game. Dress NPCs with `ApplyAsync` (sync apply for a whole crowd stalls for seconds), seeded by network id for deterministic-across-clients outfits.
- **Dynamic spawn target with price elasticity + seasonality** (fair): `target = Σ building.AddedGuests * ratingMult / max(0.25, admissionFee/50) * (peakSeason ? 2 : 1)`, clamped to a cap — a population that responds to your prices and attractions, not a fixed wave count (fair: `AI/Guests/GuestManager.cs`).
- **Weighted-random event director via tagged prefabs** (fair, sandmoney_). fair discovers event prefabs by metadata (`ResourceLibrary.GetAll<PrefabFile>().Where(x => x.GetMetadata("Type","") == "Events")`), rolls on an interval, filters already-active, and cumulative-weight-picks by `Probability`; "content = tagged prefab, discovered by metadata, weighted-picked" generalizes to loot/weather/incidents. sandmoney_ adds an **anti-monotony filter** (after 2 same-direction events, restrict the pool to the opposite drift) and dual independent event clocks (fair: `Park/Events/EventManager.cs`; sandmoney_: `Core/WorldEventManager.cs`).

### Standout sub-genre engines (lift wholesale if relevant)

- **Procedural OHLC market / price engine** ★ (sandmoney_ — no other corpus game simulates a tradeable market). A singleton `[Sync] float CurrentPrice` random-walk with six layered "phase" regimes (accumulation→breakout structure, not pure noise), progressive mean-reversion that ramps with distance (`MathF.Pow(ratio, 2.5)`) so price is soft-bounded without a hard clamp, a **lookahead buffer** (`[Sync] NetList<float> FuturePrices`, 40 ticks the bots read) for deterministic "smart" bots, and a **ring-buffer candle history** as a flat `[Sync] NetList<float>` of OHLC quads with a wrapping head index (1 hour of 1s candles in one flat list, O(1) append, no per-candle objects). Intentionally **not persisted** — only player wallets save; the market regenerates fresh each boot. Bots get a **self-balancing win-rate corridor**: they steer toward a quality-derived `targetWinRate` (82→98%), entering "recovery mode" or forcing deliberate losses to stay in corridor, keeping passive ROI predictable for economy balance (sandmoney_: `Core/MarketManager.cs`, `Player/TradingBot.cs`).
- **Procedural recursive tree gen + dynamic log splitting** ★ (lumberyard). L-system branches from scaled `cuttablelog.vmdl` segments with data-driven tapering (`taperMultiplier = Pow(TrunkTaperingRate, depth)`) and gradient leaves; `Branch.Split` cuts a procedural object at an arbitrary point and keeps **both halves valid, networked, and value-prorated** (`top.Value *= 1-splitPos`), rescaling each surviving segment's 0–1 position into the new piece's coordinate space (lumberyard: `Code/Trees/ChoppableTree.cs`, `Branch.cs`).
- **Client-predicted, host-validated GPU heightmap deformation** ★ (sneguborka). An R16F `Texture` heightmap + a texel-identical CPU float mirror (CPU mutate first, partial GPU re-upload of just the dirty rect via `Texture.Update`); carve locally for instant feedback, then `[Rpc.Host]` validates and `[Rpc.Broadcast]` fans out with `Rpc.FilterExclude(c => c == originator || c.IsHost)` — excluding **both** predictor and host (a broadcast always reaches the host, which would double-carve). Late-join sends an **RLE-vs-raw snapshot** (ship whichever is smaller behind a 1-byte format magic; heavily-carved fields of mostly-zero compress dramatically). Re-bind `Renderer.Attributes` in **both** `OnUpdate` and `OnPreRender` because engine paths (hotload, envmap rebake, LOD recreate) silently clear them — the cure for "the whole field renders as a dark slab" (sneguborka: `World/SnowField.cs`, `.Snapshot.cs`).
- **Gambling money-sinks with two-phase settlement** (fill_the_void). A weighted-symbol slot machine: `TrySpin()` rolls + stages a `PendingSettlement(bet, payout)` but does NOT touch money; `SettlePendingSpin()` (after the reel animation) applies only the **net delta** so the HUD shows one clean net result and money can't drain mid-animation. Reusable for any spin-then-reveal / loot-open (fill_the_void: `Code/Components/Interaction/SlotMachineComponent.cs`).
- **Combo multiplier with rising-pitch audio as the reward** (fill_the_void). Rapid sells in a shrinking window stack a multiplier AND extend the window; each sell plays the coin sound at a rising pitch (capped) so the player *hears* the streak climb — juice from pure parameter math (fill_the_void: `HoleSellComboBonusComponent`).
- **Two-tier weighted gacha (category roll → level-windowed pick) + pity + spot-cooldown**. farm_land fishing: first a category roll (Treasure/Junk/Fish), then a level-windowed weighted pick. sneguborka drops: a per-`Connection` **pity counter** (guaranteed roll at `PityFloor`) and a **spot-cooldown** so you can't farm one spot; drops spawn owner-only via `Rpc.FilterInclude` (farm_land: `Common/Fishing/FishingModel.cs`; sneguborka: `World/SnowField.DropRolls.cs`).
- **Leaderboard aggregation: Max vs Sum is load-bearing** (sneguborka). A monotonic count (winters survived) posts with absolute `Stats.SetValue` + `board.SetAggregationMax()`; a lifetime tally (photos found) posts with `Stats.Increment(+1)` + default Sum. Submit **owner-client-only** (stats bind to the local Steam account; the host can't post for another player), and pass the post-increment count as an RPC param so it can't race the `[Sync]` (sneguborka: `Services/LeaderboardService.cs`).

### Zero-asset / runtime-generated content (ship runnable before art)

- **Whole game from tinted engine primitives, with optional art-pack upgrade** (scoops). Every van/building/NPC is `Model.Cube`/`Model.Sphere` scaled by `worldSize / Model.Cube.Bounds.Size` and tinted via `ModelRenderer.Tint` (confirms the corpus note: procedural primitives + plain `Tint` render correctly, whereas `MeshComponent`/`PolygonMesh` face-materials are flaky). If a real FBX pack is present it upgrades — `Model.Load` treats `m.IsError` as null and **only caches successes** (caching a miss before the `.vmdl` finished compiling would stick forever) (scoops: `Build.cs`, `MegaCity.cs`).
- **Procedural runtime audio synthesis** (sneguborka — ships effectively zero authored sound files). A pure-float DSP library (`AudioSynth` + `WavWriter`): sine/sweep, white/pink(Voss-McCartney)/brown noise, ADSR, biquad LPF/HPF/BPF, and RMS-loudness normalization, baking every SFX in C# at load (sneguborka: `Audio/AudioSynth.cs`). Lighter: stream loose MP3s with `MusicPlayer.Play(FileSystem.Mounted, file)` (`Repeat=true`, `ListenLocal=false` for 3D, `.Position` updated each frame to follow a moving source) — and set `_file` even when `Play` fails so a bad path doesn't decode every frame (scoops: `MusicBox.cs`).
- **Render-to-texture screens** (fill_the_void). `Texture.CreateRenderTarget(name, RGBA16161616F, 512)` → `camera.RenderToTexture(rt)` → bind onto a `ModelRenderer` by trying multiple shader attribute slots (`TextureColor` → `Color` → `g_tColor`) with a `Material.FromShader(...).CreateCopy()` fallback; a static registry cycles a TV through cameras. The CCTV/mirror/portal/scoreboard primitive (fill_the_void: `CameraRenderToPlaneComponent`).

### Razor reactivity (the corpus consensus)

Every game drives panels with a hand-rolled `BuildHash()` over exactly the values it renders, so the panel only re-renders when one changes — the canonical s&box answer to "UI not updating / updating too much." Re-hash every frame *only while FX animate* (`fxActive ? (int)(_clock*30f) : 0`) so a money burst is smooth without constant idle re-renders (scoops `Hud.razor`, lumberyard `MoneyHud.razor`, sandmoney_ `MarketPanel.razor`, phenodex single central `Hud.razor` reading static state directly). For single-player HUD without `[Sync]`, push gameplay state into a `static` snapshot that raises a `static event Action Changed`; panels subscribe, with epsilon-guarded setters to prevent per-frame churn (fill_the_void: `SellComboBonusHudState`, `QuestMissionState`). sandmoney_ even builds a candlestick chart in pure Razor/CSS (absolutely-positioned `<div>`s with percentage geometry, aggregated to absolute candle boundaries so bars don't repack as the ring head moves).

### Read these games (net-new, by need)

- **fair** (`fair/Code`, namespace `HC3`) — the most architecturally mature management sim in the corpus. Go here for the **interface-discovered ordered versioned save** (`Persistence/`), **centralized agent-tick + utility-AI** for crowds (`AI/`), **grid + flood-fill region connectivity + pooled A*** (`Park/GridManager*.cs`), **reason-tagged self-instrumenting economy** (`Park/ParkManager.cs`), two-phase grid placement, and a weighted event director.
- **sandmoney_** (`sandmoney_/Code`, namespace `GravCoin`) — the **idle/incremental + prestige + market** reference. Procedural OHLC price engine, **host-migration recovery from sync state**, FNV+pepper+SteamId tamper save, bracket-currency prestige, fuel-bounded offline bots, `NumberFormatter` suffixes.
- **sneguborka** (`sneguborka/Code`, namespace—the `Снегуборка` dig-tycoon) — the **host-auth save + anti-cheat** gold standard. **SipHash HMAC** cookies save with owner-write/host-reapply pipeline, **`RealTime.GlobalNow` rate-limiter (F5 trap)**, client-predicted GPU heightmap, pity+spot-cooldown drops, Max-vs-Sum leaderboards, prestige-wipe with teleport.
- **farm_land** (`farm_land/Code`, namespace `FarmLand`) — **owner-authoritative** farming sim and the best **save + offline + data-engine** showcase. The **string-keyed stat-modifier bus**, one-tick-model `simulate`-flag offline, polymorphic `JsonElement` save handlers, deterministic daily-rotation, reconnect-safe orphaned-client recycle, ~55 data-authored challenges.
- **phenodex** (`phenodex/Code`) — the definitive **genetics-breeding + UTC-tick offline + cloud-backend** reference. Value-type genome + Box-Muller + IBL variance reduction, identity-by-bucket-hash, deterministic procedural naming, last-writer-wins backend reconciliation, event-driven FTUE.
- **fill_the_void** (`fill_the_void/Code`, namespace `FillTheVoid`) — **save-UX + gambling sinks + single-player reactive UI**. Multi-slot save with screenshot thumbnails + sidecar meta, forward-compat `record init` fields, deferred multi-frame restore, dynamic shop `price^count`, slot two-phase settle, combo rising-pitch, named-rules service-locator, RTT screens.
- **scoops** (`scoops/Code`) — **multiplayer crowd-sim + zero-asset + client-owned-object gotchas**. State-on-host-owned-sibling, `NetworkMode.Never` visuals, idempotent host-migration singletons, reserve-on-intent crowd caps, List<int>-doesn't-persist CSV workaround, primitive-art toolkit with optional FBX upgrade.
- **lumberyard** (`lumberyard/Code`, namespace `Lumber`) — the **physics-resource economy**. Sell-as-collision, conveyor `SurfaceVelocity`→shader timescale, ownership-transfer world economy + plot-lifecycle, procedural tree gen + log splitting, binary magic-header save, debounced value floater.

> Note (newly-mined non-tycoon games — facepunch.ss2, despawn.murder, barrelproto.ragroll): these are arena/round-based/physics titles, not tycoon/idle, so they contribute nothing net-new to *this* genre. Their patterns live in the deathmatch/arena and physics references.
