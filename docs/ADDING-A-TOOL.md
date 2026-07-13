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

- [ ] **`search_docs` the topic FIRST**: reflection gives you the *shape*; the official
      docs give you the *idiom* (sbox.game/dev/doc = the Facepunch/sbox-docs repo). A tool
      built from reflection alone can encode the wrong workflow around the right API.
- [ ] **Toolset**: existing `bridge_*` group if it fits; new group only for a new domain.
- [ ] **Name**: snake_case, stable forever, no collision with native built-ins
      (run `node scripts/audit-mcp-quality.mjs` — collisions are SILENT tool loss).
- [ ] **XML summary**: what it does, what it returns, what to pass where next,
      destructive/surprising behavior, truncation, empty-input default.
- [ ] **XML `<param>` docs on every parameter** (they become the input schema descriptions).
- [ ] **Read `hasDefault` on `get_method_signature` output** before documenting an engine
      API's parameter as required — a listed param with a default is legally omittable
      (three shipped gotchas overstated requirements this way; BRIDGE_GOTCHAS #10).
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

## Documentation standards

On the native server, **the XML docs ARE the tool's schema** — the `<summary>` becomes the
tool description agents search and read, and each `<param>` becomes that parameter's schema
description. A vague or dishonest description is a broken tool: the agent can't find it, calls
it wrong, or trusts a promise the handler doesn't keep. The v2.0.0 description sweep drove 156
quality warnings to zero and corrected ~20 dishonest descriptions precisely because this is
load-bearing. Hold new tools to the same bar (`node scripts/audit-mcp-quality.mjs` enforces it).

### The `<summary>` must cover five things

1. **What it does** — one clear sentence, in agent-facing words, with searchable terms (agents
   find tools by `search_tools`, so name the domain: "raycast", "navmesh", "prefab").
2. **What it returns** — the real shape, with the real field names, verified against the
   handler's return statement. "Returns `{ id, name }` for matches" — not "returns the results".
3. **What to do next** — the tool it chains into. "Pass a returned id to `set_transform` /
   `add_component_with_properties`." This is how agents plan a chain.
4. **Limits & truncation** — default and max `limit`, silent caps, "capped at 500 with no
   marker that more exist". If a result can be truncated, say so.
5. **Surprising or destructive behavior** — "Destructive and NOT undoable", "refused during
   play mode", "SILENTLY overwrites existing content", "not applied by the handler". Honesty
   over polish: if a param is a no-op, label it `currently not applied by the handler`.

### Param docs

Every parameter gets an XML `<param>` line — it becomes the schema description. State the
form and where the value comes from: `GUID of the target GameObject (from get_scene_hierarchy
or find_objects)`, `World position as 'x,y,z'`, `Max results. Default 50, max 500`. Fold enum
values, vector forms, and defaults into the text. A parameter with no doc is a warning in the
quality gate.

### Good vs bad — the `find_objects` example

**Bad** (vague — unsearchable, no contract, no chain):

```csharp
/// <summary>Finds objects in the scene.</summary>
```

An agent reading this doesn't know what filters exist, what it gets back, the result cap, or
what to do with the result. It's a coin-flip whether the tool even surfaces for a relevant
`search_tools` query.

**Good** (the shipped `find_objects` description):

```csharp
/// <summary>
/// Query the scene for GameObjects by name (case-insensitive substring), component type
/// name, and/or tag — combine filters (AND). Returns {id,name} for matches (limit default
/// 50, max 500). Read-only; works during play. Use it to get GUIDs to feed into
/// align/distribute/set_tint/group/delete/etc.
/// </summary>
```

That single summary tells the agent **what it does** (filtered scene query, AND-combined),
**what it returns** (`{id,name}`), the **limits** (default 50, max 500), a **read-only/play-mode
note**, and **what to do next** (feed the GUIDs into the mutating tools). It's the standard —
match it.

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
