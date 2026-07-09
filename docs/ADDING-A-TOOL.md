# Adding a new tool (v2, native MCP)

The v2 architecture makes a new tool = **one static method + XML docs** in the addon's
`Editor/` tree. No TS module, no zod schema, no npm publish, no parity audit. Discovery is
automatic: EditorTypeLibrary picks up `[McpTool]` methods on hotload, and agents find them
via the native server's `search_tools`.

Two kinds of tools:

| Kind | Where | When |
|---|---|---|
| **Wrapper over an existing bridge handler** | generated — edit the TS zod schema, re-run codegen | while the TS server still exists (v2.0.x) |
| **New native tool** | hand-written `[McpTool]` method in `Editor/Mcp/` or next to its handler family | all new tools from v2.0 on |

## The template

```csharp
using System.Threading.Tasks;
using Editor.Mcp;

/// <summary>
/// One-line: what the group is for. Shows in list_toolsets.
/// </summary>
[McpToolset( "bridge_example", "What this group of tools does, in agent-facing words." )]
public static class BridgeExampleTools
{
	/// <summary>
	/// What the tool does + WHAT IT RETURNS + what to do next. The summary is the API:
	/// "Returns matching game objects with ids, names and scene paths. Pass an id to
	/// get_game_object for full details." Mention destructive behavior, truncation,
	/// and empty-input defaults. Use searchable words — agents find tools by search_tools.
	/// </summary>
	/// <param name="id">GUID of the target GameObject (from get_scene_hierarchy or find_objects).</param>
	/// <param name="limit">Max results. Default 50.</param>
	[McpTool.ReadOnly( "example_tool" )]              // ReadOnly ONLY if it never mutates project/scene/editor state
	public static Task<object> ExampleTool( string id, int limit = 50 )
		=> McpGate.Run( "example_tool", McpGate.Args( ("id", id), ("limit", limit) ) );
}
```

Checklist per tool:

- [ ] **Toolset**: existing `bridge_*` group if it fits; new group only for a new domain.
- [ ] **Name**: snake_case, stable forever, no collision with native built-ins
      (run `node scripts/audit-mcp-quality.mjs` — collisions are SILENT tool loss).
- [ ] **XML summary**: what it does, what it returns, what to pass where next,
      destructive/surprising behavior, truncation, empty-input default.
- [ ] **XML `<param>` docs on every parameter** (they become the input schema descriptions).
- [ ] **`[McpTool.ReadOnly]` vs `[McpTool]`**: ReadOnly promises "never changes project,
      scene or editor state" — clients skip permission prompts. Moving the editor camera,
      changing selection, playing audio = NOT read-only.
- [ ] **Params**: strings for ids/paths/vectors ("x,y,z" comma form — native convention),
      C# defaults for optional params, nullable (`int?`) for optional-without-default.
- [ ] **Returns**: small labeled objects. Lists include `total`, `showing`, truncation flag.
      Screenshots return `McpResult.Image(bytes, "image/png")` — never a temp-file path.
- [ ] **Errors**: throw `Exception` with an agent-readable message (native semantics), or
      return `{ error = "..." }` from an IBridgeHandler (McpGate converts it to a throw).
- [ ] **Scene mutation in play mode**: if the tool mutates the scene, add its name to
      `_sceneMutatingCommands` in MyEditorMenu.cs so the play-mode guard covers it.
- [ ] **Verify live**: hotload → built-in `compile_status` (or `get_compile_errors`) →
      `search_tools "<name>"` finds it → `call_tool` runs it → result shape is what the
      docs promise.

## Regenerating the wrapper layer

```bash
cd sbox-mcp-server && npm run build      # freshen dist/
node scripts/extract-manifest.mjs        # TS zod schemas → scripts/tools-manifest.json
node scripts/emit-mcp-wrappers.mjs       # manifest → sbox-bridge-addon/Editor/Mcp/*.cs
node scripts/audit-mcp-quality.mjs       # collision + description quality gate
```

Generated files carry an `AUTO-GENERATED` header and are deleted+rewritten on every run —
never hand-edit them. Hand-written files in `Editor/Mcp/` (McpGate.cs,
BridgeScreenshotTools.cs) have no header and survive regeneration.

## Naming conventions

- Tools: `verb_noun` snake_case (`create_gameobject`, `find_objects`, `bake_navmesh`).
- Toolsets: `bridge_<domain>` — the `bridge_` prefix avoids colliding with the native
  built-in toolsets (asset, component, editor, log, package, play, scene).
- Never rename a shipped tool. If a rename is unavoidable, keep the old name as a second
  `[McpTool("old_name")]` method delegating to the same gate call, and note the migration
  in CHANGELOG.md.

## What NOT to build

- Anything the native built-ins already do 1:1 — check the Phase 0 inventory in
  `docs/plans/2026-07-08-native-mcp-migration.md` (52 built-ins: scene CRUD, asset io,
  packages, play control, console, undo/redo).
- Tools that dump unbounded blobs (whole scene serializations). Page or summarize instead.
