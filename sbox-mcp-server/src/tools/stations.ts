import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Interaction-station scaffold — a Tier-1 backlog item (mined from 2 games).
 *
 *   - add_interaction_station   an IPressable "station" prop (crafting bench /
 *                               shop till / arcade cabinet) with host-authoritative
 *                               single-occupant occupancy, a reservation grace
 *                               window, an unlock-level gate, and an overlay-open hook.
 *
 * Generates a clean, self-contained .cs and optionally attaches it to a live
 * GameObject. Scene/file-mutating; refused during play mode by the bridge dispatch.
 */
export function registerStationTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── add_interaction_station ───────────────────────────────────────
  server.tool(
    "add_interaction_station",
    "Generate a Component.IPressable 'station' prop (crafting bench / shop till / arcade cabinet) that ONE user occupies at a time. Occupancy is host-authoritative: the occupant is a [Sync(SyncFlags.FromHost)] Guid (GameObject/Connection aren't [Sync]-able) and Press() routes the claim to the host via an [Rpc.Host] Occupy(). Includes a reservation grace window (the station stays reserved for its last user for graceSeconds after they leave, so a brief walk-away can't jump the queue), an optional unlock-level gate (users below requiredLevel can't use it — wire the static ResolveUserLevel hook to your progression system to activate it), and an overlay-open hook (a static OnStationOpened(GameObject) event to open your UI, plus an opt-in [Rpc.Broadcast] mirror). Single-player safe. Optionally attached to an existing GameObject by GUID (only after a trigger_hotload). Give the prop a Collider so the player's use key can raycast it. Mined from interaction-station patterns across shipped s&box games.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'InteractionStation'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the .cs file (path override). Defaults to 'Code'"),
      graceSeconds: z
        .number()
        .optional()
        .describe("Seconds the station stays reserved for its last user after they leave, before anyone else can claim it. 0 = no grace window. Defaults to 5"),
      requiredLevel: z
        .number()
        .int()
        .optional()
        .describe("Unlock-level gate: users below this level can't use the station. 0 = no gate. The gate only bites once you wire the static ResolveUserLevel hook to your progression system. Defaults to 0"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach the station component to (only attaches if the type is already loaded — generate, trigger_hotload, then it places; otherwise add it after the hotload)"),
    },
    async (params) => {
      const res = await bridge.send("add_interaction_station", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
