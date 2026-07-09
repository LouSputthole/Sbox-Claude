import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Play mode control and related editor tools.
 *
 * Beyond play/pause/resume/stop, this file also registers: set_property
 * (editor-mode property writes), get/set_runtime_property (live tweaking
 * during play mode), take_screenshot, and undo/redo.
 */
export function registerPlayModeTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── start_play ───────────────────────────────────────────────────
  server.tool(
    "start_play",
    "Enter play mode — starts running the game in the editor. Scripts execute, physics simulate, everything goes live. Returns { started, method } (method 'EditorScene.Play', or 'SetPlaying (fallback)' with editorErrorSkipped when the safe path failed). While playing, scene-mutating tools refuse — use get/set_runtime_property, capture_view, and playtest, then stop_play to edit again.",
    {},
    async () => {
      const res = await bridge.send("start_play");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── stop_play ────────────────────────────────────────────────────
  server.tool(
    "stop_play",
    "Exit play mode — stops the game and returns to editor. All runtime changes are discarded",
    {},
    async () => {
      const res = await bridge.send("stop_play");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // pause_play / resume_play removed in v1.3.0 — s&box does not expose a public
  // API for pausing the editor's play mode, so the addon has no handler and the
  // tool only ever returned "Unknown command". See GitHub issue #3.

  // ── is_playing ───────────────────────────────────────────────────
  server.tool(
    "is_playing",
    "Check current play state — returns 'playing', 'paused', or 'stopped'",
    {},
    async () => {
      const res = await bridge.send("is_playing");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── take_screenshot ──────────────────────────────────────────────
  server.tool(
    "take_screenshot",
    "Capture a 1920x1080 PNG from the scene's Main Camera (ONE fixed angle — the camera may not face what you changed; use screenshot_from to aim at a target object/point). Returns { taken, note, path }, but the engine saves to its default screenshots folder regardless of the path param (<sbox install>/screenshots/sbox.<timestamp>.png) — list the newest file there and READ the image to verify.",
    {
      path: z
        .string()
        .optional()
        .describe(
          "Output path (e.g. 'screenshots/test.png'). Defaults to screenshots/screenshot_{timestamp}.png"
        ),
    },
    async (params) => {
      const res = await bridge.send("take_screenshot", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── undo ─────────────────────────────────────────────────────────
  server.tool(
    "undo",
    "Undo the last editor action via the editor's UndoSystem. Safety net for when a change goes wrong. Returns { undone: true } — it does not report WHAT was undone, so re-check state with get_scene_hierarchy or get_all_properties. Reverse it with redo. Note: file writes (write_file/create_script) are not editor undo steps.",
    {},
    async () => {
      const res = await bridge.send("undo");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── redo ─────────────────────────────────────────────────────────
  server.tool(
    "redo",
    "Re-apply the editor action most recently reverted by undo (editor UndoSystem redo — the counterpart to the undo tool). Returns { redone: true } without details of what was redone, so verify the resulting state with get_scene_hierarchy or get_all_properties.",
    {},
    async () => {
      const res = await bridge.send("redo");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_property ─────────────────────────────────────────────────
  server.tool(
    "set_property",
    "Set a property value on a component (editor mode), and PERSIST it (survives save+reload). Handles primitives, enums, value types (Color/Vector3 as comma strings), AND references: pass an asset PATH for Model/Material/Texture/SoundEvent props, or a GameObject GUID for GameObject/Component-typed props (resolved like set_component_reference). Returns success=false with a clear error if a path/GUID can't be resolved (no more silent null). For wiring object refs prefer set_component_reference; for prefab refs use set_prefab_ref",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name"),
      property: z.string().describe("Property name to set"),
      value: z
        .unknown()
        .describe(
          "New value. Primitive: '5', 'true'. Color/Vector3: a comma string ('1,0,0,1' / '0,0,200'), an array ([0,0,200]), or an object ({r,g,b,a} / {x,y,z}). Enum: the member name. Asset ref (Model/Material/...): the asset path e.g. 'models/dev/box.vmdl'. GameObject/Component ref: the target GameObject's GUID. Empty/'null' clears the property"
        ),
    },
    async (params) => {
      const res = await bridge.send("set_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_runtime_property ─────────────────────────────────────────
  server.tool(
    "get_runtime_property",
    "Read a component property value during play mode. Must call start_play first",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name"),
      property: z.string().describe("Property name to read"),
    },
    async (params) => {
      const res = await bridge.send("get_runtime_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_runtime_property ─────────────────────────────────────────
  server.tool(
    "set_runtime_property",
    "Set a component property value during play mode — tweak values live while the game runs. Errors if the game is not playing (start_play first). Returns { set, id, component, property, value } like set_property; read it back with get_runtime_property. Runtime changes are DISCARDED when play mode stops — use set_property in edit mode to persist.",
    {
      id: z.string().describe("GUID of the GameObject"),
      component: z.string().describe("Component type name"),
      property: z.string().describe("Property name to set"),
      value: z.unknown().describe("New value"),
    },
    async (params) => {
      const res = await bridge.send("set_runtime_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── drive_player (EXPERIMENTAL) ──────────────────────────────────
  server.tool(
    "drive_player",
    "EXPERIMENTAL — Drive the active PlayerController DURING PLAY MODE across multiple frames: synthesize sustained look (EyeAngles), analog movement (wish velocity), and/or hold a named action down long enough that Input.Pressed fires its rising edge. This is the reliable alternative to simulate_input, which only sets an action for ONE frame and so MISSES edge-triggered controls (Input.Pressed) and cannot inject analog move/look at all. Requires start_play first. Runs ASYNC across editor frames and returns immediately — the job keeps applying for `frames` (or `durationMs`) frames. After it runs, confirm with drive_player_status, then verify the actual effect with capture_view / get_runtime_property. Provide at least one of look / lookDelta / move / action.",
    {
      id: z
        .string()
        .optional()
        .describe(
          "GUID of the GameObject holding the controller. Omit to auto-resolve the first PlayerController (or *Controller with EyeAngles/WishVelocity) in the play scene"
        ),
      component: z
        .string()
        .optional()
        .describe(
          "Controller component type name to target (e.g. 'PlayerController'). Omit to auto-detect"
        ),
      frames: z
        .number()
        .int()
        .optional()
        .describe(
          "How many editor frames to drive for (1–1800, ~60/sec). Default 30 (~0.5s). Takes precedence over durationMs"
        ),
      durationMs: z
        .number()
        .int()
        .optional()
        .describe("Duration in ms, converted at ~60fps (ignored if `frames` is given). Default ~500ms"),
      look: z
        .union([
          z.object({
            pitch: z.number().default(0),
            yaw: z.number().default(0),
            roll: z.number().default(0),
          }),
          z.array(z.number()),
          z.string(),
        ])
        .optional()
        .describe(
          "Absolute EyeAngles target {pitch,yaw,roll} held for the whole duration (aim the camera/body). Object, [pitch,yaw,roll], or 'pitch,yaw,roll'. pitch is clamped to ±89"
        ),
      lookDelta: z
        .union([
          z.object({
            pitch: z.number().default(0),
            yaw: z.number().default(0),
            roll: z.number().default(0),
          }),
          z.array(z.number()),
          z.string(),
        ])
        .optional()
        .describe(
          "Per-frame EyeAngles delta added each frame (turn/pan over time, e.g. {yaw:2} to sweep right). Combine with `frames` to control total rotation"
        ),
      move: z
        .union([
          z.object({ x: z.number().default(0), y: z.number().default(0) }),
          z.array(z.number()),
          z.string(),
        ])
        .optional()
        .describe(
          "Analog movement in the controller's facing frame: x = forward(+)/back(-), y = left(+)/right(-). Magnitude clamped to 1. e.g. {x:1} walks forward for the whole duration"
        ),
      moveSpeed: z
        .number()
        .optional()
        .describe(
          "Units/sec used when synthesizing WishVelocity from `move` (default 160). Ignored if the controller exposes its own AnalogMove field"
        ),
      action: z
        .string()
        .optional()
        .describe(
          "A named input action ('jump','use','attack1',…) HELD DOWN every frame for the whole duration, so Input.Pressed catches the edge single-frame simulate_input misses. Auto-released on the final frame"
        ),
    },
    async (params) => {
      const res = await bridge.send("drive_player", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── drive_player_status (EXPERIMENTAL) ───────────────────────────
  server.tool(
    "drive_player_status",
    "EXPERIMENTAL — Read the result of the most recently FINISHED drive_player job: which controller members were actually written (EyeAngles / WishVelocity / AnalogMove…), how many frames applied, and why it ended. Because drive_player runs across frames and returns immediately, this is how you confirm it took effect. Returns lastResult=null if no job has finished yet.",
    {},
    async () => {
      const res = await bridge.send("drive_player_status");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
