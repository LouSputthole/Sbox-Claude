import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Scene & level-building tools (Batch 21): snap-to-ground, align, distribute,
 * grid-duplicate, and measure. Transform-level operations for arranging a
 * scene — all verifiable via the editor (screenshot or hierarchy/state).
 */

// Vector / colour accepted as EITHER an object OR a comma string, passed
// through unchanged. The C# handler parses both forms (source of truth). See
// the cross-language vector/color contract.
const Vector3Object = z.object({ x: z.number(), y: z.number(), z: z.number() });

const Vector3Schema = z
  .union([
    Vector3Object,
    z.string().describe('Comma string "x,y,z", e.g. "100,100,100"'),
  ])
  .describe('Vector — object {x,y,z} OR comma string "x,y,z"');

const ColorObject = z.object({
  r: z.number().min(0),
  g: z.number().min(0),
  b: z.number().min(0),
  a: z.number().min(0).max(1).optional(),
});

const ColorSchema = z
  .union([
    ColorObject,
    z.string().describe('Comma string "r,g,b,a", e.g. "1,0,0,1"'),
  ])
  .describe('RGBA colour — object {r,g,b,a} (0-1) OR comma string "r,g,b,a"');

export function registerLevelTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── snap_to_ground ─────────────────────────────────────────────────
  server.tool(
    "snap_to_ground",
    "Drop a GameObject straight down onto the surface below it (physics raycast). Works best on collider-less props (an object with its own collider may self-hit). Optional offset lifts it off the surface. Returns { snapped, groundZ, gameObject } with the object's updated transform — or { snapped: false, reason } (not an error) when no ground was hit below.",
    {
      id: z.string().describe("GUID of the GameObject to snap"),
      offset: z.number().optional().describe("Height above the surface to place it (default 0)"),
      startHeight: z.number().optional().describe("How far above the object to start the trace (default 2000)"),
      maxDistance: z.number().optional().describe("Max trace distance downward (default 20000)"),
    },
    async (params) => {
      const res = await bridge.send("snap_to_ground", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── align_objects ──────────────────────────────────────────────────
  server.tool(
    "align_objects",
    "Align several GameObjects on one axis so they share a coordinate. mode = first (match the first object), min, max, or average; defaults to first. Returns { aligned, axis, mode, target } — aligned is the object count and target the shared coordinate; verify positions with get_scene_hierarchy or a screenshot.",
    {
      ids: z.array(z.string()).describe("GUIDs of the GameObjects to align (>= 2)"),
      axis: z.enum(["x", "y", "z"]).describe("Axis to align on"),
      mode: z
        .enum(["first", "min", "max", "average"])
        .optional()
        .describe("Target coordinate to align to (default first)"),
    },
    async (params) => {
      const res = await bridge.send("align_objects", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── distribute_objects ─────────────────────────────────────────────
  server.tool(
    "distribute_objects",
    "Evenly space GameObjects along an axis between the lowest and highest (keeps the two ends fixed, spreads the rest evenly). Returns { distributed, axis, from, to } — the object count and the fixed end coordinates the rest were spread between.",
    {
      ids: z.array(z.string()).describe("GUIDs of the GameObjects to distribute (>= 3)"),
      axis: z.enum(["x", "y", "z"]).describe("Axis to distribute along"),
    },
    async (params) => {
      const res = await bridge.send("distribute_objects", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── grid_duplicate ─────────────────────────────────────────────────
  server.tool(
    "grid_duplicate",
    "Clone a GameObject into an X/Y/Z grid. Existing calls mutate immediately and keep the legacy result. Use dryRun:true to preview exact capped transforms and receive a planId; commit_placement_plan then clones atomically and rejects the commit if the source transform or parent changed after preview.",
    {
      id: z.string().describe("GUID of the GameObject to clone"),
      countX: z.number().int().optional().describe("Copies along X (default 1)"),
      countY: z.number().int().optional().describe("Copies along Y (default 1)"),
      countZ: z.number().int().optional().describe("Copies along Z (default 1)"),
      spacing: Vector3Schema.optional().describe("Spacing between copies per axis (default 100,100,100)"),
      dryRun: z.boolean().optional().describe("Preview only: return deterministic transforms and planId without cloning"),
    },
    async (params) => {
      const res = await bridge.send("grid_duplicate", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── commit_placement_plan ────────────────────────────────────────
  server.tool(
    "commit_placement_plan",
    "Commit a dry-run plan returned by place_along_path, grid_duplicate, or scatter_props. Plans are scene-scoped, capped, and expire after 10 minutes. Success consumes the plan; a complete rollback restores it for retry. The stored transforms are applied without rerolling randomness or repeating ground traces. Creation rolls back on failure; grid commits reject a changed/missing source before creating anything. Returns slot-to-GUID receipts.",
    {
      planId: z.string().describe("Plan id returned by a placement tool with dryRun:true"),
    },
    async (params) => {
      const res = await bridge.send("commit_placement_plan", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
  // ── measure_distance ───────────────────────────────────────────────
  server.tool(
    "measure_distance",
    "Measure the distance between two points or two GameObjects. Provide a/b as {x,y,z} or idA/idB as GUIDs. Returns straight-line distance, horizontal (ground) distance, and the delta vector. Read-only (works during play).",
    {
      a: Vector3Schema.optional().describe("First point {x,y,z}"),
      b: Vector3Schema.optional().describe("Second point {x,y,z}"),
      idA: z.string().optional().describe("First GameObject GUID (overrides a)"),
      idB: z.string().optional().describe("Second GameObject GUID (overrides b)"),
    },
    async (params) => {
      const res = await bridge.send("measure_distance", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── scatter_props ──────────────────────────────────────────────────
  server.tool(
    "scatter_props",
    "Scatter seeded model copies inside a radius. Existing calls mutate immediately and keep the legacy { scattered, groupId, seed } result. Use dryRun:true to resolve random transforms and ground traces once, returning per-slot transforms, ground status, warnings, model bounds, and planId; commit_placement_plan creates exactly that preview with rollback on failure.",
    {
      model: z.string().describe("Model path to scatter, e.g. 'models/dev/box.vmdl'"),
      center: Vector3Schema.optional().describe("Centre of the scatter area (default origin)"),
      radius: z.number().optional().describe("Scatter radius in units (default 256)"),
      count: z.number().int().optional().describe("How many to place (default 10, max 300)"),
      randomYaw: z.boolean().optional().describe("Randomly rotate each around Z (default true)"),
      snapToGround: z.boolean().optional().describe("Raycast each onto the surface below (default true)"),
      scaleMin: z.number().optional().describe("Min uniform scale (default 1)"),
      scaleMax: z.number().optional().describe("Max uniform scale (default 1; set >min for size variation)"),
      tint: ColorSchema
        .optional()
        .describe('Tint applied to every copy — object {r,g,b,a} or comma string "r,g,b,a"'),
      seed: z.number().int().optional().describe("PRNG seed for a reproducible layout (default 1)"),
      group: z.boolean().optional().describe("Parent all copies under one group object (default true)"),
      name: z.string().optional().describe("Base name for the props/group (default 'Prop')"),
      dryRun: z.boolean().optional().describe("Preview only: return deterministic transforms and planId without creating props"),
    },
    async (params) => {
      const res = await bridge.send("scatter_props", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── randomize_transforms ───────────────────────────────────────────
  server.tool(
    "randomize_transforms",
    "Add natural variation to existing objects: random yaw and/or random uniform scale within a range. Great for breaking up repetition in placed foliage/rocks/crates. Seeded — the same seed reproduces the same layout. Returns { randomized, seed } (the count of objects changed); scale only varies when scaleMax > scaleMin.",
    {
      ids: z.array(z.string()).describe("GUIDs of the GameObjects to randomize"),
      randomYaw: z.boolean().optional().describe("Randomize Z rotation (default true)"),
      scaleMin: z.number().optional().describe("Min uniform scale (default 1)"),
      scaleMax: z.number().optional().describe("Max uniform scale (default 1; set >min to vary)"),
      seed: z.number().int().optional().describe("PRNG seed (default 1)"),
    },
    async (params) => {
      const res = await bridge.send("randomize_transforms", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── group_objects ──────────────────────────────────────────────────
  server.tool(
    "group_objects",
    "Parent a set of GameObjects under a new empty group object (placed at their centroid) — tidies the hierarchy and lets you move/rotate them together.",
    {
      ids: z.array(z.string()).describe("GUIDs of the GameObjects to group"),
      name: z.string().optional().describe("Name for the group object (default 'Group')"),
    },
    async (params) => {
      const res = await bridge.send("group_objects", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
