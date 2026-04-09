import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

const Vector3Schema = z
  .object({
    x: z.number().describe("X coordinate"),
    y: z.number().describe("Y coordinate"),
    z: z.number().describe("Z coordinate"),
  })
  .describe("3D vector with x, y, z components");

const RotationSchema = z
  .object({
    pitch: z.number().describe("Pitch angle in degrees"),
    yaw: z.number().describe("Yaw angle in degrees"),
    roll: z.number().describe("Roll angle in degrees"),
  })
  .describe("Euler rotation with pitch, yaw, roll in degrees");

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
        .describe("Uniform scale (number) or per-axis scale (Vector3)"),
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
    "Clone a GameObject with all its components. Optionally offset position or rename",
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
    "Change the display name of a GameObject",
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
    "Enable or disable a GameObject (disabled objects are invisible and inactive)",
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
    "Set position, rotation, and/or scale on a GameObject. Only provided values are changed",
    {
      id: z.string().describe("GUID of the GameObject"),
      position: Vector3Schema.optional().describe("New position"),
      rotation: RotationSchema.optional().describe("New rotation"),
      scale: z
        .union([z.number(), Vector3Schema])
        .optional()
        .describe("New scale — uniform number or per-axis Vector3"),
      local: z
        .boolean()
        .optional()
        .describe("If true, values are in local space. Default is world space"),
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
    "Get the full scene tree — all GameObjects with their names, GUIDs, components, positions, and parent/child relationships. This is how you 'see' the scene",
    {
      maxDepth: z
        .number()
        .optional()
        .describe("Maximum depth of the tree to return. Defaults to 10"),
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
    "Get the GameObjects currently selected by the user in the s&box editor",
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
    "Select a GameObject in the editor (highlights it in the hierarchy and scene view)",
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
    "Move the editor camera to focus on a specific GameObject (like double-clicking in the hierarchy)",
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
