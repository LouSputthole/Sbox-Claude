import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Prefab tools: create_prefab, instantiate_prefab, list_prefabs, get_prefab_info.
 * Manages .prefab files — saving GameObjects as reusable templates and spawning instances.
 */
export function registerPrefabTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_prefab ─────────────────────────────────────────────────
  server.tool(
    "create_prefab",
    "Save an existing GameObject as a real .prefab file — FULL engine serialization: every component with its property values, and all children, in the same JSON format the editor writes. Returns { created, path, sourceId, components, children } — pass path to instantiate_prefab to spawn copies or get_prefab_info to inspect. Errors if the source GameObject is missing; overwrites an existing file at path.",
    {
      id: z.string().describe("GUID of the GameObject to save as prefab"),
      path: z
        .string()
        .describe(
          "Path for the prefab file relative to project root (e.g. 'prefabs/enemies/grunt.prefab')"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_prefab", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── instantiate_prefab ────────────────────────────────────────────
  server.tool(
    "instantiate_prefab",
    "Spawn a FULL prefab instance into the active scene — components and children recreated. Uses the engine's GameObject.Clone for registered prefabs, with a guid-remapped deserialize fallback for freshly-written files (repeat instantiations never collide). Returns { instantiated, prefab, method, gameObject, components, childCount } — gameObject.id is the new GUID for set_transform/set_property follow-ups. Optional name/position/rotation override the spawned root.",
    {
      path: z
        .string()
        .describe(
          "Path to the .prefab file (e.g. 'prefabs/enemies/grunt.prefab')"
        ),
      name: z
        .string()
        .optional()
        .describe("Rename the spawned root (defaults to the prefab's root name)"),
      position: z
        .union([
          z.object({
            x: z.number(),
            y: z.number(),
            z: z.number(),
          }),
          z.string().describe('Comma string "x,y,z", e.g. "0,0,64"'),
        ])
        .optional()
        .describe('World position to spawn at — object {x,y,z} or comma string "x,y,z". Defaults to origin'),
      rotation: z
        .object({
          pitch: z.number(),
          yaw: z.number(),
          roll: z.number(),
        })
        .optional()
        .describe("Rotation as euler angles. Defaults to identity"),
      scale: z
        .number()
        .optional()
        .describe("Uniform scale multiplier. Defaults to 1.0"),
      parent: z
        .string()
        .optional()
        .describe("GUID of parent GameObject to attach to"),
    },
    async (params) => {
      const res = await bridge.send("instantiate_prefab", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── list_prefabs ──────────────────────────────────────────────────
  server.tool(
    "list_prefabs",
    "List all .prefab files in the project. Filter by name or path",
    {
      filter: z
        .string()
        .optional()
        .describe("Search filter for prefab name or path"),
      maxResults: z
        .number()
        .optional()
        .describe("Maximum results to return. Defaults to 100"),
    },
    async (params) => {
      const res = await bridge.send("list_prefabs", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_prefab_info ───────────────────────────────────────────────
  server.tool(
    "get_prefab_info",
    "Inspect a prefab file as a structured summary: { path, name, size, modified, totalObjects, maxDepth, referencedPrefabs, tree } — tree is the object hierarchy with per-node component type lists (children capped at 8 per node with a truncation count). referencedPrefabs lists other .prefab files this one links to. Use before instantiate_prefab; find prefabs with list_prefabs; raw JSON via read_file if needed.",
    {
      path: z
        .string()
        .describe(
          "Path to the .prefab file (e.g. 'prefabs/enemies/grunt.prefab')"
        ),
    },
    async (params) => {
      const res = await bridge.send("get_prefab_info", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
