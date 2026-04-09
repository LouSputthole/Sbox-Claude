import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Scene management tools: list_scenes, load_scene, save_scene, create_scene.
 * create_scene generates .scene JSON with optional default objects (camera, light, ground).
 */
export function registerSceneTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── list_scenes ──────────────────────────────────────────────────
  server.tool(
    "list_scenes",
    "List all .scene files in the project with their paths and basic metadata",
    {},
    async () => {
      const res = await bridge.send("list_scenes");
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

  // ── load_scene ───────────────────────────────────────────────────
  server.tool(
    "load_scene",
    "Open a scene in the s&box editor by its path",
    {
      path: z
        .string()
        .describe(
          "Relative path to the .scene file (e.g. 'scenes/main.scene')"
        ),
    },
    async (params) => {
      const res = await bridge.send("load_scene", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          { type: "text", text: `Scene loaded: ${params.path}` },
        ],
      };
    }
  );

  // ── save_scene ───────────────────────────────────────────────────
  server.tool(
    "save_scene",
    "Save the currently open scene in the s&box editor",
    {
      path: z
        .string()
        .optional()
        .describe(
          "Path to save to. If omitted, saves to the current scene's path"
        ),
    },
    async (params) => {
      const res = await bridge.send("save_scene", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: "Scene saved" }],
      };
    }
  );

  // ── create_scene ─────────────────────────────────────────────────
  server.tool(
    "create_scene",
    "Create a new empty scene file. Optionally include basic objects like a camera, directional light, and ground plane",
    {
      path: z
        .string()
        .describe(
          "Relative path for the new scene (e.g. 'scenes/level_01.scene')"
        ),
      name: z.string().optional().describe("Display name for the scene"),
      includeDefaults: z
        .boolean()
        .optional()
        .describe(
          "If true, includes a Camera, Directional Light, and ground plane. Defaults to true"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_scene", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          {
            type: "text",
            text: `Scene created: ${params.path}`,
          },
        ],
      };
    }
  );
}
