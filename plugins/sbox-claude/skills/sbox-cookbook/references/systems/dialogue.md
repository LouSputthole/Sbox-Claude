# Dialogue & Speech-Bubble Systems

<!-- reference-toc:start -->
## Contents

- [What it IS (and when)](#what-it-is-and-when)
- [Canonical modern-s&box recipe](#canonical-modern-sbox-recipe)
  - [1. Author lines as a GameResource](#1-author-lines-as-a-gameresource)
  - [2. Trigger on the player entering a zone](#2-trigger-on-the-player-entering-a-zone)
  - [3. Reveal it with a typewriter PanelComponent](#3-reveal-it-with-a-typewriter-panelcomponent)
- [Variations seen across games](#variations-seen-across-games)
- [Gotchas](#gotchas)
- [Seen in](#seen-in)
- [Corpus refresh (2026): more reference implementations](#corpus-refresh-2026-more-reference-implementations)
  - [Pattern A — Polymorphic per-player objective generator (despawn.murder)](#pattern-a--polymorphic-per-player-objective-generator-despawnmurder)
  - [Pattern B — Imperative awaited branching narrative (dimmies.terryspapers)](#pattern-b--imperative-awaited-branching-narrative-dimmiesterryspapers)
  - [Pattern C — Host-authoritative time-boxed vote (lowkeynetworks.newrp)](#pattern-c--host-authoritative-time-boxed-vote-lowkeynetworksnewrp)
  - [How these three compose](#how-these-three-compose)
<!-- reference-toc:end -->

Showing NPC lines, branch choices, or confirm prompts to a player — author the text as data, trigger on proximity/interaction, then reveal it with a `PanelComponent` (often a typewriter). Use this whenever you need talking NPCs, story beats, tutorial barks, or a yes/no modal.

## What it IS (and when)

A dialogue system is three decoupled layers, and every mined game splits them the same way:

1. **Data** — lines/voice/name authored as a `GameResource` asset so designers edit `.npct` files with no recompile (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCYapper.cs:1`).
2. **Trigger** — *when* to speak: a proximity `Component.ITriggerListener` or an interactable (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCLooker.cs:24`).
3. **Presentation** — a `PanelComponent` razor panel that reveals the text and plays a per-char blip (repo facepunch.jumper: `jumper/Code/UI/JumperNPCTalker.razor:33`).

Keep them separate so the same UI panel serves NPC barks, tutorial prompts, and modal confirms.

## Canonical modern-s&box recipe

### 1. Author lines as a GameResource

```text
DialogueAsset
  register the asset type with a dialogue-specific extension
  expose an editable speaker name
  expose an ordered collection of lines
  expose an optional sound-resource reference for the voice cue
  validate that at least one line exists before the asset is used
```

This makes editable `.npct` assets in the asset browser; no code change is needed to add lines (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCYapper.cs:1-18`).

### 2. Trigger on the player entering a zone

The NPC component implements `Component.ITriggerListener` and gates on a tag. Note the collider that carries the `player` tag is a child, so the code climbs to `other.GameObject.Parent` for the real player root.

```text
when the trigger component becomes enabled
  if no dialogue assets are configured, mark the trigger unavailable
  otherwise choose an eligible dialogue asset for this activation

when a collider enters
  ignore colliders that do not carry the player tag
  resolve the logical player root from the collider object
  find that player's local dialogue presentation component
  if either the asset or presentation component is missing, stop safely
  pass the speaker, voice cue, and a non-repeating line to the presenter

when a collider exits
  optionally cancel or hide any dialogue owned by this trigger
```

Pattern verified in `JumperNPCLooker.cs:24-40` (tag-gate + `Parent` climb + random pack on enable) and `JumperFinishLine.cs:14` (same `ITriggerListener` + tag shape). The trigger needs a sibling `Collider` with **IsTrigger = true** and matching collision tags set in the prefab — without that, `OnTriggerEnter` never fires.

A non-repeating line picker avoids saying the same thing twice:

```text
chooseNonRepeatingLine(lines, previousIndex)
  if lines is empty, return no line
  if lines has one entry, return it and index zero
  choose uniformly from every index except previousIndex
  return the selected line and remember its index for the next call
```

(repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCLooker.cs:52-61`).

### 3. Reveal it with a typewriter PanelComponent

A `PanelComponent` razor panel appends one char at a time, plays a pitch-randomized blip per letter (Undertale-style), and auto-hides via `RealTimeSince`.

```text
panel state
  speakerName
  completeMessage
  revealedMessage
  voiceCue
  revealGeneration
  timeSinceLastCharacter

displayMessage(nextMessage)
  increment revealGeneration so any older reveal stops
  store nextMessage as completeMessage
  clear revealedMessage
  start reveal(nextMessage, revealGeneration)

reveal(message, generation)
  for each character in message
    stop if generation is no longer current
    append the character to revealedMessage
    reset the visibility timer
    invalidate the panel so the new text renders
    wait for a small randomized interval
    optionally play a quiet voice cue with slight pitch variation

render
  show speakerName and revealedMessage
  apply the visible style while the visibility timer is below the hide delay
  include revealedMessage and visibility state in render invalidation
```

This behavior is demonstrated in `jumper/Code/UI/JumperNPCTalker.razor:33-78`. Drive the `.visible` opacity from the panel's visible class, and include the revealed text in render invalidation so each appended character appears. The generation check prevents an older reveal from interleaving with a newer message.

## Variations seen across games

- **Modal confirm dialog (static singleton + callback).** A purchase/confirm prompt is one panel opened via a static `Open(item, onConfirm)`; the click handler invokes the stored `System.Action` and closes. Affordability disables the button (`@(CanAfford ? "" : "disabled")`) and `Input.Pressed("use")` cancels (repo playbtg.elevator: `elevator/Code/UI/ShopConfirmation.razor:36-85`). This is the cleanest "yes/no" pattern — reuse it for any confirm gate, not just shops.
- **3D product/portrait display alongside text.** The shop renders the item's `IconModel` next to a world-panel sign and wires an interaction; a dialogue NPC can do the same to show a speaker portrait or held prop (repo playbtg.elevator: `elevator/Code/Inventory/ShopDisplay.cs:12`).
- **LookAt while talking.** The same trigger that opens dialogue points the NPC's `CitizenAnimationHelper.LookAt` at the player (`EyesWeight/HeadWeight/BodyWeight = 1`) and resets to a default `LookTarget` on exit — cheap "they noticed you" polish (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCLooker.cs:29-47`).
- **Interaction-driven instead of proximity.** Trigger off an `IPressable`/use-key interactable rather than a trigger volume when the player should choose to talk (repo playbtg.elevator: `elevator/Code/Interaction/Interactables/ShopInteraction.cs:19`).

## Gotchas

- **Tag lives on the collider child, root lives on the parent.** Every game does `other.GameObject.Parent` (or `.Root`) after the tag check. Jumper is *inconsistent* — Finish/Wind mix `.Parent` and `.Root`, a latent add/remove mismatch (repo facepunch.jumper: `JumperFinishLine.cs:18` vs `JumperWindTunnel.cs`). Pick one (`other.GameObject.Root`) and stay consistent.
- **The reveal is fire-and-forget `async`.** `RevealTextAsync` is started without awaiting and has **no cancellation** — a new message arriving mid-reveal interleaves chars. Track a `CancellationTokenSource` (or a reveal-generation int) and bail if it changes before the next `await`.
- **Trigger needs a real trigger collider.** `ITriggerListener` callbacks only fire if a sibling `Collider` has `IsTrigger = true` and collision tags that match the player. Easy to forget in the prefab → silent no-op.
- **Modal singletons race on startup.** Static `Instance`/`Local` lookups (ShopConfirmation, doner_kiosk's cam panel resolved after a 3s `Task.DelaySeconds`) can be null before the panel awakes — null-guard every `Open()`/`Instance` access (repo playbtg.elevator: `ShopConfirmation.razor:44,58`).
- **The jumper talker wiring ships commented out.** In `JumperNPCLooker.cs:33-38` only the `LookAt` is live; the `DisplayMessage` call is in a `/* */` block. The pieces are all correct — you wire them together yourself.
- **Multiplayer:** the panels here are local/client UI. If the *decision* matters (which line everyone hears, a confirmed purchase), drive it from a host-authoritative source and replicate with `[Sync]` / `[Rpc.Broadcast]` — do not trust a client-only reveal.

## Seen in

- **facepunch.jumper** — full data→trigger→typewriter stack: `jumper/Code/FunStuff/JumperNPCYapper.cs` (GameResource), `JumperNPCLooker.cs` (trigger + LookAt), `jumper/Code/UI/JumperNPCTalker.razor` (typewriter), `jumper/Code/GamePlay/JumperFinishLine.cs` (ITriggerListener reference).
- **playbtg.elevator** — modal confirm dialog + 3D product display: `elevator/Code/UI/ShopConfirmation.razor`, `elevator/Code/Inventory/ShopDisplay.cs`, `elevator/Code/Interaction/Interactables/ShopInteraction.cs`.
- **luckygaming.doner_kiosk** — per-customer cam-dialog gated behind a CCTV panel + static singleton (startup-delay race): `Code/Game/CameraPanel.cs`, `Code/Game/VideoCamera.cs`.

---
**Verify live:** the installed SDK is authoritative — confirm members before coding with the bridge's reflection tools: `describe_type GameResource`, `describe_type Sandbox.Component+ITriggerListener`, `describe_type Sandbox.UI.PanelComponent`, `search_types CitizenAnimationHelper`. Reflection beats any snippet here if the API has moved.

**See also:** `sbox-api` (exact signatures for `PanelComponent`, `ITriggerListener`, `GameResource`, `RealTimeSince`) and `sbox-build-feature` (the screenshot-driven loop to wire the prefab trigger + panel and see it working).

## Corpus refresh (2026): more reference implementations

Three newly-mined games surface three distinct "dialogue-adjacent" patterns not covered above: **procedural per-player objectives** (despawn.murder), **imperative awaited branching narrative** (dimmies.terryspapers), and **server-authoritative multiplayer vote UI** (lowkeynetworks.newrp). Each is a different point in the design space from the existing typewriter/trigger/modal coverage.

---

### Pattern A — Polymorphic per-player objective generator (despawn.murder)

`Systems/GunAcquisition/` implements a procedural **quest generator**: 3 random tasks per player drawn from a polymorphic pool (FindClues / FindEvidence / VisitZone / FindBody / Survive / FindCluesOrEvidence). This is the cleanest per-player quest-contract reference in the corpus.

Key shapes:
- `GunTaskDefinition(Scene)` base — `IsEnabled()`, exclusion `Group` string (only one task per group is picked), `Make()` → returns a `GunTaskState`.
- `GunTaskManager.GenerateTasks()` shuffles enabled definitions, picks `TaskCount` honoring group exclusion, pads with clue tasks if variety is short.
- **Three progress strategies coexist**: event-hook (OnCluePickup/OnEvidencePickup called from gameplay code), polling (OnFixedUpdate zone/body/survival checks), OR-condition (`Progress >= Target || AltProgress >= AltTarget`).
- Per-player display strings (`[x/y]`) rebuilt and pushed **only to that player** via `Rpc.FilterInclude(connection)`.
- String-encoded task params: `ZoneVisitTracker.FromExtraData("zone1,zone2|seconds")` — lightweight, no extra asset type.
- Anti-pattern: progress tracked as plain fields with no cancellation path. Fix: add a `Cancel()` method to `GunTaskState` so tasks can be voided when a round ends without leaving dangling event hooks.

```text
ObjectiveDefinition contract
  canUse(currentScene) -> boolean
  exclusionGroup -> optional stable key
  createState(randomSource) -> fresh per-player objective state

generateObjectives(definitions, requestedCount, player)
  keep only definitions that can run in the current scene
  randomize their order with the match's authoritative random source
  walk the candidates until requestedCount is reached
    skip a candidate when its non-empty exclusionGroup was already selected
    create a new state and record its exclusionGroup
  if the set is short, add well-defined fallback objectives
  format progress from the resulting states
  send the display payload only to the owning player's connection
```

---

### Pattern B — Imperative awaited branching narrative (dimmies.terryspapers)

`PhoneUI.razor` (~2100 lines) is the entire life-sim story delivered as straight-line C# `async Task` methods. No tree-asset, no node graph — branches are plain `if/else`, state is flags on `PlayerData`. Three micro-primitives create a full VN engine:

```text
waitForAdvance(cancellation)
  show the continue affordance
  suspend until the input signal arrives or cancellation is requested
  consume the signal and hide the affordance

askChoice(prompt, options, cancellation)
  show the prompt and enabled options
  suspend until one valid option is selected or cancellation is requested
  hide the choice controls
  return a stable choice identifier, not display text

runShiftStory(currentShift, playerState)
  build prioritized story conditions from currentShift minus stored milestone shifts
  run the first matching major story event and mark the shift as consumed
  if no major event consumed the shift, run an eligible flavor event
  always publish the end-of-shift summary in a finalization step
```

Key lesson: **ordering encodes priority** — check story beats before flavor beats, and stop selecting once one life-changing event claims the shift. The milestone-shift fields in `PlayerData` are the scheduler; no timer component is needed.

Anti-pattern from the source: `playerData` is written directly to disk client-side with no server — fine for single-player but breaks under any multiplayer authority model. For networked games, keep the flag store server-side and push read-only copies via `[Sync]`.

---

### Pattern C — Host-authoritative time-boxed vote (lowkeynetworks.newrp)

`Code/modules/jobs/JobVoteService.cs` is a complete, reusable yes/no vote: snapshot electorate → filtered-RPC UI to voters only → host-tallied ballots → async countdown → apply. The cleanest vote-flow reference in the corpus.

```text
startVote(candidate)
  require host authority and reject if another vote is active
  snapshot every eligible voter except the candidate
  if the snapshot is empty, resolve using the documented no-voter policy
  create a vote session with a stable id, deadline, electorate, and empty ballot map
  show the vote UI only to the snapshotted electorate
  schedule resolution at the deadline

submitBallot(sessionId, caller, choice)
  execute on the host
  reject a missing, expired, or mismatched session
  reject callers outside the electorate
  validate the choice and record at most one current ballot per voter
  resolve early when every eligible voter has responded

resolveVote(sessionId)
  ignore stale timer callbacks for an older session
  atomically detach the active session before applying effects
  tally using an explicit pass and tie policy
  notify participants, close their UI, and apply the result once
```

Key technique: scope the vote-display broadcast with `Rpc.FilterInclude` so only eligible connections receive it. This same targeting pattern composes with whispers, area notifications, team prompts, and private reveals.

Anti-pattern to avoid: storing `_active` as a plain field with no null-guard on `SubmitVote` after a round ends. Always null-check `_active` and return early if a late ballot arrives after `Finish()` has cleared it.

---

### How these three compose

A dialogue-driven quest game might use all three together:
1. **Pattern A**: NPC gives the player 3 procedurally-generated tasks on talk.
2. **Pattern B**: Story cutscenes between task completions are imperative `await` scripts in a Razor panel.
3. **Pattern C**: At end of round, players vote on which optional objective to unlock next — host-tallied, time-boxed, UI filtered to eligible voters only.

The existing typewriter trigger (jumper) stays as the NPC bark layer; Pattern A replaces the single-line `DisplayMessage` with a rich task list synced per-player.

---

**Read these games** (in addition to the existing set above):
- **despawn.murder** — `Systems/GunAcquisition/Tasks/GunTaskDefinition.cs` + `GunTaskManager.cs` (polymorphic objectives), `Systems/Rounds/States/MapVoteRoundState.cs` (map vote).
- **dimmies.terryspapers** — `Code/UI/PhoneUI.razor` (full imperative VN engine), `Code/Game/GameHandler.cs` (TCS gate, shift-stamp scheduler).
- **lowkeynetworks.newrp** — `Code/modules/jobs/JobVoteService.cs` (vote flow), `Code/modules/chat/ChatService.cs` (proximity chat as social dialogue).
