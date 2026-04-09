#!/usr/bin/env node

/**
 * Entry point for the sbox-mcp MCP server.
 *
 * Creates an MCP server (stdio transport), connects to the s&box Bridge Addon
 * via WebSocket, and registers all tool handlers. Each tool domain (project,
 * scripts, console, scenes, etc.) has its own register function in src/tools/.
 *
 * CLI flags: --version / -v, --help / -h
 * Environment: SBOX_BRIDGE_HOST, SBOX_BRIDGE_PORT
 */

import { readFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { BridgeClient } from "./transport/bridge-client.js";
import { registerProjectTools } from "./tools/project.js";
import { registerScriptTools } from "./tools/scripts.js";
import { registerConsoleTools } from "./tools/console.js";
import { registerSceneTools } from "./tools/scenes.js";
import { registerGameObjectTools } from "./tools/gameobjects.js";
import { registerComponentTools } from "./tools/components.js";
import { registerAssetTools } from "./tools/assets.js";
import { registerMaterialTools } from "./tools/materials.js";
import { registerAudioTools } from "./tools/audio.js";
import { registerStatusTools } from "./tools/status.js";
import { registerPlayModeTools } from "./tools/playmode.js";

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
  SBOX_BRIDGE_HOST    Bridge WebSocket host (default: 127.0.0.1)
  SBOX_BRIDGE_PORT    Bridge WebSocket port (default: 29015)

CONNECT TO CLAUDE CODE
  claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js

TOOLS (53 total)
  Project:     get_project_info, list_project_files, read_file, write_file
  Scripts:     create_script, edit_script, delete_script, trigger_hotload
  Console:     get_console_output, get_compile_errors, clear_console
  Scenes:      list_scenes, load_scene, save_scene, create_scene
  GameObjects: create/delete/duplicate/rename_gameobject, set_parent/enabled/transform
  Components:  get/set_property, get_all_properties, list_available_components, add_component_with_properties
  Hierarchy:   get_scene_hierarchy, get_selected_objects, select_object, focus_object
  Assets:      search_assets, list_asset_library, install_asset, get_asset_info
  Materials:   assign_model, create_material, assign_material, set_material_property
  Audio:       list_sounds, create_sound_event, assign_sound, play_sound_preview
  Play Mode:   start/stop/pause/resume_play, is_playing
  Runtime:     get/set_runtime_property, take_screenshot
  Editor:      undo, redo
  Status:      get_bridge_status
`);
  process.exit(0);
}

// ── Server setup ───────────────────────────────────────────────────
const server = new McpServer({
  name: "sbox-mcp",
  version: getVersion(),
});

// Bridge client connects to s&box editor via WebSocket
const bridge = new BridgeClient(
  process.env.SBOX_BRIDGE_HOST ?? "127.0.0.1",
  parseInt(process.env.SBOX_BRIDGE_PORT ?? "29015", 10)
);

// Register all tools
registerProjectTools(server, bridge);
registerScriptTools(server, bridge);
registerConsoleTools(server, bridge);
registerSceneTools(server, bridge);
registerGameObjectTools(server, bridge);
registerComponentTools(server, bridge);
registerAssetTools(server, bridge);
registerMaterialTools(server, bridge);
registerAudioTools(server, bridge);
registerStatusTools(server, bridge);
registerPlayModeTools(server, bridge);

/** Start the MCP server on stdio and attempt initial Bridge connection. */
async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);

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
