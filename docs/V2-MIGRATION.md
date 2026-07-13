# Migrating to v2.0.0 (native MCP)

v2.0.0 moves the bridge onto **s&box's built-in editor MCP server** (shipped in the editor
since July 2026, on by default at `http://127.0.0.1:7269/mcp`). The file-IPC transport and
the big stdio tool server are replaced by `[McpTool]` methods the engine discovers itself.

> For the big picture — why the rebuild, what creators can now do, the roadmap — see the
> **[relaunch overview](RELAUNCH.md)**. This page is the practical upgrade guide.

## What you get

| | v1.x (file IPC) | v2.0 (native) |
|---|---|---|
| Transport | 50 ms file polling in %TEMP% | streamable HTTP, loopback-only |
| Screenshots | temp-file path you read back | **inline PNG in the tool result** |
| Tool discovery | 228 tools flat in tools/list | `search_tools` / 28 described toolsets |
| Permission prompts | every tool | read-only tools are `[McpTool.ReadOnly]`-hinted — clients can skip prompts |
| Errors | `{ error }` payloads in successful responses | real tool errors (thrown, readable) |
| Editor crashed? | read_log / get_compile_errors still work | same — via the slim **lifeline** server |
| Node.js required | yes (npx) | only for the optional lifeline server |

## Setup (v2.0.0)

1. Update the `claudebridge` library from the s&box Asset Library (or sync `sbox-bridge-addon/`).
2. Make sure the native server is on: **Editor → Preferences → MCP Server** (default on, port 7269).
3. Connect Claude Code to the native server:
   ```bash
   claude mcp add --transport http sbox http://127.0.0.1:7269/mcp
   ```
4. (Recommended) Keep the editor-down diagnostics:
   ```bash
   claude mcp add sbox-lifeline -- npx -y sbox-mcp-server@2 --lifeline
   ```
   The native server dies with the editor; the lifeline answers "why did the editor crash"
   (read_log, get_compile_errors, docs search, run_self_test) when nothing else can.

Plugin users: `/plugin install sbox-claude` does all of this — the plugin's `.mcp.json`
ships both entries from v2.0.0.

## Tool changes

**Names are stable.** Every v1.x tool keeps its name except six that collide with the
native server's built-ins (duplicate names are silently skipped by the engine, so we
dropped ours — the built-ins are 1:1 equivalents):

| v1.x tool | v2 replacement (built-in) |
|---|---|
| `spawn_model` | `spawn_model` (built-in scene toolset; also `spawn_models` for batches) |
| `list_scenes` | `list_scenes` (built-in) |
| `save_scene` | `save_scene` (built-in) |
| `undo` / `redo` | `undo` / `redo` (built-in) |
| `remove_component` | `remove_component` (built-in) |

Same names, same semantics — your workflows keep working; the calls are just served by
Facepunch's implementations. All six still exist over file IPC until v2.1.0.

**Invocation pattern.** The native server exposes a few entry points (`search_tools`,
`call_tool`, `call_tools`, `list_toolsets`, `describe_toolset`) and discovers everything
else live:

```
search_tools "flicker light"        → finds add_flicker_light (bridge_scaffold_polish)
call_tool  {name: "add_flicker_light", arguments: {lightId: "..."}}
call_tools [...]                    → several calls, one round trip
```

**Screenshots** (`take_screenshot`, `capture_view`, `screenshot_from`, `screenshot_orbit`)
return the PNG inline as an image content block — stop reading files from disk.

**Errors** are real tool errors now (the handler's message, thrown), not `{ error: "..." }`
payloads inside a successful response.

## New tools in the v2 surface (waves 1-4)

The relaunch shipped more than transport — four waves of new tools landed alongside it. Your
existing calls are unchanged; these are additions worth knowing about:

- **Orientation & health (wave 1):** `describe_project` (one-call project orientation),
  `find_broken_references` (scene + file-level broken-ref scan), and `batch_set_property` —
  the first landing of the **`dryRun: true`** validate-first convention.
- **Real prefabs & batch buildout (wave 2):** `create_prefab` / `instantiate_prefab` now do a
  full engine serialization + true instantiation (guid-remapped, collision-free);
  `batch_delete` / `batch_add_component` / `batch_reparent` round out `bridge_batch`;
  `playtest_abort` stops a stuck run.
- **Scene checkpoints & orientation (wave 3):** the new `bridge_workflow` toolset —
  `checkpoint_scene` / `restore_checkpoint` / `list_checkpoints`, the **agent-side undo** —
  plus `describe_scene`, `create_team_assigner`, and `create_idle_income`.
- **Vehicles (wave 4):** the new `bridge_vehicle` toolset — `create_vehicle_controller`
  (drivable raycast car with a built-in driver seat), `create_seat_system`, `tune_vehicle`
  (arcade/drift/offroad/race presets), and `create_physics_grab_tool`.

Since then, the staged `[Unreleased]` waves (2026-07-12/13) add **30 more tools** — economy &
saves (audited ledgers, signed saves, meta-progression), stats & achievements, round-flow &
UI, world & render (water volumes, render-target cameras, day-night sun), AI & systems
(utility AI, schedule brains, needs, event buses, TTS), **gameplay recording** to
`.movie` clips, and the **cinematic wave** (lipsync dialogue, built-in camera effects,
shot-list cutscene authoring, recorded playtests, killcams) — bringing the surface to
**262 native tools / 275 total / 267 handlers**.
The full record is the CHANGELOG's `[Unreleased]` section.

Browse them all in the generated [TOOLSETS.md](TOOLSETS.md); the plain-English tour is
[ECOSYSTEM.md](ECOSYSTEM.md).

## Conventions (align with the native server)

- Vectors/rotations as comma strings: `"x,y,z"`, `"pitch,yaw,roll"` (JSON objects still accepted).
- Objects and components are referenced by GUID (from `get_scene_hierarchy` / `find_objects`).
- List tools accept `limit`-style params and say so in their descriptions.
- Scene-mutating bridge tools refuse during play mode. (Auto-undo for bridge mutations is
  an engine-watch item — the public snapshot APIs are inert on current builds.)

## Timeline

- **v2.0.x** — native surface is primary; file IPC + full stdio server still compiled in
  and functional (fallback for older engine builds).
- **v2.1.0** — file IPC, the TS tool layer (except lifeline), and the parity CI retire.
