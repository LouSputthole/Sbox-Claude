# v2.0.0 — Migrate onto the native s&box MCP server

**Status:** PHASE 1 COMPLETE (89ec14c) · DESCRIPTION SWEEP COMPLETE, 156→0 warnings
(a944a81) · AUTO-UNDO PARKED, engine-inert (084e691) · WAVE-1 TOOLS IN VERIFY
(find_broken_references, batch_set_property+dryRun, describe_project — Batch 51,
231 tools / 223 handlers / 26 native toolsets incl. new bridge_batch)

## Description sweep + hardening (2026-07-09, iteration 2)

- 3 parallel agents improved 111 tool descriptions + ~50 param docs across 29 TS modules
  (style: what it does → what it returns (real fields, verified against handler return
  statements) → what to pass where next → truncation/limits → surprising behavior).
  `audit-mcp-quality.mjs`: 156 warnings → **0 warnings, 0 errors**.
- The sweep doubled as a tool audit — ~20 dishonest descriptions corrected (focus_object
  never moved the camera; get_package_details/validate_project overclaimed; create_scene
  ignores includeDefaults; several scaffold params are handler no-ops — all now flagged
  "currently not applied by the handler" per the v1.5.0 honest-schemas precedent).
- **create_sound_event FIXED** (was latent-broken: schema sent `path`, handler read
  `name`+`directory`; the .sound never referenced the source .vsnd). Handler now honors
  path/sound/volume/pitch/maxDistance — field names verified against real .sound files +
  live `describe_type SoundEvent` (no Looped/minDistance on SoundEvent → dropped from the
  schema as unrepresentable). Live-verified via the gate (path-param discriminator check).
- **execute_csharp template FIXED** (26.07.08b: `Sandbox.Log` → bare `Log`, matching the
  addon's own pattern). Compile-level fix; live QA at release.
- **Undo convention (Phase 3 item) — PARKED, engine limitation.** Verified live on
  26.07.08b: `SceneEditorSession.FullUndoSnapshot` AND `UndoSystem.Snapshot` are both
  INERT (snapshot pair around a real mutation → built-in undo says "Nothing to undo";
  `UndoSystem.Back.Count` stays 0 even after built-in tool edits). The built-in tools'
  per-edit undo uses an internal mechanism addons can't reach. Alternatives rejected:
  `ISceneEditorSession.AddUndo(title, undo, redo)` with full scene re-serialize per
  mutating call — too costly as a default on big scenes. ENGINE-WATCH: rewire when a
  public per-edit undo hook ships. Gate check downgraded to SKIP with the note.
- **New gotcha documented (BRIDGE_GOTCHAS.md #9)**: the Libraries file-watcher is
  unreliable for external edits — sync then `restart_editor` is the only dependable
  recompile loop; successful compiles log nothing (fingerprint = status.json handlerCount).
- Verify-gate extended to 18 checks (sound-event path param + undo step).

## Phase 1 progress (2026-07-08 late session)

- `scripts/extract-manifest.mjs` — fake-McpServer import of all 44 registered dist/tools
  modules → zod introspection → `scripts/tools-manifest.json` (228 tools; filters modules
  not imported by index.ts, e.g. the stale console.js).
- `scripts/emit-mcp-wrappers.mjs` — manifest → 24 `[McpToolset]` classes in
  `sbox-bridge-addon/Editor/Mcp/` (211 generated tools, 47 `[McpTool.ReadOnly]`).
  Type mapping per the table below; required-params-first ordering; XML docs from zod
  descriptions; enum values + vector forms + defaults folded into param docs.
  Excludes: 7 server-side, 6 built-in collisions, 4 screenshots (hand-written).
- `Editor/Mcp/McpGate.cs` (hand-written) — single gate: play-mode guard → handler lookup
  → JsonObject→JsonElement → error-object→throw. Null args are SKIPPED so handlers keep
  their own defaulting (source of truth unchanged).
- `Editor/Mcp/BridgeScreenshotTools.cs` (hand-written) — take_screenshot / capture_view /
  screenshot_from / screenshot_orbit ALL as inline `McpResult.Image` PNGs; orbit is now
  C#-native (was TS-side orchestration), N angles chained via WithImage.
- MyEditorMenu.cs: `GetHandler()` accessor added, `TryGetHandlerError` → internal.
- `scripts/audit-mcp-quality.mjs` — quality gate: built-in collisions (ERROR), toolset
  collisions, vague descriptions, missing param docs, missing next-step/limit notes.
  Current: 0 errors, 156 warnings (= the description-improvement backlog).
- `docs/ADDING-A-TOOL.md` — the new-tool factory template + checklist + conventions.
- ~~OPEN: nullable binding~~ **VERIFIED LIVE**: `int?` params bind correctly both omitted
  and provided (find_objects.limit) — all 235 nullable params safe.
- ~~OPEN: attribute ctors~~ **VERIFIED LIVE**: `[McpToolset("name","desc")]` and
  `[McpTool.ReadOnly("name")]` compile + register on 26.07.08b.
- `scripts/verify-native-mcp.mjs` — the live verify-gate (streamable-HTTP JSON-RPC):
  15/16 first run. All 25 bridge_* toolsets registered; search_tools discovery works;
  RO spot-runs across families pass; mutating GUID round-trip passes; take_screenshot
  returns an inline image/png block; error semantics (throw → readable tool error) pass.
- The 1 failure exposed a real arch wart: get_bridge_status + set_prefab_ref were INLINE
  dispatch special cases, invisible to ClaudeBridge.GetHandler. Fixed by converting both
  to registered handlers (`Editor/CoreCommandHandlers.cs`) and deleting the inline blocks
  — one dispatch path for both transports now.
- `--lifeline` flag added to the TS server (Phase 2 item pulled forward): registers ONLY
  read_log/get_compile_errors/search_docs/get_doc_page/list_doc_categories/run_self_test/
  get_bridge_status. Verified via stdio handshake; 12/12 existing tests still pass.
- Compile gotcha for the factory doc: hand-written Editor/Mcp files need `using Editor;`
  for SceneEditorSession (the generated ones don't reference editor types directly).
- DEFERRED to release: plugin `.mcp.json` flip to `{type:"http", url:"http://127.0.0.1:7269/mcp"}`
  + `sbox-lifeline` stdio entry pinned to `sbox-mcp-server@2.0.0 --lifeline`.

## Phase 0 results (all green)

Verified live against `sbox-editor 26.07.08b` with a throwaway `McpPhase0Tools.cs` in the
Gravehold live addon (`Libraries/sboxskinsgg.claudebridge/Editor/`, written → verified → deleted):

- **Addon discovery WORKS.** `[McpToolset("bridge_phase0")]` + two `[McpTool.ReadOnly]` static
  methods in the Libraries addon appeared in `list_toolsets`/`search_tools` alongside the built-ins,
  with the bridge's 219 IPC handlers running in the same assembly. Deleting the file
  hot-unregistered both tools within seconds, no restart.
- **Schema pipeline WORKS.** XML `<summary>` → tool description; XML `<param>` → param descriptions;
  C# default values → optional params with schema defaults; `[McpTool.ReadOnly]` →
  `annotations.readOnlyHint: true`.
- **Loose binding WORKS as documented.** Sent `NUMBER: "219"` (wrong case, string) → bound to
  `int number = 219`.
- **Bitmap → inline PNG WORKS.** `bridge_phase0_screenshot` (FindMainCamera → RenderToBitmap →
  return Bitmap) came back as an `image/png` content block (159 KB), rendered scene verified visually.
- **Wrappers can reach bridge internals** (`ClaudeBridge.HandlerCount` read live) — the
  `ExecuteViaMcp` gate design is sound.

**Built-in inventory (52 tools, 8 toolsets):**
`asset` (asset_compile, asset_dependencies, asset_files, asset_find_by_file, asset_info, asset_read,
asset_search, asset_thumbnail, asset_types, asset_write, create_asset) · `component`
(get_component_type) · `editor` (call_tool, call_tools, compile_status, console_command,
describe_toolset, editor_status, list_toolsets, read_console, search_tools) · `log` (log_error,
log_info, log_warning) · `package` (find_packages, get_package, install_package) · `play`
(play_pause, play_start, play_stop) · `scene` (add_component, camera_screenshot, create_game_object,
delete_game_object, editor_camera_screenshot, find_game_objects, get_editor_camera, get_game_object,
get_selection, list_scenes, redo, remove_component, save_scene, scene_trace, scene_tree,
set_component, set_editor_camera, set_game_object, set_selection, spawn_model, spawn_models, undo)

**Exact name collisions with our 219 (6):** `spawn_model`, `list_scenes`, `save_scene`, `undo`,
`redo`, `remove_component`. Default decision: DROP all six from our native surface — the built-ins
are 1:1 semantic equivalents (their `spawn_model` spawns a model by path; undo/redo/save/list are
commodity). Phase 2 skill sweep maps old→new names. Note the near-miss: their `create_game_object`
vs our `create_gameobject` — different strings, both register; consider aliasing ours away in favor
of theirs for scene CRUD entirely (their scene toolset covers most of our low-level layer).

**New facts learned live:**
- `call_tools` batch entry point exists (several invocations, one round trip) — the codegen'd
  scaffold families benefit (e.g. scaffold + hotload + compile_status in one request).
- Server `Instructions` (injected at initialize) state: **"Every tool that edits the scene pushes an
  undo step."** Our handlers don't systematically do this — Phase 3 adoption item (UndoSystem scope
  around ExecuteViaMcp for scene-mutating commands).
- Built-in `compile_status` + `read_console` (LogBuffer ring from editor start) cover part of our
  get_compile_errors/read_log lifeline USE CASE while the editor is alive — our lifeline value
  narrows to "editor crashed/hung" diagnosis.
- **Port 7269 bind can fail with "conflicts with an existing registration"** (stale HTTP.sys
  registration from a dying editor instance) — the editor gives up silently (log line `[MCP]
  Couldn't start MCP server on port 7269`). Restarting the editor once the stale holder exits fixes
  it. Add to TROUBLESHOOTING.md in Phase 2.
- **`execute_csharp` is broken on 26.07.08b:** its generated `__Exec_*.cs` template references
  `Sandbox.Log`, which no longer resolves in project editor assemblies → every call fails compile.
  Fix the template (bare `Log` / correct namespace) in v2.0 (or hotfix v1.20.1).
**Trigger:** Facepunch shipped a native MCP server in the editor (doc created/updated 6 Jul 2026,
https://sbox.game/dev/doc/editor/mcp-server). Confirmed present in the installed build:
`Editor.Mcp.*` types + full XML docs in `A:\SteamLibrary\steamapps\common\sbox\bin\managed\Sandbox.Tools.{dll,xml}`,
prefs `EditorPreferences.McpServerEnabled` / `McpServerPort` (default on, `http://127.0.0.1:7269/mcp`).

## Why migrate

The file-IPC transport existed because game-code sandbox blocks `System.Net`. The native server is
hosted by the editor itself, so that constraint is gone. Facepunch's layer gives us for free:

- Streamable-HTTP transport, loopback-only. No 50ms polling, no BOM bugs, no atomic-rename dance.
- **Discovery via EditorTypeLibrary** — "tools defined in addons and hotloaded assemblies appear and
  disappear automatically" (ToolRegistry XML doc). Our `Libraries/claudebridge` editor assembly
  qualifies. `[McpTool]` static method + XML docs = registered tool. Hotload = live re-registration.
- Main-thread marshaling (same model as our queue), `PickupTimeout` editor-blocked detection.
- Loose argument binding (case-insensitive names, string→number, wrapped-JSON unwrap, `[Range]` clamp)
  — kills our vector-coercion bug class centrally.
- `Bitmap` / `McpResult.Image` returns — screenshots reach the agent as inline PNGs, no disk read.
- Typed DTO returns → automatic `outputSchema` + `structuredContent`.
- `[McpTool.ReadOnly]` hint → clients skip permission prompts for read tools.
- Throw-an-exception error semantics delivered to the agent as readable text.

What it does NOT give (our remaining moat): the 219 handlers themselves (scaffolds, lints, terrain,
playtest harness, cinematics…), the skills + cookbook, and editor-down diagnostics — the native
server **dies with the editor**, our `read_log`/`get_compile_errors` don't.

## Facts pinned from the shipped XML docs

- `McpListedAttribute` is **internal on purpose** — addon tools never appear in `tools/list`; agents
  reach them through `search_tools` + `call_tool`. Built-in entry points include `search_tools`,
  `call_tool`, `list_toolsets`, `describe_toolset`, `read_console` (LogBuffer ring from editor start).
- `[McpToolset("name","desc")]` groups a class; without it the class name derives the toolset name.
- Duplicate tool names get **skipped with a warning** → name collisions with built-ins are silent
  tool loss. Must inventory built-in names live before shipping (Phase 0).
- Param descriptions: XML `<param>` docs (doc page) / `[Description]` (XML doc) — verify which wins.
- Conventions injected via `McpServer.Instructions` at initialize: `limit`/`offset` paging, comma-string
  vectors/angles ("x,y,z" / "pitch,yaw,roll"), guids for objects/components, asset paths from
  `asset_search`. Our tools should conform where they differ.

## Target architecture (v2.0.0)

```
Claude Code ──(streamable HTTP)──> native McpServer ──ToolRegistry──> [McpTool] wrappers ──> existing IBridgeHandler dispatch
                                                                       (generated, in claudebridge addon)
```

- **All handler logic stays.** New generated layer `Editor/Mcp/*.cs`: one `[McpToolset]` class per
  TS module grouping, one `[McpTool]` static method per tool, each delegating to a single gate:

```csharp
// Hand-written, ~30 lines. The one entry point every generated wrapper calls.
internal static async Task<object> ExecuteViaMcp( string command, JsonObject args )
{
    if ( Game.IsPlaying && ClaudeBridge.IsSceneMutating( command ) )
        throw new Exception( $"'{command}' mutates the scene and is refused during play mode. Stop play first (stop_play)." );

    var handler = ClaudeBridge.GetHandler( command )
        ?? throw new Exception( $"Unknown bridge command: {command}" );

    var element = JsonSerializer.Deserialize<JsonElement>( args?.ToJsonString() ?? "{}" );
    var result = await handler.Execute( element );

    // Handlers signal failure via an `error` property instead of throwing — convert to native throw semantics.
    if ( ClaudeBridge.TryGetHandlerError( result, out var err ) )
        throw new Exception( err );

    return result;
}
```

- Generated wrapper example (from the manifest, XML docs emitted from TS descriptions):

```csharp
[McpToolset( "bridge_gameobjects", "GameObject lifecycle, hierarchy and selection in the open scene" )]
public static class BridgeGameObjectTools
{
    /// <summary>
    /// Create a new GameObject in the active scene. Returns its GUID for future reference.
    /// </summary>
    /// <param name="name">Display name (e.g. 'Player'). Defaults to 'New Object'.</param>
    /// <param name="position">World position as 'x,y,z'.</param>
    /// <param name="rotation">World rotation as 'pitch,yaw,roll' degrees.</param>
    /// <param name="scale">Uniform scale (number) or 'x,y,z'.</param>
    /// <param name="parent">GUID of parent GameObject. Omit for scene root.</param>
    [McpTool( "create_gameobject" )]
    public static Task<object> CreateGameObject( string name = null, string position = null,
        string rotation = null, string scale = null, string parent = null )
        => McpGate.ExecuteViaMcp( "create_gameobject", McpGate.Args(
            ("name", name), ("position", position), ("rotation", rotation), ("scale", scale), ("parent", parent) ) );
}
```

Type mapping for generated params: primitives map 1:1; vectors/rotations/colors map to `string`
comma-form (native convention, and every handler already parses it since v1.16.0); unions and
complex objects map to `JsonNode`; arrays map to `T[]`. Handlers keep doing the real parsing.

## Phases

### Phase 0 — live verification (~half a day, needs editor open)
1. Editor → Preferences → MCP Server exists + running; `POST /mcp initialize` answers.
2. Inventory built-in tool names/toolsets (`list_toolsets`, `search_tools ""` sweep) → collision list
   against our 219 (likely suspects: `raycast`, anything asset/screenshot-shaped).
3. Hand-write ONE `[McpTool]` in the live addon (e.g. wrap `get_bridge_status`), hotload, confirm it
   appears via `search_tools` and runs via `call_tool`. Confirm XML `<param>` docs flow through, and
   whether `[McpTool.ReadOnly]` + Bitmap returns behave as documented.
4. Decide final toolset naming (prefix `bridge_*` toolsets to avoid built-in collisions).

### Phase 1 — manifest + codegen (~2–3 days)
1. **Manifest extractor** (`scripts/extract-manifest.mjs`): import each `src/tools/*.ts` register
   function with a fake `McpServer` that records `(name, description, zodSchema)`; serialize schema
   via `zod-to-json-schema` → `tools-manifest.json`. (Reuse the parsing already in `audit-parity.mjs`.)
2. **C# emitter** (`scripts/emit-mcp-wrappers.mjs`): manifest → `Editor/Mcp/<Family>Tools.cs` with
   XML docs, typed params per the mapping table, `[McpTool.ReadOnly]` from a curated read-only list
   (seed: `get_*`/`list_*`/`search_*`/`describe_*`/`find_*`/`measure_*`/`is_*` minus exceptions; NOT
   simply the complement of `_sceneMutatingCommands` — `write_file`, `install_asset` etc. write disk).
3. Exclude the 7 MCP-server-side tools (read_log, get_compile_errors, execute_csharp*, search_docs,
   get_doc_page, list_doc_categories, run_self_test) — see Lifeline below. (*execute_csharp's
   hotload-eval can move in-addon later; keep server-side for v2.0.)
4. Screenshot family upgraded by hand, not codegen: return `McpResult.Image(bitmap)` instead of a
   temp-file path (take_screenshot, screenshot_from, capture_view, screenshot_orbit, capture step).
5. Verify-gate live on Gravehold: hotload clean, `search_tools` finds all families, spot-run one tool
   per family via `call_tool`, playtest harness end-to-end, screenshots arrive as image blocks.

### Phase 2 — packaging + skills (~1–2 days)
1. Plugin `.mcp.json`: replace the stdio npx server with the native HTTP endpoint
   (`{"type":"http","url":"http://127.0.0.1:7269/mcp"}`) — port read from prefs; document non-default ports.
2. Keep a slim **lifeline** stdio server (`sbox-mcp-server --lifeline`) exposing ONLY the
   editor-down tools (read_log, get_compile_errors, docs search, run_self_test). It's the thing that
   answers "why did the editor crash" when the native server is dead.
3. Skills sweep: `sbox-build-feature`, `sbox-setup`, `sbox-scaffold-game`, `sbox-api`, cookbook — the
   invocation pattern becomes `search_tools`→`call_tool {name, arguments}`; tool NAMES stay identical.
   `sbox-setup` gains the `claude mcp add --transport http` one-liner.
4. INSTALL/README/TROUBLESHOOTING rewrite: install = (a) get addon from Asset Library, (b) one
   `claude mcp add` command. Node.js no longer required for the main path.
5. File IPC stays compiled-in and functional in v2.0.0 (harmless coexistence; TS server just isn't
   registered by default). Delete IPC + full TS tool modules in v2.1.0 once native path proves out.

### Phase 3 — build on top (ongoing, the payoff)
- New-tool velocity: a tool = one static method + XML docs. No TS module, no parity audit, no npm
  publish. The TS↔C# parity CI gate retires with the TS tool layer.
- Typed DTO returns for hot read tools (`get_scene_hierarchy`, `get_bridge_status`, `find_objects`)
  → outputSchema, agents plan around fields instead of parsing text.
- Fold conventions: adopt `limit`/`offset` paging + comma-string-first vector docs everywhere.
- Loopback multi-instance watch (v1.21.0 item) unaffected — orthogonal.

## Release sequence (v2.0.0 — user-gated steps marked 👤)

The GitHub marketplace serves the plugin from `main` — pushing early would hand v2 skills
to v1.20 users. Everything below happens in ONE release session, in order:

1. Version bumps: `sbox-mcp-server/package.json` → 2.0.0, `BridgeVersion` const → 2.0.0,
   plugin `plugin.json` + marketplace.json → 2.x, CHANGELOG `[Unreleased]` → `[2.0.0]`.
2. Plugin `.mcp.json` flip: `sbox` → `{type:"http", url:"http://127.0.0.1:7269/mcp"}`,
   add `sbox-lifeline` → `npx -y sbox-mcp-server@2.0.0 --lifeline`.
3. Gates: audit-parity, audit-mcp-quality, npm test, codegen freshness, verify-native-mcp
   (live editor), execute_csharp live QA (template fix), lifeline handshake.
4. 👤 `npm publish` (2.0.0).
5. `git push` main + tag v2.0.0 (marketplace updates now — after npm exists).
6. 👤 s&box Asset Library republish of the addon (GUI step, Org sboxskinsgg).
7. Post-release: `claude mcp add --transport http` smoke test from a clean machine profile;
   graph regen (`scripts/regen-graph.ps1 -RepoRoot ...`).

## Risks / open questions

| Risk | Mitigation |
|---|---|
| Name collision with built-ins silently drops our tool (ToolRegistry skips dupes with a warning) | Phase 0 inventory; rename once at v2.0; grep editor log for the skip warning in the verify-gate |
| Engine channel availability — user's build has it, but Asset Library users may lag | Keep file IPC functional through v2.0.x; addon works on both |
| `JsonObject`→`JsonElement` param conversion edge cases (unions, nested objects) | Handlers already coerce flexibly; verify-gate one tool per family |
| Error-object→throw conversion changes agent-visible error text | Acceptable; error strings already written for agents |
| 219 generated methods bloat compile time / TypeLibrary scan | Measure in Phase 1; families can ship in waves if needed |
| Facepunch adds overlapping high-level tools later | Fine — ours win on depth; revisit per release |

## What gets deleted (eventually, v2.1.0)

File polling + queue + atomic writes + BOM handling in `MyEditorMenu.cs` (~400 lines), the entire
TS tool layer (~40 modules) except the lifeline set, `audit-parity.mjs` + its CI job, npx/Node
install path from docs.
