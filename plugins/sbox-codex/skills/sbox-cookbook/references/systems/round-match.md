# Round / Match Flow (host-authoritative state machine)

The spine of nearly every multiplayer s&box game: a networked manager that drives a match through ordered phases (Lobby → Prep → Round → Resolution → GameOver), counts down a timer, and runs entry/exit side effects per phase. This is the single most-reused system across the mined games.

## What it IS / when you need it

You need this the moment a game has *phases* — a warm-up before a round, a timed round, a results screen, a vote, a loop back. The canonical shape is one `Component` ("GameManager" / "RoundManager" / "GameSystem") holding the authoritative state, ticked only on the host, with all clients reading replicated fields for their HUD. Don't reach for it for purely cosmetic/local sequencing (a flickering light FSM is plain local `OnUpdate`, no sync — repo/mishmaps.backrooms: LightFlicker.cs:52).

## Canonical modern-s&box approach

**1. State enum + a `[Sync(SyncFlags.FromHost)]` state property whose setter is the transition hook.**
`FromHost` means clients literally cannot write it — only the host mutates, clients replicate. Putting the side-effect switch in the *setter* (not a `[Change]` callback) means the host runs entry logic exactly once on transition (repo/vidya.terry_games: GameSystem.State.cs:48-109).

```csharp
public enum GameState { Lobby, Preparing, Round, Resolution, GameOver }

[Sync( SyncFlags.FromHost )]
public GameState State
{
    get => _state;
    private set => SetState( value );   // setter IS the transition
}
private GameState _state = GameState.Lobby;

void SetState( GameState next )
{
    if ( _state == next ) return;       // guard re-entry (GameSystem.State.cs:75)
    _state = next;
    switch ( next )                     // entry side effects, host-side
    {
        case GameState.Preparing: EnterPreparing(); break;
        case GameState.Round:     EnterRound();     break;
        case GameState.Resolution:EnterResolution();break;
        case GameState.GameOver:  EnterGameOver();  break;
    }
    Scene.RunEvent<IGameEvent>( e => e.OnGameStateChanged( next ) ); // tell UI/audio
}
```

**2. A single replicated timer as the heartbeat.** Two idioms, both seen widely:
- A `[Sync] float StateTimer` decremented each host tick (repo/suburbianites.blindloaded: GameManager.cs:237).
- A `[Sync] TimeUntil TimerEnds` set on entry and polled with `if(!TimerEnds) return;` — self-rearming, and a late joiner reads the synced deadline and is instantly in sync (repo/vidya.terry_games: GameSystem.Timer.cs:14, :88-102).

```csharp
[Sync( SyncFlags.FromHost )] public TimeUntil TimerEnds { get; set; }

void EnterRound() { TimerEnds = RoundDuration; /* spawn hazards, reset scores */ }
```

**3. Tick ONLY on the host, switch on state.** Non-host peers early-return before any logic; they are pure readers (repo/goders.natural_disaster_survival: RoundManager.cs:72-141; repo/suburbianites.blindloaded: GameManager.cs:212-267).

```csharp
protected override void OnUpdate()
{
    UpdateLocalHud();                       // client-safe work BEFORE the gate
    if ( !Networking.IsHost ) return;       // host-only past here

    switch ( State )
    {
        case GameState.Preparing: if ( TimerEnds ) SetState( GameState.Round );      break;
        case GameState.Round:     if ( TimerEnds || EveryoneDead() ) SetState( GameState.Resolution ); break;
        case GameState.Resolution:if ( TimerEnds ) SetState( GameState.GameOver );   break;
        case GameState.GameOver:  if ( TimerEnds ) SetState( GameState.Lobby );      break;
    }
}
```

**4. Editor-placed managers must `NetworkSpawn` themselves or `[Sync]` silently won't replicate.** An object dropped in the scene is not networked by default; latch it on the host in `OnStart` (repo/suburbianites.blindloaded: GameManager.cs:201).

```csharp
protected override void OnStart()
{
    if ( Networking.IsHost && !Network.Active )
        GameObject.NetworkSpawn();          // now [Sync] fields replicate to clients
}
```

**5. Client → host commands route through `[Rpc.Host]` (or a Broadcast guarded by `IsHost`).** A ready-up, a vote, a gamble — the client never sets state; it asks the host to.

```csharp
[Rpc.Host]                                  // runs on the host regardless of caller
public void RequestReady()
{
    if ( !Networking.IsHost ) return;       // belt-and-braces
    ReadyPlayerIds.Add( Rpc.CallerId );     // re-validate the caller, never trust args
}
```

## Notable VARIATIONS across the games

- **Setter-switch vs explicit `Set*()` switch.** Terry Games puts the switch in the property setter (GameSystem.State.cs:73); Natural Disaster uses a dedicated `SetRoundState()` that guards `IsHost` + no-op-on-same and runs the switch (repo/goders.natural_disaster_survival: RoundManager.cs:164-175). Same effect; the setter form prevents anyone from skipping the hook.
- **Generic machine + pluggable gamemode.** The state machine owns NO rules — each phase forwards to the active mode: `GetGamemode()?.OnPlaying()`. New microgames are prefabs + a `Gamemode : Component` subclass overriding `OnWaiting/OnPreparing/OnPlaying/OnEnding` + `GetCondition()`, with `[Property] PreparingTime/PlayingTime` tunables (repo/vidya.terry_games: Gamemode.cs:7-104). `playbtg.elevator` and `treehaven.sdiver` do the same with `BaseLevelController` / `BaseGameMode` created/destroyed at runtime.
- **Real-time vs delta timer.** Most decrement `Time.Delta`; `khamitech.battledraft` stores `[Sync] DateTime RoundExpires = UtcNow + duration` and compares wall-clock client-side — fine for a HUD countdown, NOT tick-accurate (GunGameManager.cs:19, GunGame.cs:327).
- **Transition fade double-gate.** Terry Games won't advance until BOTH `TimerEnds` AND `TransitionEnds` are done, so resetting the timer inside `OnTimerEnd()` cancels the switch — the intended (non-obvious) extension point (GameSystem.Timer.cs:99-101).
- **Self-rearming curve cadence inside a phase.** Instead of fixed sub-timers, Natural Disaster evaluates a designer `[Property] Curve` against round progress into a `[Sync] TimeUntil`, polled `if(!timeUntil)` — escalating spawn rate (repo/goders.natural_disaster_survival: disaster_manager.cs:404-411).
- **Map/mode vote between rounds.** A `[Rpc.Host] SendVote(index)` tallies per-SteamId into a dict, debounces ~2s, picks majority (or plurality on timeout) — `apl.sandboxwars`, `khamitech.battledraft` (VoteMapSystem.cs:41), `goders` map vote.
- **Build→Fight loop with ready-vote-to-skip + per-player networked state.** A sandbox base-builder runs `WaitingForPlayers → Building → Redeploy → Fight → RoundEnd`, where the *Building* phase has no fixed timer — it runs until a **ready-vote** majority skips it, and the phase deadline is itself a `[Sync] float PhaseEndsAt` compared against `Time.Now` (not a decrementing timer) so a late joiner reads the absolute deadline and is instantly in sync. Each player carries a networked `BaseBuilderPlayerState`, and the placement tool keeps an **undo history** so the round can roll back builds. Verified against klavs.basebuilder `Code/BaseBuilder/BaseBuilderRoundManager.cs`: `enum BaseBuilderPhase` + `[Sync] Phase`/`PhaseEndsAt`/`RoundNumber`/`AlivePlayers`/`RoundStatus`/`IsPaused`/`PausedTimeRemaining` (`:21-27`), host-only tick that compares `Time.Now >= PhaseEndsAt` per phase (`:104-146`), `StartRedeploy`/`StartFight` entry hooks (`:224-243`), and a `HasEnoughPlayersToRunBuildTimer()` gate so an empty server doesn't burn the build phase. Pause/resume is built in via `IsPaused` + a stored `PausedTimeRemaining`.

## Gotchas

- **Editor-placed manager not networked → silent no-replication.** The #1 bite: `[Sync]` does nothing until the host `NetworkSpawn`s the manager GameObject (repo/suburbianites.blindloaded: GameManager.cs:201).
- **Clients' synced timer JUMPS between snapshots, it does not tick locally.** For smooth countdown *audio*, keep a LOCAL clock seeded on phase entry rather than reading the snapshot every frame (repo/suburbianites.blindloaded: GameManager.cs:283 `_localCountdown`).
- **Entry side effects live in the transition switch, not a `[Change]` handler** — a late joiner who only reads the synced enum will NOT replay entry logic; design so the already-synced state is enough, or run a one-shot reconcile (repo/goders.natural_disaster_survival: RoundManager.cs:164).
- **Host migration nukes host-only state.** A promoted client has all private arrays null and a stale `[Sync] State`. On `OnBecameHost`, hard-reset / rebuild scene-scanned objects (`Scene.GetAllComponents<Panel>()`, destroy orphans) before trusting anything; keep a `_hostInitialized` fallback in `OnUpdate` for when `OnBecameHost` misfires (repo/suburbianites.blindloaded: GameManager.cs:229, PlayerSpawner.cs:82). Set `LobbyConfig.AutoSwitchToBestHost=false` or a better-ping joiner can migrate *a live match*.
- **Order `OnStart` carefully.** Natural Disaster `await Task.FrameEnd()` before touching state to dodge OnStart ordering; Terry Games has a fake `Initializing` state that immediately collapses to `Waiting` (GameSystem.State.cs:90).
- **Single-player editor testing.** Special-case it (`Game.IsEditor`) so the round doesn't end with one player / so player-count constraints don't block dev iteration (repo/goders: RoundManager.cs:107; repo/vidya: GamemodeResource canBeNextGamemode short-circuits in editor).
- **Re-validate every client request on the host.** Never trust args from `[Rpc.Host]`; re-clamp and check `Rpc.CallerId` (repo/vault77.chop_the_forest: PlayerProgression.cs request→apply→confirm triad).

## Seen in

- `suburbianites.blindloaded` — Blind: 8-phase host FSM, `[Sync] State`+`StateTimer`, lazy `NetworkSpawn`, local countdown clock, host-migration hard reset (Code/Game/GameManager.cs:212, :240; PlayerSpawner.cs:82).
- `vidya.terry_games` — generic `GameSystem` machine + `Gamemode` plugin base + `[Sync] TimeUntil TimerEnds` + `ISceneEvent` bus (Code/Logic/GameSystem.State.cs, GameSystem.Timer.cs, Gamemodes/Base/Gamemode.cs).
- `goders.natural_disaster_survival` — `RoundManager` 5-state, curve-driven wave cadence, map vote + scene swap (Code/globals/RoundManager.cs:72, :164; Code/disasters/disaster_manager.cs:404).
- `khamitech.battledraft` — wall-clock `RoundExpires`/`IsPlayMode` + `VoteMapSystem` (Code/Addons/GunGame/GunGame.cs:327; Code/Utils/VoteMapSystem.cs:41).
- `apl.sandboxwars` — `MiniGameManager` ModeSelect→Build→Battle + chat-vote skip (sandbox/Code/MiniGameManager.cs:14, :460).
- `playbtg.elevator` — `ExperienceManager` round rotation + host-migration survival via serialized queue (elevator/Code/Experiences/ExperienceManager.cs:174).
- `treehaven.sdiver` — `GameManager` + swappable `BaseGameMode` created/destroyed at runtime (Code/Managers/GameManager.cs:176; Code/Gameplay/GameMode/ExpeditionMode.cs:172).
- `clearlyy.s_miner` — vote → shared world reset round flow (MineReset.cs:125, :241).
- `dimmies.terryspapers` — in-game clock / day-cycle with a `TaskCompletionSource` "wait until world idle" day-end gate (Code/GameHandler.cs:124).
- `klavs.basebuilder` — `BaseBuilderRoundManager` Build→Fight FSM with absolute `[Sync] PhaseEndsAt`, ready-vote-to-skip build, per-player networked state + placement undo, pause/resume (Code/BaseBuilder/BaseBuilderRoundManager.cs:21, :104, :224).
- `mostudio.sweeper_otso` — co-op round flow with a host-migration watchdog (reclaim orphans + reconcile `[Sync]` registry + ~1s deferred sanity-restart) and vote-kick/temp-ban (Code/HostWatchdog.cs, Code/VoteKickSystem.cs).

## Verify live

The installed SDK is authoritative — reflect, don't trust memory. Confirm the exact members before coding:
`describe_type TimeUntil`, `describe_type Sandbox.SyncFlags`, `search_types Rpc`, `describe_type Networking` (look for `IsHost`/`IsActive`), and `describe_type Connection` for `Rpc.CallerId` / `Rpc.Caller`.

Cross-links: see **sbox-api** for the reflection workflow (`describe_type`/`search_types`) and **sbox-build-feature** for the screenshot-driven build/verify loop that turns this recipe into a working manager.

## Corpus refresh (2026): more reference implementations

Net-new patterns from the latest mining pass. The spine above is unchanged; these are *variations* worth knowing when the basic enum-switch manager isn't the right shape.

### State-as-component instead of enum-switch (the big alternative)

`despawn.murder` does NOT use one enum + a switch. Each phase is its own **`RoundState : Component`** subclass (`WaitingRoundState`, `PreparingRoundState`, `MapVoteRoundState`, `InProgressRoundState`, `PostRoundState`) with a `Begin()/Tick()/Finish()` lifecycle (`OnBegin/OnTick/OnTimeUp/OnFinish` virtuals + player-event virtuals routed from `ISessionEvent`/`IPlayerKilledEvent`). The manager just holds the *active* state component and transitions by adding/removing it. Time is still one `[Sync(SyncFlags.FromHost)] TimeUntil TimeLeft` re-armed in `Begin()`; `Tick()` early-returns on client / `HasEnded` / `!IsValid` (despawn.murder `Systems/Rounds/RoundState.cs`, `Systems/Rounds/States/*`).

```csharp
// RoundManager.TransitionNext — copy data OUT before Finish() destroys the old state component
if ( State is InProgressRoundState inProgress ) {
    GameManager.RoundsLeft--;
    var data = PreparePostRoundData( inProgress );      // grab timeline+roles NOW
    TransitionTo<PostRoundState>( x => ApplyPostRoundData( x, data ) );
    return;
}
```

Two non-obvious lessons from this shape:
- **Copy any per-phase data out *before* you transition** — `Finish()` destroys the old state's components, so the post-round screen would otherwise read from a dead object (`RoundManager.TransitionNext`).
- **Each state carries an `Identifier` string** so the component can be re-resolved *by name across the network* (clients don't get the host's object reference for free). Prefer this when phases are heavyweight enough to deserve their own files; the enum-switch spine stays simpler for 3–5 light phases.

### Paired local+RPC apply so the host's own client + proxies converge instantly

`vault108.suspectra` (`GameManager.cs`, the 2088-line spine) keeps `[Sync] GameState CurrentState` as the durable truth but does NOT wait for replication to run the visual transition. Each transition calls a local apply *and* a broadcast of the same method:

```csharp
void BeginMeetingDiscussion() {            // host-only
    ApplyDiscussionStateLocal( ... );      // host's own client runs it THIS frame
    RpcApplyDiscussionState( ... );        // [Rpc.Broadcast] → every proxy runs the same local method
    CurrentState = GameState.Discussion;   // [Sync] = durable source of truth for late joiners
}
```

The `[Sync]` enum is the reconcile path (a late joiner reads it and is correct); the RPC is the low-latency nudge so nobody waits a snapshot for the banner/SFX. Two more reusable bits from the same file: a **revision-counter** (`LobbyCountdownStartRevision` / `CancelRevision` as `[Sync] int`) lets clients detect *start/cancel edges* of a countdown for SFX without any RPC, and **every inbound networked string is host-sanitized** through `NormalizeNetworkText` (strip CR/LF, trim, length-clamp) before it's stored in a `[Sync] NetList<ChatMsg>`.

### Vote-tally details the spine glossed over

The spine notes "a map/mode vote exists." `vault108.suspectra`'s `ResolveVoting()` is a complete, copyable algorithm for *any* kick/eject/decision vote:
- Each player carries `[Sync] Guid VotedForPlayerId`; **voting for the manager's own `GameObject.Id` is the canonical "Skip" sentinel.**
- **Timed-out / empty votes count as skips:** `alive.Count(x => x.VotedForPlayerId == GameObject.Id || x.VotedForPlayerId == Guid.Empty)`.
- Group real votes, take the max, count top candidates; **tie OR skip-majority → no ejection**, signalled with string sentinel tokens (`...SkippedToken` / `...TiedToken`) so the UI renders the right "no one was ejected" copy.
- Discussion can end early when **all** alive players set a `[Sync] WantsToSkipDiscussion` (after a ~1s grace so it can't be instant-skipped).

### Self-terminating rounds without a fixed timer ("most players done")

`slamdunk.minigolf` (`RoundManager.cs`, `Component, INetworkListener`, singleton) guarantees a phase always ends even though players finish at wildly different times:
- `OnHoleCompleted` advances **immediately** if everyone holed; otherwise starts a 30s countdown **once `completedPlayers.Count >= floor(allPlayers/2)`**; on timeout, non-finishers are auto-scored `shotsTaken + Par` (a DNF penalty). The round can never hang on one AFK player.
- **Every mutator asserts authority** with `Assert.True(Networking.IsHost)` at the top (not just `OnUpdate`'s early-return) — belt-and-braces so a stray client call throws loudly instead of corrupting state.
- **Self-resetting loop with no scene reload:** when the end timer elapses, `OnUpdate` just calls `OnStart()` again to wipe the scorecard and re-draw a course.
- **Course draw = grouped random:** group hole prefabs by difficulty, `OrderBy(_ => Guid.NewGuid())`, `Take(n/3)` per tier → a balanced 9-hole set; sync the *indices* (`NetList<int>`), not the prefabs.

### Late-joiner fairness (beyond just "spectate")

`slamdunk.minigolf` `OnClientAdded`: a joiner within 15s of a hole starting (and no countdown active) is allowed to actually *play*; otherwise they spectate **and are back-filled the average score of every already-completed hole** so the scorecard stays comparable. This is the missing half of host-migration/late-join handling — not just "can they see it," but "is their score fair."

### Round FSM gates player input via a computed property

`barrelproto.ragroll` (`Code/mode/RollMode.cs`) replicates state with one `[Sync, Change] enum RagRollState { None, Waiting, Staring, Prepare, Battle, Ended }` plus `[Sync] float _stateEndTime` (host writes `Time.Now + timer`; `StateTimer => Max(0, _stateEndTime - HostTime)`). The reusable idea is that the **round state directly gates gameplay** through a computed flag the controller reads:

```csharp
public bool CanMove => _gameState != RagRollState.Prepare;   // input frozen during the Prepare phase
```

It also keeps **host vs client ticks separate**: `HostUpdate()` (host-only) drives transitions, `ClientUpdate()` runs everywhere for menu/input — a cleaner split than one `OnUpdate` with an `if(!IsHost)` halfway down.

### Swappable game-mode object that survives host migration

Both `barrelproto.ragroll` and the spine's Terry-Games/treehaven entries make the *mode* a pluggable object, but ragroll shows the **host-migration-safe** version concisely (`GameController.InitializeMode`): clone the mode prefab → `NetworkSpawn()` → `Network.SetOrphanedMode(NetworkOrphaned.ClearOwner)`, then **re-assert it in `OnBecameHost`**. The single networked `IGameMode` object outlives the host leaving — a working seamless-migration recipe for drop-in/drop-out hubs. (Contrast `mostudio.sweeper_otso`'s heavier watchdog approach in the spine: ragroll relies on orphan-clear + re-assert; sweeper rebuilds and sanity-restarts.) See also ragroll's **ping-corrected `HostClock`** (`Code/mode/networking/HostClock.cs`) when a synced *countdown* needs to be smooth, not just a HUD number — host broadcasts `[Sync] _hostTimestamp` every 0.4s, clients add `Connection.Host.Ping * 0.001f` and only snap on >0.1s drift.

### Round state as `[Sync]` bools + tags, not a List of players

`mostudio.sweeper_otso` (`Code/MINESWEEPER.cs`) expresses the machine as a bag of `[Sync]` bools with a composite read-only gate, and — crucially — **stores per-player round membership as networked tags on each player GameObject, not in a manager-side `List<Player>`**:

```csharp
[Sync] public bool BoardActive, ClearInProgress, IsGameOver, WinInProgress { get; set; }
[Sync] public bool TimerRunning { get; protected set; }
public bool RoundActuallyRunning => BoardActive && TimerRunning && !IsGameOver && !ClearInProgress && !WinInProgress;
```

A fresh mid-round joiner is auto-detected in one `OnUpdate` pass ("no `playing`/`excluded` tag → exclude") with zero list-reconciliation — the single biggest correctness win for drop-in co-op, because there's no manager collection to desync on migration.

### Phase-enter side effects can be heavy-handed (and that's fine)

`apl.sandboxwars` (`Code/MiniGameManager.cs`, `enum GamePhase { Waiting, Build, Battle, ModeSelect }`) shows entry hooks doing aggressive resets the spine's examples only gesture at: `ApplyBuildPhaseState()` **heals every player to full via a deliberate over-damage/respawn** (`player.OnDamage(new DamageInfo(5000,...))`) and re-enables spawn points; `ApplyBattlePhaseState()` **force-swaps every player off build tools** to a real weapon; leaving Battle runs `CleanupSystem.Cleanup()` to wipe the arena. Plus a host-side janitor unrelated to any single transition: `AddPropHealthToNewObjects()` runs every 2s and adds `PropHealth` to any stray `Rigidbody` so everything spawned mid-build becomes destructible in battle — *making props destructible without touching the spawn path.* Its vote is a `[ConCmd(... ConVarFlags.Server)] VoteSkip/VoteEnd/VoteCTF` (callable as `!voteskip` chat) tallied into a `HashSet<ulong>` against `players/2 + 1`.

### "Round-match" with no PvP: the day/season cycle

Not every game with phases is competitive. `facepunch.fair` (a tycoon, no avatar) has its entire temporal loop in `Utils/DayNightController.cs` firing `ITimeOfDayEvents.OnNewDay()`, with an `IsPeakSeason()` concept driving guest influx and when park rating is sampled (only 10am–9pm while open). The takeaway: the same host-authoritative-clock-+-phase-event machinery powers a tycoon day-cycle, an idle game's tick, or a survival day/night — reach for this page even when there's no winner.

### Fairness over many rounds: pity / bad-luck protection

`despawn.murder`'s `MurdererTicketManager.cs` solves "who's the special role this round" *across* rounds, persisted by SteamId: weighted-random where unpicked players accumulate tickets, the picked player takes a hefty reduction, a `Max(1, ...)` floor keeps everyone eligible, and a `Strategy` interface can swap the whole policy. Generalizes directly to "who's the impostor / seeker / loot-winner" so the same player isn't repeatedly chosen (or repeatedly skipped). Pairs with the per-phase-component machine above.

### Read these games (for round/match work)

- `despawn.murder` — **state-as-component** machine (`RoundState` subclasses + `TransitionTo<T>` + copy-data-before-Finish + per-state `Identifier`), AI clue-Director pacing, persisted pity-ticket role fairness (`Systems/Rounds/`).
- `vault108.suspectra` — social-deduction discussion→vote→eject loop, **paired local+RPC apply**, revision-counter countdown edges, complete vote-tally (skip sentinel, empty=skip, tie/skip→no eject), host string-sanitization (`GameManager.cs`).
- `slamdunk.minigolf` — **"most-players-done" self-terminating** rounds, `Assert.True(IsHost)` on every mutator, grouped-random course draw, `OnBecameHost` restart, **average-score back-fill** for late joiners, self-resetting loop via `OnStart()` (`RoundManager.cs`).
- `barrelproto.ragroll` — `IGameMode` plugin object, **round FSM gates input** via `CanMove`, `[Sync] _stateEndTime` timer, **orphan-clear host migration**, ping-corrected `HostClock` for smooth synced countdowns (`Code/mode/`).
- `mostudio.sweeper_otso` — round state as `[Sync]` bools + `RoundActuallyRunning` composite + **tag-based player membership** (no manager-side list), host-migration watchdog (`Code/MINESWEEPER.cs`).
- `apl.sandboxwars` — Build/Battle phase-enter hooks that **heal/over-kill + force-swap weapons + cleanup**, periodic runtime `PropHealth` janitor, `!voteskip` ConCmd vote (`Code/MiniGameManager.cs`).
- `facepunch.fair` — phaseless **day/season cycle** as a non-PvP use of the same clock+phase-event machinery (`Utils/DayNightController.cs`).
