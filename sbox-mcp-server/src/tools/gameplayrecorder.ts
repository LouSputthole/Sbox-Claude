import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Gameplay Recording family — record live PLAY-MODE gameplay to a .movie clip
 * via Sandbox.MovieMaker.MovieRecorder (shipped in the current editor build,
 * verified live 2026-07-12; closes engine-watch #2 in docs/TOOL_BACKLOG.md):
 *
 *   - record_gameplay_clip       start an async frame-loop recording job
 *   - stop_gameplay_recording    stop → persist as a project .movie asset
 *   - gameplay_recording_status  poll the job
 *
 * The counterpart to the MovieMaker playback family (list_movies /
 * add_movie_player / play_movie / stop_movie): those play clips, this CREATES
 * them from live gameplay. None of the three are scene-mutating — they must
 * stay callable during play mode, where recording lives.
 */
export function registerGameplayRecorderTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── record_gameplay_clip ──────────────────────────────────────────
  server.tool(
    "record_gameplay_clip",
    "Start recording live play-mode gameplay into a Sandbox.MovieMaker clip — REQUIRES play mode (start_play first; errors otherwise). Captures the given GameObjects (ids — recommended: small focused clips) or, when ids is omitted, the WHOLE scene (heavy: every object becomes tracks). Returns { started, jobId, sampleRate, maxSeconds, capture, discarded, note } immediately; recording runs ASYNC in the editor frame loop until stop_gameplay_recording or the maxSeconds safety cap (default 60s of clip time, max 600s). Only one recording at a time (a second call errors while active; a stopped-but-unsaved clip is discarded by a new start, reported in 'discarded'). Combine with playtest or drive_player to record a SCRIPTED run, then stop_gameplay_recording to save the .movie and play_movie to replay it.",
    {
      ids: z
        .array(z.string())
        .optional()
        .describe(
          "GameObject GUIDs to capture (from get_scene_hierarchy WHILE PLAYING — play-mode ids can differ from editor ids). Omit to capture the whole scene (heavy)"
        ),
      sampleRate: z
        .number()
        .int()
        .optional()
        .describe("Samples per second (default 30, clamped 1-120)"),
      maxSeconds: z
        .number()
        .optional()
        .describe("Safety cap — auto-stops the recording once the clip timeline reaches this many seconds (default 60, clamped 1-600). The clip stays in memory until stop_gameplay_recording saves it"),
    },
    async (params) => {
      const res = await bridge.send("record_gameplay_clip", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── stop_gameplay_recording ───────────────────────────────────────
  server.tool(
    "stop_gameplay_recording",
    "Stop the active gameplay recording and persist it as a project .movie asset the editor can load (written to Assets/<folder>/<name>.movie, registered + compiled — list_movies then shows it with hasCompiledClip). Also saves a job that already auto-stopped (maxSeconds cap, or play mode ended). Returns { saved, assetPath, durationSeconds, trackCount, sampleRate, compiled, stopReason, wired, note } — a trackCount of 0 means nothing was captured and the response warns about it. Pass wireToId to auto-wire a MoviePlayer on that GameObject pointed at the new clip (during play mode that wiring is RUNTIME-ONLY and discarded on stop_play; the .movie asset itself always persists). Errors if the target file already exists (the clip stays in memory — retry with another name). discard:true throws the recording away instead. Replay: add_movie_player + play_movie in play mode.",
    {
      name: z
        .string()
        .optional()
        .describe("Asset name without extension (default recording_<UTC timestamp>; sanitized to [A-Za-z0-9_-])"),
      folder: z
        .string()
        .optional()
        .describe('Assets subfolder to save into (default "recordings")'),
      wireToId: z
        .string()
        .optional()
        .describe("GameObject GUID to auto-wire a MoviePlayer at the new clip (runtime-only if done during play mode)"),
      discard: z
        .boolean()
        .optional()
        .describe("Throw the recording away instead of saving it"),
    },
    async (params) => {
      const res = await bridge.send("stop_gameplay_recording", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── gameplay_recording_status ─────────────────────────────────────
  server.tool(
    "gameplay_recording_status",
    "Poll the gameplay recording job. While recording returns { recording:true, jobId, elapsedSeconds (clip-timeline seconds), framesWithData, maxSeconds, sampleRate, capture, trackedObjectCount } (trackedObjectCount is -1 for whole-scene capture). After an auto-stop (maxSeconds cap / play mode ended) returns { stopped:true, pendingSave:true, reason } — the clip is in memory awaiting stop_gameplay_recording. After a save/discard returns that last summary (assetPath etc.). Read-only; works during play. No params.",
    {},
    async () => {
      const res = await bridge.send("gameplay_recording_status", {});
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
