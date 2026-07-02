import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Multi-slot save manager scaffold — the slot-picker sibling of create_save_system.
 *
 *   - create_save_slots   generate a SaveSlotManager component: list / create /
 *                         load / save / delete N save slots, each with picker
 *                         metadata (name + timestamp + playtime), versioned
 *                         payloads with delete-on-version-mismatch, and an
 *                         optional GUID scene-object reconciliation on load.
 *
 * File/scene-mutating; refused during play mode by the bridge dispatch.
 */
export function registerSaveSlotsTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_save_slots ─────────────────────────────────────────────
  server.tool(
    "create_save_slots",
    "Generate a multi-slot save MANAGER component (the slot-picker sibling of create_save_system). Use this when the game needs SEVERAL named save slots the player chooses between (New Game / Load Game menu, per-character or per-run saves) — not one silent autosave. Use create_save_system instead when a single implicit save file is enough. Emits one sealed Component that lists / creates / loads / saves / deletes N slots: a lightweight manifest file (saveslots.json) holds per-slot metadata for the picker (Used flag + Name + SavedAtUnix timestamp + PlaytimeSeconds) so listing never loads a heavy payload, and each slot's game state lives in its own saveslot_<i>.json. Versioned SlotData POCO with clamp-on-load Sanitize() and delete-on-version-mismatch; runs only on the owning machine (IsProxy guard). Static OnSlotLoaded / OnSlotSaved / OnSlotDeleted hooks for HUD. Storage stays within the verified FileSystem.Data.ReadJsonOrDefault / WriteJson / DeleteFile surface (index-file pattern, no directory enumeration). Set sceneReconciliation:true to also reconcile scene objects by GameObject.Id on load — records the save marks destroyed are destroyed, survivors repositioned, missing skipped (good for a placeable-world tycoon). Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'SaveSlotManager'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      maxSlots: z
        .number()
        .int()
        .optional()
        .describe("How many save slots the manager manages (manifest is normalized to exactly this many, indexed 0..N-1). Clamped to 1..100. Defaults to 3"),
      sceneReconciliation: z
        .boolean()
        .optional()
        .describe("If true, saved records carry each object's GameObject.Id GUID and load reconciles the live scene against them (destroy the save's destroyed records via Scene.Directory.FindByGuid, reposition survivors, skip missing) — call RecordObject(go) to track a placeable. If false (default), the slot save is a plain payload with no scene reconciliation. Defaults to false"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach the manager to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_save_slots", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
