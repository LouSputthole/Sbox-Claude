import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Audio tools: list_sounds, create_sound_event, assign_sound, play_sound_preview.
 * Manages sound assets, .sound event files, and SoundPointComponent attachment.
 */
export function registerAudioTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── list_sounds ──────────────────────────────────────────────────
  server.tool(
    "list_sounds",
    "List available sound assets in the project and installed packages. Filter by name",
    {
      filter: z
        .string()
        .optional()
        .describe("Search filter for sound name or path"),
      maxResults: z
        .number()
        .optional()
        .describe("Maximum results. Defaults to 50"),
    },
    async (params) => {
      const res = await bridge.send("list_sounds", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_sound_event ───────────────────────────────────────────
  server.tool(
    "create_sound_event",
    "Create a new sound event file (.sound) with volume, pitch, distance falloff, and looping settings",
    {
      path: z
        .string()
        .describe(
          "Path for the sound event file (e.g. 'sounds/footstep.sound')"
        ),
      sound: z
        .string()
        .describe("Path to the source sound asset (.vsnd)"),
      volume: z
        .number()
        .optional()
        .describe("Volume multiplier (0-1). Defaults to 1.0"),
      pitch: z
        .number()
        .optional()
        .describe("Pitch multiplier. Defaults to 1.0"),
      minDistance: z
        .number()
        .optional()
        .describe(
          "Minimum distance before falloff starts (units). Defaults to 100"
        ),
      maxDistance: z
        .number()
        .optional()
        .describe(
          "Maximum audible distance (units). Defaults to 2000"
        ),
      loop: z
        .boolean()
        .optional()
        .describe("Whether the sound should loop. Defaults to false"),
    },
    async (params) => {
      const res = await bridge.send("create_sound_event", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── assign_sound ─────────────────────────────────────────────────
  server.tool(
    "assign_sound",
    "Attach a sound event to a GameObject via SoundPointComponent. Creates the component if needed",
    {
      id: z.string().describe("GUID of the GameObject"),
      sound: z
        .string()
        .describe(
          "Sound event path (e.g. 'sounds/ambient_wind.sound')"
        ),
      playOnStart: z
        .boolean()
        .optional()
        .describe(
          "Whether the sound plays automatically when the game starts"
        ),
    },
    async (params) => {
      const res = await bridge.send("assign_sound", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── play_sound_preview ───────────────────────────────────────────
  server.tool(
    "play_sound_preview",
    "Play a sound in the editor for testing without entering play mode",
    {
      sound: z
        .string()
        .describe("Sound event or asset path to preview"),
      volume: z
        .number()
        .optional()
        .describe("Preview volume (0-1). Defaults to 1.0"),
    },
    async (params) => {
      const res = await bridge.send("play_sound_preview", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
