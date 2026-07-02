import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Character & model tools (Batch 19): spawn world models, spawn animated
 * Citizen characters, dress them with clothing, toggle bodygroups, and pose
 * them via CitizenAnimationHelper.
 *
 * Unlike particles (Batch 18), everything here is a STATIC mesh/pose that
 * renders in the editor viewport — so the screenshot loop works: after a
 * change, take_screenshot and read the result. Animation *playback* is
 * runtime, but poses preview in-editor via PlayAnimationsInEditorScene.
 */

// Colour / vector accepted as EITHER an object OR a comma string, passed
// through unchanged. The C# handler parses both forms (source of truth). See
// the cross-language vector/color contract.
const ColorObject = z.object({
  r: z.number().min(0).describe("Red, 0-1"),
  g: z.number().min(0).describe("Green, 0-1"),
  b: z.number().min(0).describe("Blue, 0-1"),
  a: z.number().min(0).max(1).optional().describe("Alpha, 0-1 (default 1)"),
});

const ColorSchema = z
  .union([
    ColorObject,
    z.string().describe('Comma string "r,g,b,a", e.g. "1,0,0,1"'),
  ])
  .describe('RGBA colour — object {r,g,b,a} (0-1) OR comma string "r,g,b,a" (model tint)');

const Vector3Object = z.object({ x: z.number(), y: z.number(), z: z.number() });

const Vector3Schema = z
  .union([
    Vector3Object,
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('World vector — object {x,y,z} OR comma string "x,y,z"');

const RotationSchema = z
  .object({ pitch: z.number(), yaw: z.number(), roll: z.number() })
  .describe("Rotation {pitch,yaw,roll} in degrees");

export function registerCharacterTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── spawn_model ────────────────────────────────────────────────────
  server.tool(
    "spawn_model",
    "Spawn a GameObject with a ModelRenderer showing a model — the quick way to place a prop. Pass an engine/project model path (e.g. 'models/citizen/citizen.vmdl', 'models/dev/box.vmdl'). Cloud (sbox.game) models must be installed first via install_asset, then pass the resulting path. Add physics separately with add_collider/add_physics if needed.",
    {
      model: z
        .string()
        .describe("Model path, e.g. 'models/dev/box.vmdl' or an installed model path"),
      name: z.string().optional().describe("GameObject name"),
      position: Vector3Schema.optional().describe("World position"),
      rotation: RotationSchema.optional().describe("World rotation"),
      scale: Vector3Schema.optional().describe("World scale (default 1,1,1)"),
      tint: ColorSchema.optional().describe("Model tint colour"),
      parentId: z.string().optional().describe("GUID of a parent GameObject"),
    },
    async (params) => {
      const res = await bridge.send("spawn_model", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── spawn_citizen ──────────────────────────────────────────────────
  server.tool(
    "spawn_citizen",
    "Spawn an animated Citizen character: a SkinnedModelRenderer with the Citizen model, plus (by default) a CitizenAnimationHelper so it idles. PlayAnimationsInEditorScene is enabled so the idle pose shows in the editor view (screenshot-verifiable). Dress it afterward with dress_citizen, pose it with pose_citizen.",
    {
      name: z.string().optional().describe("GameObject name (default 'Citizen')"),
      model: z
        .string()
        .optional()
        .describe("Override the skinned model (default 'models/citizen/citizen.vmdl')"),
      position: Vector3Schema.optional().describe("World position"),
      rotation: RotationSchema.optional().describe("World rotation"),
      scale: Vector3Schema.optional().describe("World scale (default 1,1,1)"),
      tint: ColorSchema.optional().describe("Body tint colour"),
      animator: z
        .boolean()
        .optional()
        .describe("Add a CitizenAnimationHelper for idle/pose (default true)"),
      holdType: z
        .string()
        .optional()
        .describe("Initial hold pose, e.g. None, Pistol, Rifle, Shotgun, HoldItem, Punch, Swing"),
      moveStyle: z
        .string()
        .optional()
        .describe("Movement style, e.g. Auto, Walk, Run"),
      parentId: z.string().optional().describe("GUID of a parent GameObject"),
    },
    async (params) => {
      const res = await bridge.send("spawn_citizen", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── dress_citizen ──────────────────────────────────────────────────
  server.tool(
    "dress_citizen",
    "Dress a spawned Citizen (or any GameObject with a SkinnedModelRenderer) by applying .clothing resources. Pass an array of clothing resource paths; they're loaded, added to a ClothingContainer, and applied to the body. Returns which paths applied vs were not found.",
    {
      id: z.string().describe("GUID of the Citizen GameObject"),
      clothing: z
        .array(z.string())
        .describe("Clothing resource paths, e.g. ['models/citizen_clothes/jacket/jacket.clothing']"),
      tint: z
        .number()
        .min(0)
        .max(1)
        .optional()
        .describe("ClothingContainer tint position 0-1 (skin/clothing colour variation)"),
    },
    async (params) => {
      const res = await bridge.send("dress_citizen", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_bodygroup ──────────────────────────────────────────────────
  server.tool(
    "set_bodygroup",
    "Show/hide a bodygroup on a SkinnedModelRenderer (e.g. hide hands when holding a tool, swap head variants). Provide value (int index) or choice (string name).",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
      name: z.string().describe("Bodygroup name"),
      value: z.number().int().optional().describe("Bodygroup choice index"),
      choice: z.string().optional().describe("Bodygroup choice by name (alternative to value)"),
    },
    async (params) => {
      const res = await bridge.send("set_bodygroup", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── pose_citizen ───────────────────────────────────────────────────
  server.tool(
    "pose_citizen",
    "Pose a Citizen by setting CitizenAnimationHelper params (enables PlayAnimationsInEditorScene so the pose shows in-editor). Set holdType (None/Pistol/Rifle/Shotgun/HoldItem/Punch/Swing), moveStyle (Auto/Walk/Run), specialMove, sitting (bool), and/or duckLevel (0-1).",
    {
      id: z.string().describe("GUID of the Citizen GameObject (must have a CitizenAnimationHelper)"),
      holdType: z.string().optional().describe("Hold pose, e.g. None, Pistol, Rifle, Shotgun, HoldItem"),
      moveStyle: z.string().optional().describe("Movement style, e.g. Auto, Walk, Run"),
      specialMove: z.string().optional().describe("Special move style, e.g. None, LedgeGrab, Roll"),
      sitting: z.boolean().optional().describe("Sit the character (IsSitting)"),
      duckLevel: z.number().min(0).max(1).optional().describe("Crouch amount, 0-1"),
    },
    async (params) => {
      const res = await bridge.send("pose_citizen", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── equip_model ────────────────────────────────────────────────────
  server.tool(
    "equip_model",
    "Attach a prop model (weapon, hat, tool) to a Citizen's bone or attachment point — the prop is parented so it follows that point. Tries the point as an attachment (hand_R, hand_L, eyes, hat) then as a bone. Great for arming NPCs or adding accessories.",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
      model: z.string().describe("Prop model path, e.g. 'models/dev/box.vmdl'"),
      point: z
        .string()
        .optional()
        .describe("Attachment or bone name (default 'hand_R'; try hand_L, eyes, hat, head)"),
      offset: Vector3Schema.optional().describe("Local position offset from the point"),
      rotation: RotationSchema.optional().describe("Local rotation offset"),
      tint: ColorSchema.optional().describe("Prop tint colour"),
      name: z.string().optional().describe("Name for the prop GameObject"),
    },
    async (params) => {
      const res = await bridge.send("equip_model", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_look_at ────────────────────────────────────────────────────
  server.tool(
    "set_look_at",
    "Aim a Citizen's gaze. Pass target {x,y,z} (spawns a LookTarget) or targetId (existing GameObject) and the head/eyes track it. Pass enabled:false to turn gaze tracking off. Tune eyesWeight/headWeight/bodyWeight (0-1).",
    {
      id: z.string().describe("GUID of the Citizen (must have a CitizenAnimationHelper)"),
      target: Vector3Schema.optional().describe("World point to look at"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to look at (overrides target)"),
      enabled: z.boolean().optional().describe("false to disable gaze tracking"),
      eyesWeight: z.number().min(0).max(1).optional().describe("Eye look weight 0-1"),
      headWeight: z.number().min(0).max(1).optional().describe("Head turn weight 0-1"),
      bodyWeight: z.number().min(0).max(1).optional().describe("Body turn weight 0-1"),
    },
    async (params) => {
      const res = await bridge.send("set_look_at", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_ragdoll ────────────────────────────────────────────────────
  server.tool(
    "add_ragdoll",
    "Add ModelPhysics to a skinned model so it becomes a ragdoll (physics-driven bones). NOTE: the ragdoll only flops in PLAY mode — it won't move in the static editor view, so this one is verified structurally, not by screenshot.",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
      motionEnabled: z
        .boolean()
        .optional()
        .describe("Whether physics bodies start with motion enabled"),
    },
    async (params) => {
      const res = await bridge.send("add_ragdoll", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_expression ─────────────────────────────────────────────────
  server.tool(
    "set_expression",
    "Set a facial morph (blendshape) on a skinned model — e.g. smile, frown, blink. Call with NO morph to list the model's available morph names (returned as availableMorphs). weight is typically 0-1.",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
      morph: z
        .string()
        .optional()
        .describe("Morph/blendshape name (omit to list available morphs)"),
      weight: z.number().optional().describe("Morph weight, typically 0-1 (default 1)"),
    },
    async (params) => {
      const res = await bridge.send("set_expression", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── list_animations ───────────────────────────────────────────────── (Batch 33)
  server.tool(
    "list_animations",
    "List the animation sequences available on a GameObject's SkinnedModelRenderer (a spawned Citizen or animated model), plus whether it's driven by an AnimationGraph. Call this before play_animation or set_animgraph_param to see valid names.",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
    },
    async (params) => {
      const res = await bridge.send("list_animations", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── play_animation ─────────────────────────────────────────────────
  server.tool(
    "play_animation",
    "Play a named animation sequence on a GameObject's SkinnedModelRenderer (sets the Sequence). Best for models with raw sequences; for AnimationGraph characters (Citizen) prefer set_animgraph_param. The renderer needs PlayAnimationsInEditorScene = true to animate in-editor — then screenshot to verify. Use list_animations for valid names.",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
      animation: z.string().describe("Sequence name to play (see list_animations)"),
      looping: z.boolean().optional().describe("Loop the sequence (default: the model's setting)"),
      speed: z.number().optional().describe("Playback rate multiplier (default 1)"),
    },
    async (params) => {
      const res = await bridge.send("play_animation", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_animgraph_param ────────────────────────────────────────────
  server.tool(
    "set_animgraph_param",
    "Set an AnimationGraph parameter on a GameObject's SkinnedModelRenderer (calls Set). This drives Citizen/animgraph motion — e.g. 'move_x'/'move_y' (float), 'b_grounded'/'b_ducked' (bool), or a Vector3. Pose previews in-editor when PlayAnimationsInEditorScene is on; screenshot to verify. Param names are defined by the model's animation graph.",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
      param: z.string().describe("Animgraph parameter name, e.g. 'move_x', 'b_grounded'"),
      value: z
        .union([
          z.number(),
          z.boolean(),
          z.object({ x: z.number(), y: z.number(), z: z.number() }),
          z.string().describe('Vector as a comma string "x,y,z"'),
        ])
        .describe('Value: number (float), boolean, or a vector as {x,y,z} OR a comma string "x,y,z"'),
      type: z
        .enum(["float", "int", "bool", "vector"])
        .optional()
        .describe("Force the parameter type (default: inferred from value)"),
    },
    async (params) => {
      const res = await bridge.send("set_animgraph_param", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_lipsync ────────────────────────────────────────────────────
  server.tool(
    "add_lipsync",
    "Add + wire s&box's LipSync component (new in the 2026-07 engine update): drives a SkinnedModelRenderer's facial morphs (visemes) from a playing sound. Wires the renderer on the target GameObject (or rendererId) and, if soundEvent is given, a SoundPointComponent bound to that .sound asset (see list_sounds / create_sound_event). Morphs animate at RUNTIME while the sound plays — verify in play mode with capture_view, not the static editor pose.",
    {
      id: z.string().describe("GUID of the GameObject to add LipSync to (e.g. a spawn_citizen)"),
      rendererId: z
        .string()
        .optional()
        .describe("GUID of a different GameObject holding the SkinnedModelRenderer (default: same as id)"),
      soundEvent: z
        .string()
        .optional()
        .describe("Path to a .sound event to speak, e.g. 'sounds/dialogue/hello.sound' — creates/reuses a SoundPointComponent. Omit to wire an existing sound component on the GameObject (or wire audio later)."),
      playOnStart: z.boolean().optional().describe("Play the sound on scene start (default true; only with soundEvent)"),
      volume: z.number().optional().describe("SoundPointComponent volume (only with soundEvent)"),
      morphScale: z.number().optional().describe("LipSync.MorphScale — exaggerate/dampen mouth movement (engine default when omitted)"),
      morphSmoothTime: z.number().optional().describe("LipSync.MorphSmoothTime — morph smoothing seconds (engine default when omitted)"),
    },
    async (params) => {
      const res = await bridge.send("add_lipsync", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
