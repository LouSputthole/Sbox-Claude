import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Visual & atmosphere tools (Batch 17): lighting, post-processing, fog, sky,
 * reflection probes, and compose-it-all presets.
 *
 * These wrap s&box's visual components with sensible parameters so Claude can
 * author scene mood directly, instead of hand-driving add_component_with_properties
 * (which can't even set a Color). After any change, screenshot the scene and read
 * the result — this layer is where the screenshot loop matters most.
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
  .describe('RGBA colour — object {r,g,b,a} (0-1) OR comma string "r,g,b,a"');

const Vector3Object = z.object({ x: z.number(), y: z.number(), z: z.number() });

const Vector3Schema = z
  .union([
    Vector3Object,
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('World position — object {x,y,z} OR comma string "x,y,z"');

const RotationSchema = z
  .object({ pitch: z.number(), yaw: z.number(), roll: z.number() })
  .describe("Rotation {pitch,yaw,roll} in degrees");

export function registerVisualTools(server: McpServer, bridge: BridgeClient): void {
  // ── add_light ──────────────────────────────────────────────────────
  server.tool(
    "add_light",
    "Add a light to the active scene. NOTE: s&box lights have no separate brightness field — intensity is the colour magnitude, so 'brightness' scales the colour (use >1 for bright/HDR). Types: directional = sun (aim it with rotation), point = omni-directional (range), spot = cone (range + coneInner/coneOuter degrees), ambient = global fill light.",
    {
      type: z
        .enum(["directional", "point", "spot", "ambient"])
        .describe("Light type"),
      name: z.string().optional().describe("GameObject name"),
      color: ColorSchema.optional().describe("Light colour (default white)"),
      brightness: z
        .number()
        .optional()
        .describe("Intensity multiplier on the colour (default 1; try 2-10 for point/spot)"),
      range: z
        .number()
        .optional()
        .describe("point/spot only: falloff radius in units (maps to Radius)"),
      coneInner: z
        .number()
        .optional()
        .describe("spot only: inner cone angle in degrees"),
      coneOuter: z
        .number()
        .optional()
        .describe("spot only: outer cone angle in degrees"),
      shadows: z.boolean().optional().describe("Cast shadows (default true)"),
      skyColor: ColorSchema.optional().describe(
        "directional only: ambient sky colour for the upper hemisphere"
      ),
      position: Vector3Schema.optional().describe("World position"),
      rotation: RotationSchema.optional().describe(
        "World rotation — sets the aim direction for directional/spot lights"
      ),
      parentId: z.string().optional().describe("GUID of a parent GameObject"),
    },
    async (params) => {
      const res = await bridge.send("add_light", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_fog ────────────────────────────────────────────────────────
  server.tool(
    "set_fog",
    "Add or update fog in the active scene. Types: 'gradient' (distance haze — great for mood/horror), 'cubemap' (sky-tinted distance fog), 'volumetric' (a localized fog volume). Re-running with the same targetId updates it rather than duplicating; without targetId each call creates a new fog object. Returns { created, type, gameObject } — gameObject.id addresses the fog object for later set_property/delete_gameobject; screenshot to verify the look.",
    {
      type: z
        .enum(["gradient", "cubemap", "volumetric"])
        .optional()
        .describe("Fog type (default gradient)"),
      name: z.string().optional().describe("GameObject name when creating a new fog object"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to host the fog (else a new fog object is created)"),
      color: ColorSchema.optional().describe("Fog colour (gradient/volumetric Color, cubemap Tint)"),
      startDistance: z.number().optional().describe("gradient/cubemap: distance (units) where fog begins"),
      endDistance: z.number().optional().describe("gradient/cubemap: distance (units) where fog reaches full density"),
      height: z.number().optional().describe("gradient: world height the fog settles around"),
      falloff: z.number().optional().describe("Distance/density falloff exponent (higher = sharper onset)"),
      blur: z.number().optional().describe("cubemap: sky blur amount"),
      heightStart: z.number().optional().describe("cubemap: world height where height-fog starts"),
      heightWidth: z.number().optional().describe("cubemap: height-fog band width"),
      heightExponent: z.number().optional().describe("cubemap: height-fog falloff exponent"),
      strength: z.number().optional().describe("volumetric: fog density/strength"),
      size: Vector3Schema
        .optional()
        .describe('volumetric: bounds size (units) centred on the object — object {x,y,z} or comma string "x,y,z"'),
    },
    async (params) => {
      const res = await bridge.send("set_fog", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_post_process ─────────────────────────────────────────────────
  server.tool(
    "add_post_process",
    "Add (or update) a post-processing effect on the scene's main camera (auto-enables post-processing). Generic: pass the effect component name + any of its properties. Examples — Bloom {Strength, Threshold, Tint}, Tonemapping, ColorAdjustments {Saturation, Brightness, Contrast}, Vignette {Intensity, Color}, FilmGrain, DepthOfField, ChromaticAberration, MotionBlur, Sharpen, AmbientOcclusion. Call describe_type <Effect> to discover a given effect's properties.",
    {
      effect: z
        .string()
        .describe("Post-process component type name, e.g. 'Bloom', 'Vignette', 'ColorAdjustments'"),
      properties: z
        .record(z.any())
        .optional()
        .describe("Property name -> value. Floats/ints/bools as numbers/bools, colours as {r,g,b,a}, enums as their string name."),
      cameraId: z
        .string()
        .optional()
        .describe("GUID of a specific camera GameObject (default: the scene's main camera)"),
    },
    async (params) => {
      const res = await bridge.send("add_post_process", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_skybox ───────────────────────────────────────────────────────
  server.tool(
    "set_skybox",
    "Set the scene's 2D skybox tint / indirect lighting (re-uses an existing SkyBox2D or creates one). Darken the tint for night/dusk. Optionally point it at a .vmat sky material (silently kept as-is if the material fails to load). Returns { created, gameObject } — gameObject.id is the sky object; screenshot to verify the change.",
    {
      tint: ColorSchema.optional().describe("Sky tint colour"),
      indirectLighting: z
        .boolean()
        .optional()
        .describe("Whether the sky contributes indirect/ambient light"),
      material: z.string().optional().describe("Path to a .vmat sky material (optional)"),
      name: z.string().optional().describe("Name for the sky GameObject if one is created"),
    },
    async (params) => {
      const res = await bridge.send("set_skybox", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── apply_atmosphere (preset) ─────────────────────────────────────────
  server.tool(
    "apply_atmosphere",
    "One-call scene mood: composes ambient + directional light, gradient fog, and a camera post-fx stack (tonemap + colour grade + vignette) tuned for the chosen mood. Idempotent — re-runs update the same 'Atmosphere *' objects. Returns { applied, mood, components, postFxCamera } — the post-fx stack is only applied when a camera exists (postFxCamera is null otherwise; add a camera and re-run). Screenshot to verify.",
    {
      mood: z
        .enum(["horror-night", "foggy-dawn", "overcast", "warm-interior"])
        .describe("Atmosphere preset"),
    },
    async (params) => {
      const res = await bridge.send("apply_atmosphere", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── apply_post_fx_look (preset) ───────────────────────────────────────
  server.tool(
    "apply_post_fx_look",
    "Apply just a camera post-processing look (no lights/fog): cinematic (tonemap + bloom + soft vignette), filmic-horror (desaturated, high-contrast, heavy vignette, film grain), or clean (tonemap only). Errors if the scene has no CameraComponent. Returns { applied, look, components, camera } listing the effect components added to the main camera — tune them individually afterwards with add_post_process.",
    {
      look: z
        .enum(["cinematic", "filmic-horror", "clean"])
        .describe("Post-fx look preset"),
    },
    async (params) => {
      const res = await bridge.send("apply_post_fx_look", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_envmap_probe ─────────────────────────────────────────────────
  server.tool(
    "add_envmap_probe",
    "Add an environment reflection/ambient probe (EnvmapProbe) at a position with a cubic influence volume — captures local reflections and indirect light for nearby surfaces. IMPORTANT: a placed probe captures NOTHING until baked — follow with bake_reflections. Returns { created, gameObject } — gameObject.id is the probe object's GUID.",
    {
      name: z.string().optional().describe("GameObject name"),
      position: Vector3Schema.optional().describe("World position (centre of the probe)"),
      size: z.number().optional().describe("Cubic influence size in units (default 1024)"),
      tint: ColorSchema.optional().describe("Tint applied to the captured environment"),
      feathering: z
        .number()
        .optional()
        .describe("Edge feathering 0-1 for blending between overlapping probes"),
    },
    async (params) => {
      const res = await bridge.send("add_envmap_probe", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── spawn_particle (Batch 18: VFX) ───────────────────────────────────
  server.tool(
    "spawn_particle",
    "EXPERIMENTAL — build an additive runtime ParticleEffect (no texture asset needed): kind = fire, embers, sparks, magic, dust, blood, or snow ('smoke' returns an error). Returns { created, kind, gameObject } — the component graph is created, but this runtime particle path has NOT been verified to render through the bridge; for particles you can actually see, prefer spawn_vpcf.",
    {
      kind: z
        .enum(["fire", "embers", "sparks", "magic", "dust", "blood", "snow"])
        .describe("Particle preset"),
      position: Vector3Schema.optional().describe("World position"),
      color: ColorSchema.optional().describe("Override the particle tint"),
      name: z.string().optional().describe("GameObject name"),
    },
    async (params) => {
      const res = await bridge.send("spawn_particle", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_trail ────────────────────────────────────────────────────────
  server.tool(
    "add_trail",
    "Attach a motion trail (TrailRenderer) to an existing GameObject (via targetId) so it leaves a trail as it moves — or create a standalone trail object. Only visible while the object is moving.",
    {
      targetId: z.string().optional().describe("GUID of the GameObject to attach the trail to (else a new 'Trail' object is made)"),
      position: Vector3Schema.optional().describe("World position when creating a standalone trail"),
      lifetime: z.number().optional().describe("How long (seconds) trail points persist"),
      maxPoints: z.number().optional().describe("Max points in the trail"),
      pointDistance: z.number().optional().describe("Min distance between trail points"),
      name: z.string().optional().describe("GameObject name when creating a new one"),
    },
    async (params) => {
      const res = await bridge.send("add_trail", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_beam ─────────────────────────────────────────────────────────
  server.tool(
    "add_beam",
    "EXPERIMENTAL — create an energy/laser beam (BeamEffect) from a position to a target point (default: 128u straight up) — additive, tintable. Returns { created, gameObject } — gameObject.id is the beam object's GUID. Like the other runtime particle tools, rendering through the bridge is unverified; use spawn_vpcf when you need a guaranteed-visible effect.",
    {
      position: Vector3Schema.optional().describe("Beam start (world position of the beam object)"),
      target: Vector3Schema.optional().describe("Beam end point in world space (default: 128u up)"),
      width: z.number().optional().describe("Beam width/scale (default 4)"),
      color: ColorSchema.optional().describe("Beam colour (default white)"),
      name: z.string().optional().describe("GameObject name"),
    },
    async (params) => {
      const res = await bridge.send("add_beam", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_particle_effect (generic / raw params) ────────────────────
  server.tool(
    "create_particle_effect",
    "Build a custom additive particle effect from raw params (ParticleEffect + cone emitter + sprite renderer). Use this when the spawn_particle presets aren't what you want. Texture-free (additive Texture.White glow).",
    {
      position: Vector3Schema.optional().describe("World position"),
      color: ColorSchema.optional().describe("Particle tint (default white)"),
      rate: z.number().optional().describe("Particles per second when looping (default 30)"),
      burst: z.number().optional().describe("Particle count for a one-shot burst when loop=false (default 30)"),
      loop: z.boolean().optional().describe("Continuous emission (default true) vs a single burst"),
      lifetime: z.number().optional().describe("Particle lifetime in seconds (default 2)"),
      size: z.number().optional().describe("Particle size (default 4)"),
      speed: z.number().optional().describe("Emission speed along the cone (default 100)"),
      coneAngle: z.number().optional().describe("Cone half-angle in degrees; ~85 ≈ hemisphere (default 40)"),
      gravity: z.number().optional().describe("Downward force (default 0 = none)"),
      additive: z.boolean().optional().describe("Additive (glow) blending (default true)"),
      maxParticles: z.number().optional().describe("Max live particles (default 500)"),
      name: z.string().optional().describe("GameObject name"),
    },
    async (params) => {
      const res = await bridge.send("create_particle_effect", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── spawn_vpcf ──────────────────────────────────────────────────────
  server.tool(
    "spawn_vpcf",
    "Spawn a REAL particle system by playing a compiled .vpcf asset through LegacyParticleSystem — the reliable path that actually renders, unlike spawn_particle/create_particle_effect (which build a runtime ParticleEffect graph that shows nothing). Defaults to 'particles/impact.generic.vpcf' (a sparks/impact burst — the only particle .vpcf reliably present; set looped + a warm tint for a fire-ish effect). Pass your own compiled .vpcf logical path if you have one. Screenshot-verifiable in edit mode.",
    {
      vpcf: z
        .string()
        .optional()
        .describe("Logical .vpcf path (default 'particles/impact.generic.vpcf'). NOT the .vpcf_c or .sbox/cloud cache path."),
      position: Vector3Schema.optional().describe("World position (default origin)"),
      name: z.string().optional().describe("GameObject name"),
      looped: z.boolean().optional().describe("Loop the effect (default true)"),
      playbackSpeed: z.number().optional().describe("Playback speed multiplier"),
      tint: ColorSchema.optional().describe("Color tint (e.g. orange for fire); applied to the live SceneObject if it's ready this frame"),
    },
    async (params) => {
      const res = await bridge.send("spawn_vpcf", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── bake_reflections ────────────────────────────────────────────────
  server.tool(
    "bake_reflections",
    "Bake all EnvmapProbe reflection probes in the scene (EnvmapProbe.BakeAll) so they actually capture their surroundings — placing a probe with add_envmap_probe does nothing visible until it's baked. This is a real editor compute step, not a component setter. Runs async — returns { baking, count, note, probes } immediately (each probe: id, name, mode, hasBaked), or { baked: false, count: 0 } when the scene has no probes; re-screenshot after a moment to see reflections appear.",
    {},
    async (params) => {
      const res = await bridge.send("bake_reflections", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
