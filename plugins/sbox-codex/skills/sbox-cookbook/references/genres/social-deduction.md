# Social-Deduction Recipe

How to build a social-deduction game — hidden asymmetric roles, a task/clue economy, body-reporting, and a discussion→vote→ejection loop — in modern s&box (GameObject/Component/Scene), distilled from two deeply-mined shipped games: `vault108.suspectra` (an Among-Us / Mafia clone: Innocent vs Mafia, ~25 task minigames, sabotages, emergency meetings, proximity+role voice, voting) and `despawn.murder` (a Trouble-in-Terrorist-Town / "Murder" lineage: secret Murderers vs Bystanders who earn a revolver from clues, an AI Director that paces clue spawns, a backend XP/profile service). A third game, `suburbianites.blindloaded`, touches the genre from the *hidden-information* axis (blind rounds — you can't see opponents) rather than hidden *roles*; skim it only if you want sound/decoy/mute information mechanics.

## What defines the genre

A social-deduction game is a **hidden-role round game**: at round start the host secretly assigns asymmetric roles (impostor/murderer/mafia vs crew/bystander/innocent), reveals each role **only to its owner**, then runs a loop where the informed minority sabotages/kills while the uninformed majority does tasks and gathers clues. The drama is **information asymmetry resolved socially** — a body gets reported, players gather, they *talk*, they *vote*, someone is ejected, and a win condition checks. The genre *is not* any one minigame or the combat; it's the **role-secrecy + meeting/vote machine + per-recipient visibility** that turns a lobby into accusations.

Three shapes show up in the corpus:

- **Among-Us / Mafia** (`suspectra`): a big host-driven `GameState` machine with an explicit `Discussion → Voting → Ejection` phase, a task economy whose progress bar advances even for fakers, sabotages, emergency meetings, and proximity voice with role channels. Copy this when you want the full meeting-and-vote ceremony.
- **TTT / Murder** (`despawn.murder`): a continuous round (no meeting phase) where bystanders *earn* a weapon by collecting clues or completing generated tasks, roles include data-driven sub-roles (Detective/Tracker/Snitch), and an **AI Director** paces clue spawns by live telemetry. Copy this when the "deduction" happens through gunplay + clues rather than a vote.
- **Blind-round hidden-information** (`blindloaded`): no role secrecy — instead you can't *see* opponents and act from sound/memory, with reveal/mute/decoy items. A different design axis; lift its visibility tricks, not its structure.

**Core loop (Among-Us shape):** `Waiting (lobby readies) → assign roles + tasks → Playing (tasks/kills/sabotage, body reported or meeting called) → Discussion (talk, synced chat) → Voting (tally, tie/skip resolution) → Ejection (reveal + animate) → win check → back to Playing`. Everything else (minimap, voice, HUD, achievements) is scaffolding around that. (suspectra: `Code/GameManager.cs` is the 2088-line spine.)

## The system stack to compose

Build these as separate components. References point to existing system docs where one applies.

| System | Role | Reference |
|---|---|---|
| Networked round/phase machine | `[Sync] GameState` host-only FSM with the meeting/vote phases | `references/systems/round-match.md` |
| Hidden role assignment | secret roles at round start, revealed only to self | — (below) |
| Per-recipient visibility | role hidden per-viewer; mafia see mafia; reveal screens | — (below) |
| One host-validated interaction verb | `CmdUseTarget` dispatches kill/task/report/sabotage/door | `references/systems/anti-cheat.md` |
| Task / clue economy | tasks as the win timer; clues as in-round currency | `references/systems/spawning-waves.md`, `references/systems/economy-currency.md` |
| Body-report + meeting trigger | report a corpse / press the button → snap everyone to a meeting | — (below) |
| Vote flow (tally + tie/skip) | `[Sync] VotedForPlayerId`, count, resolve, eject | `references/systems/dialogue.md` |
| Proximity / role / dead voice | worldspace voice + occlusion + mafia & dead channels | — (below) |
| Win-condition check | mafia-parity / all-tasks-done / all-impostors-out | `references/systems/round-match.md` |
| Fair role selection (anti-streak) | persisted pity tickets so the same player isn't "it" twice | `references/systems/anti-cheat.md` |
| Sub-roles / perks (data-driven) | Detective/Tracker/Snitch as `.subrole` assets | — (below) |
| Stats / achievements / leaderboard | per-match wins, ejections, tasks → backend | `references/systems/leaderboards-services.md` |
| Allocation-free multiplayer HUD | one shared per-scene snapshot, not per-panel scans | `references/genres/party-microgame.md` (and below) |

## The authority model: one synced enum + paired local/RPC transitions

The whole match is a **host-only state machine** off a single `[Sync] GameState`. The host ticks a `switch (CurrentState)` in `OnUpdate` with `Time.Delta` countdowns; clients never write state. The non-obvious part is the **transition pattern**: each phase change calls a plain local apply method *and* a matching `[Rpc.Broadcast]` that calls the same local method on everyone — the `[Sync]` enum is the durable truth, the RPC is a low-latency nudge so the host's own client and all proxies converge the same frame instead of waiting for replication. (suspectra: `Code/GameManager.cs` — `enum GameState { WaitingForPlayers, Playing, Discussion, Voting, EjectionSequence, InnocentsWon, MafiaWon }`, host-only `OnUpdate` switch, `BeginMeetingDiscussion → ApplyDiscussionStateLocal + RpcApplyDiscussionState`.)

```csharp
[Sync] public GameState CurrentState { get; set; }

protected override void OnUpdate()
{
    if ( !Networking.IsHost ) return;          // clients only read CurrentState
    switch ( CurrentState )
    {
        case GameState.Playing:    TickPlaying();    break; // sabotage timer, task-win check
        case GameState.Discussion: TickDiscussion(); break; // countdown, all-want-skip
        case GameState.Voting:     TickVoting();     break; // countdown → ResolveVoting()
    }
}
void BeginDiscussion() { ApplyDiscussionStateLocal(); RpcApplyDiscussion(); } // local + nudge
[Rpc.Broadcast] void RpcApplyDiscussion() => ApplyDiscussionStateLocal();
```

`despawn.murder` uses the same spine but as **distinct `RoundState : Component` classes** (Waiting/Preparing/MapVote/InProgress/PostRound) under one `RoundManager : SingletonComponent`, with a `[Sync] TimeUntil TimeLeft` re-armed on `Begin()` and an index-wrap `TransitionNext()` that skips states whose `CanEnter()==false`. It fires the start/end event **locally on host plus a mirror `[Rpc.Broadcast(HostOnly)]`** so client systems re-raise identical events — the component-per-state variant of the same idiom. Pick the enum-switch when phases are simple and share state; pick the class-per-state when each phase owns a lot of behavior. See `references/systems/round-match.md` for the generic skeleton. (despawn.murder: `Code/Systems/Rounds/RoundManager.cs`, `RoundState.cs`.)

**Lobby gate + start/cancel edges without RPCs:** `suspectra` needs ≥3 ready players (1 in a debug solo flag) then a 5s `LobbyTimer`; it detects start/cancel *edges* for SFX via a `[Sync] int LobbyCountdownStartRevision`/`CancelRevision` revision-counter clients watch — a clean way to fire one-shot client effects off synced state without a dedicated RPC. (suspectra: `Code/GameManager.cs::CheckLobbyReady`.)

## Hidden roles + per-recipient visibility (the heart of the genre)

Two problems: **assign roles secretly on the host**, and **reveal each role only to its owner** (and to teammates where the design allows, e.g. mafia see mafia). The role itself can be a plain `[Sync]` field — what makes it *hidden* is that the **UI gates on the local viewer**, never the raw value.

```csharp
// Role is synced, but every nameplate/HUD decides visibility per-viewer:
public enum Role { Innocent, Mafia }
[Sync] public Role MyRole { get; set; }

bool ShouldRevealRoleTo( PlayerRole viewer )
    => viewer == this                                   // always see your own
    || (viewer.MyRole == Role.Mafia && MyRole == Role.Mafia) // mafia see mafia
    || IsDead;                                          // the dead see everything
```

(suspectra: roles assigned host-side in `GameManager.StartGame()`; nameplates/voice gate on the local player's role + alive state.) `despawn.murder` syncs a `RoleInfo` host→client but **hides it per-viewer in the UI**, and layers **data-driven sub-roles** on top: a `SubRoleResource` (`.subrole` asset) carries DisplayName/Color/Icon/Perks, `Enabled`, `AlwaysAssigned` (one per murderer, e.g. Detective), an optional `EquipmentResource` to grant, and an optional `GameObject BehaviorPrefab` to clone onto the player. Assignment builds two pools — always-assigned + optional (50% roll each) — then at spawn the round state generically `Give(resource.Equipment)` + clones+`NetworkSpawn()`s `resource.BehaviorPrefab`. **Adding a perk-role = author a `.subrole` + a behavior prefab + lang keys, zero assignment-code changes.** (despawn.murder: `Code/Systems/SubRoles/SubRoleResource.cs`, `Code/Systems/Rounds/States/InProgressRoundState.Roles.cs`.)

**Reveal-only-to-some-clients at runtime** (radar, snitch ping, ghost outlines): don't recolor the real object for everyone — clone a tagged ghost and `Rpc.FilterInclude(connection)` it to just the recipients. `despawn.murder`'s radar clones the target's `SkinnedModelRenderer` + bone-merged clothing into an "Outline" ghost, adds a `HighlightOutline`, and filters the RPC to the buyer + spectators, with a time-based alpha fade and tag-based cleanup. This is the reusable "wallhack for one player" / "the dead see roles" recipe. (despawn.murder: `Code/Systems/EquipmentShop/Items/Radar.cs::RadarOutlineFactory`.)

**Fair "who's it" selection (anti-streak):** so the same player isn't the impostor twice running, keep persisted **pity tickets** by SteamId. After each round, non-its `+1` ticket, the it loses `playerCount` tickets; selection is weighted-random with a `Math.Max(1, tickets)` floor so newcomers still have a shot. Make the policy pluggable behind an interface. (despawn.murder: `Code/Systems/MurdererTickets/MurdererTicketManager.cs`, `IMurdererSelectionStrategy`.) See `references/systems/anti-cheat.md` for the fairness/pity-timer pattern; it generalizes to seeker/loot-winner selection.

## One host-validated interaction verb (`CmdUseTarget`)

Don't write a separate RPC per interaction. Funnel **every** world action — kill, do-task, report-body, fix-sabotage, carry, drop, open-door — through **one** host `[Rpc.Broadcast]` that re-validates on the host and dispatches by an enum. This is the cleanest anti-cheat seam in the corpus and the thing that keeps a social-deduction game from being trivially hacked.

```csharp
[Rpc.Broadcast] public void CmdUseTarget( int typeIndex, Guid targetId )
{
    if ( !Networking.IsHost ) return;                  // host re-validates everything
    var type = (UseTargetType)typeIndex;               // Corpse, KillTarget, Task, SabotageConsole, Door...
    if ( IsDead ) return;
    if ( type != UseTargetType.Door && !IsPlayingState(gm) ) return;
    var target = FindSceneObject( targetId );
    switch ( type )
    {
        case UseTargetType.KillTarget:
            if ( CanKillTargetNow(target, ResolveLookRotation(this)) ) DoKill(target); break;
        case UseTargetType.Task:
            if ( !CanUseGenericTargetNow(target) ) break;
            if ( IsTaskBlockedByBlackout(gm, task) ) break;
            if ( IsActiveSabotageObjectiveTask(gm, task.Type) ) break;  // can't "do" a sabotage node as a task
            RpcTaskCompletedOnClient((int)task.Type); RecordSharedTaskProgress(gm); break;
    }
}
```

(suspectra: `Code/PlayerRole.cs:1700`.) The host **re-derives the attacker's aim itself** (`ResolveLookRotation` prefers `PlayerController.EyeAngles` if locally owned, else a `[Sync]` rotation) — it never trusts a client-sent direction. `CanKillTargetNow` re-checks state==Playing, role==Mafia, cooldown, victim alive & not-mafia, **distance**, **line-of-sight**, and **facing** (flattened dot ≥ threshold). Cooldowns are **coupled** so abilities can't be chained (a kill adds a post-sabotage penalty and vice-versa), and a **shared mafia kill cooldown** is written to *every* mafia on a kill so two impostors can't tag-team instant kills. See `references/systems/anti-cheat.md` for the full validated-verb + cooldown-coupling treatment.

## Task / clue economy (and the anti-role-tell)

Tasks serve double duty: they're the **innocents' win timer** and the **cover** that lets the informed minority blend in.

- **`suspectra`** assigns per player by shuffling the task pool, `Take(5)`, ensuring at least one physical carry task, and sending a CSV of enum-ints via `[Rpc.Broadcast] RpcAssignNewTasks("3,7,11,...")`. The task enum has a documented **"append new values at the end to keep serialized indices stable"** comment — real data-migration discipline. Innocents win when `TasksCompleted >= TasksRequired` where `TasksRequired = ceil(innocentCount * 5 * 0.7)`.
- **The critical anti-role-tell:** the shared task bar advances **for mafia too** when they "complete" a fake task — otherwise you could deduce the impostor by watching the bar not move. Faking moves the bar. (suspectra: `Code/GameManager.cs::RecordTaskCompleted` — comment "убирает возможность вычислять роль по полоске задач".)
- **Client-authoritative minigame, host-gated credit:** each task `.razor` runs the puzzle entirely client-side (`Game.Random` seed, `SequenceEqual` win check, local SFX), then calls `localPlayer.CmdFinishMiniGameTask(taskId)`; the host re-validates phase/alive/distance/sabotage-state before crediting. A hacked client can fake "solved" but still can't credit a task it isn't allowed to do. The practical trust split for ~25 minigames. (suspectra: `Code/FusesTaskUI.razor` et al.)

**`despawn.murder` inverts the economy:** clues are an **in-round currency** (`Client.CluesCollected`) spent in a per-owner powerup shop, and bystanders *earn a revolver* either by collecting clues or by completing a **generated 3-task objective** (FindClues / VisitZone / FindBody / Survive…). That objective generator is the corpus's most reusable "procedural per-player quest": a polymorphic `GunTaskDefinition(Scene)` base with `IsEnabled`, an exclusion `Group` (one task per group), and `Make()`; a manager rolls N honoring group exclusion; progress is tracked by **three coexisting strategies** — event-hooks (`OnCluePickup`), polling (`OnFixedUpdate` zone/proximity/survival), and OR-conditions — with per-task `[x/y]` strings pushed per-recipient via `Rpc.FilterInclude`. On all-complete → `pawn.GiveTaskRevolver()`. (despawn.murder: `Code/Systems/GunAcquisition/`.) See `references/systems/economy-currency.md` for the clue-currency shop and `references/systems/spawning-waves.md` for clue spawn placement.

## Body-report → meeting, and the vote flow

A body report (or emergency button) **snaps everyone into the meeting** and transitions the FSM to `Discussion`. Record who died and whether the body was found for the meeting header. (suspectra: `MeetingDeathRecords` is a `[Sync] NetList<DeathRecord>`; report goes through `CmdUseTarget`'s `Corpse` case.)

Vote resolution is a complete, copyable algorithm — the part most teams get subtly wrong:

```csharp
// Each player: [Sync] Guid VotedForPlayerId. Voting for the GameManager's own Id == "Skip".
int skipVotes = alive.Count(x => x.VotedForPlayerId == GameObject.Id      // explicit skip
                              || x.VotedForPlayerId == Guid.Empty);       // timed-out = skip
var realVotes = alive.Where(x => x.VotedForPlayerId != GameObject.Id && x.VotedForPlayerId != Guid.Empty)
                     .GroupBy(x => x.VotedForPlayerId).ToList();
int maxVotes  = realVotes.Count == 0 ? 0 : realVotes.Max(g => g.Count());
var top       = realVotes.Where(g => g.Count() == maxVotes).ToList();
bool skipOrTie = skipVotes >= maxVotes || top.Count != 1;                 // skip-wins OR tie → no ejection
```

(suspectra: `Code/GameManager.ResolveVoting()`, ~150 lines, async.) Key rules to copy: **empty/timed-out votes count as skips**; **a self-id vote is the canonical Skip sentinel**; **skip-wins and ties both resolve to "no one ejected"** (with distinct sentinel tokens `EjectionVoteSkippedToken` / `EjectionVoteTiedToken` so the UI renders the right message); discussion can end early when **all** alive players set `[Sync] WantsToSkipDiscussion` (after a 1s grace). Voting chat is a `[Sync] NetList<ChatMsg>` capped at 80, **host-sanitized** (`NormalizeNetworkText` strips CR/LF, trims, length-clamps — harden every networked string). See `references/systems/dialogue.md` for the generalized vote-flow (it applies to map/kick/decision votes too).

**End-game frame trick:** `EndGame()` is `async` and does `await Task.Delay(50)` after clearing sabotage so the host's local light-restore runs one frame *before* the win screen flips — and it guards `if (!this.IsValid()) return;` after every await. A deliberate "give the renderer a frame" beat. (suspectra: `Code/GameManager.cs::EndGame`.)

## Proximity voice with role + dead channels

Voice is a marquee social-deduction feature and the occlusion/cadence/lerp details are non-obvious. Per remote speaker, compute a target volume by channel priority: **mafia-radio** (only mafia hear, non-worldspace) > **meeting-silence gate** > **dead-only** (the dead hear the dead) > **meeting** (flat 2D so everyone hears the discussion) > **proximity worldspace** with a **trace-based occlusion test** (`OccludedVoiceVolume` 0.25 vs `DirectVoiceVolume` 1.0). Throttle the occlusion trace to every 0.1s and `Lerp` the volume for smoothness; move the listener position to the active spectator/camera when dead. Mafia "radio" reuses the engine push-to-talk action (`Input.SetAction(voiceAction, true)`) so lip-sync/mic-UI still fire. (suspectra: `Code/VoiceChatHandler.cs` — the most complete voice model in the corpus.)

```csharp
float TargetVolumeFor( PlayerRole speaker )
{
    if ( MafiaRadioActive(speaker) ) return IAmMafia ? 1f : 0f;     // role channel
    if ( MeetingSilence ) return 0f;
    if ( speaker.IsDead )  return IAmDead ? 1f : 0f;                // dead channel
    if ( InMeeting )       return 1f;                              // flat 2D in discussion
    return TraceOccluded(speaker) ? OccludedVoiceVolume : DirectVoiceVolume; // worldspace
}
```

## Allocation-free multiplayer HUD

A social-deduction HUD reads scene-wide state constantly (who's alive, ready, voted, mafia counts). Doing `GetAllComponents` per-panel per-frame tanks FPS in a full lobby. Build **one** shared per-scene snapshot and have every `.razor` read it. `suspectra`'s `UiSceneSnapshotCache` rebuilds a `UiSceneSnapshot` every 0.2s (and a `Guid→GameObject` map every 1.0s) **in place** — reusable lists cleared+refilled (never reallocated), a `static Comparison<>` delegate instead of `OrderBy`, aggregates in one manual loop, all wrapped in try/catch for "scene modified during enumeration". Panels gate re-render via a `BuildHash()` folding ~30 inputs, seeded with a variable UI tick (60Hz animating / 8Hz idle). This is the canonical fix for "my Razor HUD lags in multiplayer." (suspectra: `Code/UiSceneSnapshotCache.cs`, `Code/PlayerTaskHUD.razor`.) The same engine/HUD perf concern is covered for the party genre in `references/genres/party-microgame.md`.

**Robust "who is the local player":** with reconnects/duplicates, don't trust the first match — score candidates (connection-match ×16, active, alive, has-role, has-setup) and take the best. (suspectra: `UiSceneSnapshotCache.GetLocalPlayer`, `NetworkOwnershipUtils`.)

## Build order

Build the round machine and role secrecy first — everything else hangs off them. Test with the debug solo flag, but **re-test multiplayer for real** (voice, per-viewer reveals, and vote tallies only exercise with 3+ live clients).

1. **Round/phase machine.** `GameManager` with `[Sync] GameState`, host-only `OnUpdate` switch, `Time.Delta` timers, and the paired `ApplyXLocal` + `RpcApplyX` transitions. Loop Waiting↔Playing with a placeholder. Verify a client sees the phase + timer.
2. **Lobby-ready gate.** ≥N ready players → countdown → start; revision-counter for start/cancel SFX.
3. **Hidden role assignment.** Assign roles host-side at start; sync the role but **gate all UI per-viewer** (`ShouldRevealRoleTo`). Add a role-reveal screen on start. Verify each client sees only its own role.
4. **One interaction verb.** `CmdUseTarget(typeIndex, targetId)` host-validated, starting with `Task` and `KillTarget` (distance/LOS/facing/cooldown/phase/role). Host re-derives aim.
5. **Task economy.** Assign N tasks per player (CSV RPC), client-authoritative minigames with host-gated credit, shared bar that **advances for fakers too**, innocents-win-on-tasks-done check.
6. **Body-report → meeting → vote.** Report via the verb → transition to Discussion → Voting; implement `ResolveVoting` (skip sentinel, timed-out=skip, tie/skip→no-eject), then the Ejection reveal + win check.
7. **Win conditions.** Mafia win on parity (skip at ≤N), innocents win on all-tasks or all-impostors-out.
8. **Proximity + role + dead voice.** Channel-priority volume with throttled occlusion trace and lerp.
9. **Fair "who's it" selection.** Persisted pity tickets by SteamId behind a strategy interface.
10. **Data-driven sub-roles / perks** (`.subrole` + behavior prefab), **sabotages**, **minimap**, **stats/achievements**, **allocation-free HUD snapshot**.

## Pitfalls (from the real games)

- **Role secrecy is a UI concern, not a networking one.** The role can be `[Sync]`; what hides it is gating every nameplate/HUD/voice decision on the *local viewer*. Don't try to withhold the synced value — gate the reveal. For per-recipient reveals at runtime (radar/ghost), clone a ghost and `Rpc.FilterInclude` it, never recolor the shared object.
- **The task bar must advance for the informed minority too**, or players deduce roles by watching it. Credit fake tasks to the shared counter.
- **Count timed-out (empty) votes as skips, and treat skip-wins *and* ties as "no ejection"** with distinct sentinel tokens — the most common vote-resolution bug.
- **Funnel interactions through one host-validated verb.** Re-validate distance/LOS/facing/cooldown/phase/role host-side and **re-derive aim on the host** — never trust a client-sent direction. Per-interaction RPCs multiply your attack surface.
- **Couple ability cooldowns** (kill↔sabotage) and **share the kill cooldown across the minority** so they can't chain or tag-team.
- **Host-only everything in the FSM** (`if (!Networking.IsHost) return;`); clients read synced state and react via the mirror RPC. Use the local-apply+RPC-nudge pattern so the host's own client doesn't lag a replication tick behind.
- **Sanitize every networked string** (chat, names) host-side — strip CR/LF, trim, length-clamp.
- **Don't sequence phases with `await Task.Delay`** for gameplay timing — use the synced `TimeUntil`/`Time.Delta` countdowns so mid-round joiners reconstruct the phase (the lone `await Task.Delay(50)` in `EndGame` is a one-frame render beat, not gameplay timing — and it re-checks `IsValid()` after the await).
- **Append new enum values at the end** (task types, roles) to keep serialized scene/save indices stable.
- **One shared per-scene UI snapshot**, not `GetAllComponents` per-panel-per-frame, or the HUD tanks FPS in a full lobby. Reuse lists in place; gate re-render with `BuildHash()`.
- **Host migration:** store match-critical refs as `[Sync] GameObject` and re-arm `TimeUntil` against the new host's clock (read `.Relative`, re-assign). `despawn.murder` documents this in `RoundManager.ValidateStateAfterMigration`; `suspectra` re-elects a lobby owner on `OnDisconnected`/`OnBecameHost`.
- **`MathF` does not exist in the s&box sandbox** — use `MathX`/`System.Math`. (And it's absent in the bridge editor addon too.)

## Verify live

API surfaces drift between SDK versions — confirm before relying on a signature. Use `describe_type` / `search_types` reflection against the installed SDK as authoritative for:

- `[Sync]` / `SyncFlags` (`FromHost`, `Query`), `IsProxy`, `Networking.IsHost`/`IsActive` — the FSM + role-sync surface.
- `[Rpc.Broadcast]` / `[Rpc.Host]` / `[Rpc.Owner]` and the **filter** API (`Rpc.FilterInclude(connection)`, `Rpc.Caller`) — for the single interaction verb and per-recipient reveals.
- `NetList<T>` / `NetDictionary<T>` — chat/death-record/snapshot containers (and that `List`/`Dictionary` as plain `[Sync]` is a footgun — run `networking_lint`).
- `TimeUntil` (implicit-bool reads false until elapsed, `.Relative` for host-migration re-arm), `Time.Delta`.
- `Scene.Trace.Ray` (`.WithAnyTags`/`.IgnoreGameObjectHierarchy`/`.Run`) and `SceneTraceResult` — for kill LOS + voice occlusion.
- `Sandbox.Voice` / push-to-talk `Input.SetAction(action, bool)` — the proximity/role voice channels.
- `GameResource` + `[AssetType]` — for `.subrole`/`.equip`/`.mapvote` data assets.
- `Sandbox.Services.Stats.Increment`/`Flush` — host-once-per-match leaderboard pushes (see `references/systems/leaderboards-services.md`).

Run **`networking_lint`** on the result — unguarded `[Sync]` mutators, money/health/score as plain `[Sync]`, `List`/`Dictionary` as `[Sync]`, and `[Rpc.Host]` methods that never re-check `Rpc.Caller` are exactly the social-deduction footguns it catches.

## Which games to read

- **`vault108.suspectra`** (`suspectra/Code/`) — **the Among-Us / Mafia reference.** Read `GameManager.cs` (the FSM spine, lobby gate, `ResolveVoting`, win checks, `EndGame`), `PlayerRole.cs` (`CmdUseTarget` validated-verb dispatcher + kill/cooldown validation), `VoiceChatHandler.cs` (proximity + role + dead channels), `UiSceneSnapshotCache.cs` + `PlayerTaskHUD.razor` (allocation-free HUD + `BuildHash` tick + minimap projection), `AchievementSystem.cs` + `SboxStatsServiceBridge.cs` (two-tier stats), and the `*TaskUI.razor` minigames (client-auth + host-gated credit). Bilingual via a `UiText.T(ru, en)` helper.
- **`despawn.murder`** (`despawn.murder/murder/Code/`) — **the TTT / Murder reference** + the standout systems. Read `Systems/Rounds/` (class-per-state round machine, host-migration-safe timer, match timeline + "hero of the round"), `Systems/SubRoles/SubRoleResource.cs` + `Systems/Rounds/States/InProgressRoundState.Roles.cs` (data-driven sub-roles with behavior-prefab dispatch), `Systems/MurdererTickets/` (pity-ticket fair selection), `Systems/GunAcquisition/` (the procedural per-player objective generator), `Systems/RoundDirector/` (AI Director pacing clue spawns by telemetry — adaptive difficulty), `Systems/EquipmentShop/` (clue-currency powerup store), `Systems/Game/GameConVars.cs` (ConVars-as-live-balance-DSL, incl. `"radar=1,silent=2,..."`-style mini-DSLs), and `API/ApiClient.cs` + `Systems/Inventory/MurderDataStore.cs` (resilient backend client + optimistic-store-with-reconcile).
- **`suburbianites.blindloaded`** — *adjacent, different axis.* No hidden roles; instead hidden *information* (blind rounds — you can't see opponents, you act from sound/memory, with reveal/mute/decoy items) plus a freeze-then-ordered-volley showdown and mode-voting. Skim only for visibility/information-manipulation mechanics, not for genre structure.

Cross-links: see the `sbox-api` skill for authoritative type/method lookups (`describe_type`/`search_types`), and the `sbox-build-feature` skill for the screenshot-driven build-and-verify loop that keeps the bridge out of guess-and-check. System deep-dives: `references/systems/round-match.md`, `references/systems/anti-cheat.md`, `references/systems/dialogue.md`, `references/systems/economy-currency.md`, `references/systems/spawning-waves.md`, `references/systems/leaderboards-services.md`. Closest sibling genres: `references/genres/party-microgame.md` (the task minigames are microgames), `references/genres/deathmatch-arena.md` (the kill exchange), `references/genres/roleplay.md` (hidden roles).
