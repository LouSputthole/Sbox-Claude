import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Round-flow & UI pack — three tools:
 *
 *   - create_round_timer_hud   Razor screen HUD showing round/phase time remaining;
 *                              binds at runtime (TypeLibrary reflection) to whichever
 *                              shipped round machine exists — no code coupling
 *   - scaffold_map_vote_flow   end-of-round map vote: [Rpc.Host] ballots, [Sync]
 *                              NetList tallies, countdown, deterministic tie-break,
 *                              winner -> Scene.LoadFromFile, plus a Razor vote panel
 *   - add_panel_buildhash      FILE-EDIT tool (not a scaffold): patches an existing
 *                              .razor to add the BuildHash() override razor_lint
 *                              flags as missing
 *
 * All are file-mutating (refused during play mode by the bridge dispatch). The
 * Razor output is razor_lint-safe by construction (BuildHash override, no
 * switch-expressions or non-ASCII in @code, class-selector SCSS roots), modeled
 * on create_leaderboard_panel / the UI-feedback pack.
 */
export function registerRoundUiTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_round_timer_hud ────────────────────────────────────────
  server.tool(
    "create_round_timer_hud",
    "Generate a Razor screen-panel HUD (.razor + .razor.scss) showing the active round/phase time remaining as mm:ss with the phase/state name above it. NO code coupling to your round machine: at runtime it discovers one by TypeLibrary property reflection — same-GameObject components first, then the whole scene, re-scanning every 2s while unbound — matching either shipped machine shape: create_round_phase_machine output (a [Sync] TimeUntil 'PhaseTimer' + 'CurrentPhase' enum for the label) or create_round_state_machine output (a manager with 'StateIndex' + 'Current', whose active state carries a [Sync] TimeUntil 'TimeLeft' + 'Identifier'). First matching component wins; any hand-written machine exposing those member names also binds. Adaptive BuildHash folds the WHOLE second remaining so the panel re-renders at 1 Hz, not every frame. Optional low-time warning: at/below lowTimeSeconds the clock gets the 'low' CSS class (red by default). Shows '--:--' (and fades out via the 'unbound' class) until a machine exists. Returns { created, razorPath, scssPath, className, lowTimeSeconds, note, nextSteps }. Renders NOTHING without a ScreenPanel host: follow with trigger_hotload, then add_screen_panel, then add_component_with_properties (component=className) on the same object; verify with capture_view (renderUI=true) in play mode.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated panel. Defaults to 'RoundTimerHud'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .razor + .razor.scss. Defaults to 'Code/UI'"),
      lowTimeSeconds: z
        .number()
        .optional()
        .describe("Remaining-seconds threshold at/below which the clock gets the 'low' warning class (clamped to >= 0; 0 disables). Editable per-instance via the LowTimeSeconds [Property]. Defaults to 10"),
    },
    async (params) => {
      const res = await bridge.send("create_round_timer_hud", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── scaffold_map_vote_flow ────────────────────────────────────────
  server.tool(
    "scaffold_map_vote_flow",
    "Scaffold an end-of-round map vote. Three files: <Name>.cs (sealed host-authoritative controller) + <Name>Panel.razor + <Name>Panel.razor.scss (vote UI: one button per map, live tallies, countdown, own-pick highlight, winner banner). Flow: host calls StartVote() (usually from a post-round phase/state, or set the AutoStart [Property]) -> clients click -> votes route client-to-host via [Rpc.Host] SubmitVote with the caller re-resolved HOST-SIDE from Rpc.Caller (null-checked — Connection has no IsValid on this SDK) and the map index re-validated (re-votes overwrite, keyed by SteamId) -> tallies replicate via [Sync(FromHost)] NetList<int> -> when the [Sync] TimeUntil countdown expires the host picks the winner (most votes; ties break deterministically via one LCG scramble of a time seed — no System.Random) -> after resultLingerSeconds the HOST calls Scene.LoadFromFile(winner) (API verified live on this SDK; clients follow via the scene networking layer — verify the client hand-off in a real multi-client session). Static event OnVoteFinished(sceneFile) fires on every machine. Returns { created, componentPath, razorPath, scssPath, className, panelClassName, maps, voteDurationSeconds, resultLingerSeconds, autoStart, note, nextSteps }. REQUIREMENTS: the controller must sit on a NETWORK-SPAWNED object in multiplayer or [Sync] never replicates; if maps is omitted the MapScenes list is generated EMPTY and StartVote() refuses with a warning until you fill it in the inspector. Follow with trigger_hotload, attach via add_component_with_properties, host the panel under add_screen_panel.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the controller; the panel is generated as <Name>Panel. Defaults to 'MapVote'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs + .razor + .razor.scss. Defaults to 'Code'"),
      maps: z
        .array(z.string())
        .optional()
        .describe("Scene files to vote between, e.g. [\"scenes/arena.scene\", \"scenes/docks.scene\"] (find them with list_scenes). Baked into the MapScenes [Property] list, editable later in the inspector. Defaults to an EMPTY list (StartVote() then refuses until it's filled)"),
      voteDurationSeconds: z
        .number()
        .optional()
        .describe("Seconds the vote stays open once StartVote() is called (clamped to >= 3). Defaults to 20"),
      resultLingerSeconds: z
        .number()
        .optional()
        .describe("Seconds the winner banner shows before the host loads the winning scene (clamped to >= 0). Defaults to 4"),
      autoStart: z
        .boolean()
        .optional()
        .describe("Start the vote automatically on spawn (host only). Usually false — call StartVote() from your round machine's post-round state instead. Defaults to false"),
    },
    async (params) => {
      const res = await bridge.send("scaffold_map_vote_flow", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_panel_buildhash ───────────────────────────────────────────
  server.tool(
    "add_panel_buildhash",
    "PATCH an existing .razor file (razor_lint's companion FIXER — razor_lint flags 'PanelComponent without BuildHash', this adds it). NOT a scaffold: it EDITS the file in place, inserting 'protected override int BuildHash() => System.HashCode.Combine( ... )' at the end of the first @code block, folding the fields and auto-properties declared there. HEURISTIC string/regex parsing — review the diff: computed (=>) properties, statics, consts, events and Action/Func members are skipped; reference-typed members fold by REFERENCE (a mutated List re-renders only if Count-style state is also folded); TimeSince/TimeUntil/RealTimeSince/RealTimeUntil members fold as whole seconds (1 Hz re-render, not every frame); braces inside @code string literals can confuse the block parser. If no members are found a '=> 0' stub with a TODO is inserted (silences the lint but does NOT re-render — replace it). Returns { patched, path, hashedMembers[], buildHash, note } on success, { alreadyPresent: true, path } when a BuildHash already exists (file left UNCHANGED — extend it by hand), or { error } for non-PanelComponent files, missing @code, or a path outside the project. Follow with trigger_hotload, then razor_lint to confirm the finding cleared.",
    {
      path: z
        .string()
        .describe("Project-relative path to the .razor file to patch, e.g. 'Code/UI/MyHud.razor' (from razor_lint's finding.file or list_project_files). Must '@inherits PanelComponent'; .razor.scss files are rejected"),
    },
    async (params) => {
      const res = await bridge.send("add_panel_buildhash", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
