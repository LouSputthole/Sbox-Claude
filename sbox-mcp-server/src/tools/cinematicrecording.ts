import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Cinematic Recording pair — scripted-run capture + killcam scaffold:
 *
 *   - record_playtest   one call = scripted playtest + gameplay recording of the
 *                       SAME run. Composition of the playtest harness and the
 *                       Batch 60 MovieRecorder stack: automated regression
 *                       footage — a failing playtest comes with a replayable
 *                       clip of exactly what happened.
 *   - create_killcam    codegen scaffold (sealed Component): rolling-buffer
 *                       recording + on-death replay via MoviePlayer + a
 *                       chase-camera takeover of Scene.Camera.
 *
 * Live-verified (2026-07-13, Gravehold editor): sandboxed GAME code can
 * construct + drive MovieRecorder/MoviePlayer at runtime, and
 * MovieRecorderOptions.BufferDuration is a true rolling buffer — ~8.7s of
 * recording with a 3s buffer compiled to a clip with Duration exactly 3.00s,
 * re-based to start at 0. The official recording-api doc
 * (Facepunch/sbox-docs docs/movie-maker/recording-api.md) confirms game-code
 * recording is the supported use case and documents
 * MovieRecorderOptions.Default (a static, invisible to describe_type) +
 * record `with` syntax — the whole-scene idiom create_killcam's
 * RecordWholeScene mode generates.
 *
 * record_playtest is NOT scene-mutating (it only exists in play mode — the
 * Batch 60 recording precedent). create_killcam IS scene-mutating (writes a
 * .cs scaffold to disk, like every create_* scaffold).
 */
export function registerCinematicRecordingTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  const reply = (res: any) =>
    res.success
      ? { content: [{ type: "text" as const, text: JSON.stringify(res.data, null, 2) }] }
      : { content: [{ type: "text" as const, text: `Error: ${res.error}` }] };

  // ── record_playtest ───────────────────────────────────────────────
  server.tool(
    "record_playtest",
    [
      "Run a scripted playtest AND record the same run to a .movie clip in ONE call — automated regression footage: a failing playtest comes with a replayable clip of exactly what happened. REQUIRES play mode (start_play first).",
      "steps uses the EXACT playtest schema (one verb per step: move / look / lookDelta / action / jump / set / wait / capture / assert — see the playtest tool for the full verb reference). The recording defaults to the playtest's resolved player hierarchy; pass ids to record other objects, or nothing resolvable falls back to whole-scene capture (heavy).",
      "Returns { started, steps, recordingJobId, capture, sampleRate, clipName, folder, recorderCapSeconds, note } immediately; both jobs run ASYNC in the editor frame loop and the clip AUTO-SAVES the moment the playtest finishes (a failing or aborted run still saves its footage; play mode ending early is also saved).",
      "THE POLL CHAIN: 1) playtest_status until finished:true → the per-step pass/fail transcript. 2) gameplay_recording_status → the saved clip summary { saved, assetPath, durationSeconds, trackCount } (if it still says pendingSave, the save is a frame away — poll again; a save error there means name collision: call stop_gameplay_recording yourself with a new name).",
      "Replay the footage with add_movie_player + play_movie. Errors if a playtest or gameplay recording is already active. Only one at a time.",
    ].join("\n"),
    {
      steps: z
        .array(z.record(z.string(), z.any()))
        .describe(
          "Ordered playtest step objects — identical schema to the playtest tool (move/look/lookDelta/action/jump/set/wait/capture/assert). Runs top-to-bottom in the frame loop."
        ),
      id: z
        .string()
        .optional()
        .describe(
          "GUID of the player/controller GameObject the playtest drives. Omit to auto-resolve the first PlayerController."
        ),
      component: z
        .string()
        .optional()
        .describe(
          "Controller component type to target (e.g. 'PlayerController'). Omit to auto-detect."
        ),
      ids: z
        .array(z.string())
        .optional()
        .describe(
          "GameObject GUIDs to RECORD (from get_scene_hierarchy WHILE PLAYING). Omit to record the playtest's player hierarchy (the default and usually what you want)."
        ),
      sampleRate: z
        .number()
        .int()
        .optional()
        .describe("Recording samples per second (default 30, clamped 1-120)"),
      clipName: z
        .string()
        .optional()
        .describe(
          "Saved .movie asset name without extension (default playtest_<UTC timestamp>; sanitized to [A-Za-z0-9_-])"
        ),
      folder: z
        .string()
        .optional()
        .describe('Assets subfolder to save the clip into (default "recordings")'),
    },
    async (params) => reply(await bridge.send("record_playtest", params))
  );

  // ── create_killcam ────────────────────────────────────────────────
  server.tool(
    "create_killcam",
    [
      "Generate a sealed killcam Component: a rolling-buffer MovieRecorder keeps ONLY the last MaxBufferSeconds of a target's gameplay (BufferDuration verified live: the compiled clip's Duration equals the buffer, re-based to 0), and TriggerReplay() plays that history back through a MoviePlayer while the main camera chase-follows the target (Scene.Camera takeover in OnPreRender, restored exactly afterwards; static OnReplayFinished event + IsReplaying flag).",
      "Sandbox-safe: live-verified that GAME code can construct and drive MovieRecorder/MoviePlayer at runtime, and killcams/replays are the official recording-api use case — this is the real MovieMaker path, not a transform-history approximation. The replay REWINDS THE LIVE TARGET through its recorded past (classic killcam — the target is dead/inactive when it runs; disable a still-alive controller for the duration). wholeScene:true makes the generated component default to MovieRecorderOptions.Default (all renderers/cameras/sound points/particles — the replay rewinds everything, killer included; heavy in dense scenes), and it stays toggleable per-instance via the RecordWholeScene property.",
      "Returns { created, path, className, bufferSeconds, sampleRate, cameraDistance, cameraHeight, nextSteps }. Then: trigger_hotload → attach to a MANAGER object → set_component_reference Target to the player → arm via WatchOnStart or StartWatching() from spawn code → call TriggerReplay() from death code (pairs with create_health_system). LOCAL/visual-only — wrap in an [Rpc.Broadcast] for all clients. Refuses if the file already exists.",
    ].join("\n"),
    {
      name: z
        .string()
        .optional()
        .describe('Component class/file name (default "Killcam")'),
      directory: z
        .string()
        .optional()
        .describe('Project folder for the .cs file (default "Code")'),
      bufferSeconds: z
        .number()
        .optional()
        .describe(
          "Rolling-buffer length in seconds — the replay shows at most this much history (default 10, clamped 2-120)"
        ),
      sampleRate: z
        .number()
        .int()
        .optional()
        .describe("Recorder samples per second (default 30, clamped 1-120)"),
      cameraDistance: z
        .number()
        .optional()
        .describe("Replay chase-camera distance behind the target (default 150, clamped 10-2000)"),
      cameraHeight: z
        .number()
        .optional()
        .describe("Replay chase-camera height above the target (default 60, clamped 0-2000)"),
      wholeScene: z
        .boolean()
        .optional()
        .describe(
          "Generated default for RecordWholeScene: true = buffer the WHOLE scene via MovieRecorderOptions.Default (replay rewinds everything; heavy in dense scenes), false = only the Target hierarchy (default)"
        ),
    },
    async (params) => reply(await bridge.send("create_killcam", params))
  );
}
