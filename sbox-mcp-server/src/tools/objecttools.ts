import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Object utility & query tools (Batch 23): find objects in the scene, and
 * bulk-edit tint / model / tags across one or many objects. find_objects is
 * the composable workhorse — query GUIDs, then feed them to align/distribute/
 * set_tint/group/etc.
 */

// A colour accepted as EITHER an object {r,g,b,a} OR a comma string "r,g,b,a".
// The value is passed through to the bridge unchanged; the C# handler parses
// both forms (C# is the source of truth). See the cross-language vector/color
// contract.
const ColorObject = z.object({
  r: z.number().min(0).describe("Red, 0-1"),
  g: z.number().min(0).describe("Green, 0-1"),
  b: z.number().min(0).describe("Blue, 0-1"),
  a: z.number().min(0).max(1).optional().describe("Alpha, 0-1 (default 1)"),
});

const ColorSchema = z
  .union([
    ColorObject,
    z.string().describe('Comma string "r,g,b,a", e.g. "1,0,0,1"'),
  ])
  .describe('RGBA colour — object {r,g,b,a} (0-1) OR comma string "r,g,b,a"');

export function registerObjectTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── find_objects ───────────────────────────────────────────────────
  server.tool(
    "find_objects",
    "Query the scene for GameObjects by name (case-insensitive substring), component type name, and/or tag — combine filters (AND). Returns { count, total, showing, truncated, objects:[{id,name}] } — total is EVERY match in the scene, showing/count how many rows were returned (limit default 50, max 500), truncated:true when rows were cut so you can raise limit or narrow the filter. Read-only; works during play. Use it to get GUIDs to feed into align/distribute/set_tint/group/delete/etc.",
    {
      name: z.string().optional().describe("Name substring (case-insensitive)"),
      component: z
        .string()
        .optional()
        .describe("Component type name, e.g. 'PointLight', 'SkinnedModelRenderer'"),
      tag: z.string().optional().describe("Tag the object must have"),
      limit: z.number().int().optional().describe("Max results (default 50, max 500)"),
    },
    async (params) => {
      const res = await bridge.send("find_objects", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_tint ───────────────────────────────────────────────────────
  server.tool(
    "set_tint",
    'Set the renderer tint colour on one object (id) or many (ids) at once. Works on any ModelRenderer/SkinnedModelRenderer. Pass the colour as "tint" (or its alias "color"); each accepts an object {r,g,b,a} OR a comma string "r,g,b,a".',
    {
      id: z.string().optional().describe("Single GameObject GUID"),
      ids: z.array(z.string()).optional().describe("Multiple GameObject GUIDs"),
      tint: ColorSchema.optional().describe("Tint colour to apply (object or comma string)"),
      color: ColorSchema.optional().describe('Alias for "tint" (object or comma string)'),
    },
    async (params) => {
      // "color" is an accepted alias for "tint"; normalize to "tint" and pass
      // the value through unchanged (C# parses object-or-string).
      const { color, tint, ...rest } = params as {
        color?: unknown;
        tint?: unknown;
        [k: string]: unknown;
      };
      const tintValue = tint ?? color;
      if (tintValue === undefined) {
        return {
          content: [
            { type: "text", text: 'Error: provide "tint" (or its alias "color").' },
          ],
        };
      }
      const res = await bridge.send("set_tint", { ...rest, tint: tintValue });
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── replace_model ──────────────────────────────────────────────────
  server.tool(
    "replace_model",
    "Swap the model on one object (id) or many (ids) — e.g. retheme a row of props in one call.",
    {
      id: z.string().optional().describe("Single GameObject GUID"),
      ids: z.array(z.string()).optional().describe("Multiple GameObject GUIDs"),
      model: z.string().describe("New model path, e.g. 'models/dev/sphere.vmdl'"),
    },
    async (params) => {
      const res = await bridge.send("replace_model", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_tags ───────────────────────────────────────────────────────
  server.tool(
    "set_tags",
    "Add, remove, and/or clear gameplay tags on one object (id) or many (ids). Tags drive collision groups, queries, and triggers.",
    {
      id: z.string().optional().describe("Single GameObject GUID"),
      ids: z.array(z.string()).optional().describe("Multiple GameObject GUIDs"),
      add: z.array(z.string()).optional().describe("Tags to add"),
      remove: z.array(z.string()).optional().describe("Tags to remove"),
      clear: z.boolean().optional().describe("Remove all existing tags first"),
    },
    async (params) => {
      const res = await bridge.send("set_tags", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── remove_component ───────────────────────────────────────────────
  server.tool(
    "remove_component",
    "Remove a component from a GameObject by type name (e.g. 'PointLight', 'ModelRenderer'). Removes the first match; pass all:true to remove every matching one. The counterpart to add_component_with_properties.",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name to remove"),
      all: z.boolean().optional().describe("Remove all matching components, not just the first"),
    },
    async (params) => {
      const res = await bridge.send("remove_component", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_tags ───────────────────────────────────────────────────────
  server.tool(
    "get_tags",
    "Read the tags currently on a GameObject. (Pair with set_tags to add/remove/clear, and find_objects to query by tag.)",
    {
      id: z.string().describe("GUID of the GameObject"),
    },
    async (params) => {
      const res = await bridge.send("get_tags", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
