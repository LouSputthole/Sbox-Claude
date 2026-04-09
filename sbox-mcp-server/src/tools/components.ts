import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Component inspection and manipulation tools.
 *
 * Registers: get_property, get_all_properties, list_available_components,
 * add_component_with_properties. These tools read/write component data on
 * GameObjects and discover available component types (both built-in and custom).
 */
export function registerComponentTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_property ─────────────────────────────────────────────────
  server.tool(
    "get_property",
    "Read a single property value from a component on a GameObject",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z
        .string()
        .describe("Component type name (e.g. 'ModelRenderer', 'PlayerController')"),
      property: z.string().describe("Property name to read"),
    },
    async (params) => {
      const res = await bridge.send("get_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_all_properties ───────────────────────────────────────────
  server.tool(
    "get_all_properties",
    "Dump all public properties of a component as JSON — names, types, and current values",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name"),
    },
    async (params) => {
      const res = await bridge.send("get_all_properties", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── list_available_components ────────────────────────────────────
  server.tool(
    "list_available_components",
    "List all component types available in s&box (built-in and custom). Search by name or filter by category",
    {
      filter: z
        .string()
        .optional()
        .describe("Search filter — matches against component name and title"),
      category: z
        .string()
        .optional()
        .describe("Filter by category/group (e.g. 'Rendering', 'Physics', 'Audio')"),
    },
    async (params) => {
      const res = await bridge.send("list_available_components", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_component_with_properties ────────────────────────────────
  server.tool(
    "add_component_with_properties",
    "Add a component to a GameObject and configure its properties in one call. Use list_available_components to find valid types",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z
        .string()
        .describe("Component type name (e.g. 'ModelRenderer', 'Rigidbody', 'BoxCollider')"),
      properties: z
        .record(z.unknown())
        .optional()
        .describe(
          "Key-value map of property names to values. Values are auto-converted to the correct type"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_component_with_properties", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
