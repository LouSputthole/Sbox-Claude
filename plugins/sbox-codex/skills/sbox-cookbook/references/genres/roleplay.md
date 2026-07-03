# Roleplay / DarkRP Recipe

How to build a persistent, host-authoritative roleplay sandbox (DarkRP-style) in modern s&box (GameObject/Component/Scene), distilled from `artisan.darkrpog` â€” a 2,517-file production DarkRP framework. Most of its *content* (drugs, casino, jobs) is genre-specific; its *plumbing* (persistence, host-authority, data-as-assets, interaction) is reusable for any persistent multiplayer game.

## What defines the genre

A roleplay game is a **persistent, shared world where players assume jobs, earn and spend money, own placed objects, and interact through proximity prompts + chat commands** â€” and all of it survives a server restart. There is no win state; the loop is open-ended social/economic simulation. Three properties separate it from a tycoon or a round-based game:

- **Persistence is the whole point.** Player wallets, owned props/printers, and world state must outlive disconnects *and* server restarts. This forces a per-player save (keyed by SteamId) **and** a world save, plus a stable identity layer for runtime-spawned objects.
- **Host owns every gameplay mutation.** Clients see state via `[Sync(SyncFlags.FromHost)]` (display-only) and request changes via RPCs the host re-validates. There is no trusted client.
- **Content is authored as assets, not code.** Jobs, printers, lootboxes, skills, daily rewards are `GameResource` definition files (`.jobdef` etc.) so designers add content without recompiling.

**Core loop:** `spawn â†’ pick a job (salary) â†’ earn money (printers/skills/jobs) â†’ spend (items/upgrades/bank) â†’ own placed objects â†’ disconnect/restart â†’ reload exactly where you left off`. Everything else is scaffolding around that loop.

## The one idiom that makes it work: host-authoritative money

Internalize this before writing anything else. The value is a `[Sync(FromHost)]` property with a private setter; *every* mutator is gated on `Networking.IsHost`, overflow-checked, and saves immediately. Clients can read but never write.

```csharp
public sealed partial class Player  // Player is a Component
{
    [Property, Sync( SyncFlags.FromHost )]
    public long Money { get; private set; } = RoleplayEconomyDefaults.StartingWallet;

    public bool CanAfford( long amount ) => amount >= 0 && amount <= Money;

    public void GiveMoney( long amount )
    {
        if ( !Networking.IsHost || amount <= 0 ) return;
        if ( Money > long.MaxValue - amount ) return;   // overflow guard
        Money += amount;
        SaveRoleplayData();                              // persist on every change
    }

    public bool TryTakeMoney( long amount )              // validate-then-apply, never throw
    {
        if ( !Networking.IsHost || amount < 0 || !CanAfford( amount ) ) return false;
        Money -= amount;
        SaveRoleplayData();
        return true;
    }
}
```
(artisan.darkrpog: Code/Player/Player.Roleplay.cs:9 sync prop, :20 GiveMoney, :35 TryTakeMoney) â€” use `long`, not `float`, for currency; overflow-check additions; `SetMoney` floors at `0` (:48). Higher-level ops (bank deposit/withdraw) return a **result enum** with a `DescribeFailure` enumâ†’string mapping rather than booleans, so the UI can show *why* it failed (input: Code/Economy/Bank/BankingService.cs:13).

See `references/systems/economy-currency.md` for the general host-authoritative-currency pattern.

## The system stack to compose

Build each as a `Component` or a static service. References point to existing system docs.

| System | Role | Reference |
|---|---|---|
| Host-authoritative wallet | `long Money`, `[Sync(FromHost)]`, gated mutators | `references/systems/economy-currency.md` |
| Per-player persistence (versioned) | One JSON/SteamId, migration ladder, swappable repo | `references/systems/save-persistence.md` |
| World save (scene-diff) | Serialize live scene, diff vs SceneFile baseline | `references/systems/save-persistence.md` |
| Persistent world-entity identity | Stable `Guid` so placed props survive restart | `references/systems/save-persistence.md` |
| GameResource content definitions | Jobs/printers/lootboxes/skills as `.def` assets | `references/systems/inventory.md` |
| Interaction / use-prompt | `Component.IPressable`, live tooltip, tap-vs-hold | `references/systems/inventory.md` |
| Spawnable router | `ISpawner` + ident-string â†’ spawn + authorize | `references/systems/building-placement.md` |
| Idle income generator (printer) | Passive earner w/ upgrade tree + risk | `references/systems/idle-offline.md` |
| Weighted loot table (gacha) | Lootbox/drop roller | `references/systems/gacha-loot.md` |
| Skill/XP progression | XP w/ per-source anti-farm caps | `references/systems/progression-upgrades.md` |
| Daily login streak | Retention loop, UTC day-keyed | `references/systems/idle-offline.md` |
| Chat slash-command router | `/job`, `/drop`, `/pm`â€¦ dispatch table | â€” (below) |

## Build order

Build single-player-feeling first; the host-authority idiom makes co-op "free" because `!Networking.IsHost` short-circuits true offline (host == local player).

1. **Player component + wallet.** `Money` as above. Everything reads/writes through gated mutators.
2. **Per-player persistence.** A serializable record keyed by SteamId, one JSON file per player, with a `CurrentVersion` + migration ladder. Wallet saves call this.
3. **Job definitions as assets.** `JobDefinition : GameResource` with `[Property]` fields (Salary, Title, Clothingâ€¦). `/job <name>` sets it; salary ticks pay it.
4. **Interaction layer.** `Component.IPressable` on everything pressable (doors, ATMs, machines) with a live tooltip. This is the "press E" verb of the whole game.
5. **Spawnable router.** An `ISpawner` abstraction + a central spawn RPC that parses an ident string, traces placement from the player's eyes, authorizes, then spawns.
6. **Persistent world identity.** Stamp a stable `Guid` on every player-placed object so the world save can re-own it after a restart.
7. **World save.** Scene-diff serialize so the whole map state round-trips.
8. **Income + economy content.** Printers (idle earners), lootboxes (gacha), skills (XP), daily rewards â€” each a definition asset + a service.
9. **Chat command router** for the social/admin verbs.

## How the real game does each piece

### Interaction â€” `Component.IPressable` with a live tooltip
The genre's core verb. Implement `Component.IPressable`; the engine drives Hover/CanPress/Press/Pressing/Release for you. `GetTooltip` returns a `Tooltip(title, icon, subtitle)` rendered as the world-space "press E" prompt â€” and the **subtitle is computed live** (price, owner status, your balance). Tap-vs-hold is just timing inside `Pressing`.

```csharp
public sealed class SlotMachineInteractable : Component, Component.IPressable
{
    const float OwnerManageHoldSeconds = 0.65f;
    TimeSince _timeSincePressed;
    bool _ownerMenuOpened;

    Component.IPressable.Tooltip? Component.IPressable.GetTooltip( Component.IPressable.Event e )
    {
        var player = ResolvePlayer( e );                       // resolve who is looking
        var bet = SlotMachineBetSelectionState.GetSelectedBet( Machine, player );
        return new Component.IPressable.Tooltip( "Play Slots", "casino",
            $"Tap E: spin ${bet:n0} | Hold E: manage" );        // live subtitle
    }

    bool Component.IPressable.Press( Component.IPressable.Event e )
    {
        _timeSincePressed = 0; _ownerMenuOpened = false; return true;
    }

    bool Component.IPressable.Pressing( Component.IPressable.Event e )
    {
        if ( !_ownerMenuOpened && _timeSincePressed >= OwnerManageHoldSeconds )
        {
            OpenManagementConsole( ResolvePlayer( e ) );        // hold â†’ manage
            _ownerMenuOpened = true;
        }
        return true;
    }
}
```
(artisan.darkrpog: Code/Items/Casino/SlotMachineInteractable.cs:6 class, :19 GetTooltip, :46 Press, :62 Pressing hold-detect) â€” resolve the presser with `e.Source.GameObject.Root.GetComponent<Player>()`. Press/Pressing run on the *pressing client*; the actual gameplay effect (spin, buy) is sent as an RPC the host re-validates. Suppress the prompt (`return null`) when the player's cursor is on a clickable WorldPanel so the prompt and UI don't fight.

### Content as assets â€” `GameResource` definitions
Make each job/printer/lootbox an authorable asset. Designers drop files; no code change.

```csharp
[AssetType( Name = "DarkRP Job", Extension = "jobdef", Category = "DarkRP",
            Flags = AssetTypeFlags.NoEmbedding | AssetTypeFlags.IncludeThumbnails )]
public sealed class JobDefinition : GameResource, IDefinitionResource
{
    [Property] public string Id { get; set; }
    [Property] public string Title { get; set; }
    [Property] public int Salary { get; set; } = 45;
    // â€¦Clothing, RequiredEntitlements, SkillRequirements, PlayerModelâ€¦
}

// Enumerate every authored job:
var jobs = ResourceLibrary.GetAll<JobDefinition>();
```
(artisan.darkrpog: Code/Jobs/JobDefinition.cs:1 AssetType, :6 fields) â€” the same pattern recurs for MoneyPrinterDefinition, LootboxCaseDefinition, SkillTreeDefinition, DailyLoginRewardDefinition. **Gotcha:** never cache an empty `GetAll()` â€” an early caller (hotload race, chat tick) can run before GameResources are indexed and permanently empty your catalog; guard against caching a zero-length result.

### Weighted loot table â€” the gacha roller
Pure, engine-free, trivially reusable for any drop table / shop restock / random spawn. Returns a `.Clone()` so callers can't mutate the catalog.

```csharp
public static LootboxRewardSpec Roll( IReadOnlyList<LootboxRewardSpec> rewards, Random rng = null )
{
    rng ??= Random.Shared;                                  // pass a seeded Random for replayable rolls
    var valid = rewards.Where( x => x?.Reward is not null && x.Weight > 0f ).ToArray();
    if ( valid.Length == 0 ) return null;

    var total = valid.Sum( x => x.Weight );
    var roll = (float)(rng.NextDouble() * total);
    var cumulative = 0f;
    foreach ( var r in valid )
    {
        cumulative += r.Weight;
        if ( roll <= cumulative ) return r.Clone();         // defensive copy
    }
    return valid[^1].Clone();                                // edge-case fallback
}
```
(artisan.darkrpog: Code/Lootboxes/LootboxRoller.cs:8) See `references/systems/gacha-loot.md`.

### Idle income â€” the MoneyPrinter (and the bucketed-`[Sync]` perf trick)
A printer accumulates `StoredMoney` on a cadence, has integer upgrade levels (Speed/Yield/Capacity), and a Heat mechanic that can catch fire. Its single most reusable lesson is **bucketed `[Sync]` writes**: for a value that changes every fixed tick, accumulate a local delta and only write the `[Sync]` property when it crosses a threshold â€” otherwise you ship a network packet to every viewer 60Ã—/sec for a fractional change.

```csharp
// Wave 10 PERF-B: bucket per-fixed-tick Heat cooldown so we don't [Sync]-spam 60x/sec.
float _accumulatedCoolDelta;
const float HeatCoolFlushThreshold = 0.25f;
// â€¦accumulate _accumulatedCoolDelta each tick; only when it crosses the threshold
//   do you write the [Sync(FromHost)] Heat property and reset the accumulator.
```
(artisan.darkrpog: Code/Items/MoneyPrinter.cs:3 class, :47 heat-bucket field+comment) â€” upgrade levels live on the *entity*, not the player, so they persist with the world object. See `references/systems/idle-offline.md`.

### Persistent world-entity identity â€” durable IDs for placed objects
`GameObject.Id` is **not** durable across restarts. To save the world you need your own stable identity layer: a `Guid` stamped on every player-placed object, plus Kind/Owner/SchemaVersion, all `[Sync(FromHost)]`.

```csharp
public sealed class PersistentWorldEntity : Component
{
    [Property, Sync( SyncFlags.FromHost )] public Guid PersistentId { get; set; } = Guid.NewGuid();
    [Property, Sync( SyncFlags.FromHost )] public string Kind { get; set; } = WorldEntityKinds.GenericOwnable;
    [Property, Sync( SyncFlags.FromHost )] public long OwnerSteamId { get; set; }

    public static PersistentWorldEntity Ensure( GameObject go, string kind, Connection owner = null )
    {
        var marker = go.GetOrAddComponent<PersistentWorldEntity>();   // add-or-get
        marker.Kind = kind;
        marker.PersistAcrossRestart = true;
        if ( owner is not null && owner.SteamId.Value > 0 )
            marker.OwnerSteamId = (long)owner.SteamId.Value;          // re-own after restart
        return marker;
    }
}
```
(artisan.darkrpog: Code/Persistence/World/PersistentWorldEntity.cs:6 class, :82 Ensure) â€” on destroy, record a *tombstone* (`MarkDestroyed`) so the deletion is replayed on the next load; compact tombstones or the log grows unbounded.

### World save â€” scene-diff against a baseline
`SaveSystem : GameObjectSystem<SaveSystem>` serializes the live scene to JSON and **diffs it against a baseline rebuilt from the original SceneFile**, writing only the patch (plus NetworkOwnership by SteamId, a separate `[Sync]`-state capture, and required cloud packages). Load applies the patch back onto the SceneFile, `Game.ChangeScene` loads it, then `[Sync]` values + ownership are restored. Components hook the lifecycle via `ISaveEvents` (BeforeSave/AfterSave/BeforeLoad/AfterLoad).

```csharp
Scene.RunEvent<ISaveEvents>( x => x.BeforeSave( path ) );
var baseline = BuildCompositeBaseline();                  // rebuilt from the source SceneFile(s)
// Json.CalculateDifferences(baseline, current, â€¦) â†’ write only the patch
```
(artisan.darkrpog: Code/Save/SaveSystem.cs:128 Save, :695 CollectSyncState) â€” **only objects originating from a tracked SceneFile get diffed**; runtime-spawned objects rely on the SyncState/ownership side-channels + your `PersistentWorldEntity` layer, *not* the diff. Save version is hard-pinned: a mismatched version refuses to load. Host-only. See `references/systems/save-persistence.md`.

### Per-player persistence â€” versioned, with a migration ladder
A plain serializable record keyed by SteamId, one JSON file per player, written through a **swappable repository** (`IPlayerStorage` â€” production hits the disk filesystem; tests swap an in-memory one). Loads run an append-only migration ladder that fills new fields with defaults.

```csharp
static PlayerRoleplaySaveData TryMigrate( PlayerRoleplaySaveData loaded )
{
    if ( loaded.Version < 2 ) { loaded.Bank = 0; loaded.Version = 2; }   // each rung fills new fields
    if ( loaded.Version < 3 ) { /* â€¦ */ loaded.Version = 3; }
    // â€¦ up to CurrentVersion. NEVER reorder rungs â€” the ladder is append-only.
    return loaded;
}
```
(artisan.darkrpog: Code/Player/Persistence/PlayerRoleplayStorage.cs:297 TryMigrate, :193 TryLoad; IPlayerStorage.cs:14) â€” write backup-first (`.bak.json` then primary). See `references/systems/save-persistence.md`.

### Chat slash-command router
Parse `/text` into a command, build a context (connection, resolved Player, args, reply helpers), then walk a flat dispatch table of `(name + aliases, handler)`. Fall through to job-change commands resolved from the `JobDefinition` catalog, else reply "unknown command". **Host-only.**

```csharp
public static bool TryHandle( Connection source, string text )
{
    if ( !Networking.IsHost ) return false;                 // host owns command handling
    var parsed = RoleplayChatCommandParser.Parse( text );
    if ( !parsed.IsCommand ) return false;

    var ctx = new RoleplayChatCommandContext( source, Player.FindForConnection( source ), parsed );
    foreach ( var cmd in GetCommandDefinitions() )          // flat (name+aliases, handler) table
        if ( cmd.Matches( ctx.CommandName ) ) { cmd.Execute( ctx ); return true; }

    if ( RoleplayJobCommandCatalog.TryResolve( JobDefinition.GetAll(), ctx.CommandName, out var job ) )
    { ctx.Player.ProcessSetJobRequest( job ); return true; } // /<jobname> â†’ switch job
    ctx.ReplyError( "Unknown command." );
    return true;
}
```
(artisan.darkrpog: Code/Chat/RoleplayChatCommandService.cs:29 TryHandle, :65 command table) â€” the context carries `Reply`/`ReplyError` helpers so handlers don't reimplement messaging. Job names double as commands (`/police`).

## Standout discipline worth copying

These are what make a roleplay server survive 128 players and not lose saves.

- **Wrap + time every join/spawn/disconnect hook individually.** `PostSpawnSafe(name, work)` runs each step inside a try/catch + Stopwatch, logging a throw and continuing instead of kicking the player or aborting the save chain, and warning on any step over a ms threshold. Turns "a regression anywhere in a 20-step spawn = mass kicks" into "one named feature degrades in the log" (input: Code/GameLoop/GameManager.cs:658 PostSpawnSafe, :400 DisconnectSafe).
- **Stripe heavy per-tick services one-per-frame.** A `StrideScheduler` runs exactly one registered service per frame, so 6 heavy services at 50 Hz each effectively run ~8.3 Hz â€” spreads CPU instead of spiking when everything ticks the same frame (input: Code/GameLoop/GameManager.cs:16, :450).
- **Service / pure-rules / definition split.** Almost every feature is three classes: a `*Service` (engine-touching orchestration), a pure `*Rules`/`*Validator` (no Sandbox deps, returns enums/records â€” unit-testable headless), and a `GameResource *Definition` asset. All the math/edge-cases live in the pure layer (input: BankingServiceâ†’BankAmountValidator).
- **Drain mass operations one-per-tick.** AfterLoad respawns 128 players via a queue drained one per frame; disconnect saves snapshot now / write next tick â€” all to avoid tripping the engine's hard ~10 s server-frame timeout during a mass join/restart (input: Code/GameLoop/GameManager.cs:713, :733).

## Pitfalls (from the mined code)

- Money mutators silently no-op on non-host â€” UI buttons **must** fire an RPC the host re-validates, never call `GiveMoney`/`TryTakeMoney` on a client.
- `SyncFlags.FromHost` means the client value is **display-only**; authoritative state lives on the host. Never trust a client-reported balance.
- Every mutator saving synchronously causes save thrash â€” **batch** multi-step operations (one save at the end, not per sub-step).
- `GameObject.Id` is **not** durable across restarts â€” placed objects need a `PersistentWorldEntity`-style `Guid` or they orphan on reload.
- Never cache an empty `ResourceLibrary.GetAll<T>()` result (hotload/early-tick race permanently empties the catalog).
- The migration ladder is **append-only** â€” reordering a rung corrupts older saves.
- The spawn RPC is `async void`; **wrap it in try/catch** â€” an exception in an async-void RPC entry point vanishes into the SynchronizationContext with no diagnostic.
- Don't `[Sync]`-write a rapidly-changing float every tick â€” bucket the delta and flush past a threshold.

## Verify live

API surfaces drift between SDK versions â€” confirm before relying on a signature. Use `describe_type` / `search_types` reflection against the installed SDK as authoritative for: `Sandbox.Networking` (`IsHost`/`IsActive`), `[Sync]`/`SyncFlags.FromHost`/`[Rpc.Host]`/`[Rpc.Broadcast]`, `Component.IPressable` (and its nested `Tooltip`/`Event` types), `GameResource` + `[AssetType]` (`Name`/`Extension`/`Category`/`Flags`), `ResourceLibrary.GetAll<T>`, `GameObjectSystem<T>`, `Json.CalculateDifferences`/`Json.ApplyPatch`, `Game.ChangeScene`, `Connection`/`SteamId`, `GameObject.GetOrAddComponent`, and `Scene.Trace.Ray(...)` for eye-trace placement.

Cross-links: see the `sbox-api` skill for authoritative type lookups, and the `sbox-build-feature` skill for the screenshot-driven build/iterate loop.

## Corpus refresh (2026): more reference implementations

Second DarkRP source: `lowkeynetworks.newrp` (~385 files, `Code/modules/` + `Code/content/` + `Code/framework/`) offers a structural alternative to artisan.darkrpog and introduces several patterns not covered above.

### Alternative: EventBus money (non-`[Sync]`) vs `[Sync(FromHost)]`

artisan.darkrpog puts `long Money` as `[Sync(SyncFlags.FromHost)]` so clients always have the latest value. newrp deliberately keeps money as a plain server-side `int` â€” never synced â€” and pushes balance changes to the owning client via a typed EventBus. **Trade-off:** the `[Sync]` approach is simpler and lets any component read the value; the EventBus approach avoids a per-player sync property that every connected client can observe in memory, but requires the HUD to subscribe.

```csharp
// lowkeynetworks.newrp: Code/modules/player/PlayerData.cs
public int Money { get => _money; private set => _money = MathX.Max(0, value); } // clamp in setter
public void AddMoney(int amount) {
    if (amount <= 0) return;
    Money += amount;
    EventBus.Publish(new PlayerStateChangedEvent(this, "money")); // push to HUD
    Save();
}
public bool TakeMoney(int amount) {
    if (amount <= 0 || Money < amount) return false;
    Money -= amount;
    EventBus.Publish(new PlayerStateChangedEvent(this, "money"));
    Save();
    return true;
}
```
(newrp: `Code/modules/player/PlayerData.cs`) â€” note `MathX.Max` not `System.Math.Max`; `System.Math` does not exist in the s&box sandbox. Write-through: every mutation calls `Save()` immediately â€” trades some I/O for simplicity vs. artisan's batched off-thread queue.

**Anti-pattern in newrp:** `Math.Max(0, value)` (System.Math) appears in the source but will not compile in s&box's sandbox â€” the correct call is `MathX.Max(0, value)`. Fix before copying.

### Ownable/lockable doors with linked groups and job-access tiers

A door group is a single "property" â€” buying one door charges for and locks the whole group. Selling refunds half. Certain DoorGroups (e.g. "police", "government") auto-open for the matching job roles without per-door config.

```csharp
// lowkeynetworks.newrp: Code/modules/doors/DoorOwnershipComponent.cs (sketch)
[Property] public string DoorGroup { get; set; }
[Property] public bool BuyLinkedGroup { get; set; } = true;

IEnumerable<DoorOwnershipComponent> GetLinkedDoors() =>
    Scene.GetAllComponents<DoorOwnershipComponent>()
         .Where(d => d != this && d.DoorGroup == DoorGroup);

bool Buy(PlayerComponent buyer) {
    var linked = BuyLinkedGroup ? GetLinkedDoors().Where(d => d.Owner == null) : Enumerable.Empty<DoorOwnershipComponent>();
    int total = Price + linked.Sum(d => d.Price);
    if (!buyer.Data.TakeMoney(total)) return false;
    Owner = buyer; IsLocked = true;
    foreach (var d in linked) { d.Owner = buyer; d.IsLocked = true; }
    return true;
}
void Sell() {
    var linked = BuyLinkedGroup ? GetLinkedDoors() : Enumerable.Empty<DoorOwnershipComponent>();
    int refund = (Price + linked.Sum(d => d.Price)) / 2;   // half-refund
    Owner?.Data.AddMoney(refund);
    Owner = null; IsLocked = false;
    foreach (var d in linked) { d.Owner = null; d.IsLocked = false; }
}
bool CanJobAccess(string jobId) => DoorGroup switch {
    "police" or "pd" => jobId is "police" or "police_chief",
    "government"     => jobId is "mayor",
    _                => false
};
```
(newrp: `Code/modules/doors/DoorOwnershipComponent.cs`, `DoorInteractable.cs`) â€” `DoorComponent` is `Component.ExecuteInEditor` with `DrawGizmos`, hinge inferred from `BoxCollider` bounds, and auto-authored child visual + collider. The hinge pivot is an offset, not the object origin â€” copy the `ApplyHingeTransform` math exactly, don't guess it.

### `Rpc.FilterInclude` for targeted (whisper / proximity / team) RPCs

The canonical s&box idiom for "send a `[Rpc.Broadcast]` only to a subset of connections." newrp is the clearest in the corpus for two distinct uses: proximity chat and vote UI.

```csharp
// newrp: Code/modules/chat/ChatService.cs + Code/modules/jobs/JobVoteService.cs
// 1. Proximity chat â€” send only to players within channel range
var recipients = PlayerModule.All
    .Where(p => Vector3.DistanceBetween(p.WorldPosition, sender.WorldPosition) <= channel.Range)
    .Select(p => p.Connection).ToList();
using (Rpc.FilterInclude(recipients))
    BroadcastChatMessage(sender.Name, text, channel.Tag);  // [Rpc.Broadcast] method

// 2. Vote UI â€” show only to eligible voters
var voters = PlayerModule.All
    .Where(p => p != candidate && p.Data.IsOwner)
    .Select(p => p.Connection).ToList();
if (voters.Count == 0) { AutoPass(); return; }
using (Rpc.FilterInclude(voters))
    ShowVotePanel(candidate.Name, VoteDuration);
```
(newrp: `Code/modules/chat/ChatService.cs:52`, `Code/modules/jobs/JobVoteService.cs:28`) â€” this pattern appears in no other mined game as cleanly. Use it for area chat, party announcements, per-team HUD updates, crime alerts to law enforcement only.

### Time-boxed vote flow (snapshot electorate â†’ filtered UI â†’ async timeout â†’ resolve)

Cleanest vote-system reference in the corpus. Fully generic â€” works for any yes/no decision.

```csharp
// newrp: Code/modules/jobs/JobVoteService.cs (sketch)
static VoteState _active;
[Rpc.Host] static void StartVote(Connection candidate) {
    if (!Networking.IsHost) return;
    _active = new VoteState { Candidate = candidate,
        Voters = PlayerModule.All.Where(p => p.Connection != candidate).Select(p=>p.Connection).ToList() };
    if (_active.Voters.Count == 0) { Resolve(passed: true); return; }
    using (Rpc.FilterInclude(_active.Voters)) ShowVotePanel(candidate.DisplayName, VoteDuration);
    _ = FinishLater();
}
[Rpc.Host] static void SubmitVote(bool yes) {
    if (_active?.Voters.Contains(Rpc.Caller) != true) return;
    _active.Votes[Rpc.Caller.Id] = yes;
    if (_active.Votes.Count >= _active.Voters.Count) Resolve(_active.Votes.Values.Count(v=>v) > _active.Votes.Values.Count(v=>!v));
}
static async Task FinishLater() {
    for (int i = VoteDuration; i > 0; i--) {
        await GameTask.Delay(1000);
        if (_active == null) return;
        BroadcastCountdown(i - 1);
    }
    Resolve(_active.Votes.Values.Count(v=>v) > _active.Votes.Values.Count(v=>!v));
}
```
(newrp: `Code/modules/jobs/JobVoteService.cs`) â€” `GameTask.Delay` not `Task.Delay`. `Rpc.Caller` is only valid inside an `[Rpc.Host]` method body.

### Dependency-ordered module kernel with cycle detection and fail isolation

The biggest structural pattern in newrp â€” a "mod loader" for any large multi-system game. Each `GameModule` declares `Type[] Dependencies`; `ModuleManager` topologically sorts them (DFS with permanent/temporary marks, throws on cycle), then runs 5 lifecycle phases (`PreInitialize â†’ Initialize â†’ PostInitialize â†’ Start â†’ PostStart`) in order. With `ContinueOnModuleFailure=true`, a bad module is marked `Failed`, its dependents cascade `Failed`, but everything else boots.

```csharp
// newrp: Code/framework/modules/ModuleManager.cs (sketch)
void BuildBootOrder() {
    var visited = new HashSet<Type>();
    var tempMark = new HashSet<Type>();
    void Visit(Type t) {
        if (tempMark.Contains(t)) throw new Exception($"Dependency cycle: {t.Name}");
        if (visited.Contains(t)) return;
        tempMark.Add(t);
        foreach (var dep in _modules[t].Dependencies) Visit(dep);
        tempMark.Remove(t); visited.Add(t); _bootOrder.Add(t);
    }
    foreach (var t in _modules.Keys) Visit(t);
}
void RunPhase(Phase phase) {
    foreach (var t in _bootOrder) {
        var m = _modules[t];
        if (m.HasFailedDependency(_modules)) { m.State = ModuleState.Failed; continue; }
        try { m.RunPhase(phase); }
        catch (Exception e) { Log.Error(e); if (!ContinueOnModuleFailure) throw; m.State = ModuleState.Failed; }
    }
}
```
(newrp: `Code/framework/modules/ModuleManager.cs`, `GameModule.cs`) â€” a scene `ModuleBootstrap : Component` drives `OnAwake`â†’boot and `OnUpdate`â†’module updates, and self-destructs on duplicate. This pattern is not needed for small games but is the right answer for a 10+ system RP framework.

### Host-as-superadmin shortcut + Connection.Id-keyed transient admin state

Admin state (ranks, freeze, noclip) lives in static dictionaries keyed by `Connection.Id` (a `Guid`) â€” not by SteamId â€” so it is session-local and never persisted. The listen-server host gets `superadmin` automatically.

```csharp
// newrp: Code/modules/admin/AdminService.cs (sketch)
static Dictionary<Guid, AdminRank> _ranks = new();
static bool IsHost(Connection conn) => Networking.IsHost && conn == Connection.Local;

public static AdminRank GetRank(Connection conn) =>
    IsHost(conn) ? AdminRank.Superadmin :
    _ranks.TryGetValue(conn.Id, out var r) ? r : AdminRank.None;

public static void FreezePlayer(Connection target, bool freeze) {
    if (!Networking.IsHost) return;
    var ctrl = FindController(target);
    if (ctrl == null) return;
    ctrl.UseInputControls = !freeze;
    if (freeze) ctrl.Velocity = Vector3.Zero;
}
public static void SetNoclip(Connection target, bool noclip) {
    var p = FindPlayer(target);
    if (noclip) p.GameObject.GetOrAddComponent<NoclipMoveMode>();
    else p.GameObject.GetComponent<NoclipMoveMode>()?.Destroy();
}
```
(newrp: `Code/modules/admin/AdminService.cs`) â€” `Connection.Id` is a `Guid`, not a `long`. Freeze sets `UseInputControls = false` and zeroes velocity. Noclip is a `GetOrAddComponent<NoclipMoveMode>()`.

### Network interest culling (`Component.INetworkVisible`) â€” artisan.darkrpog

By default s&box transmits every networked object to every client. For 30+ printers, doors, ATMs, and dropped money in an RP server, that floods clients. artisan implements per-object distance + role + owner visibility on top with hysteresis and a spawn warmup window.

```csharp
// artisan.darkrpog: Networking/RoleplayNetworkVisibility.cs (sketch)
public sealed class RoleplayNetworkVisibility : Component, Component.INetworkVisible
{
    [Property] public float VisibleRange { get; set; } = 2000f;
    [Property] public float ExitBuffer   { get; set; } = 200f;
    [Property] public float SpawnWarmup  { get; set; } = 4f;
    TimeSince _spawnedAt;

    bool Component.INetworkVisible.IsVisibleToConnection(Connection conn, BBox worldBounds)
    {
        if (_spawnedAt < SpawnWarmup) return true;           // warmup: always visible on spawn
        var player = PlayerModule.FindForConnection(conn);
        if (player == null) return true;                     // fail open
        if (player.Connection == Network.Owner) return true; // owner always sees it
        if (AdminService.GetRank(conn) >= AdminRank.Moderator) return true;
        float dist = Vector3.DistanceBetween(player.WorldPosition, worldBounds.Center);
        bool wasVisible = _visibleTo.Contains(conn.Id);
        float threshold = wasVisible ? VisibleRange + ExitBuffer : VisibleRange; // hysteresis
        bool visible = dist <= threshold;
        if (visible) _visibleTo.Add(conn.Id); else _visibleTo.Remove(conn.Id);
        return visible;
    }
    protected override void OnStart() { _spawnedAt = 0; Network.AlwaysTransmit = false; } // MUST set before NetworkSpawn
}
```
(artisan.darkrpog: `Networking/RoleplayNetworkVisibility.cs`) â€” **Anti-pattern:** setting `AlwaysTransmit = false` after `NetworkSpawn` races with the engine's initial ownership handshake. Set it in `OnStart` or before the spawn call. The component fails open (returns visible) if disabled via ConVar so a bad rollout is one console command.

### Razor panel performance â€” BuildHash + PerFramePanelCache + distance-gated OnUpdate

artisan's ATM screen documents the three Razor perf rules clearly. Copy this discipline to any world-facing panel that reads live game state.

```csharp
// artisan.darkrpog: UI/Atm/AtmScreen.razor (sketch â€” the three disciplines)

// 1. BuildHash: only re-render when something visible actually changed
protected override int BuildHash() =>
    HashCode.Combine(Player?.Money, Player?.Bank, HistoryStamp, SkillBonus);

// 2. PerFramePanelCache: expensive derived getters computed once per frame
readonly PerFramePanelCache<string> _formattedBalance;
string FormattedBalance => _formattedBalance.Get(() => $"${Player.Money:n0}");

// 3. Distance-gated OnUpdate: don't run per-frame logic for panels out of range
const float OnUpdateGateRangeSquared = 300f * 300f;
protected override void OnUpdate() {
    var player = Player.FindLocalPlayer();
    if (player == null) return;
    float distSq = (player.WorldPosition - WorldPosition).LengthSquared; // no sqrt
    if (distSq > OnUpdateGateRangeSquared) return;
    base.OnUpdate();
}
```
(artisan.darkrpog: `UI/Atm/AtmScreen.razor`) â€” `LengthSquared` avoids a sqrt per frame across every ATM in the scene. `PerFramePanelCache<T>` is an artisan helper â€” implement it as a `(int frame, T value)` struct that re-evaluates when `Time.Tick` changes.

### World-panel kiosk without collider alignment (WorldInput.Hovered)

artisan's ATM uses `WorldPanel.InteractionRange` + `WorldInput.Hovered` ancestry check to drive walk-up open and digit capture â€” no 3D collider needed for the UI hit-test.

```csharp
// artisan.darkrpog: UI/Atm/AtmScreen.razor (sketch)
protected override void OnUpdate() {
    bool hovered = WorldInput.Hovered?.IsDescendantOf(this) ?? false;
    if (hovered && !_isOpen) TryAutoOpen();
    // keyboard digits captured only while hovered â€” suppresses gameplay bind conflicts
    if (hovered && Input.Pressed("KP_0")) RequestAppendDigit(0); // note: "KP_0" not "KP0"
}
[Rpc.Host] void RequestAppendDigit(int d) { /* overflow + cooldown guard */ }
```
(artisan.darkrpog: `UI/Atm/AtmScreen.razor`) â€” **Gotcha:** raw numpad input names need underscores (`KP_0`, `KP_1`â€¦); `KP0` maps to `BUTTON_CODE_INVALID` and silently fires nothing. Digit input is routed through the same `[Rpc.Host]` RPCs as the physical keypad so overflow/rate-limit guards apply uniformly.

### Off-thread coalescing persistence writer â€” artisan.darkrpog

For servers with 50+ players all saving on disconnect or on-payday, synchronous disk writes block the main thread and can trip the server-frame timeout. artisan's `PersistenceFlushQueue` drains writes off-thread with per-path coalescing (only the latest save per player ever hits disk) and a priority-tiered drop policy.

Key ideas (not a full sketch â€” implement once you need > ~20 concurrent players):
- Keyed by normalized path; a second enqueue to the same path replaces the pending payload. Only the latest state reaches disk.
- Priority tiers (Critical > Gameplay > Autosave > Dashboard > Diagnostic): when the queue is full, low-priority writes are dropped, never gameplay saves.
- `DrainSynchronouslyForShutdown()` â€” called on server exit to guarantee nothing in-flight is lost.
- `TryNormalizePath` rejects `..`, `:`, and absolute paths before enqueue.

(artisan.darkrpog: `Concurrency/PersistenceFlushQueue.cs`) â€” only needed at scale; write-through `Save()` on the main thread is fine for prototypes and solo/co-op games.

### Anti-farm XP on repeatable actions â€” artisan.darkrpog

Any XP source that can be triggered repeatedly needs a key + hourly cap + per-source cooldown or players will farm it to infinity.

```csharp
// artisan.darkrpog: Skills/RoleplaySkillService.cs (sketch)
bool GrantXp(PlayerData player, SkillXpSource source, long antiFarmKey, int hourlyCap, float cooldownSeconds) {
    if (!Networking.IsHost) return false;
    var record = player.GetAntiFarmRecord(source, antiFarmKey);
    if (record.LastGrantedAt + cooldownSeconds > Time.Now) return false;         // per-key cooldown
    if (record.GrantedThisHour >= hourlyCap) return false;                       // hourly cap
    record.LastGrantedAt = Time.Now;
    record.GrantedThisHour++;
    player.Skills[source].Xp += BaseXpForSource(source);
    return true;
}
// antiFarmKey = target object's stable id (e.g. ATM GameObject id) so grinding one ATM
// is rate-limited independently from all other ATMs.
```
(artisan.darkrpog: `Skills/RoleplaySkillService.cs`) â€” `antiFarmKey` is typically the target object's `GameObject.Id.GetHashCode()` cast to `long`. Combine with per-source enum so ATM-hack XP and banking XP track independently.

### Per-player-cap vendor with live-object tracking â€” lowkeynetworks.newrp

The `MaxPerPlayer` cap in newrp's vendor is enforced by counting *live* tracked GameObjects, not a stored integer. Destroyed props (player death, admin cleanup) free the slot automatically.

```csharp
// newrp: Code/content/market/MarketService.cs (sketch)
static Dictionary<(long steamId, string itemId), List<GameObject>> _tracked = new();

[Rpc.Host] static void Purchase(string itemId) {
    var player = FindCaller();
    var item = ItemRegistry.Get(itemId);
    if (item == null || !item.Spawnable) return;
    if (item.Price > 0 && !player.Data.TakeMoney(item.Price)) return;
    // enforce per-player cap by counting still-valid live objects
    var key = (player.Data.SteamId, itemId);
    _tracked.TryGetValue(key, out var owned);
    owned?.RemoveAll(go => !go.IsValid());                  // prune destroyed first
    if (item.MaxPerPlayer > 0 && (owned?.Count ?? 0) >= item.MaxPerPlayer) {
        player.Data.AddMoney(item.Price); return;            // refund, over cap
    }
    var go = SpawnItem(player, item);
    if (!go.IsValid()) { player.Data.AddMoney(item.Price); return; } // refund on fail
    (_tracked[key] ??= new()).Add(go);
    go.Tags.Add("newrp-item-" + itemId);
    Ownable.Set(go, player.Connection);
    go.NetworkSpawn(true, null);
}
```
(newrp: `Code/content/market/MarketService.cs`) â€” spawn transform is an eye-trace 220u forward from the player, landing on `HitPosition + Normal*18u`. Spawned object gets `removable`, `newrp-market`, and `newrp-item-{id}` tags so admin cleanup tools can find them.

## Read these games

For the full roleplay pattern set, read both sources together:

- `D:\sbox-lessons\mining-v2\games\artisan.darkrpog.md` â€” production-grade framework: off-thread persistence, network interest culling, frame-budget queues, Razor panel perf, world-panel kiosk, anti-farm XP, 45 GameResource asset types, data-driven economy ROI simulator.
- `D:\sbox-lessons\mining-v2\games\lowkeynetworks.newrp.md` â€” framework architecture: dependency-ordered module kernel, `Rpc.FilterInclude` proximity/vote targeting, ownable linked-door groups, EventBus money (non-`[Sync]` alternative), per-player-cap vendor with live-object tracking, `[ConCmd]`â†’`[Rpc.Host]` chat intercept.

Neither `facepunch.ss2`, `despawn.murder`, `facepunch.fair`, nor `barrelproto.ragroll` contains roleplay/DarkRP-specific material.
