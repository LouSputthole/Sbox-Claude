import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * debug_draw_* / debug_clear — visualize debug primitives in the scene
 * (ported from the Claude Bridge for Unity's debug_draw family).
 *
 * Renders in BOTH modes via one ClaudeDebugDraw holder component:
 *   • edit scene → Gizmo.Draw.* — visible in the live editor viewport, but NOT
 *     in take_screenshot/screenshot_from (the editor gizmo pass isn't in that
 *     camera render). Confirm edit-mode draws with your own eyes in the editor.
 *   • play scene → Scene.DebugOverlay.* — visible in-game AND through the bridge
 *     via capture_view (which renders the running scene).
 *
 * Coords are world-space "x,y,z" strings; colors are "r,g,b" or "r,g,b,a"
 * floats 0–1. Primitives accumulate until debug_clear.
 */
export function registerDebugDrawTools(server: McpServer, bridge: BridgeClient): void {
  const reply = (res: any) =>
    res.success
      ? { content: [{ type: "text" as const, text: JSON.stringify(res.data, null, 2) }] }
      : { content: [{ type: "text" as const, text: `Error: ${res.error}` }] };

  const color = z
    .string()
    .optional()
    .describe('Color as "r,g,b" or "r,g,b,a" floats 0–1 (default varies per shape)');
  const thickness = z.number().optional().describe("Line thickness for edit-mode gizmos (default 2)");

  server.tool(
    "debug_draw_line",
    "Draw a debug line between two world points. Renders in the editor viewport (Gizmo) AND in play mode (DebugOverlay), accumulating until debug_clear. NB: edit-mode gizmos show in the live editor but NOT in take_screenshot/screenshot_from — use capture_view in play mode to see debug draws through the bridge.",
    {
      from: z.string().describe('Start point, world-space "x,y,z"'),
      to: z.string().describe('End point, world-space "x,y,z"'),
      color,
      thickness,
    },
    async (p) => reply(await bridge.send("debug_draw_line", p))
  );

  server.tool(
    "debug_draw_ray",
    "Draw a debug ray (drawn as an arrow) from an origin along a direction for a given length. Renders in editor and play. Ideal for visualizing a raycast result or a facing/normal direction. Accumulates until debug_clear. Returns { drawn: 'ray', count, mode } — count is the total accumulated primitives, mode is 'edit' or 'play' (edit-mode gizmos are NOT in take_screenshot; use capture_view in play mode).",
    {
      origin: z.string().describe('Ray origin, world-space "x,y,z"'),
      direction: z.string().describe('Direction vector "x,y,z" (normalized internally)'),
      length: z.number().optional().describe("Ray length in units (default 64)"),
      color,
      thickness,
    },
    async (p) => reply(await bridge.send("debug_draw_ray", p))
  );

  server.tool(
    "debug_draw_box",
    "Draw a wireframe debug box centered at a point. Renders in editor and play. Ideal for visualizing a trigger_zone's bounds or a physics_overlap box volume. Accumulates until debug_clear. Returns { drawn: 'box', count, mode } — count is the total accumulated primitives, mode is 'edit' or 'play' (edit-mode gizmos are NOT in take_screenshot; use capture_view in play mode).",
    {
      center: z.string().describe('Box center, world-space "x,y,z"'),
      size: z.string().optional().describe('Full size "x,y,z" in units (default "32,32,32")'),
      color,
      thickness,
    },
    async (p) => reply(await bridge.send("debug_draw_box", p))
  );

  server.tool(
    "debug_draw_sphere",
    "Draw a wireframe debug sphere at a point. Renders in editor and play. Ideal for visualizing a physics_overlap radius or an NPC's hearing/sight range. Accumulates until debug_clear. Returns { drawn: 'sphere', count, mode } — count is the total accumulated primitives, mode is 'edit' or 'play' (edit-mode gizmos are NOT in take_screenshot; use capture_view in play mode).",
    {
      center: z.string().describe('Sphere center, world-space "x,y,z"'),
      radius: z.number().optional().describe("Radius in units (default 32)"),
      color,
      thickness,
    },
    async (p) => reply(await bridge.send("debug_draw_sphere", p))
  );

  server.tool(
    "debug_clear",
    "Remove ALL debug-draw primitives at once by destroying the debug holder for the current scene (edit or play) — there is no way to remove a single shape. Call before redrawing a fresh frame of debug shapes. Returns { cleared, removed } where removed is how many primitives were destroyed.",
    {},
    async (p) => reply(await bridge.send("debug_clear", p))
  );
}
