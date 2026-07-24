import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * GameObject lifecycle, hierarchy, and selection tools.
 *
 * Registers: create/delete/duplicate/rename_gameobject, set_parent,
 * set_enabled, set_transform, get_scene_hierarchy, get_selected_objects,
 * select_object, focus_object.
 *
 * Uses shared Zod schemas (Vector3Schema, RotationSchema) for consistent
 * Vector3 and Rotation parameter validation across multiple tools.
 */

// A 3D vector accepted as EITHER an object {x,y,z} OR a comma string "x,y,z".
// The value is passed through to the bridge unchanged; the C# handler parses
// both forms (C# is the source of truth for parsing). See the cross-language
// vector/color contract.
const Vector3Object = z.object({
  x: z.number().describe("X coordinate"),
  y: z.number().describe("Y coordinate"),
  z: z.number().describe("Z coordinate"),
});

const Vector3Schema = z
  .union([
    Vector3Object,
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('3D vector — object {x,y,z} OR comma string "x,y,z"');

const RotationSchema = z
  .union([
    z.object({
      pitch: z.number().describe("Pitch angle in degrees"),
      yaw: z.number().describe("Yaw angle in degrees"),
      roll: z.number().describe("Roll angle in degrees"),
    }),
    z.string().describe('Comma string "pitch,yaw,roll", e.g. "0,90,0"'),
  ])
  .describe('Euler rotation: object {pitch,yaw,roll} OR comma string "pitch,yaw,roll"');

export function registerGameObjectTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_gameobject ────────────────────────────────────────────
  server.tool(
    "create_gameobject",
    "Create a new GameObject in the active scene. Returns its GUID for future reference",
    {
      name: z
        .string()
        .optional()
        .describe("Display name (e.g. 'Player', 'Enemy Spawn Point'). Defaults to 'New Object'"),
      position: Vector3Schema.optional().describe("World position"),
      rotation: RotationSchema.optional().describe("World rotation"),
      scale: z
        .union([z.number(), Vector3Schema])
        .optional()
        .describe('Uniform scale (number) or per-axis scale — object {x,y,z} or comma string "x,y,z"'),
      parent: z
        .string()
        .optional()
        .describe("GUID of parent GameObject. Omit for scene root"),
    },
    async (params) => {
      const res = await bridge.send("create_gameobject", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── delete_gameobject ────────────────────────────────────────────
  server.tool(
    "delete_gameobject",
    "Delete a GameObject from the active scene by its GUID",
    {
      id: z.string().describe("GUID of the GameObject to delete"),
    },
    async (params) => {
      const res = await bridge.send("delete_gameobject", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── duplicate_gameobject ─────────────────────────────────────────
  server.tool(
    "duplicate_gameobject",
    "Clone a GameObject with all its components. Returns { duplicated, original, gameObject } — gameObject.id is the clone's new GUID; pass it to set_transform / add_component_with_properties. If offset is omitted the clone lands exactly on top of the original.",
    {
      id: z.string().describe("GUID of the GameObject to duplicate"),
      name: z.string().optional().describe("New name for the clone"),
      offset: Vector3Schema.optional().describe(
        "Position offset from original so the clone doesn't overlap"
      ),
    },
    async (params) => {
      const res = await bridge.send("duplicate_gameobject", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── rename_gameobject ────────────────────────────────────────────
  server.tool(
    "rename_gameobject",
    "Change the display name of a GameObject identified by its GUID (the GUID itself never changes, so existing references stay valid). Returns { renamed, id, oldName, newName }. Name-based lookups (e.g. find_objects) will see the new name immediately.",
    {
      id: z.string().describe("GUID of the GameObject"),
      name: z.string().describe("New display name"),
    },
    async (params) => {
      const res = await bridge.send("rename_gameobject", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_parent ───────────────────────────────────────────────────
  server.tool(
    "set_parent",
    "Reparent a GameObject. Set parentId to null or omit to move to scene root",
    {
      id: z.string().describe("GUID of the GameObject to reparent"),
      parentId: z
        .string()
        .nullable()
        .optional()
        .describe("GUID of the new parent. Null or omitted = scene root"),
    },
    async (params) => {
      const res = await bridge.send("set_parent", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_enabled ──────────────────────────────────────────────────
  server.tool(
    "set_enabled",
    "Enable or disable a GameObject (disabled objects are invisible and inactive, including their components and children). Returns { id, enabled } confirming the new state.",
    {
      id: z.string().describe("GUID of the GameObject"),
      enabled: z.boolean().describe("true to enable, false to disable"),
    },
    async (params) => {
      const res = await bridge.send("set_enabled", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_transform ────────────────────────────────────────────────
  server.tool(
    "set_transform",
    "Atomically set position, rotation, and/or scale on a GameObject. All supplied values are parsed before mutation; values apply in world space by default. Prefer space='local' or space='world'; local remains a legacy alias. Returns legacy { transformed, gameObject } plus before/after transform and bounds receipts.",
    {
      id: z.string().describe("GUID of the GameObject"),
      position: Vector3Schema.optional().describe("New position"),
      rotation: RotationSchema.optional().describe("New rotation"),
      scale: z
        .union([z.number(), Vector3Schema])
        .optional()
        .describe('New scale — uniform number, per-axis object {x,y,z}, or comma string "x,y,z"'),
      local: z
        .boolean()
        .optional()
        .describe("Legacy alias: true selects local space and false selects world space"),
      space: z
        .enum(["world", "local"])
        .optional()
        .describe("Explicit transform space. If supplied with local, both values must agree"),
    },
    async (params) => {
      const res = await bridge.send("set_transform", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_scene_hierarchy ──────────────────────────────────────────
  server.tool(
    "get_scene_hierarchy",
    "Get the scene tree — GameObjects with their names, GUIDs, components, and parent/child relationships. Pair maxDepth with rootId to drill into a subtree without paying for the whole scene",
    {
      maxDepth: z
        .number()
        .int()
        .nonnegative()
        .optional()
        .describe("Maximum recursion depth. Defaults to 10. Use 1 or 2 for cheap top-level overviews"),
      rootId: z
        .string()
        .optional()
        .describe("Optional GUID of a GameObject to start traversal from. Omit to walk from the scene roots"),
    },
    async (params) => {
      const res = await bridge.send("get_scene_hierarchy", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_selected_objects ─────────────────────────────────────────
  server.tool(
    "get_selected_objects",
    "Get the GameObjects currently selected by the user in the s&box editor. Returns { count, selected } where each entry is a serialized GameObject (id, name, enabled, position, rotation, scale, components, childCount) — use the ids with set_transform, add_component_with_properties, etc. Handy for 'do X to what I have selected' requests.",
    {},
    async () => {
      const res = await bridge.send("get_selected_objects");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── select_object ────────────────────────────────────────────────
  server.tool(
    "select_object",
    "Select a GameObject in the editor (highlights it in the hierarchy and scene view). Replaces the current selection unless addToSelection=true. Returns { selected, id }; confirm the result with get_selected_objects.",
    {
      id: z.string().describe("GUID of the GameObject to select"),
      addToSelection: z
        .boolean()
        .optional()
        .describe("If true, adds to current selection instead of replacing it"),
    },
    async (params) => {
      const res = await bridge.send("select_object", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── focus_object ─────────────────────────────────────────────────
  server.tool(
    "focus_object",
    "Highlight a GameObject by selecting it in the editor. NOTE: s&box exposes no dedicated focus API, so this only sets the selection — it does NOT move any camera (returns { focused, id, note } saying so). To actually point the viewport at an object use frame_camera; to aim a screenshot use screenshot_from.",
    {
      id: z.string().describe("GUID of the GameObject to focus"),
    },
    async (params) => {
      const res = await bridge.send("focus_object", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
