import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Movie Authoring family — author a .movie cutscene clip from a declarative
 * shot list, deterministically, in EDIT mode. No Movie Maker dock, no play
 * mode, no real-time waiting: the whole clip bakes inside one handler call.
 *
 *   - author_movie_clip   shot list → keyframe timeline → baked .movie asset
 *
 * The mechanism (docs/BRIDGE_GOTCHAS.md §13, flipped around): in EDIT mode
 * MovieRecorder does NOT auto-advance, so manual Advance(1/sampleRate) +
 * Capture() per synthetic frame produces EXACT manual durations. This is the
 * OFFICIAL in-editor recording idiom (sbox-docs movie-maker/recording-api.md:
 * "Instead of Start and Stop, call Advance ... and Capture to record a
 * frame"). The handler steps a camera (temp or borrowed) through hold+blend
 * segments and pumps the recorder one frame at a time. Proven live
 * 2026-07-13: positions decode back exactly (x = 100·t), and FOV IS captured
 * when the CameraComponent is explicitly targeted (WithCaptureComponent) —
 * so fovDegrees is real. E2E: a 3-shot 3.5s bake took 6ms, listed loadable,
 * played to positionSeconds=3.5 in play mode, and add_movie_player
 * createTargets:true recreated the destroyed temp camera.
 *
 * Completes the MovieMaker triangle: list/add_movie_player/play/stop PLAY
 * clips, record_gameplay_clip CAPTURES live play-mode gameplay, and this
 * AUTHORS clips from data. Scene-mutating (creates/borrows-and-moves a camera
 * and writes a project asset) — registered in _sceneMutatingCommands; it also
 * refuses play mode itself, because the recorder auto-advances there and a
 * manual bake would double-count.
 */
export function registerMovieAuthoringTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── author_movie_clip ─────────────────────────────────────────────
  server.tool(
    "author_movie_clip",
    "Author a MovieMaker .movie cutscene clip from a declarative shot list — EDIT MODE ONLY, no Movie Maker dock, no play mode, no real-time waiting (a 30s clip bakes in one call, typically <1s). Builds a hold+blend keyframe timeline from the shots (smoothstep ease by default), steps a camera through it, and hand-pumps MovieRecorder Advance/Capture per synthetic frame, then saves Assets/<folder>/<clipName>.movie (registered + compiled; errors if the file exists — the scene itself is NOT saved). Returns { authored, path, name, durationSeconds, frames, sampleRate, shots, tracks, bakeMs, compiled, loadable, camera, nextSteps }. Camera: omit cameraId for a temp camera (destroyed after the bake — play back with add_movie_player createTargets:true so the missing target is recreated), or pass cameraId of an existing camera GameObject (transform + FOV restored EXACTLY afterwards; the clip then animates THAT object on playback). fovDegrees is baked for real (the clip carries a FieldOfView track). Authored clips animate ONLY the camera the bake moves — other scene objects don't move in edit mode (that's what record_gameplay_clip is for). Total timeline capped at 120s, max 32 shots. Errors during play mode (stop_play first). Verify with list_movies; play via add_movie_player + play_movie in play mode.",
    {
      shots: z
        .array(
          z.object({
            position: z
              .string()
              .describe("Camera position for this shot as 'x,y,z'"),
            lookAt: z
              .string()
              .optional()
              .describe(
                "Aim target: a GameObject GUID (resolved to its world position at bake time) or a world point 'x,y,z'. Omit to keep the previous shot's rotation (first shot: the camera's starting rotation)"
              ),
            fovDegrees: z
              .number()
              .optional()
              .describe(
                "Field of view in degrees (clamped 5-170), baked into the clip as a FieldOfView track. Omit to carry the previous shot's FOV (first shot: the camera's current FOV)"
              ),
            holdSeconds: z
              .number()
              .optional()
              .describe("How long to hold this shot's pose (default 1, clamped 0-120)"),
            blendSeconds: z
              .number()
              .optional()
              .describe(
                "Blend time from the previous shot into this one (default 1, clamped 0-120; ignored on the first shot)"
              ),
            ease: z
              .enum(["linear", "smoothstep"])
              .optional()
              .describe("Easing of the blend INTO this shot (default smoothstep)"),
          })
        )
        .describe("The shot list in order (1-32 shots). Timeline = hold₀, then blendᵢ + holdᵢ per following shot"),
      clipName: z
        .string()
        .optional()
        .describe("Asset name without extension (default authored_<UTC timestamp>; sanitized to [A-Za-z0-9_-])"),
      folder: z
        .string()
        .optional()
        .describe('Assets subfolder to save into (default "movies")'),
      sampleRate: z
        .number()
        .int()
        .optional()
        .describe("Clip samples per second (default 30, clamped 1-120)"),
      cameraId: z
        .string()
        .optional()
        .describe(
          "GUID of an existing camera GameObject (must have a CameraComponent) to bake through — restored EXACTLY afterwards, and playback then animates that object. Omit for a temp camera that is destroyed after the bake"
        ),
    },
    async (params) => {
      const res = await bridge.send("author_movie_clip", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
