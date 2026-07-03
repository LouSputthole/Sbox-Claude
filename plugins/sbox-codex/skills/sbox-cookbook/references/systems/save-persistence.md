# Save & Persistence (modern s&box)

How to store, version, and replicate game state so it survives a reload, a rejoin, or a server restart â€” without trusting clients. Mined from 17 games / 64 implementations.

## What it IS and when you need it

"Save-persistence" is three loosely-related problems that ship together in almost every s&box game:

1. **Authoritative live state** â€” who owns the numbers (money/XP/inventory) at runtime and how they replicate. In MP this is host-owned `[Sync]` state; clients display, never write.
2. **Durable storage** â€” where state lives between sessions: local disk (`FileSystem.Data`), the s&box cloud (`Sandbox.Services.Stats`), or your own HTTP backend.
3. **Safe load** â€” versioning, migration, validation/clamping, and corruption recovery so an old or tampered save never crashes a newer build.

You need it the moment a player expects progress to still be there next session. Pick storage by *trust model*: client-local disk is trivially editable (fine for solo/co-op/cosmetics), the cloud/backend is the only place to put anything competitive.

## Canonical modern approach

### 1. One host-authoritative state component

Put currencies/levels on a `Component` as `[Sync(SyncFlags.FromHost)]` auto-properties with private-ish setters. Claim host ownership once via `NetworkSpawn()`; every mutator early-returns on `IsProxy` so only the host writes â€” clients see the replicated value (enifun.shop_manager: `Code/Economy/ShopFunds.cs:18`, `:36`, `:86`).

```csharp
public sealed class ShopFunds : Component
{
    public static ShopFunds Current { get; private set; }
    [Property] public float StartingMoney { get; set; } = 500f;
    [Sync( SyncFlags.FromHost )] public float Money { get; set; }

    protected override void OnStart()
    {
        Current = this;
        if ( Networking.IsActive && Networking.IsHost && !GameObject.Network.Active )
        {
            Money = StartingMoney;        // SaveManager overwrites once it loads
            GameObject.NetworkSpawn();    // host owns it; clients become IsProxy
        }
    }

    public bool SpendMoney( float amount )
    {
        if ( IsProxy || amount <= 0f || Money < amount ) return false; // host-only
        Money -= amount;
        SaveManager.MarkDirty();          // never write disk inline; flag it
        return true;
    }
}
```

`SyncFlags.FromHost` means clients **literally cannot write** the value â€” any client-initiated change must round-trip an `[Rpc.Host]` that re-validates (`Rpc.CallerId == Network.OwnerId`) and re-clamps before applying (vault77.chop_the_forest: `Code/Player/PlayerProgression.cs:37`; artisan.darkrpog: `Code/Player/Player.Roleplay.cs:9`).

### 2. A POCO DTO + dirty-flag autosave to FileSystem.Data

Collect all state into a plain `[JsonPropertyName]`-friendly class. Don't write on every mutation â€” set a dirty flag and flush on a timer / on quit. `ReadJson<T>` / `WriteJson` handle `Vector3`/`Angles` fine (facepunch.jumper: `Code/Player/JumperProgress.cs:22`, `:58`).

```csharp
public class JumperProgressData            // plain POCO â€” public props only
{
    public Vector3 Position { get; set; }
    public float BestHeight { get; set; }
    public int TotalJumps { get; set; }    // never reset â€” lifetime
}

string FileName => $"{Scene.Name}_progress.json";

protected override void OnStart()
{
    if ( IsProxy ) return;                 // owner/host loads, proxies don't
    Current = FileSystem.Data.ReadJson<JumperProgressData>( FileName, null ) ?? new();
}

public void Save() => FileSystem.Data.WriteJson( FileName, Current );
```

Key the filename by SteamId for per-account local saves (`player_saves/{steamId}.json` â€” stepdev.xtrem_road `Code/Persistence/PlayerSaveService.cs:95`). Flush from `OnUpdate` when `_dirty`, and force a final save on `Game.IsClosing`/`OnDestroy` (clearlyy.s_miner `StatsManager.cs:1684`). `ReadJsonOrDefault<T>(path, default)` is the no-throw loader (simalami.15_puzzle_master `LevelProgression.cs:280`).

### 3. Version, migrate, and sanitize on load

Store a `Version` int. Migrate on the **raw `JsonObject` before deserialize** so you can fix shapes the current C# classes no longer match. Run a ladder keyed by version-to-migrate-FROM, bumping after each step (enifun.shop_manager: `Code/Save/SaveMigrator.cs:23`, `:33`).

```csharp
public const int CurrentVersion = 4;
static readonly Dictionary<int, Action<JsonObject>> Migrations = new()
{
    { 1, MigrateV1ToV2 }, { 2, MigrateV2ToV3 }, { 3, MigrateV3ToV4 },
};

public static JsonObject Migrate( JsonObject save )
{
    var version = save?["Version"]?.GetValue<int>() ?? 1;
    while ( version < CurrentVersion )
    {
        if ( !Migrations.TryGetValue( version, out var step ) ) return null; // -> fall back to backup
        step( save );                  // e.g. save["Loans"] ??= new JsonObject();
        save["Version"] = ++version;
    }
    return save;
}
```

After deserialize, **clamp/repair every field** (`Math.Clamp(saved, 0, Max)`, length-bounded loops, null-coalesce each section `?? new()`) so explicit JSON `null` or an out-of-range level can't crash or cheat (enifun.shop_manager `Code/Save/SaveManager.cs:326`; artisan.darkrpog `Code/Player/Persistence/PlayerRoleplayStorage.cs:297`). The migration ladder is **append-only â€” never reorder it.**

### 4. Atomic write + fallback read (anti-corruption)

Write to a temp/backup first, then promote, so a crash mid-write never destroys the good save. On load, try primary â†’ backup â†’ legacy in order (enifun.shop_manager `Code/Save/SaveManager.cs:290`, `:164`; clearlyy.s_miner `StatsManager.cs:1855`).

```csharp
// atomic write: copy current -> .backup.dat, then overwrite primary
if ( FileSystem.Data.FileExists( Primary ) ) CopyFile( Primary, Backup );
FileSystem.Data.WriteAllText( Primary, json );
```

## Notable variations

- **Cloud Stats as the whole DB (no save file).** Currency/XP live in `Sandbox.Services.Stats` keyed strings; `Stats.Increment("bp_s1", 1)` for counters, `Stats.SetValue(name, v)` for current-selection, `Stats.Flush()` to push (Blind `Code/Player/Player.cs:786`; playbtg.elevator `Code/Actors/ElevatorPlayer.Score.cs:80`). **The host CANNOT write a remote player's Steam stats** â€” award via `[Rpc.Owner(NetFlags.HostOnly)]` so the code runs on the owning client:

  ```csharp
  [Rpc.Owner( NetFlags.HostOnly )]
  public void AwardKillBP() => Sandbox.Services.Stats.Increment( "bp_s1", 1 );
  ```

- **Your own HTTP/Supabase backend.** Use `Sandbox.Http.RequestAsync(url, "GET", headers: â€¦)` / `RequestStringAsync` â€” **never `System.Net.HttpClient`** (whitelist-blocked). Cloud is source of truth, local `.bin` is a fallback (lavagame.multis_cases `Code/Game/Save/SaveCloud.cs:83`, `:104`; namicry.gacha_crawler `Code/GameManager.cs:487`). Non-host clients often have no direct internet â€” relay through the host (vault77 `Code/game/BackendClient/HttpBackendTransport.cs:27`).

- **Scene-diff "save the world."** `Json.CalculateDifferences(baseline, current, GameObject.DiffObjectDefinitions)` stores only the patch vs the original `SceneFile`, plus a side-channel for ownership/`[Sync]` state; load does `Json.ApplyPatch` then `Game.ChangeScene` (artisan.darkrpog `Code/Save/SaveSystem.cs:128`; apl.sandboxwars `Code/Cleanup/CleanupSystem.cs:14`). Terrain/voxel games store edits as a **diff against the seed-reproducible default** to keep saves tiny (master.digging_simulator `DiggableZone.cs:261`). The cleanest standalone implementation lives in a **`GameObjectSystem<SaveSystem>`** (not a deletable manager Component): it diffs the live scene against the tracked `SceneFile` baseline, and writes a single envelope `{ Version, Patch, SceneProperties, NetworkOwnership, RequiredPackages }` â€” collecting **network ownership by SteamId** and the **required cloud packages** so a reloaded base re-mounts its addons before applying the patch. Load = mount packages â†’ `Json.ApplyPatch(baseline, patch)` â†’ `BuildPatchedSceneFile` â†’ `Game.ChangeScene`, then restore ownership/`[Sync]` state on the new instance. Version is a hard gate: a mismatched `Version` refuses the file rather than risking a malformed apply (klavs.basebuilder `Code/Save/SaveSystem.cs:10` system decl, `:165` `CalculateDifferences`, `:178` envelope, `:330` `ApplyPatch`â†’`ChangeScene`; same lineage in dexlab.sandbox-reforged).

- **Storage-API multi-slot save (reconcile scene objects by GUID).** Instead of one JSON blob, use `Storage.CreateEntry("save")` + `entry.SetMeta(...)` for scalars and `entry.Files.WriteJson(name, list)` per collection (inventory / world-objects / tasks), keyed by a slot `index` in the meta. On load, **reconcile the live scene against the saved object list by `GameObject.Id`/GUID**: destroy objects no longer in the save, reposition existing ones, and clone any that are absent â€” rather than wiping and rebuilding the whole world (bublic.stone_by_stone `Code/SaveSystemComponent.cs`). Good fit for a placeable-world tycoon where most objects already exist in the scene and only a few changed.

- **Durable IDs for runtime-spawned objects.** `GameObject.Id` is **not** stable across restarts â€” stamp your own `[Sync] Guid PersistentId` and write tombstones on delete (artisan.darkrpog `Code/Persistence/World/PersistentWorldEntity.cs:6`).

- **Collections can't be `[Sync]`'d.** Serialize a `HashSet`/list to a CSV `[Sync] string` and rebuild on proxies when it changes; do optimistic local add then reconcile from host (enifun.shop_manager `Code/Economy/PlayerProgression.cs:250`).

- **Bucketed sync for hot floats.** Accumulate a delta and only flush the `[Sync]` write when it crosses a threshold â€” don't replicate a fractional value 60Ã—/sec (artisan.darkrpog `Code/Items/MoneyPrinter.cs:49`).

- **Data-driven balance/content.** Tunables as `GameResource` assets (`[AssetType(Extension="â€¦")]`, read via `ResourceLibrary.GetAll<T>()`) so designers edit without recompiling (treehaven.sdiver `Code/Definitions/EquipmentResource.cs:46`; Blind `Code/Economy/ShopItemResource.cs:22`), or as `static readonly struct[]` tables indexed by level (vault77 `Code/Player/AxeUpgradeBalance.cs:21`).

- **Disconnect-safe delivery (ACK).** Owe an offline player a reward? Write it to a host-side pending file and keep it until the client ACKs receipt â€” at-least-once + client dedupe (lavagame.multis_cases `Code/Game/Core/GameManager.cs:174`).

## Gotchas

- **`SyncFlags.FromHost` is display-only on clients.** Any client-driven change MUST round-trip `[Rpc.Host]` and be re-validated + re-clamped there; never trust the incoming number (vault77 `PlayerProgression.cs:730`).
- **Client-local `FileSystem.Data` is trivially editable.** It's per-machine, not per-Steam-account and not cloud-synced. Fine for solo/co-op/cosmetics; use the cloud/backend for anything competitive (clearlyy.s_miner, goders.natural_disaster_survival, stepdev.xtrem_road all note this).
- **`Stats.SetValue` accumulates via `.Sum`, it does NOT overwrite.** Use `Increment` + `.Sum` for counts; use `SetValue` + `.LastValue` for current-selection/flags. Ownership-as-`Increment` is write-once â€” decrementing silently revokes a paid item (Blind `Player.cs`).
- **`FileSystem.Mounted` is read-only** (authored content via `ReadJson`/`FindFile`); writable user data is `FileSystem.Data` (simalami `LevelRepository.cs:20`).
- **`Vector3` JSON-friendliness varies** â€” `WriteJson` round-trips it, but some pipelines store explicit `X/Y/Z` floats to be safe (emg.everything_must_go `Code/Shop/ProgressBootstrapper.cs`).
- **Bumping `CurrentVersion` without a migration silently wipes/refuses old saves** â€” intentional in some games (emg `CurrentVersion=21`, seed+version gate), a bug in others. Decide deliberately.
- **Enum-keyed dictionaries break on reorder; ids persisted but labels re-resolved** â€” reordering enum values or table ids corrupts old saves (clearlyy.s_miner, stepdev.xtrem_road).
- **Apply the loaded save only after all singletons exist** â€” defer to the first `OnUpdate` where everything's `Current` (with a timeout fallback), or you write into half-spawned systems (enifun `SaveManager.cs:1480`).
- **Non-host clients must NOT load** (host is sole authority) â€” guard it, and clear all static queue/reservation state on load before respawning (enifun).
- **FNV-1a / XOR is anti-tamper deterrent only, NOT crypto** (vault77, lavagame). A committed Bearer/service_role key is only acceptable because the host is trusted/whitelisted â€” never copy a hardcoded backend key (namicry's committed token is the anti-pattern).
- **Signature/version payloads must be append-only & version-conditional** â€” add new serialized fields at the tail behind a `if (Version >= N)` check or older saves fail to verify/deserialize (vault77 `LumberSteamProgressSave.cs:957`; lavagame `SaveSerializer.cs`).

## Seen in

vault77.chop_the_forest Â· clearlyy.s_miner Â· master.digging_simulator Â· enifun.shop_manager Â· emg.everything_must_go Â· dimmies.terryspapers Â· artisan.darkrpog Â· apl.sandboxwars Â· playbtg.elevator Â· facepunch.jumper Â· yellowletter.terrys_crash_course Â· stepdev.xtrem_road Â· ataco.sdoomresurrection Â· khamitech.battledraft Â· goders.natural_disaster_survival Â· treehaven.sdiver Â· namicry.gacha_crawler Â· lavagame.multis_cases Â· simalami.15_puzzle_master Â· facepunch.ss1 Â· suburbianites.blindloaded (Blind) Â· klavs.basebuilder Â· dexlab.sandbox-reforged Â· bublic.stone_by_stone Â· stellawisps.lumberyard Â· lavagame.sandmoney_ Â· facepunch.fair Â· facepunch.ss2 Â· thefancylads.farm_land Â· despawn.murder Â· barrelproto.ragroll

Open the cited file under `D:/sbox-lessons/zips-code/<game>/` to read the real implementation.

---
**Verify live:** API drifts between SDK versions â€” confirm signatures against the installed SDK with the bridge's reflection (`describe_type` / `search_types`) before relying on a member, e.g. `describe_type Sandbox.Services.Stats`, `describe_type Sandbox.Http`, `search_types FileSystem`. Reflection is authoritative, not this doc.

**See also:** `sbox-api` (resolve exact type/method signatures) and `sbox-build-feature` (the screenshot-driven build loop to wire one of these recipes into a running game).

---

## Corpus refresh (2026): more reference implementations

Net-new patterns from the latest mining pass (esp. the two Facepunch official sims `facepunch.fair` + `facepunch.ss2`, plus `thefancylads.farm_land`, `lavagame.sandmoney_`, `despawn.murder`, `barrelproto.ragroll`). These do **not** repeat the canonical recipes above â€” they're alternative architectures and hardening details.

### Interface-discovered, ordered, versioned save (the cleanest "save the whole game" without a scene diff)

`facepunch.fair`'s `PersistenceManager` is the gold-standard alternative to both the single-POCO and the scene-diff approaches. Instead of one hand-maintained DTO, **every system that wants to persist implements `ISaveDataProperty`** and is discovered by reflection â€” so adding a saver never touches the save loop. `FindProperties()` collects savers **three ways** (scene singleton Components, `GameObjectSystem`s, and plain parameterless-ctor classes via `TypeLibrary.GetTypes<ISaveDataProperty>()` â†’ `type.Create<â€¦>()`), then `DistinctBy(PropertyName).OrderBy(PropertyOrder)`. `PropertyOrder` is load-ordering control (Park money at `-5000` loads *before* Buildings). Each section is wrapped in try/catch so one corrupt block can't nuke the file (`fair/Code/Persistence/ISaveDataProperty.cs`, `PersistenceManager.cs`).

```csharp
public interface ISaveDataProperty {                      // untyped
    string PropertyName { get; }  int PropertyOrder => 0;
    void WriteValue( JsonObject into );  void ReadValue( Scene scene, JsonObject from );
}
public interface ISaveDataProperty<T> : ISaveDataProperty {   // typed â€” implementer only writes a record
    T GetSaveData();  void LoadSaveData( T data );
    void ISaveDataProperty.WriteValue( JsonObject o ) => o[PropertyName] = Json.ToNode( GetSaveData(), typeof(T) );
}
```

Two layered specializations worth lifting: `Scenario : ISaveDataProperty<ImmutableArray<GoalGroup.SaveData>>` reloads goal-group progress by **name-matching** (resilient to reorder), and `abstract SpawnedPrefabSaveData<TComponent,TSaveData>` saves **every prefab instance** grouped by `PrefabSource` path and destroys+respawns them on load â€” the one-call answer to "persist all the things the player placed" (`fair/Code/Park/Buildings/Building.Persistence.cs`).

### Versioning gates that are deliberately destructive

`facepunch.fair` keeps versioning brutally simple: a `const int CurrentSaveVersion`, and on mismatch it **deletes the save and returns false** rather than migrating â€” an explicit "old saves are gone" policy, fine for a sim in active balance churn. Choose this vs the append-only migration ladder (canonical recipe #3) by whether your players expect continuity.

```csharp
public const int CurrentSaveVersion = 2;
if ( savedVersion != CurrentSaveVersion ) { FileSystem.Data.DeleteFile( savePath ); return false; }
```

`facepunch.ss2` takes the middle road in `ProgressManager.cs`: a `ReadJsonSafe<T>(path, fallback)` no-throw load (note this exact helper name â€” a sibling of the doc's `ReadJsonOrDefault`), an in-load **field rename via null-coalesce** (single `SelectedCharmId` â†’ `SelectedCharmIds` list), and a `StateVersion` int bumped on every mutation that Razor binds to for O(1) change detection (no deep compares). It also **duplicate-ID-checks the shop catalog at build** (`Log.Error` if two `ShopItemDef`s collide) â€” a cheap content-integrity guard.

### `FileSystem.OrganizationData` â€” cross-game-instance, per-Steam-org saves

The doc previously named only `FileSystem.Data` (per-machine) and `FileSystem.Mounted` (read-only). `thefancylads.farm_land` writes to **`FileSystem.OrganizationData`** (`farm_land/player.json` + `farm_land/farm.json`) so a player's progress is **shared across every game your org ships and persists across worlds** â€” a third storage scope to pick from (`farm_land/Code/Persistence/`). Same trust caveat as `FileSystem.Data` (client-local, editable), but org-scoped not game-scoped.

### Polymorphic save handlers + per-entity `JsonElement` discriminator (heterogeneous lists without a union serializer)

`farm_land`'s save loop never grows when you add a buildable. Each building type implements `IGridSaveDataHandler { string Type; â€¦ }` and self-registers (`GridSaveHandlerRegistry.Register(new GridFarmPlotSaveHandler())` in `OnAwake`). The common envelope stores type-specific data as an opaque `JsonElement`, deserialized against the right concrete type on restore (`farm_land/Code/Persistence/SaveHandlers/GridSaveHandlers.cs`):

```csharp
class GridBuildingData {
    public string BuildingType { get; set; }   // discriminator
    public Vector2Int GridPosition { get; set; }   public Rotation Rotation { get; set; }
    public JsonElement BuildingSpecificData { get; set; }   // = JsonSerializer.SerializeToElement(plotData)
}
// restore: registry.Get(d.BuildingType).RestoreToGrid( d, d.BuildingSpecificData.Deserialize<GridFarmPlotData>() );
```

Two more `farm_land` save-design wins: **save-shrinking by deriving on load** â€” `StatTracker` objectives write `Data = null` and re-derive progress from the canonical `Statistics` store, completed objectives store only `{Type, IsCompleted}`, zero-progress challenges are skipped entirely (smaller saves + migration resilience); and **`PostLoad()` self-healing** â€” `CropResource.PostLoad()` pads/trims `StageRequirements` to match `GrowthStages` so a designer editing the asset can't desync arrays at load.

> **RPC/`JsonElement` serialization gotcha (net-new):** `farm_land` could not send `GridBuildingData` (with its `JsonElement`) over an RPC â€” so `GridFarmDataStruct` re-serializes each entry to a `string[]` (`BuildingsJson`/`UpgradesJson`) before the wire and deserializes back on the far side (`FarmData.cs`, with an in-code `// remove this data due to serialisation issues`). If a save DTO must cross an RPC, project it to strings first.

### Tamper-resistant local save: `{ Data, Hash }` envelope, hash-mismatch-**tolerant** load

`lavagame.sandmoney_` is the corpus's best local-save anti-cheat reference for a leaderboard game with no server DB (`sandmoney_/Code/Persistence/`). The envelope is `{ PlayerData, Hash }` where `Hash = FNV-1a( json + localSteamId + pepper )`:

```csharp
// PlayerSaveHasher â€” SteamId in the hash means a copied save fails on another account
string Compute( string json, long steamId ) => Fnv1a( json + steamId + "s&money_core_v1_x89!" );
```

The subtle, reusable part is the **load policy**: a hash mismatch is *accepted* (so adding a field with a default doesn't wipe everyone's save) but flagged `hashMismatch:true` â†’ the caller schedules an immediate **re-save to re-hash**. Crucially, **parse failure â‰  hash failure**: only a parse failure falls back to the `.bak`; a hash mismatch on a copied file is *not* eligible for `.bak` rescue. There's also a separate daily-reward integrity hash with **versioned peppers** (`PepperV3` with a v2 fallback) â€” a real story of *removing* an unstable field (display name differs SP vs MP) from the hash to stop false tamper flags while still validating old saves.

> **The silent NaN-kills-save footgun (net-new, high-value).** `Math.Max(0, NaN)` returns `NaN`, and `JsonSerializer` *throws* on `NaN`/`Infinity` â€” which, inside a fire-and-forget save, **silently kills the write with no error surfaced**. `sandmoney_`'s `Normalize()` sanitizes every float/double before write (`SF`/`SD` helpers) **and** clamps to legal ranges on load, so load-time `Normalize` doubles as input validation / anti-cheat. Guard every credit/debit at the mutation site too (`AddMoney`/`DeductMoney` early-return on `double.IsNaN || IsInfinity`) so one bad multiply can't poison a `[Sync]` and corrupt the save.

### Save-flush state machine â€” never lose data on quit

`sandmoney_`'s `TrySave(waitForBackend, reason)` (`PlayerTrader`/`Persistence`) is the reference for coalescing concurrent saves and never dropping a critical one:

- A `_isSaving` guard with a **single queued follow-up** (`_saveQueued` + `_saveQueuedCritical`) so concurrent requests collapse into one extra save, not a storm.
- A 10 s min-interval throttle for routine saves, but `RequestCriticalSave` **bypasses the throttle** for money events (upgrade bought, daily reward, bot purchase) â€” the canonical recipe's "dirty flag + timer" is fine for the common case, but money-critical mutations must force-save immediately.
- `OnDestroy` â†’ `ForceFlushOnExitAsync` **force-clears `_isSaving`** ("losing data on quit is worse than saving twice"), settles volatile state (liquidates open positions), saves with a `markDisconnect`, then awaits the leaderboard flush. Also fired from `INetworkListener.OnDisconnected`.

### Host-only sims must be reconstructable from their own `[Sync]` state

When a singleton simulation lives only on the host and **isn't** persisted (e.g. `sandmoney_`'s live market, which is intentionally regenerated each boot and even deletes its legacy `grav_market.json`), a host migration would corrupt it because the new host inherits only the replicated values, not the private fields. The pattern: cache `_wasProxy`, detect the proxyâ†’authority flip in `OnFixedUpdate` (`if (_wasProxy && !IsProxy) RecoverFromHostMigration();`), and have `RecoverFromHostMigration()` **rebuild private state from the `[Sync]` ring buffer/history** then `Network.Refresh()`; also wire `INetworkListener.OnBecameHost` (`sandmoney_/Core/MarketManager.cs`, `WorldEventManager.cs`). This is the persistence-shaped sibling of `despawn.murder`'s host-migration-safe round timer (re-arm `TimeUntil` from `.Relative` against the new host's clock).

### Reconnect-safe player state that outlives a disconnect

`farm_land`'s `GameNetworkManager` recycles a player's state across a reconnect instead of rebuilding it: `GetOrCreateClient` searches for an **orphaned `NetworkClient`** (no owner, matching `SteamId`) to reuse; new clients are `Clone() + BreakFromPrefab() + SetOrphanedMode(NetworkOrphaned.ClearOwner)`, and per-player farms spawn with `SetOrphanedMode(NetworkOrphaned.Host)` so **the host inherits a farm when its owner leaves** rather than destroying it (`farm_land/Code/Common/Network/GameNetworkManager.cs`). The persistence angle: in-session continuity without a disk round-trip on every reconnect.

### Ephemeral host-disk state by SteamId (a third tier, between "no save" and "account backend")

`despawn.murder` keeps its **anti-streak pity tickets** in a server-host-local `FileSystem.Data` JSON keyed by SteamId (`murderer_tickets.json`, try/catch-guarded, load-once flag) â€” non-account-bound, server-side, ephemeral fairness state (`despawn.murder/Code/Systems/MurdererTickets/MurdererTicketManager.cs`). It's a clean contrast to `MurderDataStore`/`ApiClient` (account-bound backend) in the *same game*: "ephemeral host state on disk" vs "account state in a service." And `barrelproto.ragroll` is the explicit **"no save file at all"** reference â€” `ProgressController` is an empty stub, all persistence is `Sandbox.Services` (Stats/Achievements/Leaderboards). For a pure session-score game, leaning entirely on Services is a valid architecture, not an omission.

### Deterministic daily content without any save or sync

`farm_land`'s barter vendor derives its daily order from a **day-seeded RNG** so every peer computes the identical rotation with zero networking and zero stored state: `new Random((int)(DateTime.UtcNow - epoch).TotalDays)`, cached per day (`_lastGeneratedDay`). Only the *per-player consumed-stock counter* is saved (resets when `LastRefreshDate.Date < today`). The reusable trick: deterministic-daily content = seed RNG with the day number, persist only what the player *did*, not what was *offered* (`farm_land/Code/Common/Economy/MushroomDealer.cs`).

### Read these games (save-persistence)

- **`facepunch.fair`** â€” the headline: interface-discovered/ordered/versioned save (`ISaveDataProperty`), prefab-instance persistence (`SpawnedPrefabSaveData<,>`), delete-on-version-mismatch, per-section try/catch isolation. The template if you want "save the whole sim" without a scene diff.
- **`lavagame.sandmoney_`** â€” tamper-resistant `{Data,Hash}` envelope (hash+pepper+SteamId), hash-mismatch-tolerant load for schema evolution, the NaN-kills-save footgun + `Normalize()` clamp-on-load, the save-flush coalescing/critical-bypass state machine, host-only-sim recovery from `[Sync]`.
- **`thefancylads.farm_land`** â€” `FileSystem.OrganizationData` scope, polymorphic `IGridSaveDataHandler` registry + `JsonElement` discriminator, save-shrinking by deriving from a canonical store, `PostLoad()` self-healing, the RPC `JsonElement`â†’`string[]` workaround, reconnect-safe orphaned-client recycle.
- **`facepunch.ss2`** â€” `ReadJsonSafe<T>`, single-fieldâ†’list null-coalesce migration, `StateVersion` for O(1) UI reactivity, build-time duplicate-ID catalog check.
- **`despawn.murder`** â€” ephemeral host-disk SteamId-keyed state (pity tickets) contrasted with an account backend in the same game; host-migration-safe timer re-arm.
- **`barrelproto.ragroll`** â€” the "no save file, lean entirely on `Sandbox.Services`" reference for session-score games.

Open the cited file under `D:/sbox-lessons/zips-code/<game>/` to read the real implementation.
