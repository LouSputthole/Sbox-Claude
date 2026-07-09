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
    "List the project's .sound event files (recursive scan of the project root for *.sound). Returns { count, sounds } — project-relative paths ready to pass to assign_sound, play_sound_preview, or add_lipsync. NOTE: the current handler returns every match (filter/maxResults are not applied) and only covers .sound files in the project tree — use search_assets type='sound' for other sound assets",
    {
      filter: z
        .string()
        .optional()
        .describe("Search filter for sound name or path (currently not applied by the handler — all .sound files are returned)"),
      maxResults: z
        .number()
        .optional()
        .describe("Maximum results. Defaults to 50 (currently not applied by the handler)"),
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
    "Create a .sound event file wired to a source .vsnd. Returns { created, path, soundReferenced, note } (path is project-relative); errors if the file already exists. Preview the result with play_sound_preview or attach it to an object with assign_sound. Note: .sound events have no loop flag — looping lives on the SoundPointComponent that plays the event",
    {
      path: z
        .string()
        .describe(
          "Project-relative path for the sound event file (e.g. 'sounds/footstep.sound'; '.sound' appended if missing)"
        ),
      sound: z
        .string()
        .optional()
        .describe("Path to the source sound asset (.vsnd) the event plays. Omit to create an empty event and wire it later"),
      volume: z
        .number()
        .optional()
        .describe("Volume multiplier (0-1). Defaults to 1.0"),
      pitch: z
        .number()
        .optional()
        .describe("Pitch multiplier. Defaults to 1.0"),
      maxDistance: z
        .number()
        .optional()
        .describe(
          "Maximum audible distance in units (sets Distance + enables DistanceAttenuation). Omit for the engine default"
        ),
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
    "Attach a sound event to a GameObject via SoundPointComponent. Creates the component if needed. Returns { assigned, id, sound, soundLoaded, playOnStart } — soundLoaded:false means the .sound path did not resolve (the component is still added with no event; verify the path with list_sounds)",
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
          "If true, the handler calls StartSound() immediately, so the sound starts playing right away (audible in the editor)"
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
    "Play a sound in the editor for testing without entering play mode. Returns { playing, sound, volume }. Fire-and-forget via Sound.Play — there is no stop control, and the volume param is echoed back but not currently applied to playback",
    {
      sound: z
        .string()
        .describe("Sound event or asset path to preview"),
      volume: z
        .number()
        .optional()
        .describe("Preview volume (0-1). Defaults to 1.0 (echoed in the response but not currently applied to playback)"),
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
