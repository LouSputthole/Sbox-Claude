# s&box Claude Bridge — repo guide

> Let non-coders build s&box games through conversation with Claude Code.

## Status: v2.1.0 "Action!" + Unreleased

**Current working source: 273 native tools / 28 toolsets / 57 read-only / 7 lifeline /
286 total / 278 handlers.** Released v2.1.0 added the 30-tool Tier-2 +
gameplay-recording + cinematic waves on top of v2.0.0 "Native"; those 30 were
live-verified on Gravehold — see CHANGELOG `[2.1.0]`. The eleven `[Unreleased]` tools
included in the working-source totals have passed source and offline gates; live editor
smoke remains pending. Run `get_bridge_status` for the installed live count — it's the
assembly fingerprint.

The bridge runs on **s&box's built-in editor MCP server** (`http://127.0.0.1:7269/mcp`,
on by default since editor 2026-07-06, Editor → Preferences → MCP Server). Full release
record: [CHANGELOG.md](CHANGELOG.md). Current docs:
[docs/RELAUNCH.md](docs/RELAUNCH.md) · [docs/AGENT-GUIDE.md](docs/AGENT-GUIDE.md) ·
[docs/TOOLSETS.md](docs/TOOLSETS.md) (generated, authoritative inventory) ·
[docs/V2-MIGRATION.md](docs/V2-MIGRATION.md) · [docs/BRIDGE_GOTCHAS.md](docs/BRIDGE_GOTCHAS.md).

## Architecture (v2)

```
Claude Code → (streamable HTTP) → s&box native MCP server → [McpTool] methods → BridgeDispatch handlers
              127.0.0.1:7269/mcp    hosted BY the editor       Editor/Mcp/*.cs      Editor/*Handlers.cs
```

- **Tool surface** = static `[McpTool]` methods in `Editor/Mcp/` — 27 **generated** toolset
  classes (`BridgeAssetTools.cs` … `BridgeWorldTools.cs`, emitted from the TS zod schemas)
  plus 2 **hand-written** files: `McpGate.cs` (single gate: play-mode guard, handler lookup,
  error-object → exception) and `BridgeScreenshotTools.cs` (9 camera/screenshot tools returning
  **inline PNG** `McpResult.Image` blocks).
- **Discovery is automatic**: EditorTypeLibrary picks up `[McpTool]` methods on hotload;
  agents reach them via the native server's `search_tools` / `call_tool` / `list_toolsets`.
  XML docs ARE the schema. `[McpTool.ReadOnly]` = no permission prompt.
- **Handlers** (the 278 registered `IBridgeHandler` routes in `MyEditorMenu.cs`) are the
  execution layer the gate delegates to — unchanged from v1.
- **6 tool names are deliberately absent** from our surface (native built-ins own them):
  `spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component`.
  Name collisions are SILENT tool loss — `scripts/audit-mcp-quality.mjs` guards this.
- **Lifeline** (`sbox-mcp-server --lifeline`, stdio): 7 editor-down tools — `read_log`,
  `get_compile_errors`, docs search ×3, `run_self_test`, `get_bridge_status`. The native
  server dies with the editor; the lifeline is how Claude diagnoses a dead one.
- **Legacy file IPC** (`%TEMP%/sbox-bridge-ipc/`, 50 ms polling) remains available as a compatibility fallback
  in the current source; retirement is deferred until a separate compatibility decision. Root `TROUBLESHOOTING.md` covers it;
  `docs/TROUBLESHOOTING.md` covers the native transport.

## Repo structure

```
sbox-claude/
├── CLAUDE.md                       ← you are here
├── README.md / INSTALL.md          ← public front door (v2)
├── CHANGELOG.md                    ← full release history
├── TROUBLESHOOTING.md              ← LEGACY file-IPC transport only
├── LICENSE / NOTICE                ← Source-Available 1.0, no redistribution; name is trademarked
├── scripts/
│   ├── extract-manifest.mjs        # TS zod schemas → tools-manifest.json
│   ├── emit-mcp-wrappers.mjs       # manifest → Editor/Mcp/*.cs + docs/TOOLSETS.md
│   ├── audit-mcp-quality.mjs       # collision + description quality gate
│   ├── audit-parity.mjs            # TS↔C# parity + version lock (CI)
│   ├── verify-native-mcp.mjs       # live HTTP verify-gate (--port; check families incl. wave chains)
│   └── regen-graph.ps1             # knowledge-graph refresh (pass -RepoRoot explicitly)
├── sbox-mcp-server/                # TS server: legacy full surface + --lifeline mode
│   └── src/tools/*.ts              # zod schemas = codegen SOURCE for the wrappers
├── sbox-bridge-addon/Editor/       # the addon (synced into a project's Libraries/)
│   ├── MyEditorMenu.cs             # dispatch, registration, _sceneMutatingCommands
│   ├── *Handlers.cs                # handler families (one file per domain)
│   └── Mcp/                        # [McpTool] surface (generated + McpGate + screenshots)
├── plugins/sbox-claude/            # Claude Code plugin (.mcp.json: native http + lifeline)
└── docs/                           # see docs/DOC-AUDIT.md for the full doc map
```

The LIVE addon copy s&box actually compiles lives in a project, e.g.
`Documents\s&box projects\untitled\Libraries\sboxskinsgg.claudebridge\` (Gravehold) — sync
repo ↔ live copy when developing. **Never sync the repo `.sbproj` into a project copy.**

## Dev workflow (the loop that works)

1. Edit C# in the repo `sbox-bridge-addon/Editor/` (or generate: see below).
2. Sync changed files to the live project's `Libraries/<bridge>/Editor/` — **absolute paths**
   (background shells lose cwd; a relative `cp` fails silently and you test the old assembly).
3. `restart_editor` (via native `call_tool`; works via lifeline/raw IPC too). The Libraries
   file-watcher is unreliable for external edits (gotcha #9) — restart is THE recompile loop
   (~5-7 min on Gravehold). If the addon itself is compile-broken (bridge tools dead), kill +
   relaunch `sbox-dev.exe -project <sbproj>` via PowerShell.
4. Fingerprint: `get_bridge_status` → `handlerCount` must match the new registration count.
   Successful compiles log NOTHING; only failures log `Compile of 'X' Failed`.
5. Verify live: `node scripts/verify-native-mcp.mjs` and/or `run_self_test` (8-step
   create→render→screenshot→recompile→cleanup).

**Adding a tool → read [docs/ADDING-A-TOOL.md](docs/ADDING-A-TOOL.md).** Summary: one static
`[McpTool]` method + 5-point XML summary + `<param>` docs on everything; ReadOnly only if it
truly never mutates; scene-mutating names go into `_sceneMutatingCommands`; verify live via
hotload → compile check → `search_tools` finds it → `call_tool` runs it.

**Regenerating the wrapper layer** (the TS schemas remain the current source for generated wrappers):

```bash
cd sbox-mcp-server && npm run build
node scripts/extract-manifest.mjs
node scripts/emit-mcp-wrappers.mjs
node scripts/audit-mcp-quality.mjs
```

Generated files carry an `AUTO-GENERATED` header and are rewritten every run — never
hand-edit them. CI gates: codegen-freshness diff + quality gate + parity + `npm test`.

**Scaffold generators** (create_* gameplay tools): model new ones on
`ScaffoldHandlers.cs`. **ALWAYS live-compile-verify generated game code** — generate →
hotload → compile check → TypeLibrary-load confirm. The verify-gate has caught real SDK
bugs every release (see API differences below). Generated SANDBOXED game code ≠
unsandboxed addon editor code.

**Release**: bump `sbox-mcp-server/package.json` + `BridgeVersion` + plugin
`plugin.json`/`.mcp.json` → `npm publish` → commit, push, tag → regen graph
(`scripts/regen-graph.ps1 -RepoRoot <repo>`) → republish the addon via the s&box
Asset Library GUI (Org sboxskinsgg, **IncludeSourceFiles=true** — false ships it broken).

## Critical lessons learned

### Addon location & compilation
- Addons go in the project's `Libraries/` folder, NOT global `sbox/addons/` (built-in only,
  silently won't compile custom code).
- s&box compiles **ALL `.cs` under `Editor/` including subfolders** — a duplicate `.cs`
  copy anywhere in the tree = `Ambiguity between 'X' and 'X'` = silent compile fail = the
  editor keeps running the OLD assembly. (`.bak` files are fine; real `.cs` dupes are not.)
- Renaming a library FOLDER doesn't disable it — s&box compiles any `Libraries/*/` with a
  `.sbproj`. To disable: rename the `.sbproj` (e.g. `.sbproj.disabled`).
- Never run TWO bridge copies at once: duplicate compiler-name crash at bootstrap, and both
  poll the same IPC dir (double-processed mutations).
- All scene APIs **must run on the main editor thread** — timer callbacks queue work for a
  static `[EditorEvent.Frame]` handler (works with the dock closed).

### s&box API differences (verified live — reflect before assuming)
- `SceneEditorSession.Active.Scene` = editor scene; `Game.ActiveScene` = play mode.
- `go.AddComponent<T>()` / `GetComponent<T>()` / `GetOrAddComponent<T>()`.
- `MeshCollider` does NOT exist — use `HullCollider`.
- `Rotation.Pitch()`, `.Yaw()`, `.Roll()` are **methods**, not properties.
- **`Sandbox.Connection` has NO `IsValid`** — null-check (`caller is null`). The v1.20.0
  gate caught generated code doing `.IsValid` at 3 sites. (`GameObject`/`Component`
  `.IsValid()` is a different, valid API.)
- `System.Math` / `System.MathF` COMPILE in game code on the current SDK (old "MathX only"
  rule is stale; `MathX` still fine). `Array.Clone()` is still whitelist-blocked — use
  `.ToArray()`. `GameObject.Clone()` is unrelated and fine.
- `Networking.MaxPlayers` is read-only; `Networking.IsHost` can throw when networking is
  inactive — check `Networking.IsActive` first.
- `Board2.Refresh(CancellationToken cancellation = default)` — bare `Refresh()` is legal;
  `camera.ScreenPixelToRay` is real, `GetMouseRay` is not; lights have no `Brightness` —
  intensity = `LightColor` magnitude.
- `IGameEvent`/`Dispatch()` come from `facepunch.libevents`, not base s&box.
- When in doubt: `describe_type` / `search_types` — reflection is the source of truth, the
  SDK moves between versions. Full schema also at `sbox.game/api`.

### Engine walls (work around, don't retry)
- **Undo is addon-inaccessible**: `FullUndoSnapshot` / `UndoSystem.Snapshot` are INERT on
  current builds (verified live). The practical answer is `checkpoint_scene` /
  `restore_checkpoint` (bridge_workflow) — snapshot before risky edits, roll back after.
- **Runtime `ParticleEffect` components don't render** through the bridge, cloud `.vpcf`
  won't load by logical path, and the addon can't reach a particle compiler. Author
  particles in the editor's particle editor; `spawn_vpcf` plays compiled ones.
- **`take_screenshot` renders from the scene's Main Camera** (one fixed angle). Use
  `screenshot_from` / `screenshot_orbit` to frame a target; `capture_view` for play mode
  (it's the only one that sees the RUNNING game + gizmos). All return inline PNGs on v2.
- **Input synthesis is partial**: `drive_player`/`playtest` drive controllers and hold
  actions, but they're no substitute for a human playtest (BRIDGE_GOTCHAS #1). Custom
  controllers may ignore WishVelocity — teleport + aim + action instead.
- Port 7269 bind can silently fail on a stale HTTP.sys registration (log shows
  `[MCP] Couldn't start`) — restart the editor after the holding process dies.
- **Engine-watch** (build when they ship): loopback multi-instance socket (multiplayer test
  harness — merged upstream 2026-07-02, not shipped), `MovieRecorder` record-to-clip,
  offline lipsync/viseme API. Tracked in `docs/TOOL_BACKLOG.md`.

### Legacy IPC (compatibility fallback retained)
- File IPC = `req_<id>.json`/`res_<id>.json` in `%TEMP%/sbox-bridge-ipc/`, UTF-8 **without
  BOM** (`new UTF8Encoding(false)` C# side; Node strips BOM as a safety net).
- `status.json` is a heartbeat (1 s refresh; >5 s stale = disconnected).
- Raw IPC is still the back door for driving a NEW handler when the in-session MCP server
  is a stale build: write the request file, poll for the response.

## Verified API quick-reference

```csharp
// Scene + objects
var scene = SceneEditorSession.Active?.Scene;          // editor (Game.ActiveScene in play)
var go = scene.CreateObject(true);
go.WorldPosition = new Vector3(x, y, z);
go.SetParent(parent, keepWorldPosition: true);
scene.Directory.FindByGuid(guid);  scene.Directory.FindByName("name");

// Components / models
var r = go.GetOrAddComponent<ModelRenderer>();
r.Model = Model.Load("models/dev/box.vmdl");
r.Tint = Color.Red;
go.Components.Create(typeDescription);                 // dynamic type

// Play mode / selection / save
Game.IsPlaying;
SceneEditorSession.Active.SetPlaying(scene);  SceneEditorSession.Active.StopPlaying();
SceneEditorSession.Active.Selection.Set(go);
SceneEditorSession.Active.FrameTo(go.GetBounds());
SceneEditorSession.Active.Save();

// Reflection / project
Game.TypeLibrary.GetType("ModelRenderer");  Game.TypeLibrary.GetTypes<Component>();
Project.Current.GetRootPath();  Project.Current.Config.Title;
```

## Known issues / TODO

- [ ] `add_sync_property` only annotates an existing property; `add_rpc_method` generates an
      empty stub (schemas say so honestly).
- [ ] `set_material_property` requires `MaterialOverride` set first.
- [ ] Map-edit convenience tools (`add_terrain_hill` etc.) assume `MapBuilder`/`CaveBuilder`/
      `ForestGenerator`-shaped components; `invoke_button` works anywhere.
- [ ] `is_playing.sessionPlaying` can read stale — trust `gameFlag`.
- [ ] Future compatibility decision: retire file IPC + the TS full surface only after older-engine fallback users have a migration path (lifeline stays).

## Bridge map (knowledge graph)

`docs/graph/` maps every tool → handler → docs. Consult `graph.json`/`graph.html` before
adding or changing a tool; regenerate each release (`scripts/regen-graph.ps1 -RepoRoot <repo>`
— `$PSScriptRoot` is empty under nested `powershell -File` calls, so pass it explicitly).
`graphify-out/` is the build dir and stays gitignored; the curated copy is `docs/graph/`.
