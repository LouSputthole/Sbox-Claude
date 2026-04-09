import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Diagnostic and health-check tool (get_bridge_status).
 * Reports connection state, latency, host/port, and editor version.
 */
export function registerStatusTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_bridge_status ────────────────────────────────────────────
  server.tool(
    "get_bridge_status",
    "Check the connection status to the s&box Bridge — whether it's connected, latency, host/port, and editor info. Useful for debugging",
    {},
    async () => {
      const connected = bridge.isConnected();
      let latencyMs = -1;
      let editorVersion: string | null = null;

      if (connected) {
        // Measure round-trip ping
        latencyMs = await bridge.ping();

        // Try to get editor version via project info
        try {
          const res = await bridge.send("get_project_info", {}, 5000);
          if (res.success && res.data) {
            const data = res.data as Record<string, unknown>;
            editorVersion = (data.editorVersion as string) ?? null;
          }
        } catch {
          // Non-fatal
        }
      }

      const status = {
        connected,
        host: bridge.getHost(),
        port: bridge.getPort(),
        latencyMs: connected ? latencyMs : null,
        lastPong: connected
          ? new Date(bridge.getLastPongTime()).toISOString()
          : null,
        editorVersion,
      };

      const text = connected
        ? `Bridge connected (${bridge.getHost()}:${bridge.getPort()}, ${latencyMs}ms latency)`
        : `Bridge NOT connected (${bridge.getHost()}:${bridge.getPort()}). Is s&box running?`;

      return {
        content: [
          {
            type: "text",
            text: `${text}\n\n${JSON.stringify(status, null, 2)}`,
          },
        ],
      };
    }
  );
}
