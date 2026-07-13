import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * AI & Systems pack — five scaffold generators:
 *
 *   - create_needs_system      sim/tycoon needs engine (decay -> Happiness + events)
 *   - create_utility_ai        scored-action brain (abstract Action base + brain + 2 examples)
 *   - create_npc_schedule_brain daily-routine NPC bound to the day-night clock contract
 *   - create_event_bus         typed LOCAL pub/sub static class (not a Component)
 *   - add_tts_voice            TTS speaker over Sandbox.Speech.Synthesizer
 *
 * All write a .cs file into the project (scene/file-mutating; refused during play
 * mode by the bridge dispatch). Every generated template was live-compile-verified
 * (hotload + TypeLibrary-load) against the 2026-07 SDK on 2026-07-12.
 *
 * Mirrors the gamefeel.ts module shape: zod params, one bridge.send per tool,
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

export function registerAiSystemsTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_needs_system ───────────────────────────────────────────
  server.tool(
    "create_needs_system",
    "Generate a sim/tycoon needs engine component: a [Property] list of need definitions (name, decay rate/s, critical threshold, weight) with per-need 0..100 values that decay over Time.Delta, Satisfy(name, amount) to restore, an aggregate Happiness (weighted mean, [Sync(FromHost)] when networked), and static OnNeedCritical (edge-triggered: fires once crossing below threshold, re-arms above) + OnHappinessChanged (>0.25-point moves) events. Returns {created, path, className, needs[], propertyNames[], note}. Next: trigger_hotload, get_compile_errors, then attach via targetId re-call or add_component_with_properties; drive from game code (e.g. a create_interactable that calls Satisfy). Limits: per-need values live on the simulating machine only (host) — sync per-need UI yourself via RPCs; events fire on the simulating machine only; networked default true means a no-session solo playtest won't tick (everything is a proxy) — pass networked:false to iterate solo. Refused during play mode; refuses to overwrite an existing file.",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name. Defaults to 'NeedsSystem'. Sanitized to a valid C# identifier."),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under the project root for the .cs file. Defaults to 'Code'."),
      needs: z
        .array(
          z.object({
            name: z.string().describe("Need name, e.g. 'Hunger' (the key for Satisfy/GetNeed)."),
            decayPerSecond: z.number().optional().describe("Points lost per second on the 0..100 scale. Defaults to 0.5."),
            criticalThreshold: z.number().optional().describe("OnNeedCritical fires when the value falls below this. Defaults to 20."),
            weight: z.number().optional().describe("Contribution to the Happiness weighted mean. Defaults to 1."),
          })
        )
        .optional()
        .describe("Need definitions baked as inspector-editable defaults. Defaults to the classic sim trio: Hunger(0.8/s), Energy(0.5/s), Fun(0.3/s)."),
      networked: z
        .boolean()
        .optional()
        .describe("true (default): host-authoritative (IsProxy guard) + [Sync(FromHost)] Happiness — needs a host session. false: local build that ticks in a solo playtest."),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the component to (only attaches if the type is already in the TypeLibrary — hotload first, then re-call or use add_component_with_properties)."),
    },
    async (params) => {
      const res = await bridge.send("create_needs_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_utility_ai ─────────────────────────────────────────────
  server.tool(
    "create_utility_ai",
    "Generate a utility-AI (scored-action) brain: one file with an abstract {name}Action : Component base (Score() 0..1 + Begin/Tick/End lifecycle), a sealed {name}Brain that every EvaluateInterval picks the highest-scoring sibling action (score × ScoreWeight, current action gets +HysteresisBonus so near-ties don't flip-flop), and two example actions — {name}IdleAction (constant fallback score) and {name}WanderAction (desire builds while idle, walks to random points by direct transform movement, no navmesh). How it differs from create_npc_brain: the FSM has a FIXED transition table; here behavior EMERGES from per-frame scores — add behaviors by subclassing the base on the same GameObject, no transition wiring. Returns {created, path, classNames[4], propertyNames[], note}. Next: trigger_hotload + get_compile_errors, attach the brain AND example actions to one GameObject (targetId attaches only the brain), verify in play mode via get_runtime_property CurrentActionName. Limits: networked default true = host-authoritative (won't tick in a no-session solo playtest — use networked:false); actions Tick on the simulating machine only. Refused during play mode; refuses to overwrite an existing file.",
    {
      name: z
        .string()
        .optional()
        .describe("System prefix — generates {name}Action / {name}Brain / {name}IdleAction / {name}WanderAction in {name}Ai.cs. Defaults to 'Utility'. Sanitized to a valid C# identifier."),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under the project root for the .cs file. Defaults to 'Code'."),
      evaluateInterval: z
        .number()
        .optional()
        .describe("Seconds between score evaluations (the active action still Ticks every frame). Defaults to 0.25."),
      hysteresisBonus: z
        .number()
        .optional()
        .describe("Score bonus the current action gets during evaluation — stickiness that prevents flip-flopping between near-tied actions. Defaults to 0.15."),
      moveSpeed: z
        .number()
        .optional()
        .describe("Example WanderAction walk speed in world units/s. Defaults to 80."),
      wanderRadius: z
        .number()
        .optional()
        .describe("Example WanderAction roam radius around its start position. Defaults to 300."),
      networked: z
        .boolean()
        .optional()
        .describe("true (default): host-authoritative brain (IsProxy guard) + [Sync(FromHost)] CurrentActionName. false: local build for solo iteration."),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the BRAIN to (actions must be added separately; only attaches if the type is already in the TypeLibrary — hotload first)."),
    },
    async (params) => {
      const res = await bridge.send("create_utility_ai", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_npc_schedule_brain ─────────────────────────────────────
  server.tool(
    "create_npc_schedule_brain",
    "Generate a daily-routine NPC brain: a [Property] list of schedule entries (startHour/endHour 0..24, taskName, target = named scene GameObject or fixed position), the hour read from any create_day_night_clock component (capability match: a float TimeOfDay property, same GameObject first then scene-wide) with an HONEST fallback to its own internal clock when none exists (check the generated UsingClockComponent bool), walking the NPC to the active entry's target and idling outside the schedule, plus a static OnTaskChanged(brain, taskName) event and [Sync(FromHost)] CurrentTask. Entries with endHour < startHour wrap past midnight. Returns {created, path, className, tasks[], propertyNames[], note}. Next: trigger_hotload + get_compile_errors, attach (targetId or add_component_with_properties), create the named target GameObjects (e.g. 'WorkSpot'), pair with create_day_night_clock for shared time, verify via get_runtime_property CurrentTask in play mode. Limits: default movement is a direct transform walk (walks through walls) — pass useNavMeshAgent:true for pathfinding (then bake_navmesh is REQUIRED); a clock with a different shape (e.g. 0..1 DayProgress) will NOT bind; networked default true won't tick in a no-session solo playtest (networked:false to iterate). Refused during play mode; refuses to overwrite an existing file.",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name. Defaults to 'NpcScheduleBrain'. Sanitized to a valid C# identifier."),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under the project root for the .cs file. Defaults to 'Code'."),
      schedule: z
        .array(
          z.object({
            startHour: z.number().describe("Entry start hour, 0..24 (inclusive)."),
            endHour: z.number().describe("Entry end hour, 0..24 (exclusive). Smaller than startHour = wraps past midnight (e.g. 22 -> 6)."),
            taskName: z.string().describe("Task label, e.g. 'Work' — surfaced via CurrentTask + OnTaskChanged."),
            targetName: z.string().optional().describe("Named scene GameObject to walk to (case-insensitive; wins over targetPosition). Missing name = NPC idles."),
            targetPosition: Vec3.optional().describe("Fixed world position to walk to, used when targetName is empty."),
          })
        )
        .optional()
        .describe("Schedule entries baked as inspector-editable defaults. Defaults to Work 8-17 @ 'WorkSpot', Relax 17-22 @ 'HomeSpot' (idles/sleeps otherwise)."),
      moveSpeed: z
        .number()
        .optional()
        .describe("Walk speed in world units/s. Defaults to 100."),
      arriveDistance: z
        .number()
        .optional()
        .describe("Distance at which the NPC counts as arrived and idles at the spot. Defaults to 32."),
      useNavMeshAgent: z
        .boolean()
        .optional()
        .describe("true: move via NavMeshAgent.MoveTo (real pathfinding — REQUIRES bake_navmesh or the NPC won't move). Defaults to false (direct transform walk, no navmesh needed, walks through walls)."),
      fallbackDayLengthSeconds: z
        .number()
        .optional()
        .describe("Internal fallback clock only: real seconds per 24 in-game hours when NO TimeOfDay clock component exists. Defaults to 600."),
      fallbackStartHour: z
        .number()
        .optional()
        .describe("Internal fallback clock only: starting hour 0..24. Defaults to 8."),
      networked: z
        .boolean()
        .optional()
        .describe("true (default): host-authoritative (IsProxy guard) + [Sync(FromHost)] CurrentTask. false: local build for solo iteration."),
      targetId: z
        .string()
        .optional()
        .describe("GUID of the NPC GameObject to attach to (only attaches if the type is already in the TypeLibrary — hotload first)."),
    },
    async (params) => {
      const res = await bridge.send("create_npc_schedule_brain", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_event_bus ──────────────────────────────────────────────
  server.tool(
    "create_event_bus",
    "Generate a typed LOCAL pub/sub event bus: a pure STATIC class (NOT a Component — nothing to place in the scene) with Subscribe<T>(owner, Action<T>), Unsubscribe(owner) (removes all of that owner's handlers across every event type), Publish<T>(evt) (synchronous, exact-type-T subscribers only, snapshot-iterated so handlers may subscribe/unsubscribe mid-publish), Count<T>() and Clear(), keyed by a plain Dictionary<Type, List<(object, Delegate)>> — plus a tiny example event record ({name}Ping). Decouples game systems: the quest system publishes 'EnemyDied', UI and achievements subscribe, neither knows the other. Returns {created, path, className, exampleEvent, api[], note}. Next: trigger_hotload + get_compile_errors, then Subscribe in components' OnStart and — REQUIRED — Unsubscribe(this) in OnDestroy: handler lists hold PLAIN references (no weak refs), so a component that never unsubscribes leaks itself for the scene's life; call Clear() on scene teardown. Limits: LOCAL only — Publish reaches the calling machine's subscribers, NOT other clients; for networked events pair with [Rpc.Broadcast]/[Rpc.Host] methods that Publish on arrival. No base-type dispatch (Publish<Base> won't reach Subscribe<Derived>). Refuses to overwrite an existing file; refused during play mode.",
    {
      name: z
        .string()
        .optional()
        .describe("Static class/file name. Defaults to 'EventBus'. The example event record is named {name}Ping. Sanitized to a valid C# identifier."),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under the project root for the .cs file. Defaults to 'Code'."),
    },
    async (params) => {
      const res = await bridge.send("create_event_bus", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_tts_voice ─────────────────────────────────────────────────
  server.tool(
    "add_tts_voice",
    "Generate a text-to-speech speaker component over Sandbox.Speech.Synthesizer (the OS speech engine — dynamic NPC dialog with zero recorded VO): call <class>.Say(\"text\") from game code and it builds a Synthesizer (TrySetVoice by exact VoiceName, else gender/age hint, else OS default) -> WithText -> WithRate -> Play(), returning a tracked SoundHandle — positional 3D parented to the speaker (default) or flat 2D, with stop-previous-on-say interruption, IsSpeaking, StopSpeaking(), and LogVoices() to enumerate installed OS voices. Returns {created, path, className, propertyNames[], note}. Next: trigger_hotload + get_compile_errors, attach (targetId or add_component_with_properties), then Say from game code — LOCAL audio only, wrap the Say call in an [Rpc.Broadcast] handler for everyone to hear. Limits & honesty: the editor cannot playtest audio, so RUNTIME speech is UNVERIFIED (the API surface compiles — verify with your ears in play mode); voices are machine/OS-specific and TrySetVoice is best-effort; LIPSYNC IS NOT AUTO-WIRED — s&box's Sandbox.LipSync component consumes a BaseSoundComponent, not the raw SoundHandle TTS produces, and Synthesizer.OnVisemeReached's delegate arg types can't be confirmed via reflection; enableVisemeData:true enables Handle.LipSync.Visemes for your own mouth-drive code (runtime-unverified). Refuses to overwrite an existing file; refused during play mode.",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name. Defaults to 'TtsSpeaker'. Sanitized to a valid C# identifier."),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under the project root for the .cs file. Defaults to 'Code'."),
      voiceName: z
        .string()
        .optional()
        .describe("Exact installed OS voice name (machine-specific — the generated LogVoices() lists them at runtime). Empty = use voiceGender/voiceAge, or the OS default."),
      voiceGender: z
        .string()
        .optional()
        .describe("Voice gender hint used only when voiceName is empty (e.g. 'Female', 'Male'). Must be paired with voiceAge. Passed through unvalidated."),
      voiceAge: z
        .string()
        .optional()
        .describe("Voice age hint paired with voiceGender (e.g. 'Adult', 'Child', 'Senior'). Passed through unvalidated."),
      rate: z
        .number()
        .optional()
        .describe("Speaking rate offset (integer): negative = slower, positive = faster. Defaults to 0 (normal)."),
      volume: z
        .number()
        .optional()
        .describe("Playback volume for spoken lines. Defaults to 1."),
      positional: z
        .boolean()
        .optional()
        .describe("true (default): 3D sound parented to the speaker GameObject (follows it). false: flat 2D voice on the listener (narrator/UI style)."),
      stopPreviousOnSay: z
        .boolean()
        .optional()
        .describe("true (default): a new Say() fades out the still-playing previous line. false: lines overlap."),
      stopFadeSeconds: z
        .number()
        .optional()
        .describe("Fade-out duration used when interrupting/stopping a line. Defaults to 0.1."),
      enableVisemeData: z
        .boolean()
        .optional()
        .describe("true: sets Handle.LipSync.Enabled on each played line so custom mouth-drive code can read Handle.LipSync.Visemes. Runtime behavior unverified (editor can't playtest audio). Defaults to false."),
      targetId: z
        .string()
        .optional()
        .describe("GUID of the speaker GameObject to attach to (only attaches if the type is already in the TypeLibrary — hotload first)."),
    },
    async (params) => {
      const res = await bridge.send("add_tts_voice", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
