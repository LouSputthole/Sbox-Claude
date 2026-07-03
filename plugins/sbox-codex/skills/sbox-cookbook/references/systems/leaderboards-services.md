# Leaderboards & Live Services (Stats / Leaderboards / Achievements)

How to wire global leaderboards, persistent player stats, and achievements in modern s&box using the built-in `Sandbox.Services` cloud — no custom backend required.

## What this IS and when you need it

s&box ships a free, Steam-account-keyed cloud through `Sandbox.Services`: **Stats** (per-player named counters), **Leaderboards** (a ranked view *derived from a stat*), and **Achievements** (one-shot unlocks). Reach for it whenever you want global high-score tables, lifetime "wins/kills/coins" counters that survive between sessions, personal bests, or unlockables — without standing up a server DB. 9 of the 27 mined games use it (the rest roll their own JSON/REST persistence; see the "vs. custom backend" note below).

Key mental model: **a leaderboard is not a separate object you write to — it is a *query over a stat*.** You write a number with `Stats.SetValue`/`Increment`; you read the ranked table back with `Leaderboards.GetFromStat(statName)`. The stat name string is the contract between write and read (yellowletter.terrys_crash_course: LevelLeaderboardService.cs:98-102; facepunch.ss1: Manager.cs:1188 + LeaderboardPanel.razor).

## Canonical modern approach

### 1. Submit a stat (the write side)

```csharp
using Sandbox.Services;

// Count-up metric: accumulates. Three Increment(1) → Sum == 3.
Stats.Increment( "kills", 1 );

// Snapshot/best metric: stores the value; backend aggregates per board config.
Stats.SetValue( "race_time_level1", finishSeconds );

Stats.Flush();                 // push buffered writes (rate-limited — see gotchas)
// or, before an immediate query:
await Stats.FlushAndWaitAsync();
```

`SetValue`/`Increment` only **buffer** — nothing uploads until `Flush()` (cheap to call SetValue often; enifun.shop_manager: StatsTracker.cs:54 pushes ~14 values then one Flush at :128). Guard writes so you don't pollute boards in-editor: `if ( Game.IsEditor ) return;` (namicry.gacha_crawler: LeaderboardService.cs:105; vidya.terry_games skips in editor too).

**Per-entry metadata** rides along in an optional dictionary (e.g. the fish name, scramble seed, character build) — read it back via `Entry.DataUrl`, NOT inline (see gotcha):

```csharp
Stats.SetValue( "best_power", power, new Dictionary<string, object> { ["class"] = "mage", ["lvl"] = 40 } );
```
(namicry.gacha_crawler: LeaderboardService.cs:109 — uses the `Leaderboards.Board2` API for metadata; simalami.15_puzzle_master: ClassicLeaderboardSubmitter.cs.)

### 2. Read the leaderboard back (the query side)

```csharp
var board = Leaderboards.GetFromStat( statName );   // optionally (packageIdent, statName)
board.SetAggregationMin();        // lower-is-better (race time); or SetAggregationMax / SetAggregationLast
board.SetSortAscending();         // or SetSortDescending()
board.FilterByNone();             // or FilterByMonth/Week/Day + board.SetDatePeriod(DateTime.UtcNow)
board.MaxEntries = 50;
if ( wantMyRow ) board.CenterOnMe();   // window centered on local player instead of rank 1
await board.Refresh();

foreach ( var e in board.Entries )
    Log.Info( $"#{e.Rank} {e.DisplayName} = {e.Value}  ({e.Timestamp})" );
```
(simalami.15_puzzle_master: ClassicLeaderboardQuery.cs:74-107 — verified; matches lavagame.multis_cases LeaderboardData.cs and facepunch.ss1 LeaderboardPanel.razor.)

`Entry` exposes `Rank`, `DisplayName`, `Value`, `Timestamp`, `SteamId`, `DataUrl`. **Aggregation must match your intent**: `Min` for fastest-time boards, `Max` for high scores, `Last` for "current total right now" (namicry uses `Last` so the board shows live totals, not lifetime peak — LeaderboardService.cs:209).

### 3. Read your own stat directly (no board needed)

```csharp
var stat = Stats.GetPlayerStats( packageIdent, Game.SteamId ).GetValue( statName );
float pb  = stat.Min;     // or .Max / .Sum / .LastValue depending on metric
// or the local-player shortcut:
float mine = Stats.LocalPlayer.Get( statName ).Min;
```
(yellowletter.terrys_crash_course: RaceTimerSystem.cs:411; facepunch.ss1: MenuManager.cs:156 reads `.Min` to compute highest difficulty beaten.)

### 4. Achievements

```csharp
Sandbox.Services.Achievements.Unlock( "first_win" );   // idempotent; backend ignores dupes
```
Unlock inline where earned. Wrap in try/catch and only fire on the owning client (Steam stats are per-user). Keys must be **pre-registered on the sbox.game package page** or the call silently no-ops (dimmies.terryspapers: GameHandler.cs:459; simalami.15_puzzle_master: AchievementStrategy.cs:23 wraps a safe `unlock()`).

### 5. The networking rule that bites everyone

**`Sandbox.Services.Stats` always writes to the *calling client's own* Steam account.** The host **cannot** write another player's stat. So in multiplayer you must round-trip the write to the owning client:

```csharp
// Host computes the award, then asks the owning client to log it to ITS account.
[Rpc.Broadcast]
void LogStatToBackend( ulong steamId, string statName, int amount )
{
    if ( Connection.Local.SteamId == steamId )      // only the owner actually writes
        Sandbox.Services.Stats.Increment( statName, amount );
}
```
(treehaven.sdiver: StatsManager.cs:221-242 — verified; same pattern in Blind via `[Rpc.Owner(NetFlags.HostOnly)]` AwardKillBP, Player.cs:786.) A common convention: `steamId == 0` means a **team stat every deployed client logs to its own account** (treehaven.sdiver: StatsManager.cs:225).

## Notable variations seen across games

- **Stat-as-everything (recommended baseline).** The same stat key drives PB, the board, and progression gating — no parallel state. `race-time-{level}` is the PB *and* the leaderboard *and* the unlock gate (yellowletter.terrys_crash_course: LevelLeaderboardService.cs; facepunch.ss1 uses per-difficulty stat names, Manager.cs:1601).
- **Stats as the *only* persistence (no save file).** playbtg.elevator stores coins/exp/streak/wins entirely in cloud Stats — `Increment` for sums, `SetValue` for last-value, `RefreshStats()` rehydrates on spawn from `Stats.LocalPlayer` (ElevatorPlayer.Score.cs:65). No local file at all.
- **Submission ledger / stat-name versioning.** vault77.chop_the_forest records a baseline so only *deltas* push, throttles `Flush` to 12s, and recovered corrupted v1 stats by **renaming the stat `lumber_logs_v2`** + bumping a revision to force a one-time re-submit (LumberSboxStatsServiceBridge.cs:58/167).
- **Merge cloud board with live in-session players.** stepdev.xtrem_road refreshes the global top-5 every 8s, then merges those rows with `Scene.GetAllComponents<PlayerLeaderboardStats>()` keyed by SteamId taking the max, so the table shows people in the lobby even before the backend catches up (GameLeaderboardService.cs:219). Pairs with a **local-best overlay** on the player's own row to hide minutes of backend lag (yellowletter: LevelLeaderboardService.cs:231).
- **Friends scope.** Fetch a wide window (e.g. 200), then filter with `new Friend(steamId).IsFriend` and re-rank survivors locally — true global rank is discarded in that scope (yellowletter: LevelLeaderboardService.cs:95-161).
- **Metadata via `Board2`.** namicry.gacha_crawler attaches a rich `Dictionary<string,object>` to each `SetValue` and queries `Leaderboards.Board2` for tooltip data (LeaderboardService.cs:102/195).
- **Steam-Stats-as-economy.** Blind keeps spendable currency *and* item ownership in Stats: `bp_s1` via `Increment`/`.Sum`, `owns_skin_{id}` as write-once `Increment(+1)` read via `.Sum > 0` (Player.cs:786, ChallengeService.cs). Deterministic daily challenges seed `System.Random` with an FNV-1a mix of `SteamId + periodIndex` (ChallengeDef.cs:69).
- **vs. custom backend.** When you need server-authoritative or competitive economy, games drop Services for their own store: REST/Supabase (lavagame.multis_cases SaveCloud.cs, namicry GameManager.cs), or host-authoritative `[Sync(SyncFlags.FromHost)]` + HTTP via `Sandbox.Http` (vault77 HttpBackendTransport.cs — **never raw `System.Net.HttpClient`, the whitelist blocks it**). Use Services for vanity/global boards; use a real backend for anything cheat-sensitive.

## Gotchas

- **`SetValue` does NOT overwrite — every write accumulates into `.Sum`.** Three `SetValue(1)` gives `.Sum == 3`. Use `Increment`+`.Sum` for counts; use `.LastValue` for "current selection / claim flags / epoch snapshots" (Blind: BPGainQueue/ChallengeService). A board with `SetAggregationMin/Max/Last` picks which interpretation the *table* shows — set it deliberately, it must match your metric.
- **Leaderboard lags minutes behind a just-submitted run.** Keep an in-memory PB and overlay it on the player's row; refuse to clear a known PB if the backend returns 0 (yellowletter: ApplyLocalBest, RaceTimerSystem.cs:411).
- **In-editor = empty/local boards.** Real cloud data only exists once the game is **published to sbox.game**, and stat/achievement keys must be declared on the package page (or in `addon.config`) or writes silently no-op (lavagame.multis_cases; dimmies).
- **`Flush` is platform-rate-limited.** Don't flush per-frame; throttle (vault77 = 12s; enifun = 60s; lavagame = 120s) and only push on *changed* values (stepdev guards with last-published comparisons).
- **Host can't write another player's stat** — round-trip via `[Rpc.Broadcast]`/`[Rpc.Owner]` and check `Connection.Local.SteamId == steamId` before writing (treehaven; Blind). Skip writes on proxies / in editor.
- **Set aggregation & sort *before* `Refresh()`** — they're board config, not query params.
- **`Task.WhenAll(IEnumerable)` is whitelist-blocked** in the sandbox. Start all tasks, then `await` each in a loop (simalami: ClassicLeaderboardQuery.cs:112-119).
- **Per-entry custom data is no longer returned inline** — only via `Entry.DataUrl` (one HTTP GET per row via `Sandbox.Http.RequestStringAsync`). Skip it for dense HUD tables (simalami: ClassicLeaderboardQuery.cs:96-120).
- **Wrap every query in try/catch** — offline/unauthenticated players must not break the win/finish flow; fall back to the previous list rather than blanking the UI (all games do this).

## Seen in

- **yellowletter.terrys_crash_course** — `LevelLeaderboardService.cs` (friends scope, CenterOnMe, local-best overlay), `RaceTimerSystem.cs` (PB via Stats), `LevelMedals.cs` / `LevelProgressionService.cs`.
- **simalami.15_puzzle_master** — `ClassicLeaderboardQuery.cs` / `ClassicLeaderboardSubmitter.cs` (full configure→refresh→read, DataUrl seeds), `AchievementStrategy.cs`.
- **namicry.gacha_crawler** — `Code/Services/LeaderboardService.cs` (`Board2` + metadata dictionary, `Game.IsEditor` guard).
- **facepunch.ss1** — `Manager.cs` (BroadcastVictory / GetStatName), `ui/Panels/LeaderboardPanel.razor`, `MenuManager.cs`.
- **treehaven.sdiver** — `Code/Managers/StatsManager.cs` (NetDictionary → Services dual-write via `[Rpc.Broadcast]`, team-stat `steamId==0`).
- **enifun.shop_manager** — `Code/Economy/StatsTracker.cs` (push-all + single flush, host-only).
- **stepdev.xtrem_road** — `GameLeaderboardService.cs` / `PlayerLeaderboardStats.cs` (cloud merged with in-session players).
- **playbtg.elevator** — `ElevatorPlayer.Score.cs` (Stats as sole persistence), `AchievementHelper.cs`.
- **lavagame.multis_cases** — `StatsSync.cs` / `LeaderboardData.cs` (change-detected push, client-side paging).
- **vault77.chop_the_forest** — `LumberSboxStatsServiceBridge.cs` (submission ledger, stat-name versioning, season schedule).
- **Blind (suburbianites.blindloaded)** — `Player.cs` / `ChallengeService.cs` (Stats-as-economy, Sum vs LastValue, deterministic challenges).
- **dimmies.terryspapers** — `LeaderboardManager.cs` (one-line `Stats.SetValue`), inline `Achievements.Unlock`.
- **vidya.terry_games** — `Player.cs` (`UnlockAchievement`/`IncrementStat` via `[Rpc.Owner]`, editor-skipped).

---

Verify live: the installed SDK is authoritative — run `describe_type Sandbox.Services.Stats`, `describe_type Sandbox.Services.Leaderboards`, and `search_types Board` to confirm current method names/aggregation signatures before coding; the Services API shifts between SDK versions (e.g. metadata moved to `Board2`/`DataUrl`).

See also: the **sbox-api** skill for resolving exact reflection signatures, and **sbox-build-feature** for the screenshot-driven loop to wire a leaderboard panel and confirm it renders.

## Corpus refresh (2026): more reference implementations

Net-new cross-game patterns not covered above (the four games below were not yet cited):

- **All-static leaderboard state survives host migration / scene reload.** mostudio.sweeper_otso keeps the entire leaderboard model in `static` fields so a mid-round host disconnect or component churn never blanks it. Boot from a disk cache for an *instant* UI, then a throttled fire-and-forget refresh repopulates from the cloud (mostudio.sweeper_otso: `Code/LeaderboardSystem.cs`):
  ```csharp
  static List<Row> _wins, _fastest;  static int _version;
  // load leaderboard_cache.json first → UI renders immediately
  // RefreshAll() throttled to 10s, fire-and-forget
  ```

- **Atomic multi-board swap with a single version bump.** Same file: each of the 3 boards is paged in 20-entry chunks (`Offset` up to 100, de-duping SteamIds) into a *temp* list, then all three are swapped in at once and `_version++` fires **once** — so every `LeaderboardPanel` re-renders one time per cycle, not three. Pattern for any "rebuild N lists then show" without UI flicker.

- **Targeted-RPC persistence via `Rpc.FilterInclude` (cleaner than the broadcast+steamId-check above).** Since `Stats.SetValue` is local to the calling Steam user, fluffybagel.chess_otb routes the write to exactly the owning client instead of broadcasting to everyone and filtering. A `GameObjectSystem` can't host RPCs, so a tiny component does (fluffybagel.chess_otb: `Code/Game/Networking/ChessOtbModeRpcs.cs`):
  ```csharp
  using ( Rpc.FilterInclude( targetConnection ) ) hub.RpcWriteMyEloStat( newElo, wld );
  [Rpc.Broadcast] void RpcWriteMyEloStat( int elo, ... ) {
      if ( !Rpc.Caller.IsHost ) return;           // only host may command a write
      if ( w + l + d != 1 ) return;               // result deltas must sum to exactly one game
      Stats.SetValue( ChessOtbStats.Elo(cat), elo ); Stats.Increment( "Otbs_played", 1 );
  }
  ```

- **Mirror cloud values into `[Sync(FromHost)]` so the scoreboard never hits the service.** chess_otb hydrates each player's Elo + W/L/D from `Stats` *once on join* (`HostBeginHydrateRatingsFromStats` → async `Stats.Refresh`), then exposes them as host-synced fields on a `PlayerIdentity` component — the live scoreboard reads replicated state, not the (laggy, rate-limited) Services API. It also tracks **session** W/L/D separately from career totals (fluffybagel.chess_otb: `Code/Game/Networking/PlayerIdentity.cs`). `EloMath.UpdatePair` (K=32, clamp 100–4000) is copy-paste-ready (`Code/Game/Rating/EloMath.cs`).

- **Two stores, two authorities, in one callback.** slamdunk.minigolf shows the split cleanly: in a single `OnTriggerEnter` the *local owner* (`!other.IsProxy`) writes its own score to `Stats` (only the owner can write its SteamId), while the *host* (`Networking.IsHost`) mutates the authoritative synced `Scorecard`. Don't conflate "who owns the cloud stat" with "who owns the gameplay truth" (slamdunk.minigolf: `Code/.../HoleTrigger.cs`, `StatManager.cs`):
  ```csharp
  if ( !other.IsProxy ) StatManager.LogScore( holeName, strokes ); // owner → its own Stats
  if ( Networking.IsHost ) scorecard.Record( player, strokes );    // host → synced truth
  ```

- **Achievement progress for UI bars + unlock-gated content (no currency).** slamdunk pulls live achievement state to drive cosmetic unlocks and progress bars rather than a shop. `Package.FetchAsync(ident)` → `.GetAchievements()` enumerates them; per-achievement `.ProgressionFraction` and `.IsUnlocked` feed the UI directly (slamdunk.minigolf: `StatManager.cs`, `Cosmetics/CosmeticDefinition.cs`):
  ```csharp
  [JsonIgnore] public bool  IsUnlocked => RequiresAchievement && Achievement.IsUnlocked;
  [JsonIgnore] public float Progress   => Achievement.ProgressionFraction * 100f; // live % bar
  // unlock where earned: Achievements.Unlock( "hole_feedback" );
  ```

- **Gate the submit on a cheat flag at the source.** simalami.15_puzzle_master supports a debug instant-win, so it sets a `nextSolveIsDebug` flag before solving; on solve, `ClassicProgression.onSolved` early-returns **before** touching the personal record, the leaderboard `SetValue`, *or* the achievement unlock when `cheated` is true. Any game with a god-mode/debug path should flag the win at the source and short-circuit every reward, not just the score (simalami.15_puzzle_master: `ClassicProgression.cs`).

**Read these games for live-services patterns** (in addition to those in "Seen in"): **mostudio.sweeper_otso** (`Code/LeaderboardSystem.cs` — static state + disk cache + atomic versioned swap; `Code/Inventory.cs` anti-cheat watermarks), **fluffybagel.chess_otb** (`ChessOtbModeRpcs.cs` `Rpc.FilterInclude` targeted write, `PlayerIdentity.cs` `[Sync(FromHost)]` mirror + join-hydrate, `EloMath.cs` rating math), **slamdunk.minigolf** (`HoleTrigger.cs` dual-authority callback, `StatManager.cs` + `Cosmetics/CosmeticDefinition.cs` achievement-gated unlocks via `Package.FetchAsync().GetAchievements()`).
