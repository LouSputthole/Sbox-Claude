import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * playtest / playtest_status — the gameplay-VERIFICATION harness.
 *
 * Runs a scripted step list inside the editor frame loop (async, like drive_player)
 * so input, state reads, and assertions time-align with the running game. This is the
 * only way to verify a *playable loop* (not just a static scene): TS round-trips can't
 * catch transient state (e.g. a jump's airborne frame). The job auto-disables the
 * controller's input read for `move` steps, zeros WishVelocity between steps, releases
 * held actions, and restores everything on teardown.
 *
 * Requires play mode (start_play first). One playtest at a time — poll playtest_status.
 */
export function registerPlaytestTools(server: McpServer, bridge: BridgeClient): void {
  const reply = (res: any) =>
    res.success
      ? { content: [{ type: "text" as const, text: JSON.stringify(res.data, null, 2) }] }
      : { content: [{ type: "text" as const, text: `Error: ${res.error}` }] };

  server.tool(
    "playtest",
    [
      "Run a scripted gameplay-verification sequence in PLAY MODE and get a pass/fail transcript (start_play first; poll playtest_status until finished).",
      "Each step is an object with ONE verb:",
      '• { "move": {"x":1,"y":0}, "frames":60 } — analog move in the controller frame (x=fwd/back, y=left/right); auto-sets UseInputControls=false and zeroes WishVelocity after. Movement is controller-specific/best-effort.',
      '• { "look": {"pitch":0,"yaw":90,"roll":0} } / { "lookDelta": {"yaw":2}, "frames":30 } — set/sweep EyeAngles.',
      '• { "action": "use", "frames":20 } — hold a named input action down (rising-edge safe; for use/dig/attack handled by gameplay components).',
      '• { "jump": "0,0,400" } — invoke the controller\'s Jump(velocity).',
      '• { "set": {"component":"PlayerController","property":"WorldPosition","to":"100,0,0"} } — set a runtime property (toggles, or teleport via WorldPosition — the robust positioning fallback).',
      '• { "wait": 10 } — advance N frames.',
      '• { "capture": "after-jump" } — screenshot the live player-POV camera at this frame; the PNG path is recorded in the transcript (diagnostic, never pass/fail). Pass true for no label.',
      '• { "assert": {"read":"Displacement","op":">","value":50,"desc":"moved >50u"} } — read a value and compare IN-FRAME. read = "Displacement" (scalar distance moved from job start — the clean facing-independent movement proof), "WorldPosition[.x|.y|.z]", or "<Component>.<Property>[.x|.y|.z|.Count]". op = > < >= <= == != changed. Records pass/fail.',
      "Tip: prove movement with read:'Displacement' op:'>' (facing-independent, unambiguous), and catch transient state (IsAirborne) right after the action.",
    ].join("\n"),
    {
      steps: z
        .array(z.record(z.string(), z.any()))
        .describe("Ordered step objects (see verbs above). Runs top-to-bottom in the frame loop."),
      id: z.string().optional().describe("GUID of the player/controller GameObject. Omit to auto-resolve the first PlayerController."),
      component: z.string().optional().describe("Controller component type to target (e.g. 'PlayerController'). Omit to auto-detect."),
    },
    async (p) => reply(await bridge.send("playtest", p))
  );

  server.tool(
    "playtest_status",
    "Poll the playtest started by the playtest tool. While running: { active:true, step, totalSteps, passed, failed }. When done: { finished:true, reason, verdict:'PASS'|'FAIL', passed, failed, stepsRun, totalSteps, controller, controllerResolved, transcript:[...] } — the full per-step pass/fail record, including any capture PNG paths; the finished summary stays readable until the next playtest starts. If none has run yet: { active:false, finished:false }. Stop a stuck/wrong run early with playtest_abort.",
    {},
    async () => reply(await bridge.send("playtest_status", {}))
  );

  server.tool(
    "playtest_abort",
    "Stop the RUNNING playtest immediately: releases held input actions, restores UseInputControls, and finalizes the partial transcript (reason 'aborted'). Returns { aborted, stepsRun, passed, failed } — or aborted:false if no job is running. The partial transcript stays readable via playtest_status. Use when a step list is clearly stuck or targeting the wrong object",
    {},
    async () => reply(await bridge.send("playtest_abort", {}))
  );
}
