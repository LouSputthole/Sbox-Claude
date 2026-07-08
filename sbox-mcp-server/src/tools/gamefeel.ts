import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Game Feel pack — three "juice" scaffolds (v1.19.0):
 *
 *   - create_camera_shake         trauma-based Perlin camera shake
 *   - add_flicker_light           flicker/pulse animator for an existing light
 *   - create_floating_combat_text rising/fading damage-number popups
 *
 * All generate a clean, self-contained .cs (LOCAL/visual-only — no [Sync]);
 * file/scene-mutating, refused during play mode by the bridge dispatch.
 */
export function registerGameFeelTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_camera_shake ───────────────────────────────────────────
  server.tool(
    "create_camera_shake",
    "Generate a trauma-based camera shake component (the standard game-feel model: events add Trauma 0..1, shake magnitude = Trauma², smooth Perlin offsets — not white-noise jitter — and Trauma decays every frame, so explosions slam and footsteps barely register). Attach the generated component to the CAMERA GameObject; fire from any game code via the static <Name>.Shake(0.4f). Applies in OnPreRender AFTER controllers position the camera, with an un-apply guard so it neither fights a controller-driven camera nor accumulates on a static one, and restores the camera when trauma hits zero. LOCAL-only (no [Sync]) — call Shake inside an [Rpc.Broadcast] handler if every client should feel it. Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'CameraShake'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      maxOffset: z
        .number()
        .optional()
        .describe("Positional shake at full trauma, in world units. Defaults to 6"),
      maxAngle: z
        .number()
        .optional()
        .describe("Rotational shake at full trauma, in degrees (applied to pitch/yaw/roll). Defaults to 4"),
      frequency: z
        .number()
        .optional()
        .describe("Noise speed — higher = violent rattle, lower = drunken sway. Defaults to 10"),
      decayPerSecond: z
        .number()
        .optional()
        .describe("How much trauma drains per second. Defaults to 1.5"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of the camera GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_camera_shake", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_flicker_light ─────────────────────────────────────────────
  server.tool(
    "add_flicker_light",
    "Generate a light-flicker animator and optionally attach it to an existing light GameObject by GUID. Five presets: Candle (soft organic sway), Fluorescent (mostly steady with random dips), Faulty (hard on/off cuts), Pulse (slow sine breathing), Lightning (dim baseline with rare bright flashes). Modulates the sibling Light component's LightColor around the color it found on enable (works on PointLight / SpotLight / DirectionalLight) and restores it exactly on disable; intensity 0..1 sets flicker depth, speed scales the whole pattern. The single biggest atmosphere win per call for horror/night scenes — pairs with apply_atmosphere. LOCAL/visual-only.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'FlickerLight'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      style: z
        .enum(["Candle", "Fluorescent", "Faulty", "Pulse", "Lightning"])
        .optional()
        .describe("Default flicker preset baked into the component (editable per-instance in the inspector). Defaults to 'Candle'"),
      intensity: z
        .number()
        .optional()
        .describe("Flicker depth 0..1: 0 = steady, 1 = full blackouts / double-bright flashes. Defaults to 0.5"),
      speed: z
        .number()
        .optional()
        .describe("Speed multiplier for the whole pattern. Defaults to 1"),
      lightId: z
        .string()
        .optional()
        .describe("GUID of a GameObject holding a light component to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("add_flicker_light", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_floating_combat_text ───────────────────────────────────
  server.tool(
    "create_floating_combat_text",
    "Generate a floating combat text component — rising, fading, camera-billboarded world-space popups for damage numbers, '+10 gold', pickup names. TextRenderer-based: no Razor, no WorldPanel, zero UI setup. Nothing to place in the scene — the generated class carries a static factory: <Name>.Spawn(position, \"-25\", Color.Red[, sizeMultiplier]) spawns a popup that rises at RiseSpeed, fades over Lifetime, and destroys itself. Pairs with create_health_system (spawn from the damage path so every hit prints its number). LOCAL-only — spawn inside an [Rpc.Broadcast] handler if every client should see it.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'FloatingCombatText'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      riseSpeed: z
        .number()
        .optional()
        .describe("World units the popup rises per second. Defaults to 48"),
      lifetime: z
        .number()
        .optional()
        .describe("Seconds until the popup is fully faded and destroyed. Defaults to 1.1"),
      fontSize: z
        .number()
        .optional()
        .describe("Base font size baked into Spawn() (the optional Spawn size argument multiplies it). Defaults to 24"),
    },
    async (params) => {
      const res = await bridge.send("create_floating_combat_text", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
