#!/usr/bin/env node

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { BridgeClient } from "./transport/bridge-client.js";
import { registerProjectTools } from "./tools/project.js";
import { registerScriptTools } from "./tools/scripts.js";
import { registerConsoleTools } from "./tools/console.js";
import { registerSceneTools } from "./tools/scenes.js";
import { registerGameObjectTools } from "./tools/gameobjects.js";
import { registerComponentTools } from "./tools/components.js";

const server = new McpServer({
  name: "sbox-mcp",
  version: "1.0.0",
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

// Start the server with stdio transport
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
