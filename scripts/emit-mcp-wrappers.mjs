#!/usr/bin/env node
/**
 * emit-mcp-wrappers.mjs — Phase 1 of the native-MCP migration.
 *
 * Consumes scripts/tools-manifest.json (from extract-manifest.mjs) and emits
 * C# [McpToolset] wrapper classes under sbox-bridge-addon/Editor/Mcp/, one file
 * per toolset. Every generated [McpTool] method delegates to McpGate.Run(), the
 * single hand-written entry point into the existing IBridgeHandler dispatch.
 *
 * Type mapping (see docs/plans/2026-07-08-native-mcp-migration.md):
 *   string|enum            → string        vector3/rotation/color → string ("x,y,z" form)
 *   number                 → double(?)     integer                → int(?)
 *   boolean                → bool(?)       array of primitives    → T[]
 *   object/any/mixed union → JsonNode
 *
 * Usage: node scripts/emit-mcp-wrappers.mjs [--check]
 *   --check  print stats and per-toolset table, write nothing
 */

import { readFileSync, writeFileSync, mkdirSync, readdirSync, unlinkSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, "..");
const outDir = join(repoRoot, "sbox-bridge-addon", "Editor", "Mcp");
const checkOnly = process.argv.includes("--check");

const manifest = JSON.parse(
  readFileSync(join(__dirname, "tools-manifest.json"), "utf-8")
);

// ── exclusions ─────────────────────────────────────────────────────

// MCP-server-side tools: no editor handler, they live in the lifeline server.
const SERVER_SIDE = new Set([
  "read_log", "get_compile_errors", "execute_csharp", "search_docs",
  "get_doc_page", "list_doc_categories", "run_self_test",
]);

// Exact name collisions with the native server's built-ins (Phase 0 inventory).
// Duplicate names are SILENTLY SKIPPED by ToolRegistry — never emit these.
const BUILTIN_COLLISIONS = new Set([
  "spawn_model", "list_scenes", "save_scene", "undo", "redo", "remove_component",
]);

// Hand-written in BridgeScreenshotTools.cs (McpResult.Image inline PNGs).
const HAND_WRITTEN = new Set([
  "take_screenshot", "screenshot_from", "capture_view", "screenshot_orbit",
]);

// ── toolset mapping ────────────────────────────────────────────────

// module → toolset (default). Toolsets are bridge_-prefixed to avoid colliding
// with the built-in toolset names (asset, component, editor, log, package, play, scene).
const MODULE_TOOLSET = {
  assets: "bridge_asset",
  audit: "bridge_validation",
  batch: "bridge_batch",
  workflow: "bridge_workflow",
  vehicles: "bridge_vehicle",
  audio: "bridge_audio",
  characters: "bridge_character",
  cinematics: "bridge_scaffold_polish",
  components: "bridge_component",
  debugdraw: "bridge_debug",
  debugviz: "bridge_debug",
  diagnostics: "bridge_debug",
  director: "bridge_scaffold_gameplay",
  discovery: "bridge_discovery",
  gamefeel: "bridge_scaffold_polish",
  gameobjects: "bridge_gameobject",
  gameplay: "bridge_scaffold_gameplay",
  inputs: "bridge_project",
  inspection: "bridge_validation",
  interactionpack: "bridge_scaffold_gameplay",
  leveltools: "bridge_gameobject",
  looteconomy: "bridge_scaffold_gameplay",
  materials: "bridge_material",
  moviemaker: "bridge_moviemaker",
  navigation: "bridge_navigation",
  netprimitives: "bridge_networking",
  networking: "bridge_networking",
  npc: "bridge_npc",
  objecttools: "bridge_gameobject",
  physics: "bridge_physics",
  playmode: "bridge_playmode",
  playtest: "bridge_playtest",
  prefabs: "bridge_prefab",
  project: "bridge_project",
  publishing: "bridge_project",
  roundstate: "bridge_scaffold_gameplay",
  saveslots: "bridge_scaffold_gameplay",
  scenes: "bridge_scene",
  scripts: "bridge_project",
  stations: "bridge_scaffold_gameplay",
  status: "bridge_debug",
  templates: "bridge_scaffold_gameplay",
  ui: "bridge_ui",
  uifeedback: "bridge_scaffold_polish",
  visuals: "bridge_visuals",
  world: "bridge_world",
  economysave: "bridge_scaffold_gameplay",
  statsachievements: "bridge_scaffold_gameplay",
  roundui: "bridge_scaffold_gameplay",
  worldrender: "bridge_visuals",
  aisystems: "bridge_scaffold_gameplay",
  gameplayrecorder: "bridge_moviemaker",
  dialoguefx: "bridge_scaffold_polish",
  movieauthoring: "bridge_moviemaker",
  cinematicrecording: "bridge_moviemaker",
};

// tool → toolset overrides (a tool that thematically belongs elsewhere).
const TOOL_TOOLSET = {
  invoke_button: "bridge_component",
  list_component_buttons: "bridge_component",
  set_prefab_ref: "bridge_prefab",
  recompile_asset: "bridge_asset",
  validate_project: "bridge_validation",
  simulate_input: "bridge_playtest",
  drive_player: "bridge_playtest",
  drive_player_status: "bridge_playtest",
  set_property: "bridge_component",
  get_bounds: "bridge_gameobject",
  add_component_to_new_object: "bridge_component",
  set_component_reference: "bridge_component",
  ensure_input_action: "bridge_project",
  batch_set_property: "bridge_batch",
  describe_project: "bridge_project",
  describe_scene: "bridge_scene",
  create_team_assigner: "bridge_scaffold_gameplay",
  create_idle_income: "bridge_scaffold_gameplay",
  create_round_timer_hud: "bridge_scaffold_polish",
  add_panel_buildhash: "bridge_ui",
  add_water_body: "bridge_world",
  create_utility_ai: "bridge_npc",
  create_npc_schedule_brain: "bridge_npc",
  add_tts_voice: "bridge_audio",
};

const TOOLSET_META = {
  bridge_scene: {
    class: "BridgeSceneTools",
    description:
      "Create and load scene files in the s&box editor. For saving/listing scenes use the built-in scene toolset (save_scene, list_scenes).",
  },
  bridge_gameobject: {
    class: "BridgeGameObjectTools",
    description:
      "GameObject lifecycle, hierarchy, transforms, tags, selection, and bulk layout (align, distribute, scatter, snap-to-ground, grid duplicate) in the open scene. Objects are referenced by GUID from get_scene_hierarchy or find_objects.",
  },
  bridge_component: {
    class: "BridgeComponentTools",
    description:
      "Add, configure, inspect and invoke components on GameObjects: set/get properties, wire cross-component references, call methods and editor buttons.",
  },
  bridge_prefab: {
    class: "BridgePrefabTools",
    description:
      "Create prefabs from scene objects, instantiate them, list and inspect them, and wire prefab references into component properties.",
  },
  bridge_asset: {
    class: "BridgeAssetTools",
    description:
      "Search the local asset library and sbox.game cloud assets, install packages, inspect asset metadata, copy assets with their dependency closure, and recompile assets.",
  },
  bridge_material: {
    class: "BridgeMaterialTools",
    description:
      "Assign models and materials to renderers, author .vmat materials, and set material properties.",
  },
  bridge_audio: {
    class: "BridgeAudioTools",
    description:
      "List sounds, author .sound events, attach sound components, and preview audio in the editor.",
  },
  bridge_ui: {
    class: "BridgeUiTools",
    description:
      "Generate Razor UI panels: screen-space HUDs, world-space panels, and custom PanelComponent scaffolds.",
  },
  bridge_character: {
    class: "BridgeCharacterTools",
    description:
      "Spawn and outfit citizen characters, pose and animate them, play animations, set animgraph parameters, ragdolls, lipsync, and attachments.",
  },
  bridge_physics: {
    class: "BridgePhysicsTools",
    description:
      "Rigidbodies, colliders, joints, raycasts and overlap queries in the open scene.",
  },
  bridge_visuals: {
    class: "BridgeVisualsTools",
    description:
      "Lighting, fog, post-processing, skyboxes, envmap probes, particles (.vpcf), and one-call atmosphere/look presets.",
  },
  bridge_navigation: {
    class: "BridgeNavigationTools",
    description: "Bake the navmesh and query walkable paths.",
  },
  bridge_world: {
    class: "BridgeWorldTools",
    description:
      "Terrain sculpting, hills, trails and clearings; forest painting and POIs; cave paths; and path-based object placement. Requires MapBuilder/ForestGenerator-style components in the project (invoke_button works anywhere).",
  },
  bridge_networking: {
    class: "BridgeNetworkingTools",
    description:
      "Multiplayer setup and codegen: network helpers, lobby managers, networked players, [Sync] properties, RPCs (broadcast/host/targeted), ownership, spawning, and host-migration recovery.",
  },
  bridge_npc: {
    class: "BridgeNpcTools",
    description:
      "NPC brains (state machines), spawners, patrol routes, and perception simulation.",
  },
  bridge_playmode: {
    class: "BridgePlayModeTools",
    description:
      "Enter/exit play mode, check play state, and read/write component properties on live runtime objects while playing.",
  },
  bridge_playtest: {
    class: "BridgePlaytestTools",
    description:
      "Scripted gameplay verification in play mode: run step lists with in-frame assertions (playtest), drive the player controller, and simulate input actions.",
  },
  bridge_validation: {
    class: "BridgeValidationTools",
    description:
      "Lint and validate the project: networking footguns, sandbox whitelist violations, Razor transpiler footguns, scene setup issues, publishing readiness, save-file inspection, and networked-object state dumps.",
  },
  bridge_debug: {
    class: "BridgeDebugTools",
    description:
      "Bridge health, editor restart, console commands, profiler stats, time scale, debug draw primitives (lines, boxes, spheres), and editor camera framing.",
  },
  bridge_discovery: {
    class: "BridgeDiscoveryTools",
    description:
      "Reflect over the s&box API: describe types, search types, get method signatures, list installed libraries, and search project files. Use before writing C# against unfamiliar SDK types.",
  },
  bridge_project: {
    class: "BridgeProjectTools",
    description:
      "Project info and config (.sbproj), file read/write, C# script create/edit/delete, hotload, input actions, and publishing metadata.",
  },
  bridge_scaffold_gameplay: {
    class: "BridgeScaffoldGameplayTools",
    description:
      "Generate complete, compile-verified gameplay C# components: player/NPC controllers, game managers, health, pickups, inventory, save systems, economy, loot tables, round/phase machines, interaction systems, placement mode, and more. Each tool writes a .cs file into the project; follow with trigger_hotload + compile_status.",
  },
  bridge_scaffold_polish: {
    class: "BridgeScaffoldPolishTools",
    description:
      "Generate game-feel and presentation components: camera shake, flickering lights, floating combat text, combo meters, nametags, world-panel UIs, cutscene directors, and dialogue systems.",
  },
  bridge_moviemaker: {
    class: "BridgeMovieMakerTools",
    description:
      "Wire and control Sandbox.MovieMaker cutscene playback: list .movie clips, add MoviePlayer components, play and stop clips.",
  },
  bridge_vehicle: {
    class: "BridgeVehicleTools",
    description:
      "Make things drivable: generate raycast-car controllers with built-in driver seats, standalone enter/exit seats, and physgun-style grab tools; apply arcade/drift/offroad/race handling presets to any vehicle component. Attach the generated components with batch_add_component; verify by driving in play mode.",
  },
  bridge_workflow: {
    class: "BridgeWorkflowTools",
    description:
      "Agent workflow safety net: snapshot the whole scene to temp storage before risky changes (checkpoint_scene), browse snapshots (list_checkpoints), and roll the scene back (restore_checkpoint). The undo story for bridge mutations while the engine's snapshot API stays addon-inaccessible.",
  },
  bridge_batch: {
    class: "BridgeBatchTools",
    description:
      "Bulk operations across many GameObjects in one call, with dry-run validation before applying. Get target ids from find_objects or get_selected_objects.",
  },
};

// ── read-only classification ───────────────────────────────────────

const RO_PREFIXES = /^(get_|list_|search_|find_|describe_|measure_|is_|inspect_)/;
const RO_EXTRA = new Set([
  "read_file", "raycast", "raycast_terrain", "physics_overlap", "scene_validate",
  "networking_lint", "sandbox_lint", "razor_lint", "save_inspect", "services_query",
  "playtest_status", "drive_player_status", "validate_project", "checkpoint_scene",
]);
// Tools matching an RO prefix that are NOT read-only would go here. (None today.)
const RO_DENY = new Set([]);

const isReadOnly = (name) =>
  !RO_DENY.has(name) && (RO_PREFIXES.test(name) || RO_EXTRA.has(name));

// ── C# emission helpers ────────────────────────────────────────────

const CSHARP_KEYWORDS = new Set(
  `abstract as base bool break byte case catch char checked class const continue
   decimal default delegate do double else enum event explicit extern false finally
   fixed float for foreach goto if implicit in int interface internal is lock long
   namespace new null object operator out override params private protected public
   readonly ref return sbyte sealed short sizeof stackalloc static string struct
   switch this throw true try typeof uint ulong unchecked unsafe ushort using
   virtual void volatile while`.split(/\s+/)
);

const csIdent = (name) => (CSHARP_KEYWORDS.has(name) ? "@" + name : name);

const pascal = (snake) =>
  snake.split(/[_\-]/).map((w) => w.charAt(0).toUpperCase() + w.slice(1)).join("");

const xmlEscape = (s) =>
  String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

/** Map a manifest param to { csType, defaultLit, docSuffix }. */
function mapParam(p) {
  const t = p.type;
  const opt = p.optional;
  const hasDef = "default" in p;

  const num = (cs) => {
    if (hasDef && typeof p.default === "number")
      return { csType: cs, defaultLit: cs === "int" ? String(Math.trunc(p.default)) : fmtDouble(p.default) };
    if (opt) return { csType: cs + "?", defaultLit: "null" };
    return { csType: cs, defaultLit: null };
  };

  switch (t.kind) {
    case "string":
      return {
        csType: "string",
        defaultLit: opt ? (hasDef && typeof p.default === "string" ? csStr(p.default) : "null") : null,
        docSuffix: hasDef && typeof p.default === "string" && !opt ? "" : "",
      };
    case "enum": {
      const doc = ` One of: ${t.values.join(" | ")}.`;
      return {
        csType: "string",
        defaultLit: opt ? (hasDef && typeof p.default === "string" ? csStr(p.default) : "null") : null,
        docSuffix: doc,
      };
    }
    case "number":
      return { ...num("double"), docSuffix: "" };
    case "integer":
      return { ...num("int"), docSuffix: "" };
    case "boolean": {
      if (hasDef && typeof p.default === "boolean")
        return { csType: "bool", defaultLit: String(p.default), docSuffix: "" };
      if (opt) return { csType: "bool?", defaultLit: "null", docSuffix: "" };
      return { csType: "bool", defaultLit: null, docSuffix: "" };
    }
    case "vector3":
      return { csType: "string", defaultLit: opt ? "null" : null, docSuffix: ' As "x,y,z" (or JSON {x,y,z}).' };
    case "rotation":
      return { csType: "string", defaultLit: opt ? "null" : null, docSuffix: ' As "pitch,yaw,roll" degrees.' };
    case "color":
      return { csType: "string", defaultLit: opt ? "null" : null, docSuffix: ' As "r,g,b[,a]" (0-1 floats).' };
    case "array": {
      const inner = t.items?.kind;
      if (inner === "string" || inner === "enum")
        return { csType: "string[]", defaultLit: opt ? "null" : null, docSuffix: "" };
      if (inner === "number")
        return { csType: "double[]", defaultLit: opt ? "null" : null, docSuffix: "" };
      if (inner === "integer")
        return { csType: "int[]", defaultLit: opt ? "null" : null, docSuffix: "" };
      return { csType: "JsonNode", defaultLit: opt ? "null" : null, docSuffix: " JSON array." };
    }
    default:
      return { csType: "JsonNode", defaultLit: opt ? "null" : null, docSuffix: " JSON value." };
  }
}

const fmtDouble = (n) => (Number.isInteger(n) ? String(n) : String(n));
const csStr = (s) =>
  '"' + String(s).replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\r?\n/g, "\\n") + '"';

/** Wrap doc text to ~100 cols as XML doc comment lines. */
function docLines(text, indent) {
  const words = xmlEscape(text).split(/\s+/);
  const lines = [];
  let line = "";
  for (const w of words) {
    if (line && line.length + w.length + 1 > 96) {
      lines.push(line);
      line = w;
    } else {
      line = line ? line + " " + w : w;
    }
  }
  if (line) lines.push(line);
  return lines.map((l) => `${indent}/// ${l}`).join("\n");
}

// ── build ──────────────────────────────────────────────────────────

const seen = new Set();
const toolsets = new Map(); // toolset → [tool]
let excluded = { serverSide: 0, collision: 0, handWritten: 0, dupes: 0 };

for (const tool of manifest.tools) {
  if (seen.has(tool.name)) { excluded.dupes++; continue; }
  seen.add(tool.name);
  if (SERVER_SIDE.has(tool.name)) { excluded.serverSide++; continue; }
  if (BUILTIN_COLLISIONS.has(tool.name)) { excluded.collision++; continue; }
  if (HAND_WRITTEN.has(tool.name)) { excluded.handWritten++; continue; }

  const ts = TOOL_TOOLSET[tool.name] ?? MODULE_TOOLSET[tool.module];
  if (!ts) throw new Error(`No toolset mapping for module '${tool.module}' (tool ${tool.name})`);
  if (!TOOLSET_META[ts]) throw new Error(`No TOOLSET_META for '${ts}'`);
  if (!toolsets.has(ts)) toolsets.set(ts, []);
  toolsets.get(ts).push(tool);
}

// ── stats / check mode ─────────────────────────────────────────────

let total = 0, roCount = 0;
const tableRows = [];
for (const [ts, tools] of [...toolsets.entries()].sort()) {
  const ro = tools.filter((t) => isReadOnly(t.name)).length;
  tableRows.push(`${ts.padEnd(28)} ${String(tools.length).padStart(3)} tools (${ro} read-only)`);
  total += tools.length;
  roCount += ro;
}
console.log(tableRows.join("\n"));
console.log(`\n${total} tools across ${toolsets.size} toolsets (${roCount} read-only)`);
console.log(
  `excluded: ${excluded.serverSide} server-side, ${excluded.collision} built-in collisions, ` +
  `${excluded.handWritten} hand-written (screenshots), ${excluded.dupes} dupes`
);

const nullableValueParams = [];
for (const tools of toolsets.values())
  for (const t of tools)
    for (const p of t.params) {
      const m = mapParam(p);
      if (m.csType.endsWith("?")) nullableValueParams.push(`${t.name}.${p.name}: ${m.csType}`);
    }
console.log(`nullable value-type params (verify native binding handles these): ${nullableValueParams.length}`);

if (checkOnly) process.exit(0);

// ── emit ───────────────────────────────────────────────────────────

mkdirSync(outDir, { recursive: true });

// Clean previously generated files (marker in header) so removed tools disappear.
for (const f of readdirSync(outDir)) {
  if (!f.endsWith(".cs")) continue;
  const content = readFileSync(join(outDir, f), "utf-8");
  if (content.includes("AUTO-GENERATED by scripts/emit-mcp-wrappers.mjs")) unlinkSync(join(outDir, f));
}

for (const [ts, tools] of [...toolsets.entries()].sort()) {
  const meta = TOOLSET_META[ts];
  const lines = [];
  lines.push(`// AUTO-GENERATED by scripts/emit-mcp-wrappers.mjs — DO NOT EDIT.`);
  lines.push(`// Regenerate: node scripts/extract-manifest.mjs && node scripts/emit-mcp-wrappers.mjs`);
  lines.push(`// Source of truth: sbox-mcp-server/src/tools/ (zod schemas) → scripts/tools-manifest.json`);
  lines.push(``);
  lines.push(`using System.Text.Json.Nodes;`);
  lines.push(`using System.Threading.Tasks;`);
  lines.push(`using Editor.Mcp;`);
  lines.push(``);
  lines.push(`/// <summary>`);
  lines.push(docLines(meta.description, ""));
  lines.push(`/// </summary>`);
  lines.push(`[McpToolset( "${ts}", ${csStr(meta.description)} )]`);
  lines.push(`public static class ${meta.class}`);
  lines.push(`{`);

  const sorted = [...tools].sort((a, b) => a.name.localeCompare(b.name));
  for (const tool of sorted) {
    // Required params first (C# optional params must trail).
    const req = tool.params.filter((p) => !p.optional);
    const opt = tool.params.filter((p) => p.optional);
    const ordered = [...req, ...opt];

    lines.push(`\t/// <summary>`);
    lines.push(docLines(tool.description + (tool.description.endsWith(".") ? "" : "."), "\t"));
    lines.push(`\t/// </summary>`);
    for (const p of ordered) {
      const m = mapParam(p);
      let doc = (p.description || "").trim();
      if (doc && !doc.endsWith(".")) doc += ".";
      if (m.docSuffix) doc += m.docSuffix;
      if ("default" in p && !["number", "integer", "boolean"].includes(p.type.kind))
        doc += ` Default: ${JSON.stringify(p.default)}.`;
      else if ("default" in p && ["number", "integer", "boolean"].includes(p.type.kind) && false)
        doc += "";
      lines.push(`\t/// <param name="${csIdent(p.name).replace(/^@/, "")}">${xmlEscape(doc)}</param>`);
    }

    const attr = isReadOnly(tool.name) ? `[McpTool.ReadOnly( "${tool.name}" )]` : `[McpTool( "${tool.name}" )]`;
    lines.push(`\t${attr}`);

    const sig = ordered
      .map((p) => {
        const m = mapParam(p);
        return m.defaultLit !== null
          ? `${m.csType} ${csIdent(p.name)} = ${m.defaultLit}`
          : `${m.csType} ${csIdent(p.name)}`;
      })
      .join(", ");

    const argTuples = ordered
      .map((p) => `( "${p.name}", ${csIdent(p.name)} )`)
      .join(", ");

    lines.push(`\tpublic static Task<object> ${pascal(tool.name)}( ${sig} )`.replace("(  )", "()"));
    lines.push(
      argTuples
        ? `\t\t=> McpGate.Run( "${tool.name}", McpGate.Args( ${argTuples} ) );`
        : `\t\t=> McpGate.Run( "${tool.name}", McpGate.Args() );`
    );
    lines.push(``);
  }
  if (lines[lines.length - 1] === ``) lines.pop();
  lines.push(`}`);
  lines.push(``);

  writeFileSync(join(outDir, `${meta.class}.cs`), lines.join("\n"));
}

console.log(`\nEmitted ${toolsets.size} toolset files → ${outDir}`);

// ── emit docs/TOOLSETS.md (kept in lockstep with the generated surface) ──

const doc = [];
doc.push(`# Native MCP toolsets`);
doc.push(``);
doc.push(`> AUTO-GENERATED by \`scripts/emit-mcp-wrappers.mjs\` — do not edit by hand.`);
doc.push(`> ${total + HAND_WRITTEN.size} tools across ${toolsets.size + 1} toolsets (${roCount + 1} read-only).`);
doc.push(``);
doc.push(`Agents browse these with the native server's \`list_toolsets\` / \`describe_toolset\` and`);
doc.push(`find individual tools with \`search_tools\`. **Read-only** tools carry the`);
doc.push(`\`[McpTool.ReadOnly]\` hint — clients may run them without permission prompts. Everything`);
doc.push(`else can mutate the project, scene, or editor state.`);
doc.push(``);
doc.push(`Not part of this surface: the 6 tools whose names collide with native built-ins`);
doc.push(`(${[...BUILTIN_COLLISIONS].join(", ")} — use the built-in versions), and the 7`);
doc.push(`stdio-server-side tools (${[...SERVER_SIDE].join(", ")}).`);
doc.push(`The slim \`--lifeline\` mode exposes 7 editor-down tools: that set minus execute_csharp,`);
doc.push(`plus get_bridge_status (which is also on the native surface).`);
doc.push(``);
doc.push(`| Toolset | Tools | Read-only | Purpose |`);
doc.push(`|---|---|---|---|`);
for (const [ts, tools] of [...toolsets.entries()].sort()) {
  const ro = tools.filter((t) => isReadOnly(t.name)).length;
  doc.push(`| \`${ts}\` | ${tools.length} | ${ro} | ${TOOLSET_META[ts].description.split(/\.\s/)[0].replace(/\.$/, "")}. |`);
}
doc.push(`| \`bridge_screenshot\` | 4 | 1 | Inline-PNG screenshots: main camera, framed object, free camera, multi-angle orbit (hand-written). |`);
doc.push(``);
for (const [ts, tools] of [...toolsets.entries()].sort()) {
  doc.push(`## ${ts}`);
  doc.push(``);
  doc.push(TOOLSET_META[ts].description);
  doc.push(``);
  doc.push(`| Tool | Mode | Description |`);
  doc.push(`|---|---|---|`);
  for (const t of [...tools].sort((a, b) => a.name.localeCompare(b.name))) {
    const mode = isReadOnly(t.name) ? "read" : "**mutate**";
    doc.push(`| \`${t.name}\` | ${mode} | ${t.description.replace(/\|/g, "\\|")} |`);
  }
  doc.push(``);
}
doc.push(`## bridge_screenshot`);
doc.push(``);
doc.push(`Hand-written (Editor/Mcp/BridgeScreenshotTools.cs) — every capture returns an INLINE PNG`);
doc.push(`image block via McpResult.Image, no temp-file paths to read back.`);
doc.push(``);
doc.push(`| Tool | Mode | Description |`);
doc.push(`|---|---|---|`);
doc.push(`| \`take_screenshot\` | read | Screenshot the main camera view (player view in play mode) as an inline PNG. |`);
doc.push(`| \`capture_view\` | **mutate** | Screenshot from a chosen viewpoint: auto-frame a GameObject by id, or a free position+lookAt camera (temporary camera, removed afterwards). |`);
doc.push(`| \`screenshot_from\` | **mutate** | Historical alias of capture_view (kept for existing workflows). |`);
doc.push(`| \`screenshot_orbit\` | **mutate** | Orbit a GameObject and return every angle as an inline PNG in one call. |`);
doc.push(``);

writeFileSync(join(repoRoot, "docs", "TOOLSETS.md"), doc.join("\n"));
console.log(`Emitted docs/TOOLSETS.md`);
