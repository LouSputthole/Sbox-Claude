import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Dialogue & Camera FX pair — two scaffold generators:
 *
 *   - generate_lipsync_dialogue  NPCs speak their lines with moving mouths
 *                                (Synthesizer TTS + viseme-driven morphs + loose
 *                                create_dialogue_system HUD bind)
 *   - create_camera_effects      static conveniences over the SDK's built-in
 *                                CameraComponent.AddShake/AddPunch/AddTilt
 *
 * Both write a .cs file into the project (scene/file-mutating; refused during
 * play mode by the bridge dispatch). Both templates were live-verified on
 * 2026-07-13: default render written into the live project, hotloaded, compile
 * clean, TypeLibrary-load confirmed, then deleted. Key SDK facts verified the
 * same day: Handle.LipSync.Visemes is IReadOnlyList<float> in the engine's
 * 15-viseme order (read live from Sandbox.LipSync.VisemeNames), the viseme->
 * morph mapping is baked into models (Model.GetVisemeMorph — nonzero on
 * Citizen), and the camera built-ins are whitelist-callable from game code.
 *
 * Mirrors the aisystems.ts module shape: zod params, one bridge.send per tool,
 * JSON.stringify(res.data) on success.
 */

// A world-space Vector3 accepted as EITHER an object {x,y,z} OR a comma string
// "x,y,z", passed through unchanged. The C# handler parses both forms.
const Vec3 = z
  .union([
    z.object({
      x: z.number().describe("X"),
      y: z.number().describe("Y"),
      z: z.number().describe("Z"),
    }),
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('A world-space Vector3 — object {x,y,z} OR comma string "x,y,z"');

export function registerDialogueFxTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── generate_lipsync_dialogue ─────────────────────────────────────
  server.tool(
    "generate_lipsync_dialogue",
    "Generate a lipsync dialogue performer — NPCs SPEAK their lines with MOVING MOUTHS: a sealed Component holding a [Property] line list (speaker GameObject name + text) that, per line, (a) mirrors the line into a generated create_dialogue_system HUD when one exists (loose TypeLibrary capability bind: List<string> Lines + Begin() + bool IsActive — neither system references the other), (b) speaks the text via Sandbox.Speech.Synthesizer positionally AT the speaker (per-speaker voice name/gender/age/rate via the Voices list), (c) drives the speaker's SkinnedModelRenderer mouth morphs from the live viseme stream — Handle.LipSync.Visemes (IReadOnlyList<float> in the engine's 15-viseme order, read live from Sandbox.LipSync.VisemeNames 2026-07-13) multiplied through the model's own baked viseme->morph table (Model.GetVisemeMorph — verified nonzero on Citizen, e.g. viseme_AA -> openjawL/R; NOT a hand-guessed morph map), with MorphScale + smoothing, and (d) advances when the audio handle stops (LineGapSeconds pause, LineTimeoutSeconds safety-skip, Skip() to cut a line short, StopDialogue() to abort). Static events: OnLineStarted(dialogue, lineIndex, speaker) + OnDialogueFinished(dialogue). Returns {created, path, className, lineCount, voiceCount, propertyNames[], note}. Next: trigger_hotload + get_compile_errors, attach (targetId re-call or add_component_with_properties), fill Lines/Voices in the inspector or bake them via params, call Begin() from game code (or autoStart:true; pair with create_interactable). Limits & honesty: the editor cannot playtest audio, so the LIVE viseme stream is RUNTIME-UNVERIFIED — the generated LogVisemes() helper + DebugLogVisemes property confirm it in seconds in play mode (the API surface and mapping data ARE live-verified); models without baked viseme data log a warning and stay audio-only (Citizen has it); voices are machine/OS-specific and TrySetVoice is best-effort; LOCAL-only — call Begin() inside an [Rpc.Broadcast] for everyone; a bound HUD's own advance input stays active (it only ends that HUD's display, not the audio). Refused during play mode; refuses to overwrite an existing file.",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name. Defaults to 'LipsyncDialogue'. Sanitized to a valid C# identifier."),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under the project root for the .cs file. Defaults to 'Code'."),
      lines: z
        .array(
          z.object({
            speaker: z.string().optional().describe("Scene GameObject name to speak from (case-insensitive; drives THAT object's mouth). Empty/omitted = the component's own GameObject."),
            text: z.string().describe("The spoken line."),
          })
        )
        .optional()
        .describe("Dialogue lines baked as inspector-editable defaults. Defaults to a two-line demo on the own GameObject."),
      voices: z
        .array(
          z.object({
            speaker: z.string().describe("Speaker name these voice settings apply to (matches lines[].speaker, case-insensitive)."),
            voiceName: z.string().optional().describe("Exact installed OS voice name (machine-specific — the generated component's Voices list is inspector-editable). Empty = use gender/age hints."),
            voiceGender: z.string().optional().describe("Voice gender hint used when voiceName is empty (e.g. 'Female', 'Male'). Pair with voiceAge. Passed through unvalidated."),
            voiceAge: z.string().optional().describe("Voice age hint paired with voiceGender (e.g. 'Adult', 'Child', 'Senior')."),
            rate: z.number().optional().describe("Speaking rate offset (integer): negative = slower, positive = faster. Defaults to 0."),
          })
        )
        .optional()
        .describe("Per-speaker voice settings baked as defaults. Defaults to empty (every speaker uses the OS default voice)."),
      volume: z.number().optional().describe("Playback volume for spoken lines. Defaults to 1."),
      positional: z
        .boolean()
        .optional()
        .describe("true (default): 3D audio parented to the speaker GameObject. false: flat 2D narrator voice."),
      driveMouth: z
        .boolean()
        .optional()
        .describe("true (default): drive the speaker's SkinnedModelRenderer mouth morphs from the viseme stream. false: audio-only."),
      morphScale: z
        .number()
        .optional()
        .describe("Multiplier on viseme-derived morph weights (same idea as Sandbox.LipSync.MorphScale). Defaults to 1."),
      mouthSmoothSeconds: z
        .number()
        .optional()
        .describe("Seconds of exponential smoothing on mouth morphs (0 = raw viseme weights). Defaults to 0.05."),
      lineGapSeconds: z
        .number()
        .optional()
        .describe("Pause between a line's audio ending and the next line starting. Defaults to 0.2."),
      lineTimeoutSeconds: z
        .number()
        .optional()
        .describe("Safety: a line whose audio never starts (synthesis pending/failed) is skipped after this many seconds. Defaults to 20."),
      bindHud: z
        .boolean()
        .optional()
        .describe("true (default): loosely bind a create_dialogue_system HUD in the scene (TypeLibrary capability match) and mirror each line into it. false: no HUD mirroring."),
      autoStart: z
        .boolean()
        .optional()
        .describe("true: Begin() fires in OnStart. Defaults to false (call Begin() from game code)."),
      debugLogVisemes: z
        .boolean()
        .optional()
        .describe("true: log the live viseme stream ~4x/second while speaking — the fast way to runtime-verify the mouth drive. Defaults to false."),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the component to (only attaches if the type is already in the TypeLibrary — hotload first, then re-call or use add_component_with_properties)."),
    },
    async (params) => {
      const res = await bridge.send("generate_lipsync_dialogue", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_camera_effects ─────────────────────────────────────────
  server.tool(
    "create_camera_effects",
    "Generate static conveniences over the SDK's BUILT-IN camera effects — CameraComponent.AddShake(amplitude, frequency, duration), AddPunch(Vector3 direction, amplitude, frequency, duration, fovAmplitude), AddPunch(Angles, ...) and AddTilt(Angles, duration, easeTime), all fire-and-forget, self-expiring, whitelist-verified in sandboxed game code 2026-07-13: a sealed Component exposing {name}.Shake/ShakeAt/Punch/PunchAngles/Tilt statics that resolve the main camera (Scene.Camera, else IsMainCamera search, else first camera; warn + return null when the scene has none) and return the live Sandbox.CameraEffectSystem.BaseEffect (Stop()/IsDone; ShakeAt sets Epicenter+Radius for distance falloff), plus one-word preset triggers — HitPunch() / ExplosionShake() / ExplosionShakeAt(position, radius) / LandingTilt() — driven by [Property] tunables. Statics work with NO instance placed; place the component only to tune presets in the inspector. RELATIONSHIP: create_camera_shake is the CONTINUOUS trauma model (AddTrauma accumulates and decays); these built-ins are ONE-SHOT engine effects — they compose safely, but don't fire both for the same event or hits feel doubled. Returns {created, path, className, staticApi[], presetTriggers[], propertyNames[], note}. Next: trigger_hotload + get_compile_errors, call the statics from game code (e.g. {name}.Shake(4, 25, 0.8) on explosion) or attach via targetId and trigger presets. Limits & honesty: compile + camera resolution verified; the editor cannot judge FEEL — tune amplitudes in a human playtest; effects are LOCAL visuals (wrap in [Rpc.Broadcast] for everyone). Refused during play mode; refuses to overwrite an existing file.",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name. Defaults to 'CameraFx'. Sanitized to a valid C# identifier."),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under the project root for the .cs file. Defaults to 'Code'."),
      hitPunchDirection: Vec3.optional().describe(
        "HitPunch preset: punch direction. Defaults to Vector3.Backward (camera kicks back)."
      ),
      hitPunchAmplitude: z.number().optional().describe("HitPunch preset: positional kick strength. Defaults to 8."),
      hitPunchFrequency: z.number().optional().describe("HitPunch preset: oscillation frequency. Defaults to 20."),
      hitPunchDuration: z.number().optional().describe("HitPunch preset: seconds. Defaults to 0.25."),
      hitPunchFovAmplitude: z.number().optional().describe("HitPunch preset: FOV kick amount (0 = none). Defaults to 3."),
      explosionShakeAmplitude: z.number().optional().describe("ExplosionShake preset: shake strength. Defaults to 5."),
      explosionShakeFrequency: z.number().optional().describe("ExplosionShake preset: oscillation frequency. Defaults to 25."),
      explosionShakeDuration: z.number().optional().describe("ExplosionShake preset: seconds. Defaults to 0.8."),
      landingTiltAngles: Vec3.optional().describe(
        "LandingTilt preset as {x: pitch, y: yaw, z: roll} degrees (or 'p,y,r'). Defaults to 5 pitch / 0 yaw / 2 roll."
      ),
      landingTiltDuration: z.number().optional().describe("LandingTilt preset: seconds the tilt lasts. Defaults to 0.35."),
      landingTiltEase: z.number().optional().describe("LandingTilt preset: ease-in/out time within the duration. Defaults to 0.15."),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the component to — only needed to tune presets in the inspector; the statics work with no instance (only attaches if the type is already in the TypeLibrary — hotload first)."),
    },
    async (params) => {
      const res = await bridge.send("create_camera_effects", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
