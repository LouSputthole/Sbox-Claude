import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Workflow tools (Batch 53): scene checkpoints (the agent-side undo safety net),
 * describe_scene orientation, and the team-assigner / idle-income scaffolds.
 */
export function registerWorkflowTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── checkpoint_scene ─────────────────────────────────────────────
  server.tool(
    "checkpoint_scene",
    "Snapshot the ENTIRE open scene (every root GameObject, full serialization) to temp storage OUTSIDE the project. The agent-side undo: checkpoint before risky batch edits or experimental changes, roll back with restore_checkpoint if they go wrong. Returns { checkpointed, id, label, scene, rootObjects, sizeBytes } — keep the id. Refused during play mode (runtime state would poison the snapshot). Snapshots survive editor restarts (temp dir, subject to OS cleanup); browse them with list_checkpoints",
    {
      label: z
        .string()
        .optional()
        .describe("Short label baked into the checkpoint id (e.g. 'before-batch-tint'). Defaults to 'checkpoint'"),
    },
    async (params) => {
      const res = await bridge.send("checkpoint_scene", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── restore_checkpoint ───────────────────────────────────────────
  server.tool(
    "restore_checkpoint",
    "REPLACE the open scene's entire contents with a checkpoint_scene snapshot: destroys every current root object, then rebuilds the snapshot's tree (guids preserved, so internal references stay wired). DESTRUCTIVE — requires an explicit id (from checkpoint_scene or list_checkpoints), never guesses. Returns { restored, id, destroyedRoots, restoredRoots }. The scene FILE on disk is untouched until save_scene. Scene-mutating: refused during play mode",
    {
      id: z
        .string()
        .describe("Checkpoint id to restore (e.g. 'cp_20260709_153000_before-batch-tint')"),
    },
    async (params) => {
      const res = await bridge.send("restore_checkpoint", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── list_checkpoints ─────────────────────────────────────────────
  server.tool(
    "list_checkpoints",
    "List this project's scene checkpoints (newest first). Returns { total, checkpoints } — each { id, createdUtc, sizeBytes }. Pass an id to restore_checkpoint to roll the scene back, or create one first with checkpoint_scene. Read-only; no limit (checkpoints are few)",
    {},
    async (params) => {
      const res = await bridge.send("list_checkpoints", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── describe_scene ───────────────────────────────────────────────
  server.tool(
    "describe_scene",
    "One-call orientation for the OPEN scene (works in edit and play mode): total/root object counts, component histogram (top 20 types), every camera with position, light count, tag histogram (top 12), and the aggregate world bounds of renderable content. Returns a structured summary — orient here, then find_objects by component/tag, get_scene_hierarchy for structure, find_broken_references for health, screenshot_orbit to look at something. Complements describe_project (project-level). Read-only",
    {},
    async (params) => {
      const res = await bridge.send("describe_scene", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_team_assigner ─────────────────────────────────────────
  server.tool(
    "create_team_assigner",
    "Generate a host-authoritative balanced team assigner component (smallest-bucket draft): AssignSmallest(steamId) drops a joining player into the emptiest team, announces via [Rpc.Broadcast] so every client's roster agrees, and fires static OnTeamAssigned(steamId, index, name); plus Rebalance(), GetTeam, GetMembers. Writes a .cs file and returns { created, path, className, teams, nextSteps } — follow with trigger_hotload + compile_status, attach to your game manager, call AssignSmallest from your join hook (e.g. INetworkListener.OnActive)",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name (default 'TeamAssigner' -> Code/TeamAssigner.cs). Errors if the file exists"),
      directory: z
        .string()
        .optional()
        .describe("Directory for the .cs file. Default 'Code'"),
      teams: z
        .array(z.string())
        .optional()
        .describe('Team names in index order. Default ["Red", "Blue"]'),
    },
    async (params) => {
      const res = await bridge.send("create_team_assigner", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_idle_income ───────────────────────────────────────────
  server.tool(
    "create_idle_income",
    "Generate a host-authoritative passive income component: every tickSeconds the host grants incomePerTick × Multiplier, auto-wiring the first sibling component with an AddMoney(int) method (a create_economy_wallet scaffold plugs in with zero code) or an overridable Grant() seam; TotalEarned is [Sync(FromHost)] and static OnIncomeTick fires per grant. The idle-game kit: wallet (create_economy_wallet) + this + create_offline_progress. Writes a .cs file and returns { created, path, className, nextSteps } — follow with trigger_hotload + compile_status",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name (default 'IdleIncome' -> Code/IdleIncome.cs). Errors if the file exists"),
      directory: z
        .string()
        .optional()
        .describe("Directory for the .cs file. Default 'Code'"),
      incomePerTick: z
        .number()
        .optional()
        .describe("Amount granted per tick. Default 1"),
      tickSeconds: z
        .number()
        .optional()
        .describe("Seconds between grants. Default 1"),
    },
    async (params) => {
      const res = await bridge.send("create_idle_income", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
