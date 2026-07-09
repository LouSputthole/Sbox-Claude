#!/usr/bin/env node

/**
 * Entry point for the sbox-mcp MCP server.
 *
 * Creates an MCP server (stdio transport), connects to the s&box Bridge Addon
 * via file-based IPC (a shared temp dir), and registers all tool handlers. Each tool domain (project,
 * scripts, console, scenes, etc.) has its own register function in src/tools/.
 *
 * CLI flags: --version / -v, --help / -h
 * Environment: SBOX_BRIDGE_IPC_DIR (the real knob); SBOX_BRIDGE_HOST / SBOX_BRIDGE_PORT (legacy, cosmetic)
 */

import { readFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { BridgeClient } from "./transport/bridge-client.js";
import { registerProjectTools } from "./tools/project.js";
import { registerScriptTools } from "./tools/scripts.js";
import { registerSceneTools } from "./tools/scenes.js";
import { registerGameObjectTools } from "./tools/gameobjects.js";
import { registerComponentTools } from "./tools/components.js";
import { registerAssetTools } from "./tools/assets.js";
import { registerMaterialTools } from "./tools/materials.js";
import { registerAudioTools } from "./tools/audio.js";
import { registerStatusTools } from "./tools/status.js";
import { registerPlayModeTools } from "./tools/playmode.js";
import { registerPrefabTools } from "./tools/prefabs.js";
import { registerPhysicsTools } from "./tools/physics.js";
import { registerUITools } from "./tools/ui.js";
import { registerTemplateTools } from "./tools/templates.js";
import { registerNetworkingTools } from "./tools/networking.js";
import { registerPublishingTools } from "./tools/publishing.js";
import { registerWorldTools } from "./tools/world.js";
import { registerDiscoveryTools } from "./tools/discovery.js";
import { registerVisualTools } from "./tools/visuals.js";
import { registerCharacterTools } from "./tools/characters.js";
import { registerLevelTools } from "./tools/leveltools.js";
import { registerObjectTools } from "./tools/objecttools.js";
import { registerDiagnosticTools } from "./tools/diagnostics.js";
import { registerDocsTools } from "./tools/docs.js";
import { registerNavigationTools } from "./tools/navigation.js";
import { registerSelfTestTools } from "./tools/selftest.js";
import { registerGameplayTools } from "./tools/gameplay.js";
import { registerNpcTools } from "./tools/npc.js";
import { registerInspectionTools } from "./tools/inspection.js";
import { registerInputTools } from "./tools/inputs.js";
import { registerDebugVizTools } from "./tools/debugviz.js";
import { registerDebugDrawTools } from "./tools/debugdraw.js";
import { registerPlaytestTools } from "./tools/playtest.js";
import { registerRoundStateTools } from "./tools/roundstate.js";
import { registerStationTools } from "./tools/stations.js";
import { registerDirectorTools } from "./tools/director.js";
import { registerSaveSlotsTools } from "./tools/saveslots.js";
import { registerGameFeelTools } from "./tools/gamefeel.js";
import { registerMovieMakerTools } from "./tools/moviemaker.js";
import { registerNetPrimitivesTools } from "./tools/netprimitives.js";
import { registerInteractionPackTools } from "./tools/interactionpack.js";
import { registerLootEconomyTools } from "./tools/looteconomy.js";
import { registerUiFeedbackTools } from "./tools/uifeedback.js";
import { registerCinematicsTools } from "./tools/cinematics.js";
import { registerAuditTools } from "./tools/audit.js";
import { registerBatchTools } from "./tools/batch.js";
import { registerWorkflowTools } from "./tools/workflow.js";
import { registerVehicleTools } from "./tools/vehicles.js";

// ── CLI flags ──────────────────────────────────────────────────────
const args = process.argv.slice(2);

/** Read the package version from package.json, or return "unknown" on failure. */
function getVersion(): string {
  try {
    const __filename = fileURLToPath(import.meta.url);
    const __dirname = dirname(__filename);
    const pkg = JSON.parse(
      readFileSync(join(__dirname, "..", "package.json"), "utf-8")
    );
    return pkg.version ?? "unknown";
  } catch {
    return "unknown";
  }
}

if (args.includes("--version") || args.includes("-v")) {
  console.log(`sbox-mcp ${getVersion()}`);
  process.exit(0);
}

if (args.includes("--help") || args.includes("-h")) {
  console.log(`sbox-mcp ${getVersion()} — MCP Server for s&box game engine

USAGE
  node dist/index.js              Start the MCP server (stdio transport)
  node dist/index.js --help       Show this help
  node dist/index.js --version    Show version

ENVIRONMENT VARIABLES
  SBOX_BRIDGE_IPC_DIR   IPC directory — MUST match the s&box addon's dir.
                        Default: <os tmpdir>/sbox-bridge-ipc
  SBOX_BRIDGE_HOST      Legacy/cosmetic — shown in get_bridge_status only
  SBOX_BRIDGE_PORT      Legacy/cosmetic — shown in get_bridge_status only

CONNECT TO CLAUDE CODE
  claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js

TOOLS (245 total / 237 editor handlers · v2: the native editor MCP server at http://127.0.0.1:7269/mcp is the primary path — this stdio server is the full-surface fallback; --lifeline exposes just the editor-down diagnostics)
  Project:     get_project_info, list_project_files, read_file, write_file
  Scripts:     create_script, edit_script, delete_script, trigger_hotload
  Scenes:      list_scenes, load_scene, save_scene, create_scene
  GameObjects: create/delete/duplicate/rename_gameobject, set_parent/enabled/transform
  Components:  get/set_property, get_all_properties, list_available_components, add_component_with_properties, set_prefab_ref
  Hierarchy:   get_scene_hierarchy (with maxDepth + rootId), get_selected_objects, select_object, focus_object
  Assets:      search_assets, list_asset_library, install_asset, get_asset_info
  Materials:   assign_model, create_material, assign_material, set_material_property
  Audio:       list_sounds, create_sound_event, assign_sound, play_sound_preview
  Play Mode:   start_play, stop_play, is_playing
  Runtime:     get/set_runtime_property, take_screenshot
  Editor:      undo, redo
  Prefabs:     create_prefab, instantiate_prefab, list_prefabs, get_prefab_info
  Physics:     add_physics, add_collider, add_joint, raycast
  UI:          create_razor_ui, add_screen_panel, add_world_panel
  Templates:   create_player_controller, create_npc_controller, create_game_manager, create_trigger_zone
  Networking:  add_network_helper, configure_network, get_network_status, network_spawn, set_ownership
  Net Scripts: add_sync_property, add_rpc_method, create_networked_player, create_lobby_manager, create_network_events
  Publishing:  get_project_config, set_project_config, validate_project, set_project_thumbnail, get_package_details
  World Gen:   invoke_button, list_component_buttons, raycast_terrain, build_terrain_mesh
  Map Edit:    add_terrain_hill/clearing/trail, clear_terrain_features, sculpt_terrain
  Caves:       add_cave_waypoint, clear_cave_path
  Forest:      add_forest_poi/trail, set_forest_seed, clear_forest_pois, paint_forest_density
  Placement:   place_along_path
  Discovery:   describe_type, search_types, get_method_signature, find_in_project
  Status:      get_bridge_status

  ── New in v1.4.0 ───────────────────────────────────
  Visual:      add_light, set_fog, add_post_process, set_skybox, add_envmap_probe, apply_atmosphere, apply_post_fx_look
  Characters:  spawn_model, spawn_citizen, dress_citizen, set_bodygroup, pose_citizen, equip_model, set_look_at, add_ragdoll, set_expression
  Scene:       snap_to_ground, align_objects, distribute_objects, grid_duplicate, measure_distance
  Environment: scatter_props, randomize_transforms, group_objects
  Utilities:   find_objects, set_tint, replace_model, set_tags
  VFX (exp):   spawn_particle, create_particle_effect, add_trail, add_beam

  ── New in v1.5.0 ───────────────────────────────────
  Diagnostics: read_log, get_compile_errors  (MCP-server-side — work even if the editor crashed)
  Camera:      screenshot_from (AIM a shot at an object/point — take_screenshot is fixed to the Main Camera), frame_camera
  Navigation:  bake_navmesh, get_navmesh_path
  Spatial:     physics_overlap (volume counterpart to raycast)
  Reflections: bake_reflections
  Particles:   spawn_vpcf (compiled .vpcf via LegacyParticleSystem — the supported particle path)
  Console/Exec: console_run, execute_csharp (experimental)
  Object utils: remove_component, get_tags
  Docs search: search_docs, get_doc_page, list_doc_categories  (MCP-server-side — official Facepunch/sbox-docs)
`);
  process.exit(0);
}

// ── Server setup ───────────────────────────────────────────────────
const server = new McpServer(
  {
    name: "sbox-mcp",
    version: getVersion(),
  },
  {
    // The `instructions` field surfaces every Claude Code session that uses this
    // server (the way other MCP servers like Supabase / TurboTax do). Use it to
    // tell Claude how to work effectively with the bridge — the disciplines that
    // are easy to skip without a reminder.
    instructions: `You are working with the s&box Claude Bridge — a file-based IPC bridge into the s&box game engine editor.

To get good results:

1. Always call \`mcp__sbox__get_bridge_status\` first to confirm the bridge addon is connected and s&box is running. If ping responds but other tools time out, the editor side isn't processing requests.

2. For visual changes (models, positions, animations, UI panels, lighting), call \`mcp__sbox__take_screenshot\` after the change and READ THE PNG yourself. You're a multimodal model — you can see the result. Guessing about visual outcomes from code alone produces long iteration loops. The screenshot tool saves to <sbox-install>/screenshots/sbox.<timestamp>.png — list the newest file and read it.

3. Before writing code that touches an unfamiliar s&box type, call \`mcp__sbox__describe_type\` or \`mcp__sbox__search_types\`. s&box's API changes between SDK versions — reflection is the source of truth, not training data.

4. \`get_scene_hierarchy\` honors \`maxDepth\` (default 10) and accepts optional \`rootId\` to traverse from a specific GameObject. Use these to avoid dumping the entire scene into a tool result.

5. Scene-mutating tools (create_gameobject, set_property, etc.) refuse during play mode and return a clear error. Stop play before making scene edits.

6. First session with the bridge (or when the user asks "how do I start?" / "what can this do?")? Offer to run setup — invoke the \`sbox-setup\` skill: it verifies the connection, detects the user's installed libraries (\`list_libraries\`), recommends a first move, and points to help + feedback.

If you're running inside Claude Code, install the companion plugin for the full workflow:
    /plugin marketplace add LouSputthole/Sbox-Claude
    /plugin install sbox-claude

The plugin ships an \`sbox-build-feature\` skill that codifies the workflow above plus a list of common s&box gotchas (MathF not available in sandbox, Cloud assets ephemeral, head bone case-sensitive, CitizenAnimationHelper.IkRightHand works at runtime, etc.). Read its SKILL.md before starting non-trivial features.`,
  },
);

// Bridge client talks to the s&box editor via file IPC. host/port are cosmetic.
const bridge = new BridgeClient(
  process.env.SBOX_BRIDGE_HOST ?? "127.0.0.1",
  parseInt(process.env.SBOX_BRIDGE_PORT ?? "29015", 10)
);

// ── Lifeline mode (v2 migration) ───────────────────────────────────
// With the native editor MCP server carrying the full tool surface, this stdio
// server's long-term job is "editor-down diagnostics": the native server dies
// with the editor; these tools read the log / docs directly and don't. --lifeline
// registers ONLY that set by filtering server.tool() — everything else is a no-op.
const LIFELINE_TOOLS = new Set([
  "read_log", "get_compile_errors", "search_docs", "get_doc_page",
  "list_doc_categories", "run_self_test", "get_bridge_status",
]);
if (args.includes("--lifeline")) {
  const realTool = server.tool.bind(server);
  (server as unknown as { tool: (...a: unknown[]) => unknown }).tool = (
    ...toolArgs: unknown[]
  ) => {
    if (LIFELINE_TOOLS.has(toolArgs[0] as string)) return realTool(...(toolArgs as Parameters<typeof realTool>));
    return undefined;
  };
}

// Register all tools
registerProjectTools(server, bridge);
registerScriptTools(server, bridge);
registerSceneTools(server, bridge);
registerGameObjectTools(server, bridge);
registerComponentTools(server, bridge);
registerAssetTools(server, bridge);
registerMaterialTools(server, bridge);
registerAudioTools(server, bridge);
registerStatusTools(server, bridge);
registerPlayModeTools(server, bridge);
registerPrefabTools(server, bridge);
registerPhysicsTools(server, bridge);
registerUITools(server, bridge);
registerTemplateTools(server, bridge);
registerNetworkingTools(server, bridge);
registerPublishingTools(server, bridge);
registerWorldTools(server, bridge);
registerDiscoveryTools(server, bridge);
registerVisualTools(server, bridge);
registerCharacterTools(server, bridge);
registerLevelTools(server, bridge);
registerObjectTools(server, bridge);
registerDiagnosticTools(server, bridge);
registerDocsTools(server, bridge);
registerNavigationTools(server, bridge);
registerSelfTestTools(server, bridge);
registerGameplayTools(server, bridge);
registerNpcTools(server, bridge);
registerInspectionTools(server, bridge);
registerInputTools(server, bridge);
registerDebugVizTools(server, bridge);
registerDebugDrawTools(server, bridge);
registerPlaytestTools(server, bridge);
registerRoundStateTools(server, bridge);
registerStationTools(server, bridge);
registerDirectorTools(server, bridge);
registerSaveSlotsTools(server, bridge);
registerGameFeelTools(server, bridge);
registerMovieMakerTools(server, bridge);
registerNetPrimitivesTools(server, bridge);
registerInteractionPackTools(server, bridge);
registerLootEconomyTools(server, bridge);
registerUiFeedbackTools(server, bridge);
registerCinematicsTools(server, bridge);
registerAuditTools(server, bridge);
registerBatchTools(server, bridge);
registerWorkflowTools(server, bridge);
registerVehicleTools(server, bridge);

/** Start the MCP server on stdio and attempt initial Bridge connection. */
async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);

  console.error("");
  console.error("  ╔═══════════════════════════════════════════════════╗");
  console.error("  ║  s&box Claude Bridge — MCP Server                ║");
  console.error("  ║  Build s&box games through conversation          ║");
  console.error("  ║                                                   ║");
  console.error("  ║  A project by sboxskins.gg                       ║");
  console.error("  ║  https://sboxskins.gg                            ║");
  console.error("  ╚═══════════════════════════════════════════════════╝");
  console.error("");

  console.error(`[sbox-mcp] IPC directory: ${bridge.getIpcDir()}`);

  // Attempt initial connection to s&box (non-fatal if it fails)
  try {
    await bridge.connect();
    console.error("[sbox-mcp] Connected to s&box Bridge");
  } catch {
    console.error(
      "[sbox-mcp] Warning: Could not connect to s&box Bridge. Will retry on first tool call."
    );
  }
}

main().catch((err) => {
  console.error("[sbox-mcp] Fatal error:", err);
  process.exit(1);
});
