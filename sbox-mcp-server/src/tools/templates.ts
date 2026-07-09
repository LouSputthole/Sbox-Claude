import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Game logic template tools: create_player_controller, create_npc_controller,
 * create_game_manager, create_trigger_zone.
 *
 * These generate fully functional C# scripts with configurable boilerplate,
 * saving non-coders from writing game logic from scratch.
 */
export function registerTemplateTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_player_controller ──────────────────────────────────────
  server.tool(
    "create_player_controller",
    "Generate a player controller script with WASD movement, mouse look, jumping, and sprint. Supports first-person, third-person, and top-down movement modes. Optionally places a player rig (GameObject + CharacterController + Camera) in the scene — note the generated component is attached AFTER a trigger_hotload (it isn't in the TypeLibrary until a recompile)",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'PlayerController'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      type: z
        .enum(["first_person", "third_person", "top_down"])
        .optional()
        .describe(
          "Movement mode: 'first_person' (mouse-look body+camera, WASD relative to facing), 'third_person' (mouse yaw, WASD relative to facing, boom camera), or 'top_down' (screen-relative WASD, fixed overhead camera, no jump). Defaults to 'first_person'"
        ),
      moveSpeed: z
        .number()
        .optional()
        .describe("Movement speed in units/sec. Defaults to 300"),
      jumpForce: z
        .number()
        .optional()
        .describe("Jump force (ignored for top_down). Defaults to 350"),
      sprintMultiplier: z
        .number()
        .optional()
        .describe("Sprint speed multiplier (held 'run' action). Defaults to 1.5"),
      placeInScene: z
        .boolean()
        .optional()
        .describe(
          "If true, build a player rig in the scene: a GameObject (tagged 'player') with a CharacterController and (unless createCamera=false) a Camera. The generated controller component is NOT attached in this call — trigger_hotload then add_component_with_properties on the returned GameObject. Defaults to false (file-only)."
        ),
      createCamera: z
        .boolean()
        .optional()
        .describe(
          "When placeInScene is true, also create a Camera (FP/TP: child at eye/boom offset; top_down: fixed overhead). Defaults to true."
        ),
      spawnPosition: z
        .union([
          z.object({ x: z.number(), y: z.number(), z: z.number() }),
          z.string().describe('Comma string "x,y,z", e.g. "0,0,64"'),
        ])
        .optional()
        .describe(
          'When placeInScene is true, the world position to spawn the player rig at — object {x,y,z} or comma string "x,y,z". Defaults to the origin.'
        ),
    },
    async (params) => {
      const res = await bridge.send("create_player_controller", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_npc_controller ─────────────────────────────────────────
  server.tool(
    "create_npc_controller",
    "Generate an NPC controller script with NavMeshAgent pathfinding. Supports patrol, chase, and patrol-chase behaviors",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'NpcController'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      behavior: z
        .enum(["patrol", "chase", "patrol_chase"])
        .optional()
        .describe(
          "AI behavior: 'patrol' (follow waypoints), 'chase' (follow player), 'patrol_chase' (patrol until player nearby). Defaults to 'patrol'"
        ),
      moveSpeed: z
        .number()
        .optional()
        .describe("Movement speed. Defaults to 150"),
      chaseRange: z
        .number()
        .optional()
        .describe(
          "Detection range for chase behavior. Defaults to 500"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_npc_controller", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_game_manager ───────────────────────────────────────────
  server.tool(
    "create_game_manager",
    "Generate a minimal game-manager Component: a static Instance singleton, [Property] MaxPlayers / GameState, and a Component.INetworkListener OnActive hook that logs player connects. Writes <name>.cs and returns { created, path, className }. NOTE: the includeScore/includeTimer/includeSpawning params are not currently applied — the same minimal manager is always generated (for richer game-loop scaffolds see create_round_phase_machine / create_objective_system / create_economy_wallet). Follow with trigger_hotload, then get_compile_errors, then place via add_component_to_new_object",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'GameManager'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      includeScore: z
        .boolean()
        .optional()
        .describe("Include score tracking (currently not applied by the handler)"),
      includeTimer: z
        .boolean()
        .optional()
        .describe("Include round timer with countdown (currently not applied by the handler)"),
      includeSpawning: z
        .boolean()
        .optional()
        .describe(
          "Include player spawning from prefab at spawn point (currently not applied by the handler)"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_game_manager", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_trigger_zone ───────────────────────────────────────────
  server.tool(
    "create_trigger_zone",
    "Generate a trigger-zone Component (Component.ITriggerListener): auto-adds a trigger BoxCollider on start, filters entrants by a TriggerTag [Property] (default 'player'), and logs enter/exit via private OnPlayerEnter/OnPlayerExit extension points you fill in. Writes <name>.cs and returns { created, path, className }. NOTE: the action/filterTag params are not currently applied at generation time — the zone always logs; implement teleport/damage/spawn in the generated methods (edit_script). Follow with trigger_hotload, then get_compile_errors",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'TriggerZone'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      action: z
        .enum(["log", "teleport", "damage", "spawn"])
        .optional()
        .describe(
          "What happens on trigger (currently not applied by the handler — the generated zone always logs; implement the effect in OnPlayerEnter yourself)"
        ),
      filterTag: z
        .string()
        .optional()
        .describe(
          "Only trigger for objects with this tag (currently not applied at generation — the generated TriggerTag [Property] defaults to 'player'; change it per-instance with set_property)"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_trigger_zone", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
