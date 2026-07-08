import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * MovieMaker / cutscene family (v1.20.0) — first bridge coverage of
 * Sandbox.MovieMaker (landed in the shipping build; verified live 2026-07-08):
 *
 *   - list_movies       enumerate the project's .movie resources
 *   - add_movie_player  wire a MoviePlayer component + MovieResource
 *   - play_movie        start playback (real playback advances in play mode)
 *   - stop_movie        stop playback (optionally rewind)
 *
 * Movies are AUTHORED in the editor's Movie Maker dock — the bridge wires and
 * plays them; it does not author keyframes. Only add_movie_player is
 * scene-mutating; play/stop stay callable during play mode.
 */
export function registerMovieMakerTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── list_movies ───────────────────────────────────────────────────
  server.tool(
    "list_movies",
    "List the project's .movie resources (Sandbox.MovieMaker clips authored in the editor's Movie Maker dock: Window → Movie Maker). Returns each movie's asset-relative path, whether it currently loads via ResourceLibrary, and whether it has a compiled clip. Start here before add_movie_player / play_movie — and if the list is empty, the movie has to be authored in the dock first (the bridge plays movies; it doesn't author keyframes).",
    {},
    async () => {
      const res = await bridge.send("list_movies", {});
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_movie_player ──────────────────────────────────────────────
  server.tool(
    "add_movie_player",
    "Add a Sandbox.MovieMaker.MoviePlayer component and optionally wire a .movie resource into it — the cutscene playback primitive. Creates a new 'Movie Player' GameObject when no id is given. Set playOnStart to begin playback the moment play mode starts (intro cinematics), or leave it and trigger via play_movie (scripted cutscenes — call it from a trigger zone or dialogue beat). isLooping + timeScale map straight onto the component. Movies must already exist as .movie assets (list_movies; author in the Movie Maker dock). Scene-mutating — refused during play mode.",
    {
      id: z
        .string()
        .optional()
        .describe("GameObject GUID to attach to. Omit to create a new 'Movie Player' object"),
      moviePath: z
        .string()
        .optional()
        .describe("Asset-relative path of the .movie resource to wire (see list_movies)"),
      isLooping: z.boolean().optional().describe("Loop playback"),
      timeScale: z
        .number()
        .optional()
        .describe("Playback speed multiplier (1 = normal)"),
      createTargets: z
        .boolean()
        .optional()
        .describe("Let the player create missing track-target objects on play"),
      playOnStart: z
        .boolean()
        .optional()
        .describe("Begin playing as soon as play mode starts (intro cinematic)"),
    },
    async (params) => {
      const res = await bridge.send("add_movie_player", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── play_movie ────────────────────────────────────────────────────
  server.tool(
    "play_movie",
    "Start MoviePlayer playback. Targets the MoviePlayer on the given GameObject, or the first MoviePlayer in the scene when id is omitted. Pass moviePath to load-and-play a different .movie on the same player; positionSeconds seeks before playing; isLooping/timeScale apply immediately. Clips genuinely advance in PLAY MODE (start_play first, then verify with capture_view) — in edit mode this only sets state, which the response calls out. NOT scene-mutating, so it works during play mode.",
    {
      id: z
        .string()
        .optional()
        .describe("GameObject GUID holding the MoviePlayer. Omit to use the first MoviePlayer in the scene"),
      moviePath: z
        .string()
        .optional()
        .describe("Asset-relative .movie path to load and play (otherwise plays the wired Resource)"),
      positionSeconds: z
        .number()
        .optional()
        .describe("Seek to this time (seconds) before playing"),
      timeScale: z
        .number()
        .optional()
        .describe("Playback speed multiplier (1 = normal)"),
      isLooping: z.boolean().optional().describe("Loop playback"),
    },
    async (params) => {
      const res = await bridge.send("play_movie", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── stop_movie ────────────────────────────────────────────────────
  server.tool(
    "stop_movie",
    "Stop MoviePlayer playback (the counterpart to play_movie). Targets the MoviePlayer on the given GameObject, or the first MoviePlayer in the scene when id is omitted. Pass rewind to also reset the playhead to 0 so the next play_movie starts from the top. Works during play mode.",
    {
      id: z
        .string()
        .optional()
        .describe("GameObject GUID holding the MoviePlayer. Omit to use the first MoviePlayer in the scene"),
      rewind: z
        .boolean()
        .optional()
        .describe("Also reset the playhead to 0"),
    },
    async (params) => {
      const res = await bridge.send("stop_movie", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
