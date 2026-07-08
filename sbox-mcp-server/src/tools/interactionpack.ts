import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Interaction pack + Carry (v1.20.0) — three interaction scaffolds:
 *
 *   - add_interaction_prompt   eye-traced "Press E" HUD bound to IPressable targets
 *                              (generates a .razor + .razor.scss pair, razor_lint-safe)
 *   - create_hold_to_confirm   hold-to-fill progress action + static OnConfirmed
 *   - create_carry_system      pickup / carry / throw with host-routed RPCs + ownership
 *
 * All are file/scene-mutating (refused during play mode by the bridge dispatch).
 * The generated game code was authored against live reflection on this SDK
 * (Scene.Trace, Component.IPressable, Rigidbody, GameObject.Network) — not memory.
 */
export function registerInteractionPackTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── add_interaction_prompt ────────────────────────────────────────
  server.tool(
    "add_interaction_prompt",
    'Generate an eye-traced interaction-prompt HUD — a PanelComponent (.razor + .razor.scss pair, like create_leaderboard_panel) that every frame traces a ray from the scene camera (Scene.Trace.Ray, out to [Property] float Range) and, when the crosshair is on a component implementing Component.IPressable, shows a centered "Press E"-style pill. The prompt text comes from the target\'s IPressable.GetTooltip() when it overrides it (most don\'t), else a [Property] DefaultPrompt built from the action. This is the visible half of the interaction loop: it PAIRS with create_interactable / add_interaction_station (which implement IPressable) — this tool tells the player they CAN press, those tools handle the press. Host it under a ScreenPanel (add_screen_panel), then add the component to that panel object. The generated Razor is razor_lint-safe by construction: PanelComponent + BuildHash override folding the visible state, no switch-expressions and no non-ASCII in @code, and a class root selector in the SCSS. LOCAL/visual-only (no [Sync]).',
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name for the generated .razor. Defaults to 'InteractionPrompt'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .razor + .razor.scss. Defaults to 'Code/UI'"),
      action: z
        .string()
        .optional()
        .describe("Verb woven into the default prompt text ('Press E to <action>'). Defaults to 'use'"),
      range: z
        .number()
        .optional()
        .describe("Eye-trace reach in world units — how close the crosshair must be to a pressable to show the prompt. Defaults to 120"),
    },
    async (params) => {
      const res = await bridge.send("add_interaction_prompt", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_hold_to_confirm ────────────────────────────────────────
  server.tool(
    "create_hold_to_confirm",
    "Generate a hold-to-confirm action component (sealed Component). While a named input action is held (Input.Down), a public Progress value fills 0→1 over [Property] float HoldSeconds; releasing early snaps back to 0, or drains down if [Property] bool DecayOnRelease. Reaching 1 fires the static OnConfirmed(GameObject) event, then a short CooldownSeconds blocks re-triggering. The classic 'hold E to disarm / open / revive' interaction. No UI is generated — read the public Progress (0..1) from your own HUD to draw a radial or bar; a #region Feedback hook marks where to tie in a sound/effect. LOCAL/owner-only: input is IsProxy-guarded so it never fires on proxies and is single-player safe. For a host-authoritative outcome, call an [Rpc.Host] from inside the OnConfirmed subscriber. Attach to the player (or any owned object that reads input); optionally attach to an existing GameObject by GUID after a hotload.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'HoldToConfirm'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      action: z
        .string()
        .optional()
        .describe("Input action name that must be held (must exist in the project's Input settings — see ensure_input_action). Defaults to 'use'"),
      holdSeconds: z
        .number()
        .optional()
        .describe("Seconds of continuous hold required to confirm. Defaults to 1.5"),
      decayOnRelease: z
        .boolean()
        .optional()
        .describe("Baked default for DecayOnRelease: if true, releasing early drains Progress back down instead of snapping to 0 (editable per-instance). Defaults to false"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the component to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_hold_to_confirm", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_carry_system ───────────────────────────────────────────
  server.tool(
    "create_carry_system",
    "Generate a first-person pickup / carry / throw component (sealed Component) for physics props. Attach it to the PLAYER (the object that owns the camera). It eye-traces from Scene.Camera for a Rigidbody-bearing GameObject tagged [Property] CarryTag (default 'carryable') within [Property] Range; grabbing routes a host-authoritative [Rpc.Host] request that re-validates the target and caller, hands the object's network ownership to the carrier (GameObject.Network.AssignOwnership), and disables the rigidbody's MotionEnabled while held. The held object follows a hold point ([Property] Vector3 HoldOffset in front of the camera) each FixedUpdate; dropping restores physics, throwing applies an impulse ([Property] float ThrowForce). The held-object id is [Sync(SyncFlags.FromHost)] so proxies see the carrying state, and static OnPickedUp / OnDropped events fire uniformly for SFX/VFX. PAIRS with physics props — give each carryable a Rigidbody + Collider and the CarryTag (set_tags); network-spawn them for multiplayer so ownership + transform replicate. Single-player safe (IsProxy is false and RPCs run locally with no session). Inputs: GrabAction (default 'use') grabs/drops, ThrowAction (default 'attack1') throws. Optionally attach to an existing player GameObject by GUID after a hotload.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'CarrySystem'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      range: z
        .number()
        .optional()
        .describe("Eye-trace reach for grabbing a carryable, in world units. Defaults to 130"),
      throwForce: z
        .number()
        .optional()
        .describe("Impulse magnitude applied on throw (scales with the prop's mass — tune per game). Defaults to 20000"),
      carryTag: z
        .string()
        .optional()
        .describe("Only objects with this tag (and a Rigidbody) can be picked up; lower-cased/underscored to match s&box tag convention. Defaults to 'carryable'"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of the PLAYER GameObject (the one with the camera) to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_carry_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
