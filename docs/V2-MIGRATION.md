# Migrating to v2.0.0 (native MCP)

v2.0.0 moves the bridge onto **s&box's built-in editor MCP server** (shipped in the editor
since July 2026, on by default at `http://127.0.0.1:7269/mcp`). The file-IPC transport and
the big stdio tool server are replaced by `[McpTool]` methods the engine discovers itself.

## What you get

| | v1.x (file IPC) | v2.0 (native) |
|---|---|---|
| Transport | 50 ms file polling in %TEMP% | streamable HTTP, loopback-only |
| Screenshots | temp-file path you read back | **inline PNG in the tool result** |
| Tool discovery | 228 tools flat in tools/list | `search_tools` / 25 described toolsets |
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
