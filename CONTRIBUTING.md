# Contributing to the s&box Claude Bridge

Thanks for your interest in contributing! This project lets non-coders build s&box games
through conversation with Claude Code.

## Architecture overview (v2)

Since v2.0.0 "Native" the bridge runs on **s&box's built-in editor MCP server**:

```
Claude Code → (streamable HTTP) → s&box native MCP server → [McpTool] methods → IBridgeHandler handlers
              127.0.0.1:7269/mcp    hosted BY the editor       Editor/Mcp/*.cs      Editor/*Handlers.cs
```

- **A tool is a static `[McpTool]` method** in the addon's `Editor/Mcp/` tree. The engine's
  EditorTypeLibrary discovers it on hotload; agents find it via the native server's
  `search_tools` / `call_tool` / `list_toolsets`. **XML docs ARE the schema** — the
  `<summary>` is the tool description, each `<param>` is that parameter's schema description.
- **Handlers are the execution layer.** The `IBridgeHandler` classes registered in
  `sbox-bridge-addon/Editor/MyEditorMenu.cs` are unchanged from v1; the `[McpTool]` methods
  delegate to them through `McpGate` (play-mode guard, handler lookup, error-object → thrown
  tool error).
- **The TypeScript server survives in two compatibility roles.** As the **lifeline**
  (`npx -y sbox-mcp-server@2 --lifeline`): 7 editor-down diagnostics tools that keep working
  when the editor — and the native server with it — is dead. And as the **legacy file-IPC
  fallback** (`%TEMP%/sbox-bridge-ipc/`, 50 ms polling) for older engine builds. It remains
  available but is not the default; retirement requires a separate compatibility decision.
  Root `TROUBLESHOOTING.md` covers the legacy transport;
  `docs/TROUBLESHOOTING.md` covers the native one.
- **Six tool names are deliberately absent** from our surface — the native built-ins own
  them 1:1: `spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component`.
  Name collisions are SILENT tool loss; `scripts/audit-mcp-quality.mjs` guards this.

**The v2.0.0 surface:** 232 native tools / 28 `bridge_*` toolsets / 53 read-only /
7 lifeline / **245 total** (232 + 7 + 6 built-in-served) / **237 handlers**.
`get_bridge_status` reports the live `handlerCount` — that's the assembly fingerprint.
`docs/TOOLSETS.md` (generated) is the authoritative inventory.

## Adding a new tool

**Read [docs/ADDING-A-TOOL.md](docs/ADDING-A-TOOL.md) — it is the template, checklist, and
documentation standard.** Don't work from this file or from memory of the v1 flow.

The short version: a new tool is **one static `[McpTool]` method with a 5-point XML
`<summary>` and `<param>` docs on every parameter**, delegating to a handler via
`McpGate.Run`. No TS module, no zod schema, no npm publish. `[McpTool.ReadOnly]` only if it
truly never mutates project/scene/editor state; scene-mutating names go into
`_sceneMutatingCommands` in `MyEditorMenu.cs`.

Two kinds, per ADDING-A-TOOL:

| Kind | Where the change goes |
|---|---|
| New native tool (all new tools from v2.0 on) | hand-written `[McpTool]` method in `Editor/Mcp/` or next to its handler family |
| Change to an existing **wrapped** tool | edit the TS zod schema, re-run the codegen (below) — the TS schemas remain the source for the current generated wrapper layer |

## Development setup

```bash
# Build + test the TS server (lifeline / legacy fallback / codegen source)
cd sbox-mcp-server
npm install
npm run build
npm test

# Watch mode (auto-rebuild)
npm run dev
```

Connect Claude Code for live testing:

```bash
claude mcp add --transport http sbox http://127.0.0.1:7269/mcp   # the native server (editor must be open)
claude mcp add sbox-lifeline -- npx -y sbox-mcp-server@2 --lifeline
```

Node is only needed for the lifeline, the codegen, and the gates — the main tool path is
C# inside the editor.

## The codegen / regen pipeline

The generated wrapper layer (27 `Editor/Mcp/Bridge*Tools.cs` classes + `docs/TOOLSETS.md`)
is emitted from the TS zod schemas. Whenever a schema changes:

```bash
cd sbox-mcp-server && npm run build      # freshen dist/
node scripts/extract-manifest.mjs        # TS zod schemas → scripts/tools-manifest.json
node scripts/emit-mcp-wrappers.mjs       # manifest → sbox-bridge-addon/Editor/Mcp/*.cs + docs/TOOLSETS.md
node scripts/audit-mcp-quality.mjs       # collision + description quality gate
```

Generated files carry an `AUTO-GENERATED` header and are deleted + rewritten on every run —
**never hand-edit them**. Hand-written files in `Editor/Mcp/` (`McpGate.cs`,
`BridgeScreenshotTools.cs`) have no header and survive regeneration.

## Verification loop (the loop that works)

Editing addon C# and trusting silence is how you end up testing a stale assembly. The
dependable loop:

1. **Sync** changed files to a live project's `Libraries/<bridge>/Editor/` using
   **absolute paths** (background shells lose cwd; a relative `cp` fails silently and you
   test the old assembly).
2. **`restart_editor`.** The Libraries file-watcher is unreliable for external edits
   ([docs/BRIDGE_GOTCHAS.md](docs/BRIDGE_GOTCHAS.md) #9) — a restart is THE recompile loop
   (~5–7 min on a real project). If the addon itself is compile-broken (bridge tools dead),
   kill + relaunch `sbox-dev.exe -project <sbproj>` from the shell.
3. **Fingerprint:** `get_bridge_status` → `handlerCount` must match the new registration
   count. Successful compiles log NOTHING; only failures log `Compile of 'X' Failed`.
4. **Live gate:** `node scripts/verify-native-mcp.mjs` against the open editor. See
   [TESTING.md](TESTING.md) for what it checks and the manual smoke path.

For generated game code (the `create_*` scaffolds): **always** generate → hotload →
compile check → confirm the class in TypeLibrary. The verify-gate has caught real SDK bugs
every release; a code review has not.

## CI gates

`.github/workflows/ci.yml` runs on every push/PR to main:

| Gate | What it catches |
|---|---|
| `npm ci && npm run build` | the TS server must compile |
| `node scripts/audit-parity.mjs` | TS tools ↔ C# handlers parity + 4-way version lock (kept in lockstep while the compatibility fallback ships) |
| `npm test` | transport-client regressions (heartbeat staleness, timeout diagnostics, IPC-dir override) |
| Codegen freshness | re-runs `extract-manifest` + `emit-mcp-wrappers`, then `git diff --exit-code` on `scripts/tools-manifest.json`, `sbox-bridge-addon/Editor/Mcp`, `docs/TOOLSETS.md` — a dirty tree means someone edited schemas without regenerating (or hand-edited a generated file) |
| `node scripts/audit-mcp-quality.mjs` | tool-name collisions (hard fail — collisions are silent tool loss) + description quality warnings |

## Handler conventions (still current in v2)

The C# handler layer didn't change in the migration; these rules still apply:

- One handler class per command, `{CommandPascalCase}Handler : IBridgeHandler`, registered
  via `Register( "command_name", () => new XHandler() )` in `RegisterHandlers()` (factory
  registration so a broken handler can't take the whole bridge offline).
- Tab indentation, Allman-ish braces with s&box spacing; `Log.Info()` / `Log.Warning()`
  prefixed `[SboxBridge]`.
- **File paths** must resolve through `ClaudeBridge.TryResolveProjectPath` (canonicalize +
  project containment, separator-safe). Do not hand-roll a containment check.
- **Generated identifiers** from user strings go through `ClaudeBridge.SanitizeIdentifier`.
- **Errors:** throw an `Exception` with an agent-readable message, or return an object with
  an `error` field — `McpGate` converts the latter into a real thrown tool error on the
  native surface.

### Generated-code templates (scaffolds)

Several tools emit C# source as `$@"..."` interpolated strings. Each of these escaping
rules has shipped a real bug — learn them before writing a new scaffold:

- Literal `{` / `}` in the output → `{{` / `}}`. Forgetting compiles fine in the generator
  and emits a broken interpolation site in the generated file.
- Literal `"` → `""` (verbatim strings do not recognize `\"`).
- Empty string literal in the output → `""""` (four quotes). This exact bug shipped twice.
- Run `scripts/check-csharp-syntax.py` on scaffold output before calling a tool done.
- Never trust API names from docs or the corpus without `describe_type` first — the
  `GetMouseRay` incident (the real SDK method is `ScreenPixelToRay`) was caught by the
  verify-gate, not by review.

## Tool counts in docs

The final v2.0.0 numbers all docs agree on are the table above (232/28/53/7/245/237) —
[docs/DOC-AUDIT.md](docs/DOC-AUDIT.md) is the reconciliation record. Between releases the
count drifts as tools land: keep exact numbers in `CHANGELOG.md` entries and rely on
`get_bridge_status` / the generated `docs/TOOLSETS.md` header for the live truth rather
than sweeping every doc.

## License & pull requests

- The project is **source-available, no redistribution** (s&box Claude Bridge
  Source-Available License 1.0) — see [LICENSE](LICENSE) and [NOTICE](NOTICE). By
  contributing you agree your contribution is licensed under the same terms. The
  "s&box Claude Bridge" name and branding are trademarks, not licensed for reuse.
- Before opening a PR: run the gates locally (build, `npm test`, quality audit, codegen
  freshness) and the live verify loop if you touched the addon.
- Keep diffs focused; never hand-edit `AUTO-GENERATED` files; **never rename a shipped
  tool** (if unavoidable, keep the old name as a delegating `[McpTool("old_name")]` and
  note the migration in `CHANGELOG.md`).
