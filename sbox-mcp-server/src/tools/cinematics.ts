import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Cinematics & Dialogue pack — two engine-proof, no-asset scaffolds (v1.20.0):
 *
 *   - create_cutscene_director  hand-authored camera-shot cutscene player
 *   - create_dialogue_system    typewriter NPC/story dialogue (Component + Razor HUD)
 *
 * These are the HAND-AUTHORED cinematic path (zero assets, full C# control). The
 * keyframed/timeline path is the MovieMaker family (add_movie_player / play_movie
 * / stop_movie / list_movies), which wires a Sandbox.MovieMaker MoviePlayer to a
 * .movie clip authored in the editor's Movie Maker dock.
 *
 * All generate self-contained, sandbox-safe, LOCAL/visual-only code; the Razor
 * output is razor_lint-safe by construction. File/scene-mutating, so refused
 * during play mode by the bridge dispatch.
 */
export function registerCinematicsTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_cutscene_director ──────────────────────────────────────
  server.tool(
    "create_cutscene_director",
    "Generate a hand-authored cutscene player component that needs NO .movie asset — the zero-asset alternative to the MovieMaker family (add_movie_player/play_movie, which play keyframed .movie clips authored in the editor's Movie Maker dock). You author the shots directly in the inspector as parallel lists: ShotPositions (Vector3), ShotAngles (pitch/yaw/roll — Angles, not raw quaternions), ShotHoldSeconds, ShotBlendSeconds, and an optional per-shot ShotLookAt GameObject (aim at a target instead of using ShotAngles). At runtime it takes over the main camera (Scene.Camera) in OnPreRender ONLY while playing — smoothstep-eased Vector3.Lerp + Rotation.Slerp between shots — captures the camera's prior transform and restores it exactly when finished (same un-apply discipline as create_camera_shake). Play from any game code via the static <Name>.Play() (first director) or <Name>.Play(\"name\") (matches CutsceneName); subscribe the static <Name>.OnCutsceneFinished, or gate game logic on the static <Name>.IsCutscenePlaying. LockInput freezes player input each frame via Input.ClearActions() while still reading the SkipAction press first so the cutscene stays skippable. Optional letterbox generates a razor_lint-safe black-bars overlay panel (host under a ScreenPanel) shown while IsCutscenePlaying. LOCAL-only (each client renders its own view) — trigger inside an [Rpc.Broadcast] for all clients. Attach to ANY GameObject; it drives the camera itself and does not need to sit on the camera. Compile with trigger_hotload afterward.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'CutsceneDirector'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated files. Defaults to 'Code'"),
      skipAction: z
        .string()
        .optional()
        .describe("Input action that ends the cutscene early (read before input is cleared, so a locked cutscene is still skippable). Sanitized to a safe token. Empty = not skippable. Defaults to 'jump'"),
      lockInput: z
        .boolean()
        .optional()
        .describe("Freeze player input during playback via Input.ClearActions() each frame. Defaults to true"),
      letterbox: z
        .boolean()
        .optional()
        .describe("Also generate a razor_lint-safe letterbox overlay (two black bars, shown while IsCutscenePlaying — host it under a ScreenPanel). Defaults to false"),
    },
    async (params) => {
      const res = await bridge.send("create_cutscene_director", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_dialogue_system ────────────────────────────────────────
  server.tool(
    "create_dialogue_system",
    "Generate an NPC/story dialogue system — a sealed state+data Component paired with a razor_lint-safe Razor HUD panel. Lines are authored in the inspector as a List<string> using the 'Speaker: text' convention (the part before the first colon is the speaker). The generated HUD panel binds to <Name>.Current automatically (no wiring) and renders the current line with a TimeSince-driven typewriter reveal at CharsPerSecond, folding the visible substring into BuildHash so it re-renders as characters appear. Press the AdvanceAction once to snap the whole line into view instantly, again to move to the next line; dismissing the last line ends the conversation. Start from any game code via the static <Name>.StartDialogue(string[] lines) or set Lines and call the instance Begin(). Static events for hooks: OnLineShown(index, speaker) — pair with add_lipsync to drive facial morphs / audio per line — and OnDialogueFinished when the conversation ends. Pairs with create_interactable to trigger dialogue on use. LOCAL-only (per-client HUD) — call StartDialogue inside an [Rpc.Broadcast] if every client should see it. Attach the panel under a ScreenPanel (add_screen_panel) so the HUD renders. Compile with trigger_hotload afterward.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated dialogue component (the HUD panel is generated as '<Name>Panel'). Defaults to 'DialogueSystem'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated files. Defaults to 'Code'"),
      charsPerSecond: z
        .number()
        .optional()
        .describe("Typewriter reveal speed in characters per second (clamped to >= 1). Defaults to 40"),
      advanceAction: z
        .string()
        .optional()
        .describe("Input action that completes the reveal / advances to the next line. Sanitized to a safe token. Defaults to 'use'"),
    },
    async (params) => {
      const res = await bridge.send("create_dialogue_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
