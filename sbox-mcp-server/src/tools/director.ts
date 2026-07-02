import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * AI-director scaffold — generalized L4D-style event pacing.
 *
 *   - create_event_director  generate a host-authoritative "AI director" component that
 *                            rolls on an interval, weighted-picks an event prefab,
 *                            deduplicates against the active set, spawns it, and gives
 *                            each spawned event a timed self-destruct.
 *
 * Code-gen + scene-mutating; refused during play mode by the bridge dispatch.
 */
export function registerDirectorTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_event_director ─────────────────────────────────────────
  server.tool(
    "create_event_director",
    "Generate a generalized L4D-style AI/pacing director component (host-authoritative). On a configurable interval the host rolls a weighted pick over a [Property] List<GameObject> EventPrefabs (with a parallel List<float> Weights), skips any event already active (dedupe) and anything past a MaxActive concurrency cap, clones the chosen prefab, NetworkSpawns it, and attaches a generated {name}TimedEvent companion so each spawned event self-destructs after EventLifetime seconds. Great for ambient events, waves, and world events. Single-player safe (IsProxy guard; NetworkSpawn falls back to a local clone). Fill EventPrefabs/Weights in the inspector or via the bridge after a hotload; edit the RollInterval() stub to make pacing adaptive (player-count/inactivity/time-pressure factors) per the ai-director cookbook. Optionally attached to an existing GameObject by GUID (after a hotload). NOTE: emits ONE .cs file containing two classes ({name} + {name}TimedEvent); the type only resolves after trigger_hotload.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the director (a {name}TimedEvent companion is generated alongside it). Defaults to 'EventDirector'"),
      path: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      intervalSeconds: z
        .number()
        .optional()
        .describe("Base seconds between director rolls. Defaults to 30 (clamped to >= 0.1)"),
      maxActive: z
        .number()
        .int()
        .optional()
        .describe("Maximum number of concurrently-live events. Defaults to 3 (clamped to >= 1)"),
      eventLifetime: z
        .number()
        .optional()
        .describe("Seconds before each spawned event self-destructs. Defaults to 60 (clamped to >= 0.1)"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach the director to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_event_director", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
