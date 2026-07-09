import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Batch operation tools (Batch 52): batch_delete, batch_add_component,
 * batch_reparent. All follow the dryRun convention from batch_set_property.
 */
export function registerBatchTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── batch_delete ─────────────────────────────────────────────────
  server.tool(
    "batch_delete",
    "Delete MANY GameObjects (and their entire subtrees) in one call. ALWAYS pass dryRun:true first — it reports each object's name and child count WITHOUT deleting, so you can confirm the target list. Returns { total, succeeded, failed, dryRun, results } with per-object ok/error. Destructive and NOT undoable — get ids from find_objects or get_selected_objects and verify the dry-run before applying. Scene-mutating: refused during play mode",
    {
      ids: z
        .array(z.string())
        .describe("GUIDs of the GameObjects to delete (subtrees included)"),
      dryRun: z
        .boolean()
        .optional()
        .describe("true = report names/child counts without deleting anything. Default false — but run a dry pass first"),
    },
    async (params) => {
      const res = await bridge.send("batch_delete", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── batch_add_component ──────────────────────────────────────────
  server.tool(
    "batch_add_component",
    "Add one component type to MANY GameObjects in one call (e.g. BoxCollider on every crate). Skips objects that already have the component unless skipExisting:false. dryRun:true validates the type and reports per-object alreadyHas WITHOUT adding. Returns { total, succeeded, failed, skipped, dryRun, results }. Follow with batch_set_property to configure the new components. Scene-mutating: refused during play mode",
    {
      ids: z
        .array(z.string())
        .describe("GUIDs of the target GameObjects (from find_objects or get_selected_objects)"),
      component: z
        .string()
        .describe("Component type name to add, e.g. 'BoxCollider', 'PointLight' (search with list_available_components)"),
      skipExisting: z
        .boolean()
        .optional()
        .describe("Skip objects that already have this component type. Default true"),
      dryRun: z
        .boolean()
        .optional()
        .describe("true = validate and report without adding anything. Default false"),
    },
    async (params) => {
      const res = await bridge.send("batch_add_component", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── batch_reparent ───────────────────────────────────────────────
  server.tool(
    "batch_reparent",
    "Move MANY GameObjects under a new parent (or to the scene root) in one call — organize scattered props into a folder object, or regroup a level section. dryRun:true reports each object's current parent and destination WITHOUT moving. Returns { total, succeeded, failed, dryRun, keepWorldPosition, results }. Errors if parent is among ids (no cycles). Scene-mutating: refused during play mode",
    {
      ids: z
        .array(z.string())
        .describe("GUIDs of the GameObjects to move"),
      parent: z
        .string()
        .optional()
        .describe("GUID of the new parent GameObject. Omit (or empty) to move to the scene root"),
      keepWorldPosition: z
        .boolean()
        .optional()
        .describe("Preserve each object's world position while reparenting. Default true"),
      dryRun: z
        .boolean()
        .optional()
        .describe("true = report current parents and the destination without moving anything. Default false"),
    },
    async (params) => {
      const res = await bridge.send("batch_reparent", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
