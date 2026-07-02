import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * create_round_state_machine — the COMPLEX, multi-state round machine scaffold.
 *
 * The sibling of create_round_phase_machine. Both are host-authoritative round
 * flow, but they scaffold different shapes:
 *
 *   create_round_phase_machine  → ONE sealed component with a [Sync] enum cycled
 *       on a shared per-phase timer. Reach for it for 3–5 LIGHT phases whose
 *       only per-phase logic is "how long" (round/day-night/match phases).
 *
 *   create_round_state_machine  → a RoundManager singleton + an abstract
 *       RoundState base (Begin/Tick/OnTimeUp/Finish lifecycle + per-state
 *       [Sync] TimeUntil timer) + one sealed stub class PER state. Reach for it
 *       when each phase deserves its OWN file/behaviour: entry side-effects,
 *       per-frame Tick logic, a CanEnter() skip condition, copy-data-out-on-exit.
 *       This is the "state-as-component" pattern from the round-match cookbook.
 *
 * The generated manager auto-attaches the state components on start (so a
 * non-coder only places the manager), index-wraps when advancing, skips states
 * whose CanEnter() returns false, and announces transitions via a static
 * OnStateChanged event mirrored by an [Rpc.Broadcast] so proxies converge
 * instantly (the [Sync] index is the durable late-joiner reconcile path).
 * Single-player safe. Scene/file-mutating; refused during play mode.
 */
export function registerRoundStateTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "create_round_state_machine",
    "Generate a host-authoritative MULTI-STATE round machine (the complex variant of create_round_phase_machine). Produces one .cs file: a RoundManager singleton component + an abstract RoundState base (Begin/Tick/OnTimeUp/Finish lifecycle with a per-state [Sync(SyncFlags.FromHost)] TimeUntil timer) + one sealed stub class per named state. The manager auto-attaches the state components on start (you only place the manager), ticks ONLY the active state on the host, Advance()s on timeout with index-wrap, SKIPS any state whose CanEnter() returns false, and announces every transition via a static OnStateChanged event plus an [Rpc.Broadcast] mirror so the host fires immediately and proxies converge without waiting a snapshot (the [Sync] index reconciles late joiners). Single-player safe. USE THIS (not create_round_phase_machine) when each phase needs its OWN behaviour — entry side-effects, per-frame Tick logic, a skip condition, or copy-data-out-on-exit; use the phase machine for 3–5 light phases that differ only in duration. Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z
        .string()
        .optional()
        .describe("Manager class name. Defaults to 'RoundManager'. The abstract base is derived from it (RoundManager → RoundState)"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the .cs file (path override). Defaults to 'Code'"),
      states: z
        .array(z.string())
        .optional()
        .describe('Ordered state names — each becomes a sealed {Name}State stub class. Defaults to ["Waiting","Active","PostRound"]'),
      duration: z
        .number()
        .optional()
        .describe("Default seconds each state lasts (each state also gets its own tunable [Property] Duration). 0 = no auto-advance for a state. Defaults to 30"),
      durations: z
        .union([
          z.array(z.number()),
          z.record(z.number()),
        ])
        .optional()
        .describe('Optional per-state duration override: an array aligned to `states` ([10,120,8]) OR an object keyed by state name ({"Waiting":10,"Active":120}). Any state not covered falls back to `duration`'),
      loop: z
        .boolean()
        .optional()
        .describe("Loop back to the first state after the last (true) or hold on the last state (false). Defaults to true"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach the manager to (only if the type is already loaded — trigger_hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_round_state_machine", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
