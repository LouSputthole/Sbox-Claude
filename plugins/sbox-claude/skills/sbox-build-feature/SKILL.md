---
name: sbox-build-feature
description: Use when building, modifying, or polishing any feature in an s&box game project through the Claude Bridge — gameplay systems, UI panels, character abilities, animation, world generation, anything that produces a visible or runtime change. Codifies the screenshot-driven iteration workflow that prevents the "guess-and-check" loop the bridge is most susceptible to.
---

# Building s&box Features Through the Bridge

Use this workflow for every non-trivial player or scene change. Pair it with
`sbox-api`: that skill teaches the s&box C# model; this one drives the editor,
compiles, runs, and verifies the result. Live reflection through `describe_type`,
`search_types`, and `get_method_signature` is authoritative for the installed SDK.

**Invocation (v2, native MCP):** find plain bridge tool names with
`search_tools`, run them with `call_tool`, batch fixed sequences with `call_tools`,
and browse groups with `list_toolsets` / `describe_toolset`. The native server also
provides `spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, and
`remove_component` directly.

## Hard rule: see every visual feature before calling it done

`take_screenshot` renders the main camera. It may not face the changed object. Use
`capture_view id=<objectId>` (historical alias: `screenshot_from`) to auto-frame an
object, `capture_view position=... lookAt=...` for a free camera, or
`screenshot_orbit id=<objectId>` for several angles. These native wrappers return
PNG images inline. Inspect the image; do not infer visual correctness from code.

## The six-step workflow

### 1. Confirm the bridge is alive

```
get_bridge_status
```

If it does not answer, confirm s&box is running and **Editor → Preferences → MCP
Server** is enabled at `http://127.0.0.1:7269/mcp`. A log line saying the server
could not start on port 7269 usually means a stale editor process still owns the
HTTP registration; restart after that process exits. Do not continue until the
server responds.

### 2. Design non-trivial behavior before coding

Brainstorm first when the work adds a state machine, component/system, animation,
IK, camera behavior, or any result whose appearance is uncertain. Agree on the
smallest observable outcome and how it will be verified.

### 3. Verify unfamiliar APIs live

```
describe_type           name="CitizenAnimationHelper"
search_types            pattern="*Renderer"
get_method_signature    type="GameObject" method="AddComponent"
```

Do this before writing a type or member you have not confirmed in the installed
SDK. Use official Facepunch documentation for broader concepts, but let reflection
win on exact signatures.

### 4. Implement in bite-sized edits

- Make one coherent change at a time and keep file ownership clear.
- Read the project's instructions before editing.
- Never copy the repository's `claudebridge.sbproj` into a project's `Libraries/`.
  The published copy uses `Org: sboxskinsgg`; the project working copy must remain
  `Org: local`, or the compiler can see a duplicate assembly name.

### 5. Hotload and prove the compile

```
trigger_hotload
compile_status
```

If an externally edited project `.cs` file did not compile, `start_play` forces a
project compile; inspect the status, then `stop_play` before more scene mutation.
Changes to the addon editor assembly under `Libraries/` require `restart_editor`.
When the optional lifeline server is enabled, use its `get_compile_errors` and
`read_log` tools when the native editor server is unavailable. Ignore old failure lines unless their timestamps and paths
match the current change.

### 6. Capture, inspect, and iterate

```
capture_view       id=<objectId>
capture_view       position="x,y,z" lookAt="x,y,z"
screenshot_orbit   id=<objectId>
```

Compare the returned image with the intended result. Adjust a measured property or
transform, compile if needed, and capture again. A visual feature is complete only
after the returned image supports that claim.

## Verify the running game

Play-mode tools prove behavior as well as layout:

- `take_screenshot` shows the live main-camera view, including screen-space UI.
- `capture_view` can render the active scene from the main camera or a temporary
  viewpoint. Temporary-camera captures include world-space UI but not fullscreen
  screen-space panels, so use both capture paths when UI matters.
- `invoke_button` calls a public parameterless component method and can advance
  game state when a UI click cannot be synthesized.
- `playtest` runs movement/action/set/wait/capture/assert steps across editor frames.
  Start play first, then poll `playtest_status` until `finished:true`.
- `get_runtime_property` is the unambiguous check for a live component state.
- Host-authoritative components may be proxies in a solo no-session test. Use a
  non-networked iteration path or start a host session; do not report proxy behavior
  as a gameplay failure.

Runtime object IDs can differ from edit-scene IDs. Rediscover the target with
`get_scene_hierarchy` or `find_objects` after entering play mode, then use that
runtime ID for runtime tools.

## Coordinate multi-agent work around one editor

Only one orchestrator may mutate the scene, run play mode, or capture screenshots
against a given s&box editor instance. Parallel agents may author disjoint source
files, but they must not share scene or play-state control. The orchestrator compiles,
runs, and verifies their combined result serially. Truly independent concurrent
editor work requires separate s&box instances and bridge connections.

A self-bootstrapping `GameObjectSystem<T>` can avoid shared scene-file edits. Read
the system and runtime-ID guidance in the gotchas reference before using it.

## Read the gotchas reference

Read [references/gotchas.md](references/gotchas.md) before implementing a
non-trivial feature and whenever a tool result disagrees with runtime behavior. It
covers sandboxed math, assets, hotload, generated code, runtime IDs, UI rerendering,
animation, lighting, audio, and scene-free systems. Do not grow another large gotcha
table in this router; update that reference instead.

## Bridge map for maintainers

The bridge knowledge graph lives at `docs/graph/`. Before changing a bridge tool,
consult `docs/graph/graph.json` or `docs/graph/graph.html` and check the date in
`GRAPH_REPORT.md`. Regenerate the graph for a release with
`scripts/regen-graph.ps1` (or the full `/graphify` workflow).

## Project instructions

If the project contains `CLAUDE.md`, read it first. It carries project-specific
input bindings, assets, roles, and scene decisions that this generic workflow cannot
know.

## When stuck

1. Capture the current state.
2. Inspect the returned image or runtime property yourself.
3. State exactly what differs from the intended result.
4. Make one specific, measured adjustment and verify again.

The screenshot and runtime-state loops close faster than repeated guesses.
