import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient, STATUS_STALE_MS, ipcWarn } from "../transport/bridge-client.js";
import { readFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

// The running MCP server's own version (from package.json) — compared against the
// addon's reported BridgeVersion to catch a stale server vs. a newer addon (or vice
// versa), the #1 source of "the new tools aren't showing up" confusion.
let SERVER_VERSION = "unknown";
try {
  const pkg = JSON.parse(
    readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "..", "package.json"), "utf-8")
  );
  SERVER_VERSION = (pkg.version as string) ?? "unknown";
} catch {
  /* best-effort */
}

/**
 * Diagnostic and health-check tool (get_bridge_status).
 * Reports connection state, latency, IPC directory, heartbeats, and editor version.
 */
export function registerStatusTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_bridge_status ────────────────────────────────────────────
  server.tool(
    "get_bridge_status",
    "Check the s&box Bridge connection — call this FIRST in a session. Returns a human summary plus JSON: connected, roundTripOk (heartbeat can be fresh while the editor's request loop is stalled — trust roundTripOk), bridgeVersion vs mcpServerVersion + versionsAligned (a mismatch means restart Claude Code / republish the addon), handlerCount, heartbeatAgeMs, processHeartbeatAgeMs + blockedBy (editor process alive but main thread stalled = a modal dialog is open), gameAssembly (fingerprint that changes on every hotload — compare against trigger_hotload's assemblyBefore), latencyMs, and ipcDir (transport is file IPC).",
    {},
    async () => {
      const connected = bridge.isConnected();
      const ipcDir = bridge.getIpcDir();
      const st = bridge.readStatus();
      const heartbeatAgeMs = st.heartbeatMs;
      const processHeartbeatAgeMs = st.processHeartbeatMs;
      const processAlive =
        processHeartbeatAgeMs !== null && processHeartbeatAgeMs <= STATUS_STALE_MS;
      let latencyMs: number | null = null;
      let bridgeVersion: string | null = null;
      let handlerCount: number | null = null;
      let gameAssembly: unknown = null;
      let roundTripOk = false;

      if (connected) {
        latencyMs = await bridge.ping();

        // A real round-trip via the addon's get_bridge_status command — distinguishes
        // a live editor from one whose heartbeat is fresh but whose request loop is
        // stalled, and surfaces the bridge version + handler count.
        try {
          const res = await bridge.send("get_bridge_status", {}, 5000);
          roundTripOk = res.success;
          if (res.success && res.data) {
            const data = res.data as Record<string, unknown>;
            bridgeVersion = (data.version as string) ?? null;
            handlerCount = (data.handlerCount as number) ?? null;
            gameAssembly = data.gameAssembly ?? null;
          }
        } catch (err) {
          // Non-fatal — reported in the summary as roundTripOk:false.
          ipcWarn(`get_bridge_status round-trip failed: ${err instanceof Error ? err.message : String(err)}`);
        }
      }

      const versionsAligned =
        bridgeVersion == null || bridgeVersion === SERVER_VERSION;

      const status = {
        connected,
        ipcDir,
        heartbeatAgeMs,
        processHeartbeatAgeMs,
        blockedBy: st.blockedBy,
        protocolVersion: st.protocolVersion,
        roundTripOk,
        bridgeVersion,
        mcpServerVersion: SERVER_VERSION,
        versionsAligned,
        handlerCount,
        gameAssembly,
        latencyMs: connected ? latencyMs : null,
        lastPong: connected
          ? new Date(bridge.getLastPongTime()).toISOString()
          : null,
      };

      let text: string;
      if (!connected && processAlive) {
        text = `Bridge NOT responding — the editor PROCESS is alive (addon poll thread beat ${processHeartbeatAgeMs}ms ago) but its MAIN THREAD has not ticked for ${heartbeatAgeMs ?? "?"}ms${st.blockedBy ? ` (${st.blockedBy})` : ""}. This is almost always a modal dialog (e.g. "External Changes Detected") — bring the editor to the foreground and dismiss it. IPC: ${ipcDir}.`;
      } else if (!connected) {
        text = `Bridge NOT connected — no recent heartbeat in ${ipcDir}. Is s&box running with the Claude Bridge addon?`;
      } else if (roundTripOk) {
        text = `Bridge connected and responding — addon v${bridgeVersion ?? "?"} / server v${SERVER_VERSION}, ${handlerCount ?? "?"} handlers (IPC: ${ipcDir}, heartbeat ${heartbeatAgeMs ?? "?"}ms ago).${versionsAligned ? "" : ` ⚠️ Version mismatch — restart Claude Code (and/or republish the addon) so the MCP server and addon match.`}`;
      } else {
        text = `Bridge heartbeat is live but a test round-trip FAILED — the editor isn't draining requests. IPC: ${ipcDir}. Check the s&box editor console for [SboxBridge] lines.`;
      }

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

  // ── restart_editor ────────────────────────────────────────────────
  server.tool(
    "restart_editor",
    "Restart the s&box editor and wait for the bridge to reconnect — closes the C#-edit→recompile loop so addon/bridge changes apply without a manual restart. Relaunches straight back into the current project (EditorUtility.RestartEditor). Saves unsaved scenes by default (pass save:false to discard them). Blocks until the bridge is back (or waitMs elapses), then reports the handler count.",
    {
      save: z
        .boolean()
        .optional()
        .describe("Save unsaved scenes before restarting (default true; false discards them)"),
      waitMs: z
        .number()
        .int()
        .optional()
        .describe("Max ms to wait for reconnect (default 150000)"),
    },
    async (params) => {
      const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
      // Fire the restart. The editor closes mid-request, so a timeout/no-response here is EXPECTED.
      try {
        await bridge.send("restart_editor", { save: params.save ?? true }, 5000);
      } catch (err) {
        /* editor going down — expected */
        ipcWarn(`restart_editor: send failed while the editor went down (expected): ${err instanceof Error ? err.message : String(err)}`);
      }

      const waitMs = params.waitMs ?? 150000;
      const deadline = Date.now() + waitMs;
      // Let the old process actually exit (heartbeat goes stale) before checking, so we
      // don't false-positive on the pre-restart connection.
      await sleep(8000);

      while (Date.now() < deadline) {
        await sleep(2500);
        if (bridge.isConnected()) {
          // Heartbeat is fresh again — confirm the request loop drains + read the count.
          try {
            const st = await bridge.send("get_bridge_status", {}, 5000);
            if (st.success && st.data) {
              const hc = (st.data as Record<string, unknown>).handlerCount;
              return {
                content: [
                  {
                    type: "text",
                    text: `Editor restarted — bridge reconnected${hc ? ` with ${hc} handlers` : ""}.`,
                  },
                ],
              };
            }
          } catch (err) {
            /* still settling — keep polling */
            ipcWarn(`restart_editor: bridge still settling: ${err instanceof Error ? err.message : String(err)}`, true);
          }
        }
      }

      return {
        content: [
          {
            type: "text",
            text: `Restart fired, but the bridge didn't reconnect within ${waitMs}ms — the editor may still be compiling. Try get_bridge_status in a moment.`,
          },
        ],
      };
    }
  );
}
