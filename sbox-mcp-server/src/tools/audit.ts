import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Project audit & batch operation tools (v2 relaunch wave 1, Batch 51):
 * find_broken_references, batch_set_property, describe_project.
 */
export function registerAuditTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── find_broken_references ───────────────────────────────────────
  server.tool(
    "find_broken_references",
    "Scan the project for broken references, two layers in one call: (1) every GameObject in the open scene — renderers with no Model (missing_model), component properties pointing at DESTROYED GameObjects/Components (dead_gameobject_ref / dead_component_ref), null component entries whose type no longer exists (missing_component); (2) every .scene/.prefab FILE — prefab references to deleted/renamed files (missing_prefab_file). Returns { total, showing, truncated, objectsScanned, filesScanned, issues } — each issue has { id, name, component, kind, detail } (file-level issues carry the file path in name). Fix missing models with assign_model, dead refs with set_property/set_component_reference, missing prefab files by fixing the path or recreating via create_prefab. Read-only; safe any time. Results cap at `limit` (default 100, max 500)",
    {
      limit: z
        .number()
        .int()
        .optional()
        .describe("Max issues to return (default 100, max 500). total still counts everything"),
      scanFiles: z
        .boolean()
        .optional()
        .describe("Include the .scene/.prefab file scan for missing prefab references. Default true"),
    },
    async (params) => {
      const res = await bridge.send("find_broken_references", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── batch_set_property ───────────────────────────────────────────
  server.tool(
    "batch_set_property",
    "Set ONE component property to the same value across MANY GameObjects in a single call (e.g. Tint on 40 props, Enabled on every light). Pass dryRun:true first to validate — it reports each object's current value and what would change WITHOUT applying anything. Returns { total, succeeded, failed, dryRun, results } with per-object ok/error and the previous value on success. Get ids from find_objects or get_selected_objects. Scene-mutating: refused during play mode. Value coercion matches set_property (numbers, bools, enums, 'x,y,z' vectors, colors, asset paths)",
    {
      ids: z
        .array(z.string())
        .describe("GUIDs of the GameObjects to modify (from find_objects, get_scene_hierarchy, or get_selected_objects)"),
      component: z
        .string()
        .describe("Component type name present on each object, e.g. 'ModelRenderer', 'PointLight'"),
      property: z
        .string()
        .describe("Property name to set, e.g. 'Tint', 'Enabled', 'LightColor' (see get_all_properties)"),
      value: z
        .any()
        .describe("New value — number, bool, string, enum name, 'x,y,z' vector, color, or asset path"),
      dryRun: z
        .boolean()
        .optional()
        .describe("true = validate and report current values without changing anything. Default false"),
    },
    async (params) => {
      const res = await bridge.send("batch_set_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── describe_project ─────────────────────────────────────────────
  server.tool(
    "describe_project",
    "One-call project orientation: identity (name/ident/org/type), the open scene with object count, scene and prefab file lists (capped at 50 each, Libraries/.sbox excluded), code footprint (.cs/.razor counts), custom Component types (up to 100, engine types excluded), and installed libraries. Returns a structured summary — orient here first, then get_scene_hierarchy for the scene, describe_type for components, find_broken_references for project health. Read-only",
    {},
    async (params) => {
      const res = await bridge.send("describe_project", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
