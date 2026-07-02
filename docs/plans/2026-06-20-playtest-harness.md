# Plan — the playtest / gameplay-verification harness (next major update)

**Premise.** The bridge can build + visually verify *static* scenes and *structurally* lint
multiplayer, but it's weak at verifying **playable loops** — it can build a double-jump or a
shop but can't reliably press the button, run the loop, and assert the result. `drive_player`
is a half-step. This wave makes the bridge **verify gameplay**.

> Status: **SHIPPED in v1.17.0** (2026-06-25). Dogfooded live on Gravehold — a full
> walk → assert-moved → jump → assert-airborne-in-frame → land loop verified PASS and
> re-ran clean. The queued enhancements (a `capture` step, a `Displacement` assert read) SHIPPED in v1.17.1.

## What we proved live (the hard, uncertain parts)

Tested on the Gravehold player (facepunch `PlayerController` + `MoveModeWalk` + `Keeper*` layer):

- **Movement injection works IF you disable the controller's own input read first.** Setting
  `WishVelocity` alone did nothing (`appliedMembers:["WishVelocity"]`, position unchanged) —
  the controller reads `Input.AnalogMove` each frame (`UseInputControls=true`) and overwrites
  it. Set `UseInputControls=false` first → the same drive moved the player **0 → 526u forward**.
- **Position read for assertions** = `get_bounds.position` (returns the GO's WorldPosition).
- **State read** = `get_runtime_property` (read `Velocity`, `IsOnGround`, `Mode`, any field).
- **Jump** = `invoke_method(PlayerController, "Jump", "0,0,400")` — a direct method, no input.
- **Actions** (use/dig/grab) = hold `Input.SetAction(action,true)` across frames (the existing
  `drive_player action=` approach); the single-frame `simulate_input` misses the `Input.Pressed`
  rising edge, holding it down does not.

### Two learnings that drive the design
1. `WishVelocity` **persists** under `UseInputControls=false` — the player kept moving at ~160 u/s
   after the drive ended. The runner must **zero WishVelocity** (and restore `UseInputControls`)
   between steps / at teardown.
2. Transient effects (a jump's z-velocity) are **already gone** by the time a *separate* bridge
   call lands. Assertions on momentary state **cannot** be done via TS round-trips.

## Architecture decision

**The harness is a single in-addon async job (a `PlaytestHandler`, modeled on `drive_player`'s
frame loop) that executes a scripted step list inside the editor frame loop** — setting input,
reading state, and evaluating assertions *at the correct frame*. TS only kicks it off and polls
the transcript. This is forced by learning #2: only code running in the frame loop can time-align
input + assertion with gameplay.

## Tool surface (prototype)

- **`playtest({ steps:[...], component?, id? })`** — start the scripted job (async, returns a job id).
- **`playtest_status()`** — poll the running/finished job: per-step transcript + pass/fail.

### Step DSL (each step runs for `frames`/`seconds`, default 1 frame)
- `{ move:{x,y}, frames }` — analog move in the controller frame (auto-sets `UseInputControls=false`,
  writes `WishVelocity`; zeroed when the step ends).
- `{ look:{pitch,yaw,roll} }` / `{ lookDelta:{...}, frames }` — set/sweep `EyeAngles`.
- `{ action:"use", frames }` — hold a named input action down (rising-edge safe).
- `{ jump:[x,y,z] }` — `invoke_method` the controller's `Jump`.
- `{ invoke:{ component, method, args } }` — call any method (drive game state past a menu, etc.).
- `{ set:{ component, property, to } }` — `set_runtime_property` (toggles, teleport via WorldPosition).
- `{ wait: frames }` — advance N frames.
- `{ assert:{ read, op, value, desc } }` — read a value (`WorldPosition.x`, `<Component>.<Field>`,
  `get_bounds`/`get_runtime_property` under the hood), compare with op (`>`,`<`,`==`,`!=`,`changed`,
  `approx`), record pass/fail with `desc`. Evaluated **in-frame**, so transient state is catchable.
- `{ capture: true }` — `capture_view` at this frame; path recorded in the transcript.

### Teardown (always)
Restore `UseInputControls`, zero `WishVelocity`, release held actions — so the play session is
left clean whether the script passed, failed, or errored.

## Build steps
1. `PlaytestHandler.cs` (new addon file) — the async frame-loop job + a small assertion evaluator
   (reuse `ParseVector3`/reflection-read from `MyEditorMenu`; reuse `drive_player`'s frame-driver).
2. `playtest` + `playtest_status` TS tools (`src/tools/` — fold into `playmode.ts` or a new
   `playtest.ts`); register; parity audit.
3. **Dogfood on Gravehold**: script `move forward → assert WorldPosition.x rose`,
   `jump → assert IsAirborne within N frames`, `action:"use" near DiggableEarth → assert a Keeper
   state/inventory change`. Iterate on the reflection→teleport fallback for non-`PlayerController`
   controllers.
4. Only ship (version bump + changelog + republish) once it reliably verifies a real loop.

## Risks / open questions
- **Controller diversity** — `WishVelocity`/`UseInputControls` are facepunch-`PlayerController`
  specifics. Custom controllers (e.g. a top-down `CustomController`) may expose neither →
  fallback chain: known-member reflection → set `Velocity` → teleport `WorldPosition` per frame.
- **Action names** are project-specific (`.sbproj` InputSettings) — the script names them; the
  runner just holds them. A `list_input_actions` helper would make this discoverable.
- **Frame-stepping is still time-based** (no deterministic editor tick API) — assertions key off
  frames-since-step-start, evaluated in the frame loop, which is good enough.
- **Walls (out of scope):** 2-player netcode session (one instance = proxies), and "is it *fun*"
  (still a human's call). This verifies *mechanics fire*, not game feel.
