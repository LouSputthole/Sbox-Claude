import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * World-generation and map-editing tools.
 *
 * Drives terrain (MapBuilder), caves (CaveBuilder), and forests (ForestGenerator)
 * components. Includes a generic invoke_button for pressing any [Button] on any
 * component, sculpt brushes for direct heightmap editing, and place_along_path
 * for dropping assets along a curve.
 *
 * Most "add_*" tools default to rebuilding the affected feature (Build Terrain,
 * Build Cave, Generate Forest) so changes are visible immediately. Set
 * `rebuild: false` to batch many edits and rebuild manually.
 *
 * Component lookup: by default each tool finds the first instance of the
 * relevant component (MapBuilder, CaveBuilder, ForestGenerator) in the scene.
 * Pass `id` (GameObject GUID) to target a specific GameObject.
 */
export function registerWorldTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── invoke_button ────────────────────────────────────────────────
  server.tool(
    "invoke_button",
    "Call a public method on a component. Matching is tried in order: (1) a [Button] attribute label, (2) the exact method NAME, (3) case-insensitive name with spaces stripped. Calls ANY public method, not only [Button]-attributed ones (e.g. 'StartGame'). Pass `args` to call methods that take parameters — the arg count must match and each value is coerced to the parameter type (primitives: string/number/bool work; complex types like Vector3 may not coerce). Omit args (or []) for parameterless methods. (list_component_buttons only lists [Button] methods, so a plain method may be invokable yet not appear there.)",
    {
      component: z
        .string()
        .describe("Component type name (e.g. 'MapBuilder', 'SasquatchedGame')"),
      button: z
        .string()
        .describe("A [Button] label OR a public method name (e.g. 'Build Terrain', 'StartGame'); case- and space-insensitive"),
      id: z
        .string()
        .optional()
        .describe("Optional GameObject GUID — if omitted, finds first matching component in scene"),
      args: z
        .array(z.unknown())
        .optional()
        .describe("Arguments to pass (must match the method's parameter count); coerced to each parameter type"),
    },
    async (params) => {
      const res = await bridge.send("invoke_button", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── list_component_buttons ───────────────────────────────────────
  server.tool(
    "list_component_buttons",
    "List the [Button]-attributed methods on a component. NOTE: this only finds methods decorated with [Button]; invoke_button can ALSO call any plain public no-arg method by name, so a method missing here may still be invokable. Use describe_type / get_method_signature to find non-button methods.",
    {
      component: z.string().describe("Component type name"),
      id: z.string().optional().describe("Optional GameObject GUID"),
    },
    async (params) => {
      const res = await bridge.send("list_component_buttons", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── raycast_terrain ──────────────────────────────────────────────
  server.tool(
    "raycast_terrain",
    "Sample MapBuilder terrain height at world (x, y). Returns z (the surface height). Use to place props on the terrain surface.",
    {
      x: z.number().describe("World X coordinate"),
      y: z.number().describe("World Y coordinate"),
      id: z.string().optional().describe("Optional GameObject GUID for MapBuilder"),
      component: z
        .string()
        .optional()
        .describe("Override the terrain component type name (default MapBuilder)"),
    },
    async (params) => {
      const res = await bridge.send("raycast_terrain", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_terrain_hill ─────────────────────────────────────────────
  server.tool(
    "add_terrain_hill",
    "Add a hill (cosine-falloff bump) to MapBuilder's Hills list. Negative height creates a depression. Returns `added`, `total` (hills now in the list), and `rebuilt`; verify the surface with raycast_terrain at the hill center.",
    {
      x: z.number().describe("World X of hill center"),
      y: z.number().describe("World Y of hill center"),
      radius: z.number().default(500).describe("Hill radius in world units"),
      height: z.number().default(100).describe("Peak height (negative for depression)"),
      rebuild: z.boolean().default(true).describe("Rebuild terrain after adding (set false to batch)"),
      id: z.string().optional().describe("GUID of the GameObject holding the MapBuilder; omit to auto-find the first MapBuilder in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_terrain_hill", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_terrain_clearing ─────────────────────────────────────────
  server.tool(
    "add_terrain_clearing",
    "Add a flat clearing zone to MapBuilder's Clearings list (lerps height toward base inside radius). Returns `added`, `total` (clearing count), and `rebuilt`. Rebuilds terrain immediately by default; set rebuild=false to batch edits, then rebuild once (rebuild=true on the last call, or invoke_button 'Build Terrain').",
    {
      x: z.number().describe("World X of clearing center"),
      y: z.number().describe("World Y of clearing center"),
      radius: z.number().default(300).describe("Clearing radius in world units. Default 300."),
      rebuild: z.boolean().default(true).describe("Rebuild terrain after adding (default true; set false to batch)"),
      id: z.string().optional().describe("GUID of the GameObject holding the MapBuilder; omit to auto-find the first MapBuilder in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_terrain_clearing", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_terrain_trail ────────────────────────────────────────────
  server.tool(
    "add_terrain_trail",
    "Carve a trail depression between two points on MapBuilder (appends to its Trails list). Returns `added`, `total` (trail count), and `rebuilt`. Rebuilds terrain immediately by default; set rebuild=false to batch, then invoke_button 'Build Terrain' once.",
    {
      from: z.object({ x: z.number(), y: z.number() }).describe("Trail start point (world x/y)"),
      to: z.object({ x: z.number(), y: z.number() }).describe("Trail end point (world x/y)"),
      rebuild: z.boolean().default(true).describe("Rebuild terrain after adding (default true; set false to batch)"),
      id: z.string().optional().describe("GUID of the GameObject holding the MapBuilder; omit to auto-find the first MapBuilder in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_terrain_trail", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── clear_terrain_features ───────────────────────────────────────
  server.tool(
    "clear_terrain_features",
    "Wipe MapBuilder feature lists — Hills, Clearings, Trails, CavePath, or all of them (default). Destructive: the feature definitions are removed. Returns `cleared` (map of list name → entries removed) and `rebuilt`; re-add features with add_terrain_hill / add_terrain_clearing / add_terrain_trail.",
    {
      what: z
        .enum(["Hills", "Clearings", "Trails", "CavePath", "all"])
        .default("all")
        .describe("Which feature list to clear. Default 'all' (clears all four)."),
      rebuild: z.boolean().default(true).describe("Rebuild terrain after clearing (default true)"),
      id: z.string().optional().describe("GUID of the GameObject holding the MapBuilder; omit to auto-find the first MapBuilder in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("clear_terrain_features", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_cave_waypoint ────────────────────────────────────────────
  server.tool(
    "add_cave_waypoint",
    "Append (or insert at `index`) a waypoint to CaveBuilder.Path. Z is depth (negative = underground). Returns `added`, `total` (waypoint count), and `rebuilt` — rebuilds the cave ('Build Cave') immediately unless rebuild=false.",
    {
      x: z.number().describe("World X of the waypoint"),
      y: z.number().describe("World Y of the waypoint"),
      z: z.number().default(0).describe("Z depth — negative = underground"),
      index: z
        .number()
        .int()
        .optional()
        .describe("Optional insert position (default: append to end)"),
      rebuild: z.boolean().default(true).describe("Rebuild the cave after adding (default true; set false to batch)"),
      id: z.string().optional().describe("GUID of the GameObject holding the CaveBuilder; omit to auto-find the first CaveBuilder in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_cave_waypoint", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── clear_cave_path ──────────────────────────────────────────────
  server.tool(
    "clear_cave_path",
    "Clear all waypoints in CaveBuilder.Path and remove the built cave from the scene (invokes 'Clear Cave'). Destructive. Returns `cleared` — the number of waypoints removed; start a new path with add_cave_waypoint.",
    {
      id: z.string().optional().describe("GUID of the GameObject holding the CaveBuilder; omit to auto-find the first CaveBuilder in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("clear_cave_path", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_forest_poi ───────────────────────────────────────────────
  server.tool(
    "add_forest_poi",
    "Add a point of interest (clearing) to ForestGenerator.POIs. Returns `added`, `index` (the new POI's index — pass it to add_forest_trail as from_index/to_index), `total`, and `rebuilt`. Forest gen is slow (~1s), so rebuild defaults to false — batch your POIs/trails, then regenerate once (rebuild=true on the last call, or invoke_button 'Generate Forest').",
    {
      name: z.string().default("POI").describe("Display name for the POI. Default 'POI'."),
      x: z.number().describe("World X of POI center"),
      y: z.number().describe("World Y of POI center"),
      radius: z.number().default(300).describe("POI radius in world units. Default 300."),
      density_multiplier: z
        .number()
        .default(1)
        .describe("Multiplies forest density inside this POI's region"),
      rebuild: z
        .boolean()
        .default(false)
        .describe("Forest gen is slow (~1s); default false to batch"),
      id: z.string().optional().describe("GUID of the GameObject holding the ForestGenerator; omit to auto-find the first ForestGenerator in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_forest_poi", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_forest_trail ─────────────────────────────────────────────
  server.tool(
    "add_forest_trail",
    "Add a trail gap between two POIs to ForestGenerator.Trails. Returns `added`, `total` (trail count), and `rebuilt`. rebuild defaults to false — nothing changes visually until you regenerate (rebuild=true, or invoke_button 'Generate Forest').",
    {
      from_index: z.number().int().describe("Index of the start POI (the `index` returned by add_forest_poi)"),
      to_index: z.number().int().describe("Index of the end POI (the `index` returned by add_forest_poi)"),
      rebuild: z.boolean().default(false).describe("Regenerate the forest after adding (default false — batch, then rebuild once)"),
      id: z.string().optional().describe("GUID of the GameObject holding the ForestGenerator; omit to auto-find the first ForestGenerator in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_forest_trail", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── set_forest_seed ──────────────────────────────────────────────
  server.tool(
    "set_forest_seed",
    "Set ForestGenerator.Seed and (by default) regenerate — re-rolls the forest layout while keeping POIs, trails, and density regions. Returns `set`, `seed`, and `rebuilt`; take a screenshot afterwards (screenshot_from) to judge the new layout.",
    {
      seed: z.number().int().default(77).describe("New random seed (integer). Default 77."),
      rebuild: z.boolean().default(true).describe("Regenerate the forest after setting (default true)"),
      id: z.string().optional().describe("GUID of the GameObject holding the ForestGenerator; omit to auto-find the first ForestGenerator in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("set_forest_seed", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── clear_forest_pois ────────────────────────────────────────────
  server.tool(
    "clear_forest_pois",
    "Wipe all POIs and trails in ForestGenerator and clear placed forest objects from the scene (invokes 'Clear Forest'). Destructive. Returns `cleared` — the number of POIs removed; rebuild a layout with add_forest_poi + add_forest_trail, then invoke_button 'Generate Forest'.",
    {
      id: z.string().optional().describe("GUID of the GameObject holding the ForestGenerator; omit to auto-find the first ForestGenerator in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("clear_forest_pois", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── sculpt_terrain ───────────────────────────────────────────────
  server.tool(
    "sculpt_terrain",
    "Apply a heightmap brush at (x, y) to MapBuilder. Modes: raise, lower, flatten, smooth. Modifies the current heightmap directly and rebuilds the mesh; edits survive between calls but are lost when 'Build Terrain' regenerates from the feature lists. Returns `sculpted`, `mode`, and `affected_vertices`; verify with raycast_terrain at the brush center.",
    {
      x: z.number().describe("World X of brush center"),
      y: z.number().describe("World Y of brush center"),
      radius: z.number().default(400).describe("Brush radius in world units"),
      strength: z.number().default(50).describe("Height delta (units) for raise/lower; ignored for flatten/smooth"),
      mode: z.enum(["raise", "lower", "flatten", "smooth"]).default("raise").describe("Brush mode. Default 'raise'. `strength` applies to raise/lower only."),
      id: z.string().optional().describe("GUID of the GameObject holding the MapBuilder; omit to auto-find the first MapBuilder in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("sculpt_terrain", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── paint_forest_density ─────────────────────────────────────────
  server.tool(
    "paint_forest_density",
    "Add a circular biome region with overridden forest density to ForestGenerator.DensityRegions. Multiple regions stack via cosine falloff. density: 0=no trees, 1=normal, 2=double. Returns `painted`, `total` (region count), and `rebuilt` — rebuild defaults to false, so regenerate (rebuild=true, or invoke_button 'Generate Forest') to see the change.",
    {
      x: z.number().describe("World X of region center"),
      y: z.number().describe("World Y of region center"),
      radius: z.number().default(800).describe("Region radius in world units. Default 800."),
      density: z.number().default(1).describe("Density multiplier (0=clear, 1=normal, 2=dense)"),
      rebuild: z.boolean().default(false).describe("Regenerate the forest after adding (default false; batch, then rebuild once)"),
      id: z.string().optional().describe("GUID of the GameObject holding the ForestGenerator; omit to auto-find the first ForestGenerator in the scene"),
      component: z
        .string()
        .optional()
        .describe(
          "Override the builder component type name — set this if your project's terrain/cave/forest component is named differently than the default (MapBuilder/CaveBuilder/ForestGenerator)"
        ),
    },
    async (params) => {
      const res = await bridge.send("paint_forest_density", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── place_along_path ─────────────────────────────────────────────
  server.tool(
    "place_along_path",
    "Place up to 2,048 model instances along a waypoint path. Existing calls mutate immediately and keep the legacy { placed, folder } result, but reject larger paths before creating anything. Use dryRun:true for a deterministic, non-mutating preview with exact transforms, model-local bounds, warnings, and a 10-minute planId; then call commit_placement_plan to create those exact placements atomically with slot-to-GUID receipts. align follows path direction; randomizeYaw is used only when align is false.",
    {
      model: z.string().describe("Model path (e.g. 'models/dev/box.vmdl' or installed-asset path)"),
      points: z
        .array(
          z
            .object({
              x: z.number().finite(),
              y: z.number().finite(),
              z: z.number().finite().default(0),
            })
            .strict()
        )
        .min(2)
        .describe("Path waypoints (at least 2)"),
      spacing: z.number().finite().positive().default(200).describe("Distance between placements (world units; must be > 0)"),
      jitter: z.number().finite().nonnegative().default(0).describe("Max random XY offset (must be >= 0)"),
      min_scale: z.number().finite().positive().default(1).describe("Minimum positive uniform scale per instance. Default 1."),
      max_scale: z.number().finite().positive().default(1).describe("Maximum positive uniform scale per instance; must be >= min_scale. Default 1."),
      seed: z.number().int().default(42).describe("Random seed for jitter and scale — same seed reproduces the same placement. Default 42."),
      name: z.string().default("PathItem").describe("Base name for placed objects"),
      align: z.boolean().optional().describe("Face each instance along its path segment (default false)"),
      randomizeYaw: z.boolean().optional().describe("Randomize yaw when align is false (default false)"),
      dryRun: z.boolean().optional().describe("Preview only: return deterministic transforms and planId without creating objects"),
    },
    async (params) => {
      const res = await bridge.send("place_along_path", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── build_terrain_mesh ───────────────────────────────────────────
  server.tool(
    "build_terrain_mesh",
    "Build a standalone heightmap terrain mesh (a MeshComponent) from a hills/clearings JSON spec — independent of MapBuilder. Use when you don't have a MapBuilder component in the scene and want one-shot terrain. Returns `built`, `id` (the new GameObject's GUID), `name`, `vertices`, and `faces`; pass `id` to assign_material, set_transform, or delete_gameobject. Note: raycast_terrain cannot sample this mesh (it requires MapBuilder).",
    {
      size: z.number().default(9600).describe("Total terrain size (world units, square)"),
      resolution: z.number().int().default(64).describe("Grid resolution per side"),
      name: z.string().default("Generated Terrain").describe("Name for the created terrain GameObject. Default 'Generated Terrain'."),
      hills: z
        .array(
          z.object({
            x: z.number(),
            y: z.number(),
            radius: z.number().default(500),
            height: z.number().default(100),
          })
        )
        .default([])
        .describe("Hill bumps: array of {x, y, radius (default 500), height (default 100; negative = depression)}. Default: none."),
      clearings: z
        .array(z.object({ x: z.number(), y: z.number(), radius: z.number().default(300) }))
        .default([])
        .describe("Zones flattened back toward height 0: array of {x, y, radius (default 300)}. Default: none."),
    },
    async (params) => {
      const res = await bridge.send("build_terrain_mesh", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── set_prefab_ref ───────────────────────────────────────────────
  server.tool(
    "set_prefab_ref",
    "Set a GameObject-typed property on a component to a loaded prefab. Use this when set_property can't handle prefab references (which it can't, because prefabs are GameObjects not primitives).",
    {
      id: z.string().describe("GUID of the GameObject holding the component"),
      component: z.string().describe("Component type name"),
      property: z.string().describe("Property name to set (must be GameObject-typed)"),
      prefabPath: z.string().describe("Prefab asset path (e.g. 'prefabs/player.prefab')"),
    },
    async (params) => {
      const res = await bridge.send("set_prefab_ref", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
