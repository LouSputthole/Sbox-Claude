import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Console and error feedback tools: get_console_output, get_compile_errors, clear_console.
 * Reads from the Bridge's circular log buffer (LogCapture) to surface editor output.
 */
export function registerConsoleTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_console_output ───────────────────────────────────────────
  server.tool(
    "get_console_output",
    "Read recent console log entries from s&box. Returns log messages, warnings, and errors with timestamps",
    {
      count: z
        .number()
        .optional()
        .describe("Maximum number of log entries to return. Defaults to 50"),
      severity: z
        .enum(["all", "info", "warning", "error"])
        .optional()
        .describe("Filter by severity level. Defaults to 'all'"),
    },
    async (params) => {
      const res = await bridge.send("get_console_output", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          { type: "text", text: JSON.stringify(res.data, null, 2) },
        ],
      };
    }
  );

  // ── get_compile_errors ───────────────────────────────────────────
  server.tool(
    "get_compile_errors",
    "Get current C# compilation errors and warnings from s&box. Returns file path, line number, column, error code, and message for each diagnostic",
    {},
    async () => {
      const res = await bridge.send("get_compile_errors");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }

      const data = res.data as { errors?: unknown[]; warnings?: unknown[] } | undefined;
      const errors = data?.errors ?? [];
      const warnings = data?.warnings ?? [];

      let text: string;
      if (errors.length === 0 && warnings.length === 0) {
        text = "No compilation errors or warnings. Code is clean!";
      } else {
        text = JSON.stringify(res.data, null, 2);
      }

      return {
        content: [{ type: "text", text }],
      };
    }
  );

  // ── clear_console ────────────────────────────────────────────────
  server.tool(
    "clear_console",
    "Clear all console log entries in the s&box editor",
    {},
    async () => {
      const res = await bridge.send("clear_console");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: "Console cleared" }],
      };
    }
  );
}
