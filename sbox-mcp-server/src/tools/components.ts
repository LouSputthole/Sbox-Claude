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
    "Dump all public properties of every component on a GameObject. Returns { id, components } where each entry is { component, properties: [{ name, type, value }] } — values are stringified (unreadable ones show '<error>'). Use the exact component/property names it reports with set_property or get_property; can be large on component-heavy objects.",
    {
      id: z.string().describe("GUID of the GameObject"),
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
    "List all instantiable component types in the TypeLibrary — built-in AND your project's custom components (abstract types excluded); filter does a substring match on the type name. Returns { count, components } with { name, title, description, fullName } per type, sorted by name — the unfiltered list is LARGE, so pass filter. Use the returned name with add_component_with_properties, and describe_type for a type's full property list.",
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
    "Add a component to a GameObject and configure its properties in one call (properties PERSIST through save+reload). Use list_available_components to find valid types. Returns appliedProperties + failedProperties so you can see exactly what stuck",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z
        .string()
        .describe("Component type name (e.g. 'ModelRenderer', 'Rigidbody', 'BoxCollider')"),
      properties: z
        .record(z.unknown())
        .optional()
        .describe(
          "Key-value map of property names to values, each auto-converted to the property's real type. Primitives '5'/true; Color/Vector3 as comma strings '1,0,0,1'; enum member names; ASSET refs as a path ('Model':'models/dev/box.vmdl', 'MaterialOverride':'materials/x.vmat'); GameObject/Component refs as a target GUID. Best-effort per key — failures are reported in failedProperties, not silently dropped"
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

  // ── invoke_method ────────────────────────────────────────────────
  server.tool(
    "invoke_method",
    "Call a public method BY NAME on a component of a live scene GameObject, passing ARGUMENTS. The with-args sibling of invoke_button (which only calls parameterless [Button]/methods on a scene component). Finds a public method matching name + arg-count, coerces each JSON arg to the parameter type (primitives/enums; Color/Vector3 as comma strings '1,0,0,1'; asset refs as a path; GameObject/Component refs as a target GUID), invokes it, and returns the method's return value as a string (null for void). Returns success=false with a clear error on resolve/coerce/throw",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z
        .string()
        .optional()
        .describe(
          "Component type name to target (e.g. 'Health', 'PlayerController'). Omit to search all components on the object for a method matching name + arg-count"
        ),
      method: z
        .string()
        .describe("Name of the public method to call (e.g. 'TakeDamage', 'AddGold')"),
      args: z
        .array(z.unknown())
        .optional()
        .describe(
          "Ordered arguments, each coerced to the matching parameter's type. Numbers/bools/strings pass through; Color/Vector3/Rotation as comma strings '1,0,0,1'; enum member names; ASSET refs as a path ('models/dev/box.vmdl'); GameObject/Component refs as a target GUID. Omit (or []) for a no-arg method"
        ),
    },
    async (params) => {
      const res = await bridge.send("invoke_method", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
