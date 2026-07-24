# Party / Microgame-Collection Recipe

<!-- reference-toc:start -->
## Contents

- [What defines the genre](#what-defines-the-genre)
- [The system stack to compose](#the-system-stack-to-compose)
- [The director: one FSM, one clock](#the-director-one-fsm-one-clock)
- [The microgame contract: state-mirrored virtuals](#the-microgame-contract-state-mirrored-virtuals)
- [Data-driven microgame pool with lobby constraints](#data-driven-microgame-pool-with-lobby-constraints)
- [Pawn-swap elimination (player ↔ spectator)](#pawn-swap-elimination-player--spectator)
- [Win/condition resolution (decoupled from elimination)](#wincondition-resolution-decoupled-from-elimination)
- [Per-mode synced sub-timer (late-joiner-safe sequencing)](#per-mode-synced-sub-timer-late-joiner-safe-sequencing)
- [Trigger-zone gameplay (the microgames themselves)](#trigger-zone-gameplay-the-microgames-themselves)
- [The push verb + scaffolding](#the-push-verb--scaffolding)
- [Build order](#build-order)
- [Pitfalls (from the real game)](#pitfalls-from-the-real-game)
- [Verify live](#verify-live)
- [Corpus refresh (2026): more reference implementations](#corpus-refresh-2026-more-reference-implementations)
  - [Variation 1 — Three-manager hub split (playbtg.elevator)](#variation-1--three-manager-hub-split-playbtgelevator)
  - [Variation 2 — Synced-bool state machine + tag-based player exclusion (mostudio.sweeperotso)](#variation-2--synced-bool-state-machine--tag-based-player-exclusion-mostudiosweeperotso)
  - [Variation 3 — HostWatchdog: round recovery after host disconnect (mostudio.sweeperotso)](#variation-3--hostwatchdog-round-recovery-after-host-disconnect-mostudiosweeperotso)
  - [Variation 4 — IGameMode swappable interface cloned by a controller (barrelproto.ragroll)](#variation-4--igamemode-swappable-interface-cloned-by-a-controller-barrelprotoragroll)
  - [Variation 5 — Anti-streak fairness across rounds (despawn.murder)](#variation-5--anti-streak-fairness-across-rounds-despawnmurder)
  - [Read these games](#read-these-games)
<!-- reference-toc:end -->

How to build a Fall-Guys-style party game — a rotating collection of short microgames with elimination — in modern s&box (GameObject/Component/Scene), distilled from one deep mined game: `vidya.terry_games` (Terry Games), a host-authoritative collection of ~15 microgames (Red Light Green Light, Tag, King of the Hill, Color Shuffle, Floor is Lava, Race, Soccer, Glass Bridge, Punch-Out, Mingle…) driven by one networked state machine and one synced clock.

## What defines the genre

A party-microgame game is a **round-rotator**: a single host-authoritative director runs a short loop — pick a random microgame valid for the current lobby, play it, decide winners, eliminate or score, repeat — until one player (or team) is left. The genre *is not* any one minigame; it's the **engine/content split** that lets you author a new 30-line microgame as a subclass while the director owns flow, timing, networking, and elimination.

Two halves you always build:

- **The director** (`GameSystem`): one networked 5-state machine beating off one `[Sync] TimeUntil`, plus a constraint-filtered random pool of microgame prefabs.
- **The microgame contract** (`Gamemode` base + subclasses): virtuals mirroring the director's states; each concrete mode overrides two or three.

**Core loop:** `Waiting (lobby fills) → Preparing (pick & spawn a microgame, show its name) → Playing (the mode's rules run, win/lose evaluated each tick) → Ending (kill non-winners, pay out) → back to Preparing`. Everything else (HUD timer, push verb, ragdolls, money) is scaffolding around that loop. (terry_games: `Code/Logic/GameSystem.State.cs:87-163`)

## The system stack to compose

Build these as separate components. References point to existing system docs where one applies.

| System | Role | Reference |
|---|---|---|
| Networked director / state machine | `[Sync(FromHost)]` 5-state FSM, host-only transitions | `references/systems/round-match.md` |
| Single synced clock (`TimeUntil`) | One heartbeat drives every transition + HUD | — (below) |
| Microgame base + lifecycle hooks | `abstract Gamemode : Component`, state-mirrored virtuals | — (below) |
| Data-driven microgame pool | `GameResource` `.mode` assets → constraint-filtered random pick | `references/systems/round-match.md` |
| Pawn-swap elimination | `AssignPawn<T>()` swaps player↔spectator pawn | — (below) |
| Win/condition resolution | `EndCondition {Failed,Won,Active}` + synced winner list | `references/systems/round-match.md` |
| Trigger-zone gameplay | `ITriggerListener` finish-lines, hazards, hill, tiles | — (below) |
| Per-mode synced sub-timer | second `[Sync] TimeUntil` + enum for internal phases | — (below) |
| Health / damage / ragdoll | `IDamageable` + `[Rpc.Host]` damage + ragdoll clone | `references/systems/progression-upgrades.md` (scoring seam) |
| Push / shove verb | the party-game core interaction (owner-predicted) | — (below) |
| Currency + Steam stats/achievements | payout to winners, `Sandbox.Services` | `references/systems/economy-currency.md`, `references/systems/leaderboards-services.md` |
| Scene-event bus | `ISceneEvent<T>` for decoupled cross-system signals | — (below) |

## The director: one FSM, one clock

The whole game beats off a **single** `[Sync(SyncFlags.FromHost)] TimeUntil TimerEnds`. The host advances state only when it elapses; clients just read it for the HUD. A *second* `TransitionEnds` double-gates the switch so resetting the timer inside `OnTimerEnd()` cleanly cancels a transition — that double-gate is the intended extension point. (terry_games: `Code/Logic/GameSystem.Timer.cs:88-102`)

```csharp
public enum GameState { Initializing, Waiting, Preparing, Playing, Ending }

public partial class GameSystem : Component
{
    [Sync(SyncFlags.FromHost | SyncFlags.Query)]
    public GameState CurrentState { get => _state; private set => InternalSetState(value); }

    [Sync(SyncFlags.FromHost)] public TimeUntil TimerEnds { get; set; }
    [Sync(SyncFlags.FromHost)] public TimeUntil TransitionEnds { get; set; }

    void TimerUpdate() // called every OnUpdate
    {
        if (!Networking.IsHost) return;
        if (!TimerEnds) return;                 // TimeUntil is false until it elapses
        if (!IsTransitioning) OnTimerEnd();     // host may reset the timer here to cancel
        if (TimerEnds && TransitionEnds) SwitchToNextState();
    }
}
```

State *entry* is a switch that calls `On<State>State()` hooks (which set `TimerEnds` for that state's duration) and fires a scene event; per-frame work lives in `StateUpdate → On<State>Update()`, and each delegates to the active mode. **Every transition is host-only** — clients must never call `SetState`. `Initializing` is a fake state that immediately collapses to `Waiting`. (terry_games: `Code/Logic/GameSystem.State.cs:73-128`, `:197-209`)

```csharp
void InternalSetState(GameState state)
{
    if (_state == state) return;
    _state = state;
    switch (state)
    {
        case GameState.Initializing: _state = GameState.Waiting; OnWaitingState(); break;
        case GameState.Preparing:    OnPreparingState();  break; // sets TimerEnds=3, spawns mode
        case GameState.Playing:      OnPlayingState();    break; // TimerEnds = mode.PlayingTime
        case GameState.Ending:       OnEndingState();     break; // mode.OnEnding() kills losers
    }
    Scene.RunEvent<IGameEvent>(x => x.OnGameStateChanged(state)); // decoupled bus
}
```

## The microgame contract: state-mirrored virtuals

`abstract Gamemode : Component` is the per-round plugin. It exposes virtuals that **mirror the director's states** (`OnWaiting/OnPreparing/OnPlaying/OnEnding` + `*Update` variants), plus `GetCondition(timeOut)` for win/lose, `DetermineWinners()`, and `GetTimerText()`. Designer-tunable knobs live on the base as `[Property]`: `PreparingTime/PlayingTime/EndingTime`, `MinRequiredPlayers/MaxAllowedPlayers`, `EvenPlayersOnly`, `EndWithOnePlayer`. A new microgame overrides a handful. (terry_games: `Code/Logic/Gamemodes/Base/Gamemode.State.cs:42-92`)

```csharp
public partial class Gamemode : Component
{
    public virtual void OnPreparing() { }   // setup: spawn props, place players
    public virtual void OnPlaying()   { }   // start the rules
    public virtual void PlayingUpdate() { } // per-frame rule eval (host-gated inside)
    public virtual void OnEnding()    { }   // most modes: kill everyone who isn't a winner
    // Win/lose: return Active to keep playing
    public virtual EndCondition GetCondition(bool timeOut) => /* base handles 0-alive, etc. */;
}
```

This engine/content split is the single most teachable lesson here: the director is generic, the mode supplies content. A whole microgame can be a ~30-line subclass.

## Data-driven microgame pool with lobby constraints

Don't hardcode a mode list. Author `.mode` `GameResource` assets each holding prefab variants; the director keeps a net-synced pool and picks a random valid one. `canBeNextGamemode()` **reflects each candidate prefab's `Gamemode` component** and rejects on: same-as-last, odd count vs `EvenPlayersOnly`, `living < MinRequiredPlayers`, `living > MaxAllowedPlayers`. (terry_games: `Code/Logic/GameSystem.Mode.cs:162-202`)

```csharp
bool canBeNextGamemode(PrefabFile prefab)
{
    var go = GameObject.GetPrefab(prefab.ResourcePath);
    if (!go.Components.TryGet<Gamemode>(out var mode)) return false;
    if (Game.IsEditor) return true;            // solo dev ignores all player-count rules
    if (prefab == LastGamemode) return false;
    var living = Scene.GetLivingClients().Count();
    if (living % 2 != 0 && mode.EvenPlayersOnly) return false;
    if (living < mode.MinRequiredPlayers) return false;
    if (mode.MaxAllowedPlayers is int max && living > max) return false;
    return true;
}
```

Refill when exhausted; clone the chosen prefab and `NetworkSpawn` it. This is reusable for any "pick a valid next level/event/wave for the current lobby" problem. **Gotcha:** `Game.IsEditor` short-circuits to always-true, so constraints go untested when you solo-test in the editor.

## Pawn-swap elimination (player ↔ spectator)

Elimination is **not** "disable input" — it's swapping the pawn type. The 'ShrimplePawns' layer: a `Client : Component` holds a synced `ConnectionId` + current `Pawn`; `AssignPawn<T>()` resolves the prefab path from a `[Pawn("prefabs/…")]` attribute on the pawn type, clones it, network-spawns it, and reassigns ownership. One mechanism covers spawn, death→spectator, and the winning-screen pawn. (terry_games: `Code/Client.cs:82-156`, `Code/PawnAttribute.cs:10-13`)

```csharp
[Pawn("prefabs/spectate.prefab")] public sealed class SpectatePawn : Pawn { /* ... */ }

// Host-only elimination at a finish line / hazard:
void ITriggerListener.OnTriggerEnter(GameObject other)
{
    if (!Networking.IsHost) return;
    if (!other.Components.TryGet<PlayerPawn>(out var p, FindMode.EverythingInSelfAndParent)) return;
    Round.AddWinner((Client)p.Owner);
    p.Owner.AssignPawn<SpectatePawn>(p.WorldTransform); // swap to spectator
    p.GameObject?.Destroy();
}
```

`AssignPawn` is strictly host-only; forget the `[Pawn]` attribute and it silently logs and returns null. `InternalAssign` does a careful `Network.ClearInterpolation()` dance around `WorldTransform` so the new pawn doesn't lerp in from origin — copy it exactly or pawns teleport visibly. (terry_games: `Code/Client.cs:135-148`)

## Win/condition resolution (decoupled from elimination)

`EndCondition {Failed, Won, Active}`. Each `PlayingUpdate` calls the mode's `GetCondition(timeOut)`; a non-`Active` return sets state to `Ending` and runs `DetermineWinners()`. Winners accumulate in a synced `List<Client>` via a `[Rpc.Host] AddWinner` (deduped). The base `GetCondition` handles the common cases: 0 alive = `Failed`; `EndWithOnePlayer` & 1 alive = that player wins; timeout falls back to a per-mode `TimeOutEndCondition`. (terry_games: `Code/Logic/Gamemodes/Base/Gamemode.Logic.cs:36-173`)

**Gotcha:** most modes' `OnEnding` does `if(!IsWinner(player)) player.DamagePlayer(100)` — winners must be registered **before** the Ending state or they get killed. And `GetCondition` skips the one-player-win shortcut in `Game.IsEditor` so solo rounds don't instantly end.

## Per-mode synced sub-timer (late-joiner-safe sequencing)

When a microgame needs internal phases (RLGL's doll: `Idle → Singing → Staring`), do **not** use `async`/await delays. Use a *second* net-synced `TimeUntil` + a synced enum, advanced in `PlayingUpdate`. A mid-game joiner reads the synced `TimeUntil` and reconstructs the exact phase; the host can cancel/retime freely. This is the s&box-correct answer to "timed sequence in multiplayer." (terry_games: `Code/Logic/Gamemodes/RLGL/RedLightGreenLight.cs:164-231`)

```csharp
public enum DollState { Idle, Singing, Staring }
[Sync(SyncFlags.FromHost)] public DollState CurrentDollState { get; set; }
[Sync(SyncFlags.FromHost)] public TimeUntil NextDollState { get; set; }

public override void PlayingUpdate()
{
    if (!Networking.IsHost) return;
    if (!NextDollState) return;          // synced TimeUntil, not an async delay
    OnDollStateEnd(CurrentDollState);    // switch → set next enum + duration
}
void SetDollStateAndDuration(DollState s, float dur) { CurrentDollState = s; NextDollState = dur; }
```

Sync **both** the enum and the `TimeUntil`. (terry_games: `Code/Logic/Gamemodes/RLGL/RedLightGreenLight.cs:227-231`)

## Trigger-zone gameplay (the microgames themselves)

Most microgames are `Component.ITriggerListener` zones: finish-lines add a winner + spectate (Race/RLGL); a `DamageTrigger` kills on contact (lava/walls); King of the Hill reads `Collider.Touching` each frame to accumulate `TimeInHill`; Color Shuffle reads which tiles players touch and kills those off the winning color. The shared idiom — **always host-gate and resolve the player up the hierarchy:**

```csharp
if (!Networking.IsHost) return;  // callbacks fire on every peer — gate or you double-apply
if (!other.Components.TryGet<PlayerPawn>(out var p, FindMode.EverythingInSelfAndParent)) return;
// the collider is usually on a CHILD of the pawn — EverythingInSelfAndParent is required
```

`Collider.Touching` can be empty/null and one pawn can register multiple colliders — guard with `.Any()`/`.Distinct()`. (terry_games: `Code/Logic/Gamemodes/KingOfTheHill/KingOfTheHill.cs:41-83`, `Code/Player/Health.cs:89-98`)

## The push verb + scaffolding

Party games need one shared interaction: **push/shove**. Poll `Input.Pressed("attack1")` (owner only) in `OnFixedUpdate`, enforce a `TimeSince` cooldown, sphere-trace forward, push any `Pushable` in the hit hierarchy, and `[Rpc.Broadcast]` the anim/sound/particle for feel. The active mode can veto via `Gamemode.CanPush`. (terry_games: `Code/Player/PlayerPush.cs:7-86`)

Round out with: host-authoritative damage via `[Rpc.Host] DamagePlayer` + a ragdoll clone on death (terry_games: `Code/Player/Player.cs:193-301`); winner payout + `Sandbox.Services.Achievements.Unlock`/`Stats.Increment` wrapped in `[Rpc.Owner]` so they run on the right user's client and no-op in editor (terry_games: `Code/Player/Player.cs:69-85`); and an `ISceneEvent<T>` bus (`IGameEvent`/`IGamemodeEvent`) so systems signal each other without holding references (terry_games: `Code/Logic/GameSystem.State.cs:10-27`).

## Build order

1. **Director skeleton.** `GameSystem : Component` with the `GameState` enum, `[Sync(FromHost)] CurrentState`, `[Sync(FromHost)] TimerEnds`, and `TimerUpdate`/`InternalSetState`/`SwitchToNextState`. Loop Waiting↔Preparing with a placeholder. Verify the timer counts down on a client.
2. **Microgame base.** `abstract Gamemode : Component` with the state-mirrored virtuals and `[Property]` knobs. Wire the director's state switch to `GetGamemode()?.On<State>()`.
3. **One real microgame** as a subclass (start with Race or RLGL — pure trigger logic). Author its prefab; override `OnPlaying`/`GetCondition`.
4. **Pawn-swap elimination.** Add `Client`/`Pawn`/`[Pawn]` + `AssignPawn<T>()`; swap to a SpectatePawn on death/finish.
5. **Data-driven pool.** `.mode` `GameResource` assets + `canBeNextGamemode()` constraint filter; refill + clone + `NetworkSpawn`.
6. **Win resolution + payout.** `EndCondition`, synced winner list, `OnEnding` kills losers, winning-screen pays out.
7. **More microgames + the push verb + ragdolls + Steam stats.** Each new mode is a thin subclass + a prefab.

## Pitfalls (from the real game)

- **Host-only everything.** Transitions, mode selection, `AssignPawn`, trigger logic, sub-timers — all guarded with `if (!Networking.IsHost) return`. Clients only read synced state.
- **Register winners before `Ending`.** `OnEnding` kills non-winners; a late `AddWinner` kills the winner.
- **`Game.IsEditor` shortcuts mask bugs.** Pool constraints and one-player-win are bypassed in editor — test multiplayer for real before shipping.
- **Two synced `TimeUntil`s, not async.** The global clock *and* per-mode sub-timers are synced so mid-game joiners reconstruct phase. Never sequence with `await Task.Delay`.
- **`FindMode.EverythingInSelfAndParent`** when resolving a pawn from a trigger — the collider is a child.
- **`[Sync]` `TimeUntil` reads `false` until it elapses** — `if (!TimerEnds)` means "not yet done." Don't invert it.

## Verify live

Reflection is the source of truth for the installed SDK — confirm the networking/lifecycle surface before writing it:

- `describe_type SyncFlags` / `describe_type TimeUntil` — confirm `FromHost`/`Query` flags and the implicit-bool/`Relative` members.
- `search_types Rpc` and `describe_type` the attribute — confirm `Rpc.Host`/`Rpc.Broadcast`/`Rpc.Owner` exist as written.
- `describe_type GameResource`, `Connection`, `Component.ITriggerListener`, `INetworkListener` — confirm the data-asset + networking interfaces.
- `search_types ISceneEvent` — confirm the scene-event bus API before defining `IGameEvent`.

Cross-links: see **$sbox-api** for authoritative type/method lookups (`describe_type`/`search_types`) and **$sbox-build-feature** for the screenshot-driven build-and-verify loop that keeps the bridge out of guess-and-check.

## Corpus refresh (2026): more reference implementations

Three games surface important variations that the original `vidya.terry_games` coverage doesn't capture: a 3-manager hub split (`playbtg.elevator`), a co-op round-manager using synced bools + tag-based player state instead of an enum FSM (`mostudio.sweeper_otso`), and a host-migration watchdog pattern that makes any round-based game hard to soft-lock. `facepunch.ss2`, `despawn.murder`, `facepunch.fair`, and `barrelproto.ragroll` add supporting techniques noted inline.

### Variation 1 — Three-manager hub split (playbtg.elevator)

Instead of one God-object director, **The Elevator** uses three cooperating host-authoritative components. This avoids the "GameSystem grows forever" problem and makes the between-rounds vs in-round logic cleanly separable.

- **`LobbyManager`** — between-rounds only. Majority-ready-up (`(ready*2 > total) || isHostReady`), `[Sync] CountdownRemaining`, "everyone ready → snap to 5s" shortcut. Detects level-end by edge-detecting `ExperienceLoaded` falling (no polling).
- **`ExperienceManager`** — in-round. Owns the live countdown, early-end when `alive == 0`, coin spawning, and teardown. Kills stragglers who didn't board at round end (`TakeDamage(Health*2, "missed their ride")`).
- **`ElevatorController`** — door animation state machine. Fires `OnDoorsOpened`/`OnDoorsClosed` actions; the ExperienceManager subscribes. Animation completion is detected from the door *object's* own callback, not a timer — decouples physics/anim from logic.

**Tag-based teardown (critical for host migration):** every object spawned during a round is tagged `active-level` or `temporary`. Cleanup is a single scene scan — no stored references that go stale on host change:

```csharp
// ExperienceManager — called on round end or host migration recovery
void TeardownLevel()
{
    foreach (var go in Scene.GetAllObjects(true)
                            .Where(o => o.Tags.Has("active-level") || o.Tags.Has("temporary")))
        go.Destroy();
}
```

**Host-migration-safe round ordering** (`playbtg.elevator` Standout #1): store the shuffled queue as a `[Sync(SyncFlags.FromHost)] string ExperienceOrderSerialized` (CSV of titles). A new host calls `RestoreExperienceOrder()` — rebuild from the synced CSV — so a host crash mid-session doesn't reset the rotation. Generalize: *replicate the seed/order of any procedural sequence as a primitive string.*

**Anti-pattern:** storing the level prefab in a reference variable (`_level`) and calling `Destroy()` on it directly. If the host disconnects, `_level` is null on the new host, so the teardown silently no-ops. Tag-based cleanup is the fix. (`Code/Experiences/ExperienceManager.cs`)

### Variation 2 — Synced-bool state machine + tag-based player exclusion (mostudio.sweeper_otso)

`MinesweeperGenerator` expresses round state as a **bag of `[Sync]` bools** rather than a single `[Sync]` enum. This is simpler for co-op games (no sequential phases) and a fresh joiner reads exactly which combination of flags is active without needing to know what transitions are legal:

```csharp
[Sync] public bool BoardActive, ClearInProgress, IsGameOver, WinInProgress { get; set; }
[Sync] public bool TimerRunning, GameWon { get; protected set; }

// Derived "actually running" is just a bool expression — no switch:
public bool RoundActuallyRunning =>
    BoardActive && TimerRunning && !IsGameOver && !ClearInProgress && !WinInProgress;
```

**Tag-based player exclusion** (survives host migration better than a `List<PlayerPawn>` winner list). Tags `"playing"` / `"excluded"` / `"dead"` / `"ghost"` are broadcast to all clients and live on the player GameObject — a fresh host sees them immediately via a scene scan without any RPC reconciliation:

```csharp
// Host OnUpdate — auto-exclude any fresh joiner mid-round:
foreach (var player in Scene.GetAll<PlayerPawn>())
{
    if (!player.Tags.Has("playing") && !player.Tags.Has("excluded"))
        UpdatePlayerExclusion(player, "excluded");  // [Rpc.Broadcast] sets the tag
}
```

**Anti-pattern:** `List<Client> Winners` synced as a `NetList` — on host migration, the new host's NetList may not have fully replicated yet, causing it to re-run `OnEnding` kills against an empty winner set (everyone dies). `[Sync] NetList<Vector3> FlaggedPositions` on the manager (sweeper_otso Standout #1) shows the safer pattern: store *world-space data*, not *object references*.

### Variation 3 — HostWatchdog: round recovery after host disconnect (mostudio.sweeper_otso)

Every round-based game soft-locks when the host disconnects mid-round unless you explicitly handle it. `sweeper_otso`'s `HostWatchdog` pattern is a complete, droppable playbook:

```csharp
public partial class GameManager : Component, Component.INetworkListener
{
    bool _wasHost;

    protected override void OnUpdate()
    {
        if (Networking.IsHost && !_wasHost)
            OnBecameHost();          // host migration detected
        _wasHost = Networking.IsHost;
    }

    async void OnBecameHost()
    {
        ForceResetTransientFlags();  // clear stuck ClearInProgress/WinInProgress from dead host's async tasks
        ReclaimOrphanedObjects();    // TakeOwnership on any NetworkOrphaned objects
        await Task.DelaySeconds(1f); // let in-flight packets settle
        if (!ValidateRoundState())   // recount/verify — is the board coherent?
            ForceRestart();          // too broken: clean restart
    }

    void ForceResetTransientFlags()
    {
        ClearInProgress = false;
        WinInProgress = false;
        // never reset BoardActive — it may be legitimately true
    }
}
```

**Key insight:** prefer re-validate-and-restart over preserve-state. Preserving mid-round state across host migration requires every piece of state to be in `[Sync]` fields; transient async state (the old host was mid-`await`) cannot be preserved. Accept a restart and make it fast. (`Code/HostWatchdog.cs`)

### Variation 4 — `IGameMode` swappable interface cloned by a controller (barrelproto.ragroll)

`RagRoll` structures the mode as a **separate spawnable component** that `GameController` clones and `NetworkSpawn`s, rather than having the director own the mode inline. The mode exposes one interface and the controller doesn't need to know the concrete type:

```csharp
public interface IGameMode
{
    IEnumerable<Connection> Players { get; }
    bool CanMove { get; }
    void OnGameReady();
    void OnPlayerJoined(Connection c);
    // ...
}

// GameController (host-only transition):
void StartMode(PrefabFile modePrefab)
{
    var go = GameObject.Clone(modePrefab);
    go.Network.SetOrphanedMode(NetworkOrphaned.Host);
    go.NetworkSpawn();
    _activeMode = go.Components.Get<IGameMode>();
    _activeMode.OnGameReady();
}
```

The mode's `CanMove` property gates player input from the round FSM — cleaner than a global `UseInputControls` flag the director toggles directly. (`Code/mode/RollMode.cs`)

**Comparison to terry_games pattern:** terry_games uses `abstract Gamemode : Component` with state-mirrored virtuals — good for many modes sharing a thick base. The `IGameMode` interface pattern is lighter and better when modes have no shared state, or when modes are from different inheritance trees (e.g. one mode is a horror scene, one is a party game).

### Variation 5 — Anti-streak fairness across rounds (despawn.murder)

When the same players keep getting picked for the "bad" role (Murderer, Seeker, It), a weighted ticket system prevents streaks without giving players perfect control. `MurdererTicketManager` accumulates tickets for the unpicked and reduces them for the picked:

```csharp
// Generalized pattern — works for any "who is it this round" role assignment:
void PickRole(List<Connection> candidates)
{
    // tickets[conn] increases each round you're NOT picked, decreases when you are
    var total = candidates.Sum(c => _tickets[c]);
    var roll = Game.Random.Next(0, total);
    var cum = 0;
    foreach (var c in candidates)
    {
        cum += _tickets[c];
        if (roll < cum) { AssignRole(c); _tickets[c] = MathX.Max(1, _tickets[c] / 3); return; }
    }
}
// Persist _tickets by SteamId across sessions so the fairness carries over.
```

Persisting tickets by SteamId (not `Connection`) means the protection survives disconnects and reconnects. (`Systems/Rounds/MurdererTicketManager.cs`)

### Read these games

| Game | What it adds to this genre |
|---|---|
| `vidya.terry_games` | Original reference: enum FSM director, `TimeUntil` clock, `Gamemode` virtuals, data-driven pool, pawn-swap, trigger zones |
| `playbtg.elevator` | 3-manager hub split; tag-based teardown; host-migration-safe order CSV; majority ready-up; shop-as-a-mode |
| `mostudio.sweeper_otso` | Synced-bool state machine; tag-based player exclusion; HostWatchdog recovery pattern; co-op win condition |
| `despawn.murder` | Distinct-state-class round architecture; anti-streak fairness tickets; ConVar-live-balance DSL |
| `barrelproto.ragroll` | `IGameMode` swappable interface; ping-corrected shared game clock |
