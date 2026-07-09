import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Gameplay scaffold tools — Phase 1 of "playable game in one ask".
 *
 * Two low-level capability gap-fillers:
 *   - set_component_reference   wire a component property to a LIVE scene object
 *   - add_component_to_new_object  atomic create-GO + add-component + props
 *
 * Three system scaffolds (generate a clean, self-contained .cs, optionally place it):
 *   - create_objective_system   the win/lose primitive (ObjectiveManager)
 *   - create_health_system      Health/damage component
 *   - create_pickup             trigger-based collectible
 *
 * The `sbox-scaffold-game` skill orchestrates these into a playable starter.
 * All scene/file-mutating; refused during play mode by the bridge dispatch.
 */

// A 3D vector accepted as EITHER an object {x,y,z} OR a comma string "x,y,z",
// passed through unchanged. The C# handler parses both forms (source of truth).
// See the cross-language vector/color contract.
const Vector3Object = z.object({
  x: z.number().describe("X coordinate"),
  y: z.number().describe("Y coordinate"),
  z: z.number().describe("Z coordinate"),
});

const Vector3Schema = z
  .union([
    Vector3Object,
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('3D vector — object {x,y,z} OR comma string "x,y,z"');

const RotationSchema = z
  .object({
    pitch: z.number().describe("Pitch angle in degrees"),
    yaw: z.number().describe("Yaw angle in degrees"),
    roll: z.number().describe("Roll angle in degrees"),
  })
  .describe("Euler rotation with pitch, yaw, roll in degrees");

export function registerGameplayTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── set_component_reference ───────────────────────────────────────
  // Assign one scene GameObject (or a component on it) to a component property.
  // set_property can now also set a GameObject/Component ref from a GUID, but this
  // tool is the ergonomic choice for refs: it can pull a specific component type off
  // the target (targetComponent) and validates the wiring. set_prefab_ref is for
  // PREFAB assets. Wires live scene objects: Spawner.SpawnPoint = thatEmpty,
  // Camera follows thatPlayer, Door.Hinge = thatPivot.
  server.tool(
    "set_component_reference",
    "Wire a component's GameObject/Component-typed property to ANOTHER live object in the scene by GUID (e.g. ObjectiveManager.Player = the player, a camera's follow target, a door's hinge). Preferred for object/component refs (can pick a specific component type off the target via targetComponent, and validates). set_property also accepts a GUID for ref props; set_prefab_ref is for prefab assets. Set clear:true to null the reference",
    {
      id: z
        .string()
        .describe("GUID of the GameObject that HOLDS the component you're writing into"),
      component: z
        .string()
        .describe("Component type name on that object (e.g. 'ObjectiveManager', 'CameraComponent')"),
      property: z
        .string()
        .describe("The property to set (must be a GameObject- or Component-typed property)"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of the GameObject to reference. Required unless clear:true"),
      targetComponent: z
        .string()
        .optional()
        .describe("If the property is a Component subtype, the specific component type to pull off the target object. Omit to auto-match by the property's type"),
      clear: z
        .boolean()
        .optional()
        .describe("If true, set the reference to null instead of assigning a target"),
    },
    async (params) => {
      const res = await bridge.send("set_component_reference", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_component_to_new_object ───────────────────────────────────
  server.tool(
    "add_component_to_new_object",
    "Create a new GameObject, add a component, set its properties, and optionally parent/position/tag it — all in one atomic call. Collapses the create_gameobject → add_component_with_properties → set_parent sequence. NOTE: a freshly GENERATED component type only resolves after a trigger_hotload; generate the script, hotload, THEN call this",
    {
      name: z
        .string()
        .optional()
        .describe("Display name for the new GameObject. Defaults to the component type name"),
      component: z
        .string()
        .describe("Component type name to add (e.g. 'CameraComponent', 'ObjectiveManager'). Use list_available_components to find valid types"),
      properties: z
        .record(z.unknown())
        .optional()
        .describe("Key-value map of property names to values, auto-converted to the right type (same convention as add_component_with_properties)"),
      position: Vector3Schema.optional().describe("World position"),
      rotation: RotationSchema.optional().describe("World rotation"),
      scale: Vector3Schema.optional().describe("World scale (per-axis)"),
      parentId: z
        .string()
        .optional()
        .describe("GUID of a parent GameObject. Omit for scene root"),
      tags: z
        .array(z.string())
        .optional()
        .describe("Tags to add to the new GameObject (e.g. ['player'])"),
    },
    async (params) => {
      const res = await bridge.send("add_component_to_new_object", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_objective_system ───────────────────────────────────────
  // The win/lose primitive — turns "objects in a scene" into "a game with a goal".
  server.tool(
    "create_objective_system",
    "Generate an ObjectiveManager component — the win/lose brain of a game. Tracks an objective (collect_all / reach_goal / survive_time / eliminate_all), fires a win, and handles a lose condition (fall below kill-Z / timer / out of lives). Self-contained C#; other systems call ObjectiveManager.Instance. Optionally placed as a scene singleton. Returns { created, path, className, gameObject, note } — gameObject is the placed singleton, or null with a note when the fresh type isn't in the TypeLibrary yet. Follow with trigger_hotload, then get_compile_errors; if placement was skipped, place with add_component_to_new_object after the hotload",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'ObjectiveManager'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      objective: z
        .enum(["collect_all", "reach_goal", "survive_time", "eliminate_all"])
        .optional()
        .describe("Win condition. Defaults to 'reach_goal'"),
      targetCount: z
        .number()
        .int()
        .optional()
        .describe("How many to collect/eliminate (for collect_all / eliminate_all). Defaults to 3"),
      timeLimit: z
        .number()
        .optional()
        .describe("Seconds — survive this long to win (survive_time) or before losing (loseOn=timer). Defaults to 60"),
      loseOn: z
        .enum(["fall", "timer", "lives", "none"])
        .optional()
        .describe("Lose condition. 'fall' = player drops below killZ. Defaults to 'fall'"),
      killZ: z
        .number()
        .optional()
        .describe("World Z below which the player is considered fallen out of the world. Defaults to -1000"),
      lives: z
        .number()
        .int()
        .optional()
        .describe("Lives before game over (loseOn=lives). Defaults to 1"),
      placeInScene: z
        .boolean()
        .optional()
        .describe("Place the manager as a scene singleton. Defaults to true. (Only attaches if the type is already loaded — generate, hotload, then it places; otherwise add it after hotload.)"),
    },
    async (params) => {
      const res = await bridge.send("create_objective_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_health_system ──────────────────────────────────────────
  server.tool(
    "create_health_system",
    "Generate a Health component: MaxHealth, [Sync] CurrentHealth, TakeDamage/Heal, an OnDeath event, optional regen and respawn. Host-authoritative damage when networked, single-player safe. Optionally attached to an existing GameObject by GUID",
    {
      name: z.string().optional().describe("Class name. Defaults to 'Health'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      maxHealth: z
        .number()
        .optional()
        .describe("Starting/maximum health. Defaults to 100"),
      regen: z
        .boolean()
        .optional()
        .describe("Include passive health regeneration after a delay. Defaults to false"),
      respawn: z
        .boolean()
        .optional()
        .describe("On death, respawn at a RespawnPoint (wire it with set_component_reference) instead of disabling. Defaults to false"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach the Health component to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_health_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_economy_wallet ─────────────────────────────────────────
  server.tool(
    "create_economy_wallet",
    "Generate a host-authoritative currency Wallet component: a [Sync(SyncFlags.FromHost)] Money balance (only the host can write it — plain [Sync] money is the classic economy exploit) with AddMoney / TrySpend / SetMoney / CanAfford and an OnMoneyChanged event. Single-player safe. Optionally attached to an existing GameObject by GUID (after a hotload). Pairs with a save system for persistence. Mined from the most-requested currency pattern across 51 games.",
    {
      name: z.string().optional().describe("Class name. Defaults to 'Wallet'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      startingMoney: z
        .number()
        .int()
        .optional()
        .describe("Initial balance the host seeds on start. Defaults to 0"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach the Wallet to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_economy_wallet", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_round_phase_machine ────────────────────────────────────
  server.tool(
    "create_round_phase_machine",
    "Generate a host-authoritative round/phase machine: a [Sync(SyncFlags.FromHost)] CurrentPhase cycled through your named phases on a per-phase timer (host-only), with a static OnPhaseChanged event that fires on every machine. Great for round/match flow, match phases, or a day/night cycle. Single-player safe. Optionally attached to an existing GameObject by GUID (after a hotload). Mined from the round-flow pattern across the 51 games.",
    {
      name: z.string().optional().describe("Class name. Defaults to 'GameDirector'"),
      directory: z.string().optional().describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      phases: z
        .array(z.string())
        .optional()
        .describe('Ordered phase names (become an enum), e.g. ["Lobby","Day","Night","Payout"]. Defaults to ["Lobby","Active","Ended"]'),
      duration: z
        .number()
        .optional()
        .describe("Default seconds per phase (each phase also gets its own tunable [Property]). Defaults to 60"),
      loop: z
        .boolean()
        .optional()
        .describe("Loop back to the first phase after the last (true) or hold on the last phase (false). Defaults to true"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach to (only if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_round_phase_machine", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_day_night_clock ────────────────────────────────────────
  server.tool(
    "create_day_night_clock",
    "Generate a host-authoritative time-of-day clock: [Sync(SyncFlags.FromHost)] TimeOfDay (0–24) + Day advancing by Time.Delta, IsDay/IsNight from sunrise/sunset hours, and static OnNewDay / OnDayNightChanged events to drive lighting, NPC schedules, or spawns. Single-player safe. Pairs with create_round_phase_machine. Optionally attached to a GameObject by GUID (after a hotload).",
    {
      name: z.string().optional().describe("Class name. Defaults to 'DayNightClock'"),
      directory: z.string().optional().describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      dayLengthSeconds: z.number().optional().describe("Real seconds per in-game day. Defaults to 600 (10 min)"),
      startHour: z.number().optional().describe("Hour the clock starts at (0–24). Defaults to 8"),
      sunriseHour: z.number().optional().describe("Hour day begins. Defaults to 6"),
      sunsetHour: z.number().optional().describe("Hour night begins. Defaults to 20"),
      targetId: z.string().optional().describe("GUID of an existing GameObject to attach to (hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_day_night_clock", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_interactable ───────────────────────────────────────────
  server.tool(
    "create_interactable",
    "Generate a Component.IPressable interactable: the built-in PlayerController 'use' key drives Press()/Hover()/Blur() with no custom player code. Includes a static OnPressed event, an optional cooldown (TimeUntil), and a private OnPress() extensionpoint for effects. For host-authoritative side-effects call an [Rpc.Host] from OnPress(). The Prompt property is left to your game's HUD. Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z.string().optional().describe("Class name. Defaults to 'Interactable'"),
      directory: z.string().optional().describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      prompt: z
        .string()
        .optional()
        .describe("Prompt string shown by the game's HUD when hovering. Defaults to 'Press'"),
      cooldownSeconds: z
        .number()
        .optional()
        .describe("Seconds before the interactable can be pressed again. 0 = no cooldown. Defaults to 0"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach the component to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_interactable", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // -- create_weighted_loot_table ----------------------------------------
  server.tool(
    "create_weighted_loot_table",
    "Generate a cumulative-weight random loot picker: parallel Name/Weight lists (inspector-editable), a Roll() method that returns a winning entry name and fires a static OnLoot event, and optional pity (guarantee the last/rarest entry after PityAfter consecutive non-rare rolls). Roll() is host-authoritative -- only call it on the host and replicate the result (clients rolling their own loot is equivalent to clients writing their own money balance). Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z.string().optional().describe("Class name. Defaults to 'LootTable'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      entries: z
        .union([
          z.array(z.object({ name: z.string(), weight: z.number() })),
          z.string().describe('Compact "name:weight,name:weight" string, e.g. "common:70,uncommon:25,rare:5"'),
        ])
        .optional()
        .describe("Loot table entries. Defaults to common:70 / uncommon:25 / rare:5"),
      pity: z
        .boolean()
        .optional()
        .describe("If true, guarantee the last (rarest) entry after PityAfter consecutive non-rare rolls. Defaults to false"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach to (only if the type is already loaded -- hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_weighted_loot_table", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // -- create_pickup -----------------------------------------------------
  server.tool(
    "create_pickup",
    "Generate a trigger-based collectible component. On enter by a tagged object it raises OnCollected (wire it to your objective/score system) and despawns. Optionally builds a visible pickup GameObject with a trigger SphereCollider (+ a model) in one call. Returns { created, path, className, gameObject, note } — gameObject is the placed pickup (null unless placeInScene=true); a note flags when the component couldn't attach because the fresh type needs a hotload. Follow with trigger_hotload, then get_compile_errors",
    {
      name: z.string().optional().describe("Class name. Defaults to 'Pickup'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      action: z
        .enum(["score", "heal", "item", "custom"])
        .optional()
        .describe("Effect flavour (all self-contained; the heal/item branches show the typed call to a companion system in comments). Defaults to 'score'"),
      amount: z
        .number()
        .optional()
        .describe("Magnitude of the effect (score points, heal amount). Defaults to 1"),
      filterTag: z
        .string()
        .optional()
        .describe("Only collect for objects with this tag. Defaults to 'player'"),
      placeInScene: z
        .boolean()
        .optional()
        .describe("Also build a pickup GameObject (trigger SphereCollider + optional model). Defaults to false"),
      position: Vector3Schema.optional().describe("World position when placeInScene is true"),
      radius: z
        .number()
        .optional()
        .describe("Trigger sphere radius when placed. Defaults to 24"),
      model: z
        .string()
        .optional()
        .describe("Optional model path for a visible pickup (e.g. 'models/dev/box.vmdl'). Cloud assets must be installed first"),
    },
    async (params) => {
      const res = await bridge.send("create_pickup", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // -- create_save_system ------------------------------------------------
  server.tool(
    "create_save_system",
    "Generate a versioned save-system component: a SaveData POCO with Version bump on schema change, dirty-flag autosave on a TimeUntil timer, clamp-on-load Sanitize() for corrupt/hand-edited saves, and delete-on-version-mismatch to start fresh instead of crashing. Runs only on the owning machine (IsProxy guard). Fires static OnLoaded/OnSaved hooks for HUD and analytics. FileSystem.Data.ReadJsonOrDefault/WriteJson verified live on the current SDK. Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z.string().optional().describe("Class name. Defaults to 'SaveSystem'"),
      directory: z.string().optional().describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      fileName: z
        .string()
        .optional()
        .describe("Save file name under FileSystem.Data (e.g. 'save.json'). Defaults to 'save.json'"),
      version: z
        .number()
        .int()
        .optional()
        .describe("Schema version embedded in SaveData. Old saves with a different version start fresh. Defaults to 1"),
      autosaveSeconds: z
        .number()
        .optional()
        .describe("Seconds between autosave ticks (0 disables autosave). Defaults to 10"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an existing GameObject to attach to (only if the type is already loaded -- hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_save_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
  // -- create_leaderboard_panel ------------------------------------------
  server.tool(
    "create_leaderboard_panel",
    "Generate a Razor PanelComponent that fetches and displays a Sandbox.Services leaderboard derived from a stat name. Produces TWO files: {name}.razor and {name}.razor.scss. The panel auto-refreshes every 30 s, shows rank/displayName/value rows, handles loading state, and includes a BuildHash() override (razor-lint clean). Must be hosted under a ScreenPanel or WorldPanel. Stats must be configured for the project ident on sbox.game. Uses Leaderboards.Get(statName) + board.Refresh() -- the exact API from ServicesQueryHandler. Returns { created, razorPath, scssPath, className, note }. Follow with trigger_hotload, then get_compile_errors, then host it via add_screen_panel (panelComponent=className).",
    {
      name: z.string().optional().describe("Class name for the panel component. Defaults to 'LeaderboardPanel'"),
      directory: z.string().optional().describe("Subdirectory for the generated files. Defaults to 'Code/UI'"),
      statName: z.string().optional().describe("Sandbox.Services stat name the leaderboard is derived from. Defaults to 'score'"),
      title: z.string().optional().describe("Display title shown at the top of the panel. Defaults to 'Leaderboard'"),
      maxRows: z.number().int().optional().describe("Maximum leaderboard rows to fetch and display. Defaults to 10"),
    },
    async (params) => {
      const res = await bridge.send("create_leaderboard_panel", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // -- create_inventory --------------------------------------------------
  server.tool(
    "create_inventory",
    "Generate a slot-based inventory component using parallel List<string> ItemIds / List<int> Counts (serialization-safe, inspector-editable). Includes TryAdd (stack-first, partial-add rejected), TryRemove, CountOf, Move (swap or merge same-id slots), and Clear. Static OnChanged event fires after every successful mutation. Host-authoritative usage note: mutate on the host in multiplayer, replicate via your own [Sync]/RPC. Pairs with create_pickup.",
    {
      name: z.string().optional().describe("Class name. Defaults to 'Inventory'"),
      directory: z.string().optional().describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      capacity: z.number().int().optional().describe("Total slot count. Defaults to 24"),
      maxStack: z.number().int().optional().describe("Maximum items per slot (stack cap). Defaults to 99"),
      targetId: z.string().optional().describe("GUID of an existing GameObject to attach to (hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_inventory", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // -- create_stat_modifier_system ---------------------------------------
  server.tool(
    "create_stat_modifier_system",
    "Generate an enum-keyed stat modifier system with three modifier layers: SET (highest-priority-wins hard override), ADD (summed bonuses), MULT (multiplied factors applied last). Modifier storage uses parallel private Lists of primitive types (serialization-safe). RemoveModifiersFrom(source) cleans up all mods from a buff/debuff source by reference. Static OnStatChanged(stat, value) event fires after every add/remove. Mined from RPG/buff/debuff patterns across shipped s&box games. Returns { created, path, className, stats, placedOn, note } — stats echoes the sanitized stat names ({name}Stat enum values); placedOn is the target GameObject when attached (needs the type hotloaded). Follow with trigger_hotload, then get_compile_errors.",
    {
      name: z.string().optional().describe("Class name prefix -- generates {name}Stat enum + {name} Component. Defaults to 'StatSystem'"),
      directory: z.string().optional().describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      stats: z.union([z.array(z.string()), z.string()]).optional().describe("Stat names as a JSON array or comma-separated string. Defaults to 'Health,Speed,Damage'"),
      targetId: z.string().optional().describe("GUID of an existing GameObject to attach to (hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_stat_modifier_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // -- create_placement_mode ---------------------------------------------
  server.tool(
    "create_placement_mode",
    "Generate a ghost-preview + commit placement component (single class). StartPlacing() clones GhostPrefab as a NetworkMode.Never preview with colliders disabled and ModelRenderers tinted semi-transparent. Each frame while placing: ray from Scene.Camera.GetMouseRay(), IgnoreGameObjectHierarchy(ghost), snap hit position to GridSize (0 = freeform), move ghost. On Input.Pressed('attack1') TryPlace() re-validates distance and commits a real clone. StopPlacing() destroys the ghost. Static OnPlaced(GameObject, Vector3) event. Includes a multiplayer RPC note. API grounded in building-placement cookbook (enifun.shop_manager pattern).",
    {
      name: z.string().optional().describe("Class name. Defaults to 'PlacementMode'"),
      directory: z.string().optional().describe("Subdirectory for the .cs file. Defaults to 'Code'"),
      gridSize: z.number().optional().describe("Snap grid size in world units (0 = freeform placement). Defaults to 0"),
      targetId: z.string().optional().describe("GUID of an existing GameObject to attach to (hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_placement_mode", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
