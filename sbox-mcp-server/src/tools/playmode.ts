import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Play mode control and related editor tools.
 *
 * Beyond play/pause/resume/stop, this file also registers: set_property
 * (editor-mode property writes), get/set_runtime_property (live tweaking
 * during play mode), take_screenshot, and undo/redo.
 */
export function registerPlayModeTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── start_play ───────────────────────────────────────────────────
  server.tool(
    "start_play",
    "Enter play mode — starts running the game in the editor. Scripts execute, physics simulate, everything goes live",
    {},
    async () => {
      const res = await bridge.send("start_play");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── stop_play ────────────────────────────────────────────────────
  server.tool(
    "stop_play",
    "Exit play mode — stops the game and returns to editor. All runtime changes are discarded",
    {},
    async () => {
      const res = await bridge.send("stop_play");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── pause_play ───────────────────────────────────────────────────
  server.tool(
    "pause_play",
    "Pause the running game — freezes simulation but stays in play mode. Use resume_play to continue",
    {},
    async () => {
      const res = await bridge.send("pause_play");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── resume_play ──────────────────────────────────────────────────
  server.tool(
    "resume_play",
    "Resume a paused game — unfreezes simulation",
    {},
    async () => {
      const res = await bridge.send("resume_play");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── is_playing ───────────────────────────────────────────────────
  server.tool(
    "is_playing",
    "Check current play state — returns 'playing', 'paused', or 'stopped'",
    {},
    async () => {
      const res = await bridge.send("is_playing");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── take_screenshot ──────────────────────────────────────────────
  server.tool(
    "take_screenshot",
    "Capture the current editor viewport as a PNG screenshot",
    {
      path: z
        .string()
        .optional()
        .describe(
          "Output path (e.g. 'screenshots/test.png'). Defaults to screenshots/screenshot_{timestamp}.png"
        ),
    },
    async (params) => {
      const res = await bridge.send("take_screenshot", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── undo ─────────────────────────────────────────────────────────
  server.tool(
    "undo",
    "Undo the last editor action. Safety net for when a change goes wrong",
    {},
    async () => {
      const res = await bridge.send("undo");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── redo ─────────────────────────────────────────────────────────
  server.tool(
    "redo",
    "Redo the last undone editor action",
    {},
    async () => {
      const res = await bridge.send("redo");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_property ─────────────────────────────────────────────────
  server.tool(
    "set_property",
    "Set a property value on a component (editor mode). Reads the value back to confirm",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name"),
      property: z.string().describe("Property name to set"),
      value: z.unknown().describe("New value — auto-converted to the correct type"),
    },
    async (params) => {
      const res = await bridge.send("set_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_runtime_property ─────────────────────────────────────────
  server.tool(
    "get_runtime_property",
    "Read a component property value during play mode. Must call start_play first",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name"),
      property: z.string().describe("Property name to read"),
    },
    async (params) => {
      const res = await bridge.send("get_runtime_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_runtime_property ─────────────────────────────────────────
  server.tool(
    "set_runtime_property",
    "Set a component property value during play mode — tweak values live while the game runs",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name"),
      property: z.string().describe("Property name to set"),
      value: z.unknown().describe("New value"),
    },
    async (params) => {
      const res = await bridge.send("set_runtime_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
