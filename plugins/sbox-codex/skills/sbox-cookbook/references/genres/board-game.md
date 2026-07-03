# Board-Game Recipe (turn-based grid: chess / checkers / minesweeper / go)

How to build a **turn-based board game on a grid** in modern s&box (GameObject/Component/Scene) — a seated/walkable physical board, an authoritative turn arbiter, grid↔world mapping, win/draw resolution, and (for physical boards) anti-cheat that trusts the *simulated world* over the network message. Distilled from two deeply-mined shipped games: `fluffybagel.chess_otb` (over-the-board 3D chess — you sit at a table and **drag wooden pieces by hand**, slap a clock to end your turn; a full ported engine + Elo + 4 game modes + tournament pairing + dual persistence, one of the most complete networked codebases in the corpus) and `mostudio.sweeper_otso` (multiplayer 3D Minesweeper — players **walk on a tile grid**, stepping reveals via flood-fill or detonates a distance-rippled mine wave; built three-layers-deep so a host disconnect never soft-locks the round).

## What defines the genre

A board game is **a discrete grid state that players mutate one legal action at a time, with authoritative turn arbitration and a terminal win/draw rule.** The board is a finite set of cells; a move/reveal/flag transforms cell state; only the active actor (or any actor, for co-op) may act; the host owns truth and re-validates every action; and a separate *arbiter* decides when the game is over (mate/stalemate/timeout/all-safe-revealed/all-mines-flagged). The drama is **perfect-information state plus turn discipline** — unlike a real-time arena, nothing happens between actions, so the genre lives or dies on three things: the **grid↔world mapping**, the **turn/arbitration machine**, and **trusting the right source of truth** when input is physical.

Two shapes show up in the corpus, and they sit at opposite ends of one axis — *how does a player commit a move?*

- **Alternating-turn, physical-input board** (`chess_otb`): two seats, strict turn ownership, a Fischer clock, and pieces you **physically grab and drop in world space**. The hard problem is that the host can't trust a "SubmitMove e2e4" RPC — so it re-derives the move by diffing where pieces *physically sit* against legal moves (bitboard XOR). Copy this for chess/checkers/go/shogi/any 1v1 rated board game with hand-placed pieces.
- **Co-op walkable grid, body-as-cursor** (`sweeper_otso`): everyone's on the same team against the board itself; the "move" is **standing your body in a tile's trigger volume** (reveal) or placing a flag, and a single mine ends the round for the whole lobby. The hard problem is flood-fill reveal + a host-migration-proof tile/flag registry. Copy this for minesweeper, co-op tile puzzles, "don't-step-on-the-wrong-square" party games.

Both share the spine below; pick the turn model (strict-alternating vs. shared-co-op) and the input model (hand-grab vs. body-trigger) independently — they compose.

**Core loop (alternating, chess shape):** seat both players → assign colors → start clock for side-to-move → active player physically moves a piece → host reconciles physical board against legal moves → on a clean legal match, commit move + flip clock + switch side-to-move → arbiter checks terminal state → repeat until mate/draw/timeout → broadcast result → rating/recording/rematch react.

**Core loop (co-op, minesweeper shape):** host generates board (mines + safe tiles) → cascade-spawn tiles → teleport players on → any player steps a tile → safe tile flood-fill-reveals neighbors and decrements `SafeTilesRemaining`, mine detonates a distance-ordered wave that kills the lobby → win when `SafeTilesRemaining<=0`, loss on any detonation → celebrate/clear → loop.

## The system stack to compose

Build these as separate components. References point to existing system docs where one applies.

| System | Role for a board game | Reference / source pattern |
|---|---|---|
| **Grid model + world mapping** | A finite cell set + functions `WorldToCell`/`CellToWorld`; the board is the container object. | below; chess `ChessBoardComponent.Move.cs` (project world pos → 8×8), sweeper `MINESWEEPER.cs` (grid → safe-spawn positions) |
| **Turn / match arbiter** | Strict alternating turn ownership + clock, OR shared co-op round; a *separate* terminal-state arbiter (mate/draw/timeout/all-clear). | `references/systems/round-match.md`; chess `Arbiter.cs` + `GameResult.cs`, sweeper `RoundActuallyRunning` composite |
| **Action request → host-validate → commit** | Client requests a move/reveal/flag; host re-validates legality and mutates truth. | `references/systems/anti-cheat.md`; chess `SubmitMove`/`TryCommitMove`, sweeper `RequestPlaceFlag` |
| **Physical-board anti-cheat** (hand-input only) | Re-derive the move from observed piece positions; diff vs. legal moves; classify leftover squares as illegal. | below — the gold; chess `ChessGameState.Displacement.cs` |
| **Flood-fill / cascade reveal** (co-op grid only) | Reveal-cell propagates to zero-adjacent neighbors; explosion ripples by distance. | below; sweeper `TileMine.TriggerMineWave` (distance-ordered), board recount cross-check |
| **Rule-variant handlers** (optional) | Swap capture/placement/turn-end rules via a config without touching the dispatcher. | below; chess `ChessBoardEventBus.cs` strategy-over-bus |
| **AI opponent** | A bot that drives the *same* commit path a human does. | below; chess "robot human" client-owned pawn + Leorik engine |
| **Rating / Elo** | Update ratings on match-end, persist per-player. | `references/systems/leaderboards-services.md`; chess `EloMath.cs` (textbook Elo, copy-paste) |
| **Result fan-out** | Match-end broadcast that independent systems (rating/recording/tournament) each react to. | `references/systems/round-match.md`; chess `IChessMatchEvents.OnMatchEnded` |
| **Host-migration resilience** | Round survives the host leaving — re-validate over preserve. | `references/engine/networking-authority.md`; sweeper `HostWatchdog.cs`, chess arena orphan + `HostEnsure` |
| **Themeable board assets** | Piece/tile sets as data, swappable with zero code. | `references/systems/save-persistence.md` (GameResource); chess `ChessSet : GameResource`, sweeper folder-convention tiles |
| **Tournament / matchmaking** (optional) | Continuous Swiss-ish pairing, seat-by deadlines, standings. | `references/systems/spawning-waves.md` (host-side actor pacing); chess `ArenaSystem.cs` |

## Build order

Build the grid model and turn arbiter first — they're engine-agnostic and testable before any networking. Then add the input model, then anti-cheat, then the bolt-ons. **Re-test multiplayer for real** — turn ownership, physical reconciliation, and host migration only exercise with 2+ live clients.

1. **Grid model + win/terminal rule, in isolation.** A plain `sealed class` (no `Component`) owning cell state + `WorldToCell`/`CellToWorld` + the legal-move/reveal rule + an `Arbiter` that returns a terminal enum. You can `console_run`/unit-test it before any GameObject exists (this is the puzzle genre's "pure model" discipline — see `references/genres/puzzle.md`).
2. **Board container + visual cells.** One board `GameObject`; spawn cell objects (pieces or tiles) at `CellToWorld` positions. Screenshot to confirm the layout maps to the grid.
3. **Turn arbiter component.** Host-authoritative: `[Sync(SyncFlags.FromHost)]` side-to-move (chess) or `[Sync]` round bools (minesweeper); a host-only `OnUpdate` tick that flips turns / checks the clock / checks all-clear.
4. **One action verb.** `[Rpc.Host]` request → host re-validates (your turn? legal move? cell in bounds? not already revealed?) → mutate → broadcast. Pass-turn / reveal works end to end with placeholder art.
5. **Input model.** Hand-grab drag-and-drop (chess) OR body-trigger reveal + slot-hotbar flag placement (minesweeper). This is the screenshot-heavy step.
6. **Anti-cheat for the input model.** Physical-board bitboard reconciliation (chess) OR host re-validate-after-grace + flood-fill recount cross-check (minesweeper).
7. **Arbiter → result fan-out.** Terminal state → `OnMatchEnded`/`TriggerWin` scene event; rating/recording/celebration each react independently.
8. **Polish:** Elo + persistence, AI opponent, rule variants, host-migration watchdog, tournament/matchmaking, themeable sets.

## How the real games do it

### 1. Grid ↔ world mapping is the foundation (both games)

Everything else needs to turn a world position into a cell and back. `sweeper_otso` pre-computes safe player-spawn positions from grid coordinates *up front* so teleport works before the spawn cascade even finishes (sweeper: `MINESWEEPER.cs`). `chess_otb` projects every live piece's **world position** onto an 8×8 grid to build a fresh bitboard, **skipping** captured/graveyard pieces and any piece in `Grabbed` state (a held piece is snapped to the hand bone, so its square is meaningless) (chess: `Code/Game/Components/ChessBoardComponent.Move.cs`, `BuildPhysicalBitboards`).

```csharp
// Keep the mapping pure and on the board object. Snap-to-cell for placement,
// world→cell for reading physical state. (Both games center cells on a pitch.)
int WorldToCell( Vector3 world )                       // e.g. chess BuildPhysicalBitboards
{
    var local = BoardRoot.WorldTransform.PointToLocal( world );
    int file = (int)MathF.Floor( (local.x + HalfBoard) / CellSize );  // verify MathF live
    int rank = (int)MathF.Floor( (local.y + HalfBoard) / CellSize );
    return (file is >= 0 and < 8 && rank is >= 0 and < 8) ? rank * 8 + file : -1;
}
Vector3 CellToWorld( int cell ) =>
    BoardRoot.WorldTransform.PointToWorld( new Vector3(
        (cell % 8) * CellSize - HalfBoard + CellSize/2,
        (cell / 8) * CellSize - HalfBoard + CellSize/2, 0 ) );
```

> `MathF` is **whitelisted in some SDK builds and absent in others** (it's used in both `sweeper_otso` and the puzzle source, but the bridge editor + some sandbox builds ban it). Prefer `MathX`/`System.Math` and verify with `describe_type` before relying on `MathF`. See the gotcha list.

### 2. Turn arbiter: authoritative side-to-move, decoupled from scoring (chess)

The match lifecycle is split across `IChessGameMode` implementations selected per a `GameMode` enum (Open/Ranked/Arena/Solo/Matchmaking); a `ChessTable` is the match container, and seating both seats triggers `BeginMatchWithRandomColors` (chess: `Code/Game/GameMode/`). The decisive design move is that **terminal-state detection is a separate `Arbiter`**, not baked into whoever scores the game — it returns a rich enum (`WhiteIsMated`, `BlackTimeout`, `Stalemate`, `Repetition`, `FiftyMoveRule`, `DrawByArbiter`…) that scoring/rating consume but don't compute (chess: `Code/Game Result/GameResult.cs` + `Arbiter.cs`).

```csharp
public sealed class TurnArbiter : Component
{
    [Sync(SyncFlags.FromHost)] public PieceColor SideToMove { get; set; }
    [Sync(SyncFlags.FromHost)] public TimeUntil WhiteClock { get; set; }
    [Sync(SyncFlags.FromHost)] public TimeUntil BlackClock { get; set; }

    protected override void OnUpdate()
    {
        if ( !Networking.IsHost ) return;                 // clients only read
        var clock = SideToMove == PieceColor.White ? WhiteClock : BlackClock;
        if ( clock )                                      // TimeUntil → true when elapsed
            EndMatch( SideToMove == PieceColor.White ? GameResult.WhiteTimeout
                                                      : GameResult.BlackTimeout );
    }
    // commit happens only after anti-cheat reconciliation says the move is clean (§4):
    public void CommitMove( Move m )
    {
        ApplyToBoard( m );
        var terminal = Arbiter.Evaluate( BoardState );    // separate, pure
        if ( terminal != GameResult.Ongoing ) { EndMatch( terminal ); return; }
        SideToMove = SideToMove.Opposite();               // flip clock by switching side
    }
}
```

Turn timing is fine off a synced `TimeUntil` — it does **not** need tick accuracy. "Queue another match across the scene reload" survives via a **static latch** (`ChessOtbGameManager.SetRequeueRankedTimeControlId`/`Consume…`) since statics survive `Scene.Load` (chess). See `references/systems/round-match.md` for the generic phase/timer skeleton.

### 3. Co-op round as a bag of `[Sync]` bools + tag-driven membership (minesweeper)

`sweeper_otso`'s whole round lives in `MinesweeperGenerator : Component` (host-authoritative) and expresses its state machine as `[Sync]` bools every client reads and only the host writes — plus a composite read-only property that ANDs them (sweeper: `Code/MINESWEEPER.cs`):

```csharp
[Sync] public bool BoardActive, ClearInProgress, IsGameOver, WinInProgress { get; set; }
[Sync] public bool TimerRunning, GameWon { get; protected set; }
[Sync] public int  SafeTilesRemaining { get; protected set; }
public bool RoundActuallyRunning =>
    BoardActive && TimerRunning && !IsGameOver && !ClearInProgress && !WinInProgress;
```

The composable lesson: **don't store round membership in a `List<Player>`** — store it as networked **tags on each player GameObject** (`"playing"`/`"excluded"`/`"dead"`/`"ghost"`), because tags survive host migration via broadcast where a List of references desyncs. A mid-round joiner is auto-detected in one `OnUpdate` pass as "no playing/excluded tag → exclude" (sweeper: `UpdatePlayerExclusion`). Notice the `protected set;` on the synced fields — that's deliberate, so a subclass (the tutorial board) can reuse 90% of the manager (sweeper: `TutorialMinesweeperGenerator.cs`).

### 4. Physical-board anti-cheat: trust the simulation, not the message ★ the gold (chess)

This is the single most novel idea in the corpus and the thing that makes a *physical* board game possible. Because pieces are dragged by hand, the host **cannot** trust "a SubmitMove RPC said e2e4" — a hacked client could send anything. So the host re-derives the move from where pieces physically sit (chess: `Code/Game/Gameplay/ChessGameState.Displacement.cs`):

1. At each turn start, snapshot the two colour **bitboards** (`SnapshotDisplacementBitboards` → `_turnStartWhiteBitboard`/`_Black`).
2. `BuildPhysicalBitboards()` projects every live piece's world position onto the 8×8 grid (§1), skipping captured + `Grabbed` pieces.
3. `EvaluateDisplacements()` **XORs** snapshot vs. physical for the side to move (`moverDiff`), generates all legal moves, and compares `moverDiff` against each move's `ExpectedDisplacementMask` (from+to bits; castling adds the deterministic rook from/to bits).

```csharp
// chess: ChessGameState.Displacement.cs (paraphrased)
ulong moverDiff = physicalForMover ^ _turnStartBitboardForMover;   // squares that changed
foreach ( var move in LegalMoves( BoardState ) )
{
    ulong expected = move.ExpectedDisplacementMask;                // from|to (+rook for castle)
    if ( moverDiff == expected ) return Resolved( move );          // exact → legal move made
    if ( (moverDiff & ~expected) == 0 ) return PartialMatch();     // SUBSET → mid-flight, wait
}
ulong illegal = moverDiff & ~coveredByAnyLegalMove;                // leftover bits
HighlightIllegalSquares( illegal );                                // SUPERSET → cheat / fumble
```

**The cheat signature is a SUPERSET** (extra unrelated bits changed); a legit-but-incomplete move is a **SUBSET** (mid-castle, piece in hand). The host only clears a player's staged pending move when the diff is *not* a partial match, so it never destroys a legitimate in-progress move. `ValidatePhysicalAgainstMove` returns `ExtraSquares`/`MissingSquares` to validate a *claimed* move — and "Missing" is tolerated as a transient replication gap while a client-owned piece is mid-flight. Because ~32 pieces × 60fps full legal-move generation is the hot path, the per-frame result is **cached by `Time.Now` + board identity** (`EvalThisFrame`). This generalizes to any physical board: *diff observed positions against a turn-start snapshot, match against generated legal moves, classify the leftover as cheating.*

### 5. Co-op input anti-cheat: flood-fill + re-validate-after-grace + recount cross-check (minesweeper)

`sweeper_otso`'s "move" is your body entering a tile trigger, so its integrity problems are different. Three patterns compose (sweeper: `MINESWEEPER.cs`, `Mine.cs`, `Inventory.cs`):

- **Three-layer flag truth, ranked by migration-survivability.** "Is this mine flagged?" is answered by, in order: (a) `MinesweeperGenerator.FlaggedPositions` — a **`[Sync] NetList<Vector3>`** that survives host migration and auto-delivers to joiners; (b) the local `"flagged"` tag (fast path); (c) a scene scan for a nearby `Flag_Cover` (last-ditch). The **networked position registry on the manager** is the key insight — ownership, network ids, dicts, and tags all desync on host change; a `[Sync]`'d list of world-positions doesn't.
- **Optimistic-tag-then-spawn for zero-latency safety.** `RequestPlaceFlag` adds the `"flagged"` tag via broadcast *before* loading the prefab + `NetworkSpawn` (which take ms), so a player who clicks-then-steps doesn't die in the gap; revert the tag if the spawn fails. (Also documents that `Rpc.Caller` returned the *host's* connection even for a proxy-initiated `[Rpc.Host]` on that SDK — use `Network.Owner` of the component as the real placer.)
- **Mine re-validates after a grace + recount cross-checks the win.** A mine step runs `DelayedDetonate` (~100ms grace) so a legit flag placed the same frame as a step isn't a death; and **every win/clear path re-counts the live scene** (`RecountSafeTilesInScene`) before the irreversible decision, aborting if the cheap `SafeTilesRemaining` counter disagrees — overlapping flood-fills and migration hiccups drift the counter. Pattern: *fast counter for the common case, authoritative recount as a gate on round-ending events.*

The mine wave itself is a literal expanding shock-front — detonate mines **ordered by distance** from the trigger with `await Task.DelaySeconds(dist/700f)` between each, guarded by a `static bool _waveInProgress` so two simultaneous steps don't double-explode (sweeper: `TileMine.TriggerMineWave`).

### 6. The action verb: request → host re-validate → broadcast (both)

Both games funnel actions through a host-validated request. `chess_otb`'s bot uses the **exact same** client→host RPCs a human does (`SubmitMove`, `NotifyManualCaptureGraveyardPlaced`, `TryCommitMove`) so the host can't tell a bot drove the input — all the anti-cheat paths are reused for free. `sweeper_otso` validates on the host inside `RequestPlaceFlag`. The rule is identical to every other multiplayer genre: **the client asks, the host re-validates the caller + the action's legality, mutates truth, then fans the result.** See `references/systems/anti-cheat.md`.

### 7. AI opponent as a client-owned "robot human" (chess) ★

The cleanest bot pattern in the corpus: the bot is **not** host logic. `ChessOtbGameManager.SpawnBotPawn` clones the *player* prefab, strips owner-only components (camera, input, interaction), attaches `BotPlayController`, and **network-spawns it owned by the human seated at that table**. That client runs the Leorik engine search locally, drives IK to physically pick up / carry / place pieces, and submits via the same RPCs humans use (chess: `Code/Bot/`, `BotPlayController.cs` — a 20-state FSM incl. victim-carry and castle sub-sequences). Benefit: zero special-casing — anti-cheat, validation, animation, and networking all treat the bot exactly like a player. (The whole Leorik engine + an embedded NNUE eval is vendored under `Code/Leorik/` — a reference for "run a heavy CPU algorithm client-side.")

### 8. Rule variants via strategy-over-a-bus (chess) — optional but the cleanest data-variant pattern in the corpus

`ChessBoardEventBus` holds an `ICaptureHandler`/`IMoveHandler`/`ITurnHandler` triple chosen from `LobbyRules` at game start (`ConfigureHandlers`: Manual vs Auto capture, Free vs Strict placement, Manual-clock vs Auto end-turn). The bus **fans every event to all three handlers** and ANDs all three for `CanCommitTurn()`. Adding a new ruleset = drop in a handler, no edits to the dispatcher (chess: `Code/Game/EventBus/ChessBoardEventBus.cs`, `Code/Game/Handlers/*.cs`). The interaction system raises events (`PiecePickedUpEvent`, `PieceToGraveyardEvent`, `TurnCommitRequestedEvent`); handlers own the logic. Compose this when your board game needs configurable rules (chess variants, casual vs. strict).

### 9. Result fan-out: one match-end fact, many independent reactors (chess)

Game termination flows through **one scene event** that multiple systems subscribe to independently — `OnMatchEnded(ChessGameState, GameResult, IReadOnlyList<ushort> packedMoves)` — instead of one god-handler calling each. `EloSystem`, `GameRecordingSystem`, `ArenaSystem`, and `SessionStatsSystem` each react in isolation (chess: `Code/Game/Events/IChessMatchEvents.cs`). This is the composable spine: broadcast the match-end fact, let rating/recording/tournament-scoring react separately. Same idea as the minesweeper `TriggerWin → ClearBoard` loop, just with more subscribers.

### 10. Elo + persistence (chess) — copy-paste ready

On match-end, `EloSystem` runs `EloMath.UpdatePair` — textbook Elo, K=32, clamp 100–4000 (chess: `Code/Game/Rating/EloMath.cs`, ~10 lines) — then persists three ways, each composable on its own: (1) s&box **Stats** (`Otb_elo_blitz`, `Otbs_played`…), (2) a `PlayerIdentity` component mirroring Elo + W/L/D as `[Sync(FromHost)]` so the scoreboard reads it without hitting the service, hydrated on join via async `Stats.Refresh`, and (3) an external HTTP backend. The **targeted-RPC** trick for per-player cloud persistence: `Stats.SetValue`/`Increment` are local to the Steam user, so the host tells *only* the owning client to write via `using (Rpc.FilterInclude(target)) hub.RpcWriteMyEloStat(...)` (chess: `Code/Game/Networking/ChessOtbModeRpcs.cs`). See `references/systems/leaderboards-services.md`.

### 11. Host-migration resilience (both — the genre's defensive backbone)

A turn-based round that soft-locks when the host leaves is broken. Two complementary playbooks:

- **`sweeper_otso` — re-validate-and-restart over preserve.** `HostWatchdog` detects `Networking.IsHost && !_wasHost`, `ForceResetTransientFlags()` (clear stuck `ClearInProgress`/`WinInProgress` left by the dead host's async tasks), reclaims + `TakeOwnership`s orphaned flags, rebuilds the flag→tile dict by position, then **defers the "is the board OK?" check ~1.0s** (let in-flight packets settle) and `RecountSafeTiles()` or force-`ClearBoard()` for a clean restart if tiles are >10% missing (sweeper: `Code/HostWatchdog.cs`). This is a complete reusable playbook.
- **`chess_otb` — survive the host *leaving* (arena).** Arena lobbies set `DestroyWhenHostLeaves = false`; `ArenaState`/`PlayerIdentity` spawn `NetworkOrphaned.Host` + `OwnerTransfer.Fixed`; on the become-host transition (a sticky latch), the new host calls idempotent `ArenaState.HostEnsure()` then `ArenaSystem.HostHydrateFromState()` to rebuild its private dict from a synced `NetList` (chess: `ChessOtbGameManager.DetectHostTransition`). Host-only by-products (rematch memory) are knowingly reset as acceptable degradation.

See `references/engine/networking-authority.md`.

### 12. Themeable board assets as data (both)

`chess_otb` defines `ChessSet : GameResource` (`[AssetType(Extension="chessset")]`) — a piece→Model table; `GetModel(int pieceType)` maps engine `Piece.Pawn..King` ints to models, referencing six `.piece` definitions. Swap themed sets with zero code (chess: `Code/Game/Resources/ChessSet.cs`). `sweeper_otso` uses **folder convention** instead — `FlagCatalog` scans `ResourceLibrary.GetAll<PrefabFile>()` keeping any whose path contains `flags/{rarity}/`, and tiles `Tile0..Tile8`+`MineTile` are `[Property] GameObject` slots (sweeper: `Code/inventory/Flagcatalog.cs`). Either way: **content lives in assets/folders, not code.** See `references/systems/save-persistence.md` for the GameResource pattern.

## Gotchas / pitfalls (from the real games)

- **Trust the simulated world, not the client message — for physical input.** A "SubmitMove" RPC is a *claim*; re-derive the move from observed piece positions and diff against legal moves. The cheat signature is the **superset** (extra changed squares); a legit incomplete move is a **subset** — never clear a pending move on a subset (it's mid-flight). (chess `ChessGameState.Displacement.cs`)
- **Cache per-frame legal-move generation.** Full legal-move generation × board size × 60fps is the hot path; cache by `Time.Now` + board identity. (chess `EvalThisFrame`)
- **Skip held/captured pieces when reading the physical board.** A `Grabbed` piece is snapped to the hand bone, so its cell is garbage; graveyard pieces aren't on the board. Exclude both before building the grid state. (chess `BuildPhysicalBitboards`)
- **Don't store round membership in a `List<Player>`** — use networked **tags** on player objects + a few `[Sync]` bools on the manager; tags + a `[Sync] NetList<Vector3>` registry survive host migration, object refs/dicts/ids don't. (sweeper)
- **Fast counter + authoritative recount before any irreversible decision.** Maintain a cheap `SafeTilesRemaining`, but re-count the live scene before declaring a win — overlapping flood-fills and migration drift the counter. (sweeper `RecountSafeTilesInScene`)
- **Plan for host migration from day one** in a round game, or it soft-locks. Re-validate-and-restart (clear transient async flags, reclaim orphans, deferred settle check) beats trying to preserve exact state. (sweeper `HostWatchdog`, chess arena orphan model)
- **`Rpc.Caller` can return the host's connection** for a proxy-initiated `[Rpc.Host]` on some SDK versions — use the component's `Network.Owner` as the real actor. (sweeper `FlagPlacer`)
- **Networked `Destroy` only propagates from the owner** — to clean up everyone's pieces/flags, `[Rpc.Broadcast]` and have *each client destroy what it owns*, plus a host pass for genuine orphans; `TakeOwnership()`+`Destroy()` back-to-back doesn't work (transfer hasn't propagated). (sweeper `BroadcastForceDestroyAllFlags`)
- **Teleporting a networked physics player is a footgun** — the controller sweeps it off a fresh tile and `[Rpc.Owner]` doesn't reliably loop back to the caller; de-dupe by serial (send 3× for packet loss), local-owner fast path, and **freeze velocity every frame for ~1.5s** after the snap. (sweeper `Teleport.cs`)
- **Reveal hands/hidden cells only to the owner** with `Rpc.FilterInclude(owner)` — don't `[Sync]` a hidden state to everyone (relevant for fog-of-war board games like Battleship). (cf. card-battler reveal-scoping)
- **`MathF` is SDK-version-dependent.** It's *used* in `sweeper_otso` (`MathF.Round`/`Max`) and the puzzle source, but it's **absent in the s&box sandbox and the bridge editor addon** on other builds. Prefer `MathX`/`System.Math`; verify with `describe_type` before relying on it.
- **A GameObject's `Name` is NOT networked** — if you key cleanup off a renamed object (sweeper renames a placed flag to `Flag_Cover`), every client must re-set the name in the spawn broadcast. (sweeper `BroadcastPlaceFlag`)
- **Sibling component `OnStart` order isn't guaranteed** — components that subscribe to the board/arbiter's events should re-resolve + re-hook in `OnFixedUpdate` if their handle is null, and replay the last snapshot if the event already fired. (chess `ChessBoardComponent`/`ChessClockComponent`)
- **Solo-testing a networked 1v1 is painful** — `chess_otb`'s `DevSettings.cs` lets each editor instance spoof a different Steam id + backend URL so two editor windows simulate two players. Bake an equivalent override in early.

## Verify live

API surfaces drift between SDK versions — confirm before relying on a signature. Use `describe_type` / `search_types` reflection against the installed SDK as authoritative for:

- `[Sync]` / `SyncFlags` (`FromHost`), `IsProxy`, `Networking.IsHost` — the arbiter + round-state surface.
- `[Rpc.Host]` / `[Rpc.Broadcast]` and the **filter** API (`Rpc.FilterInclude(connection)`, `Rpc.Caller`) — the action verb + per-player Stats persistence (and the `Rpc.Caller`-returns-host footgun above).
- `NetList<T>` / `NetDictionary<T>` — the flag/position registry and synced player lists (and that plain `List`/`Dictionary` as `[Sync]` is a footgun — run `networking_lint`).
- `TimeUntil` (implicit-bool reads false until elapsed, `.Relative` for host-migration re-arm) — the chess clock + co-op round timer.
- `NetworkOrphaned` (`Host`/`Destroy`) + `OwnerTransfer` (`Fixed`/`Takeover`) + `DestroyWhenHostLeaves` — orphan/migration setup for tiles, flags, arena state.
- `GameResource` + `[AssetType]` — `.chessset`/`.piece` (or your tile-set) data assets; ctor args vary across builds.
- `Sandbox.Services.Stats` (`GetPlayerStats`/`SetValue`/`Increment`/`Refresh`) + `Sandbox.Services.Leaderboards` — Elo/rating persistence (see `references/systems/leaderboards-services.md`).
- `Scene.Trace.Ray` / `ScreenPixelToRay` — picking a board cell (collider trace vs. pure ray-vs-cell geometry; see `references/genres/puzzle.md` for the no-collider pick).
- `MathF` vs `MathX`/`System.Math` — confirm which exists before any grid math.

Run **`networking_lint`** on the result — unguarded `[Sync]` mutators, score/rating as plain `[Sync]`, `List`/`Dictionary` as `[Sync]`, and `[Rpc.Host]` methods that never re-check `Rpc.Caller` are exactly the board-game footguns it catches. Run **`scene_validate`** to catch trigger-vs-trace mismatches on walkable tiles.

## Which games to read

- **`fluffybagel.chess_otb`** (`chess_otb/Code/`) — **the alternating-turn / physical-board reference**, and one of the most complete networked codebases in the corpus. Read `Game/Gameplay/ChessGameState.Displacement.cs` + `Game/Components/ChessBoardComponent.Move.cs` (the bitboard-XOR anti-cheat — *the* gold), `Game Result/Arbiter.cs` + `GameResult.cs` (terminal-state arbiter decoupled from scoring), `Game/Events/IChessMatchEvents.cs` (match-end fan-out), `Game/EventBus/ChessBoardEventBus.cs` + `Game/Handlers/` (strategy-over-bus rule variants), `Game/Rating/EloMath.cs` + `Game/Systems/EloSystem.cs` + `Game/Networking/PlayerIdentity.cs` (copy-paste Elo + synced rating + targeted-RPC persistence), `Bot/` + `BotPlayController.cs` + `ChessOtbGameManager.SpawnBotPawn` (the client-owned "robot human" bot), `Game/Systems/ArenaSystem.cs` (continuous Swiss-ish tournament pairing + host-migration via orphans), `Game/Resources/ChessSet.cs` (themeable piece sets), and `Game/Services/DevSettings.cs` (two-editor solo-test override).
- **`mostudio.sweeper_otso`** (`sweeper_otso/Code/`) — **the co-op walkable-grid reference** + the host-migration-resilience playbook. Read `MINESWEEPER.cs` (`[Sync]`-bool round machine + `RoundActuallyRunning` + tag-driven membership + `RecountSafeTilesInScene` cross-check + the row-cascade spawn), `Mine.cs` (`TriggerMineWave` distance-ordered explosion + three-layer flag truth + `DelayedDetonate` grace), `HostWatchdog.cs` (**the** migration playbook — re-validate-and-restart, deferred settle, orphan reclaim), `Teleport.cs` (de-duped/retried/velocity-frozen networked teleport), `Inventory.cs`/`FlagPlacer.cs` (optimistic-tag-then-spawn, owner-vs-`Rpc.Caller`, networked-Destroy-from-owner), `inventory/Flagcatalog.cs` (folder-convention themeable tiles), and `TutorialMinesweeperGenerator.cs` (subclass-via-`protected`-setters + tutorial→matchmaking handoff).

Cross-links: see the **sbox-api** skill for authoritative type/method lookups (`describe_type`/`search_types`), and the **sbox-build-feature** skill for the screenshot-driven build-and-verify loop the board layout lives or dies by. System deep-dives: `references/systems/round-match.md` (turn/phase machine + timer), `references/systems/anti-cheat.md` (the action-verb + cooldown posture), `references/engine/networking-authority.md` (host migration, orphans, `[Sync]` vs RPC), `references/systems/leaderboards-services.md` (Elo/Stats/leaderboard persistence), `references/systems/save-persistence.md` (GameResource themeable sets). Closest sibling genres: `references/genres/puzzle.md` (pure-grid model + no-collider ray-pick + deterministic generation — the engine-agnostic-rules discipline applies directly), `references/genres/card-battler.md` (the same request→host-validate→broadcast turn idiom + reveal-scoping for hidden state), `references/genres/party-microgame.md` (the co-op round/celebration loop).
