import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Stats & Achievements pack — five scaffolds:
 *
 *   - add_leaderboard_stat        batched write-side stat reporter (Stats.Increment)
 *   - create_achievement_set      achievement engine + unlock toast HUD (razor)
 *   - add_achievement_trigger     trigger zone firing Progress/Unlock (creates a GO)
 *   - create_speedrun_leaderboard timer + min-aggregation submit + friends-filter panel
 *   - create_elo_rating_system    host-authoritative elo math + persisted ratings
 *
 * The write-side partners of create_leaderboard_panel (the read side). All are
 * file/scene-mutating and refused during play mode by the bridge dispatch.
 * Sandbox.Services APIs used by the generated code were verified live via
 * describe_type (Stats.Increment/SetValue/Flush, Leaderboards.GetFromStat ->
 * Board2.SetFriendsOnly/SetAggregationMin/SetSortAscending/Refresh(token)).
 */

// A 3D vector accepted as EITHER an object {x,y,z} OR a comma string "x,y,z".
// The value is passed through to the bridge unchanged; the C# handler parses
// both forms (C# is the source of truth for parsing).
const Vector3Object = z.object({
  x: z.number().describe("X coordinate"),
  y: z.number().describe("Y coordinate"),
  z: z.number().describe("Z coordinate"),
});

const Vector3Schema = z
  .union([
    Vector3Object,
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('3D vector — object {x,y,z} OR comma string "x,y,z"');

export function registerStatsAchievementsTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── add_leaderboard_stat ──────────────────────────────────────────
  server.tool(
    "add_leaderboard_stat",
    "Generate a batched write-side stat reporter component for Sandbox.Services.Stats — the write partner of create_leaderboard_panel. Gameplay code calls the static <Name>.Report(\"kills\", 1) from anywhere; amounts accumulate locally and flush as Stats.Increment deltas on a timer (default every 12 s, also on disable/destroy). Baseline-delta bookkeeping means a partial flush retries the un-sent remainder instead of double-counting, and deltas larger than maxChunk are sent in chunks. Returns { created, path, className, placedOn, note, nextSteps }. Place ONE in the scene after trigger_hotload (add_component_to_new_object), or pass targetId to attach immediately when the type is already compiled. Stats are PER LOCAL PLAYER (each client reports its own) and only exist on leaderboards once the stat is registered for the project ident on sbox.game. Fails if the file already exists.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'StatReporter'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      flushIntervalSeconds: z
        .number()
        .optional()
        .describe("Seconds between batched flushes to the backend. Defaults to 12, clamped to >= 1"),
      maxChunk: z
        .number()
        .optional()
        .describe("Largest amount sent in a single Stats.Increment call; bigger deltas are chunked. Defaults to 1000, clamped to >= 1"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("add_leaderboard_stat", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_achievement_set ────────────────────────────────────────
  server.tool(
    "create_achievement_set",
    "Generate an achievement engine: a component with an AchievementDef list (id/title/description/target), per-achievement progress persisted via FileSystem.Data JSON (survives restarts), Progress(id, amount) / Unlock(id) API on a static Instance, a static OnAchievementUnlocked event, and an optional Stats.Increment mirror ('ach-<id>' += 1) on unlock. Also emits a Razor unlock-toast HUD (<Name>Toast.razor + .razor.scss, razor_lint clean) unless makeToast=false. Returns { created, path, className, toastRazorPath, toastScssPath, toastClassName, achievements, placedOn, note, nextSteps }. Ids are sanitized to [a-z0-9_-]; omitting achievements bakes 3 editable samples. After trigger_hotload: place ONE set in the scene, and host the toast under a ScreenPanel (add_screen_panel). Pair with add_achievement_trigger for world-trigger unlocks. LOCAL-only: achievements belong to each client's local player. Fails if the .cs or toast .razor already exists.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated engine component (toast panel becomes <name>Toast). Defaults to 'AchievementSet'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for all generated files. Defaults to 'Code'"),
      achievements: z
        .array(
          z.object({
            id: z.string().describe("Stable id, sanitized to [a-z0-9_-] (used in the save file and the mirrored stat name)"),
            title: z.string().describe("Display title shown in the toast"),
            description: z.string().optional().describe("Short description shown in the toast"),
            target: z.number().optional().describe("Progress value that unlocks it. Defaults to 1, clamped to >= 1"),
          })
        )
        .optional()
        .describe("Achievement definitions baked into the component. Omit for 3 editable samples (first_steps, collector, veteran)"),
      fileName: z
        .string()
        .optional()
        .describe("Save file name inside FileSystem.Data. Defaults to 'achievements.json'"),
      mirrorToStats: z
        .boolean()
        .optional()
        .describe("Mirror each unlock into Sandbox.Services.Stats as 'ach-<id>' += 1. Defaults to true"),
      makeToast: z
        .boolean()
        .optional()
        .describe("Also emit the <name>Toast.razor + .razor.scss unlock toast HUD. Defaults to true"),
      toastSeconds: z
        .number()
        .optional()
        .describe("Seconds each unlock toast stays on screen. Defaults to 4, clamped to >= 0.5"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the engine to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_achievement_set", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_achievement_trigger ───────────────────────────────────────
  server.tool(
    "add_achievement_trigger",
    "SCENE-MUTATING: generate a data-driven achievement trigger-zone component AND create its GameObject now (named zone with a sized BoxCollider, IsTrigger=true, at the given position). When an object tagged triggerTag enters, the component calls <achievementSetClass>.Instance.Progress(achievementId, amount) — or Unlock() when unlock=true — with a once-only latch and optional destroy-after-fire. Returns { created, path, className, achievementSetClass, achievementId, gameObject, attached, note, nextSteps }. The generated component only attaches to the zone after trigger_hotload — until then `attached` is false and nextSteps carries the exact add_component_with_properties follow-up. The generated code references the set class BY NAME: run create_achievement_set first or the project will not compile (the result warns via `note`). Re-running with the same name fails unless reuseClass=true, which skips codegen and just places another zone (attaching + configuring immediately since the class is already compiled). Refused during play mode.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated trigger component. Defaults to 'AchievementTrigger'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      achievementSetClass: z
        .string()
        .optional()
        .describe("Class name of the achievement set the zone reports to (from create_achievement_set). Defaults to 'AchievementSet'"),
      achievementId: z
        .string()
        .describe("Id of the achievement to progress/unlock (sanitized to [a-z0-9_-])"),
      amount: z
        .number()
        .optional()
        .describe("Progress amount added per fire (ignored when unlock=true). Defaults to 1"),
      unlock: z
        .boolean()
        .optional()
        .describe("Call Unlock() instead of Progress(). Defaults to false"),
      triggerTag: z
        .string()
        .optional()
        .describe("Tag the entering object must carry (put it on the player via set_tags). Defaults to 'player'"),
      onceOnly: z
        .boolean()
        .optional()
        .describe("Only the first tagged entry fires. Defaults to true"),
      destroyAfterFire: z
        .boolean()
        .optional()
        .describe("Destroy the zone GameObject after firing. Defaults to false"),
      createObject: z
        .boolean()
        .optional()
        .describe("Create the zone GameObject now (with BoxCollider). Defaults to true; false = code-gen only"),
      objectName: z
        .string()
        .optional()
        .describe("Name for the zone GameObject. Defaults to '<name>Zone'"),
      position: Vector3Schema.optional().describe("World position of the zone GameObject"),
      scale: z
        .union([z.number(), Vector3Schema])
        .optional()
        .describe('BoxCollider size — uniform number, object {x,y,z}, or comma string "x,y,z". Defaults to 100,100,100'),
      reuseClass: z
        .boolean()
        .optional()
        .describe("If the .cs already exists, skip codegen and just place another zone with the existing class. Defaults to false"),
    },
    async (params) => {
      const res = await bridge.send("add_achievement_trigger", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_speedrun_leaderboard ───────────────────────────────────
  server.tool(
    "create_speedrun_leaderboard",
    "Generate a speedrun timer component plus a leaderboard display panel. The timer (<Name>.cs) is TimeSince-based with a static Instance: StartTimer() at run start, StopTimer() at the finish (pairs with a trigger zone), ResetTimer() to abort. StopTimer persists the local best via FileSystem.Data and submits Stats.SetValue(statName, seconds) ONLY when the run beats it — configure the stat with MIN aggregation on sbox.game so the global board keeps best times. The panel (<Name>Panel.razor + .razor.scss, razor_lint clean) fetches via Leaderboards.GetFromStat with min aggregation + ascending sort, has a clickable Friends-only filter button, and overlays a local-best row read from the same save file. Returns { created, path, className, panelRazorPath, panelScssPath, panelClassName, statName, placedOn, note, nextSteps }. After trigger_hotload: place ONE timer (add_component_to_new_object or targetId) and host the panel under a ScreenPanel/WorldPanel (add_screen_panel). maxRows clamps to 1..50; makePanel=false skips the panel files. Fails if the .cs or panel .razor already exists.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated timer component (panel becomes <name>Panel). Defaults to 'SpeedrunTimer'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for all generated files. Defaults to 'Code'"),
      statName: z
        .string()
        .optional()
        .describe("Sandbox.Services stat the best time is written to (sanitized to [a-z0-9_-]). Defaults to 'best_time'"),
      fileName: z
        .string()
        .optional()
        .describe("Save file name inside FileSystem.Data for the local best. Defaults to 'speedrun.json'"),
      title: z
        .string()
        .optional()
        .describe("Panel title text. Defaults to 'Best Times'"),
      maxRows: z
        .number()
        .optional()
        .describe("Leaderboard rows fetched/shown. Defaults to 10, clamped to 1..50"),
      makePanel: z
        .boolean()
        .optional()
        .describe("Also emit the <name>Panel.razor + .razor.scss display panel. Defaults to true"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the timer to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_speedrun_leaderboard", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_elo_rating_system ──────────────────────────────────────
  server.tool(
    "create_elo_rating_system",
    "Generate a self-contained, host-authoritative elo rating component: standard elo math (expected = 1/(1+10^((Rb-Ra)/400)), delta = K * (score - expected)) with a [Property] K-factor, ratings in a [Sync(SyncFlags.FromHost)] NetDictionary<long,float> keyed by SteamId, and host-side persistence via FileSystem.Data JSON. API on a static Instance: ReportMatch(winnerSteamId, loserSteamId) for 1v1 and ReportTeamMatch(winnerIds, loserIds) for teams (team-average elo, uniform delta per member) — both are IsProxy-guarded no-ops on clients; GetRating(steamId) works anywhere (unknown players = defaultRating); the static OnRatingChanged(steamId, newRating) fires on EVERY machine via an [Rpc.Broadcast]. Returns { created, path, className, kFactor, defaultRating, placedOn, note, nextSteps }. After trigger_hotload: place ONE in the scene and network its GameObject (network_spawn) or the [Sync] never replicates. Only the HOST's disk holds the ratings ledger. Fails if the file already exists.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'EloRatingSystem'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      kFactor: z
        .number()
        .optional()
        .describe("Elo K-factor — how far one result moves ratings (32 = fast, 16 = stable). Defaults to 32, clamped to >= 1"),
      defaultRating: z
        .number()
        .optional()
        .describe("Rating assigned to players with no recorded matches. Defaults to 1000"),
      fileName: z
        .string()
        .optional()
        .describe("Save file name inside FileSystem.Data (host-side ledger). Defaults to 'elo_ratings.json'"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_elo_rating_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
