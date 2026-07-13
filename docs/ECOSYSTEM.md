# Toolset Ecosystem — the plain-English tour

The generated **[TOOLSETS.md](TOOLSETS.md)** is the authoritative inventory — every tool,
its toolset, and its read-only status, emitted straight from the addon. **This** file is the
companion: what each toolset is *for*, when you'd reach for it, and how the groups fit
together. If a capability here contradicts TOOLSETS.md, TOOLSETS.md wins (it's generated;
this is written).

The surface: **262 native tools across 28 `bridge_*` toolsets** (53 read-only), plus the
**7 lifeline tools** for editor-down diagnostics. Agents browse with `list_toolsets` /
`describe_toolset` and find individual tools with `search_tools`; see the
[Agent Guide](AGENT-GUIDE.md) for the working loop.

**Read the safety note on any *mutating* toolset.** Read-only tools (`[McpTool.ReadOnly]`)
run without a permission prompt and never change your project; everything else can, and
scene-mutating tools refuse during play mode.

---

## Scene & object foundation

### `bridge_gameobject` — GameObjects, transforms, layout
*25 tools (6 read-only).* The core of scene work: create / delete / duplicate / rename /
reparent GameObjects, set transforms and tags, select and query, and **bulk layout**
(align, distribute, scatter, grid-duplicate, snap-to-ground, group, randomize).

- Place a row of pillars, then `align_objects` / `distribute_objects` to space them cleanly.
- `scatter_props` for instant foliage/rocks/debris; `randomize_transforms` to break up repetition.
- `find_objects` by name / component / tag to get the GUIDs everything else consumes.

*Prompt:* "Scatter 30 rocks around the campfire and randomize their rotation and scale."

*Safety:* Mutating (except the read queries). Refused in play mode. `delete_gameobject` is
permanent — for many objects use `batch_delete` with a dry-run.

*Related:* `bridge_batch`, `bridge_component`, `bridge_world`.

### `bridge_component` — components on objects
*10 tools (4 read-only).* Add, configure, inspect, and invoke components: set/get
properties (with persistence through save+reload), wire cross-component references by GUID,
and call methods and `[Button]`s.

- `add_component_with_properties` / `add_component_to_new_object` to build and configure in one call.
- `set_component_reference` to wire "ObjectiveManager.Player = the player" by GUID (validated).
- `invoke_button` / `invoke_method` to call a component method (with args) live.

*Prompt:* "Add a Rigidbody to the barrel and turn its gravity off."

*Safety:* Mutating (except `get_property` / `get_all_properties` / `list_available_components` /
`list_component_buttons`). A freshly generated component type only resolves after `trigger_hotload`.

*Related:* `bridge_discovery` (find the type first), `bridge_prefab`, `bridge_gameobject`.

### `bridge_scene` — scene files & orientation
*3 tools (1 read-only).* `create_scene` (empty `.scene`, registered so `load_scene` works),
`load_scene`, and `describe_scene` — one-call orientation for the open scene (component
histogram, cameras with positions, light count, tag histogram, aggregate content bounds;
works in play mode too). For saving/listing scenes, use the native built-ins (`save_scene`,
`list_scenes`).

- `describe_scene` as your first read when you enter an unfamiliar scene.
- `create_scene` + `load_scene` to spin up a fresh level, then compose it.

*Prompt:* "What's in the currently open scene?"

*Safety:* `create_scene` / `load_scene` mutate the editor; `describe_scene` is read-only.

*Related:* `bridge_project` (`describe_project` is the project-level partner), `bridge_gameobject`.

### `bridge_prefab` — real prefabs
*5 tools (2 read-only).* Prefabs are real in v2: `create_prefab` writes a **full engine
serialization** (every component with values, all children) and `instantiate_prefab` genuinely
recreates the tree (engine `Clone`, with a guid-remapped deserialize fallback so repeat
spawns never collide). Plus `get_prefab_info` (structured tree summary), `list_prefabs`, and
`set_prefab_ref` (wire a prefab asset into a component property).

- Turn a hand-built enemy into a prefab, then spawn a dozen without collisions.
- Inspect a prefab's structure before instantiating with `get_prefab_info`.

*Prompt:* "Save this decorated crate as a prefab and drop three copies along the wall."

*Safety:* `create_prefab` / `instantiate_prefab` / `set_prefab_ref` mutate; refused in play mode.
`create_prefab` overwrites an existing file at the path.

*Related:* `bridge_npc` (spawner prefabs), `bridge_networking` (`network_spawn`), `bridge_batch`.

### `bridge_batch` — bulk operations with dry-run
*4 tools (0 read-only).* One op across many GameObjects in one call, each with the
**`dryRun: true`** validate-first convention: `batch_set_property`, `batch_add_component`,
`batch_delete`, `batch_reparent`. Get target ids from `find_objects` / `get_selected_objects`.

- Set `Tint` on 40 props or `Enabled` on every light in one call.
- Add a `BoxCollider` to every crate; regroup a level section under one parent.

*Prompt:* "Add a box collider to every object named 'crate' — show me the plan first."

*Safety:* All mutating; refused in play mode. **`batch_delete` is destructive and not
undoable** — always dry-run first. `batch_reparent` guards against cycles.

*Related:* `bridge_gameobject`, `bridge_workflow` (checkpoint before a big batch).

---

## Building the game (codegen scaffolds)

### `bridge_scaffold_gameplay` — gameplay systems
*43 tools (0 read-only).* The largest toolset: **compile-verified C# scaffolds** for the
systems every game hand-rolls — player/NPC controllers, game managers, health, pickups and
interactables, inventory, save systems (single + multi-slot, signed tamper-evident,
roguelite meta-progression), economy (wallets, audited currency accounts, idle income and
geometric idle economies, Steam-stat currency), loot and gacha tables (including data-asset
`.loot` GameResources), achievements and leaderboard stats, Elo ratings, speedrun boards,
round/phase/state machines and map-vote flows, day-night clocks, objective systems, stat
modifiers, needs systems, typed event buses, placement mode, teams, carry, hold-to-confirm,
currency pickups, trigger zones, and more. Each writes a `.cs` file; follow with
`trigger_hotload` + `compile_status`.

- `create_health_system` + `create_objective_system` for a win/lose core in two calls.
- `create_economy_wallet` + `create_idle_income` + `create_offline_progress` for a full idle kit — add `create_idle_economy` for geometric bulk-buy upgrades and `create_currency_account` for an audited ledger.
- `create_round_state_machine` when each phase needs its own behaviour (vs the lighter `create_round_phase_machine`); `scaffold_map_vote_flow` for end-of-round map voting.
- `create_achievement_set` + `add_achievement_trigger` for achievements with a toast HUD; `add_leaderboard_stat` is the write-side partner to `create_leaderboard_panel`.

*Prompt:* "Give this game an inventory, a health system, and a save system."

*Safety:* All mutating (they write files). A generated type isn't in the TypeLibrary until a
hotload — generate, hotload, then attach.

*Related:* `bridge_networking` (host-authoritative patterns), `bridge_scaffold_polish`,
`bridge_ui`, `bridge_component` (attach the results).

### `bridge_scaffold_polish` — game feel & presentation
*11 tools (0 read-only).* The juice layer: `create_camera_shake` (trauma model),
`create_camera_effects` (statics over the SDK's built-in `AddShake` / `AddPunch` / `AddTilt`
one-shots, with distance-falloff `ShakeAt` and HitPunch/ExplosionShake/LandingTilt presets —
composes with the trauma model), `add_flicker_light` (Candle/Fluorescent/Faulty/Pulse/Lightning),
`create_floating_combat_text` (billboarded damage popups), `create_combo_meter`,
`create_proxy_nametag`, `create_worldpanel_ui`, `create_cutscene_director` (zero-asset
camera-shot player), `create_dialogue_system` (typewriter Razor HUD),
`generate_lipsync_dialogue` (NPCs *speak* their lines with moving mouths — positional TTS
plus data-driven viseme→morph animation from the model's own baked data), and
`create_round_timer_hud` (a Razor timer HUD that binds by reflection to either shipped
round machine, 1 Hz adaptive BuildHash).

- `add_flicker_light "Faulty"` on a hallway light — the biggest atmosphere win per call.
- `create_floating_combat_text` wired into a damage path so every hit prints its number.
- `create_cutscene_director` for an intro sequence with no `.movie` asset needed.
- `create_round_timer_hud` after either round machine so the countdown is visible.
- `generate_lipsync_dialogue` to make an NPC actually deliver its dialogue, mouth included;
  `create_camera_effects` for one-shot hit punches and explosion shakes with no scaffold state.

*Prompt:* "Add screen shake on explosions and floating damage numbers on hits."

*Safety:* Mutating (write files). Most are LOCAL/visual-only (no `[Sync]`) — call from an
`[Rpc.Broadcast]` if every client should feel it.

*Related:* `bridge_scaffold_gameplay` (`create_health_system` pairs with combat text/combo),
`bridge_moviemaker` (the keyframed-clip alternative to the cutscene director), `bridge_visuals`.

### `bridge_ui` — Razor UI panels
*4 tools (0 read-only).* `add_screen_panel` (full-screen HUD host), `add_world_panel`
(in-world 3D UI host), `create_razor_ui` (a basic PanelComponent file), and
`add_panel_buildhash` (a FILE-EDIT tool that patches an existing `.razor` to add a
`BuildHash` override — `razor_lint`'s companion fixer). A `PanelComponent`
renders nothing without a host `ScreenPanel`/`WorldPanel`.

- Host a generated HUD (leaderboard, combo meter, dialogue) under a `ScreenPanel`.
- `add_world_panel` for health bars / signs / nameplates in world space.
- `razor_lint` flags a panel that never re-renders → `add_panel_buildhash` fixes it in place.

*Prompt:* "Add a screen panel to host the leaderboard I just generated."

*Safety:* Mutating. `add_screen_panel` / `add_world_panel` **silently skip** a `panelComponent`
whose type isn't loaded — hotload first, then verify via the returned `components`. For
clickable world UI the scene also needs a `Sandbox.WorldInput` (see `create_worldpanel_ui`).

*Related:* `bridge_scaffold_polish` / `bridge_scaffold_gameplay` (they generate the panels),
`bridge_validation` (`razor_lint`).

### `bridge_networking` — multiplayer setup & codegen
*14 tools (1 read-only).* Everything multiplayer: `add_network_helper`, `create_lobby_manager`,
`create_networked_player`, `add_sync_property`, RPC generators (`add_rpc_method`,
`create_host_rpc_action`, `add_targeted_rpc`, `create_network_events`), `network_spawn`,
`set_ownership`, `create_local_player_resolver`, `add_host_migration_recovery`, and
`configure_network` / `get_network_status`.

- `create_host_rpc_action` for the SAFE "client asks host to DO something" skeleton (caller re-resolved, per-SteamId cooldown) — the answer to the #1 exploit class.
- `create_local_player_resolver` to stop running local-player logic against a proxy.
- `add_targeted_rpc` to send to one player (a private prompt, a personal reward) instead of broadcasting.

*Prompt:* "Wire up a lobby and a networked player, and make the buy action host-authoritative."

*Safety:* Mutating (except `get_network_status`, read-only). Codegen tools write files
(hotload after). Several patterns only fire with a real session/host migration.

*Related:* `bridge_validation` (`networking_lint`, `inspect_networked_object`), `bridge_scaffold_gameplay`, `bridge_prefab`.

### `bridge_npc` — NPC brains & perception
*7 tools (0 read-only).* `create_npc_brain` (Idle/Patrol/Wander/Chase/Search/Flee/Ambush state
machine with occlusion-aware perception), `create_utility_ai` (an abstract self-scoring
`Action` base + a brain with hysteresis — emergent behaviour where the FSM is scripted; the
two pair), `create_npc_schedule_brain` (a daily schedule with midnight wrap that binds by
capability to any float-`TimeOfDay` clock, honest internal fallback clock, optional
NavMeshAgent), `create_npc_spawner`, `place_patrol_route` + `assign_patrol_route`, and
`simulate_npc_perception` — an edit-mode verifier that runs the same line-of-sight math the
brain uses, so you can confirm "the tree blocks LOS" without play mode.

- Give an enemy a chase brain, lay a patrol route, and verify its sightline in edit mode.
- Spawn escalating waves from spawn points with `create_npc_spawner`.
- `create_npc_schedule_brain` for villagers with a daily routine; `create_utility_ai` when behaviour should *emerge* from scored needs instead of fixed states.

*Prompt:* "Add a guard that patrols these waypoints and chases the player when it sees them."

*Safety:* Mutating (including `simulate_npc_perception`, which is a read-only-style verifier but
grouped as mutating). Sits on top of `bake_navmesh` + `NavMeshAgent`.

*Related:* `bridge_navigation`, `bridge_scaffold_gameplay` (`create_npc_controller`), `bridge_prefab`.

### `bridge_vehicle` — make things drivable
*4 tools (0 read-only).* `create_vehicle_controller` (raycast car with 4-corner suspension,
built-in hidden-driver seat, chase camera, angular-velocity steering), `create_seat_system`
(standalone one-occupant seat — chairs, benches, turret mounts), `create_physics_grab_tool`
(physgun-lite, physics stays live), and `tune_vehicle` (arcade / drift / offroad / race presets
by property name).

- Turn a crate into a car, then `tune_vehicle "drift"` and playtest it.
- Add a `create_seat_system` seat to a bench so players can sit.
- `create_physics_grab_tool` for a Garry's-Mod-style prop grab.

*Prompt:* "Make this box drivable with an arcade feel."

*Safety:* Mutating. `tune_vehicle` is refused in play mode (use `set_property` on runtime
objects while playing). **Honest limit:** compiles + drives is gate-verified; *feel* needs a
human playtest.

*Related:* `bridge_physics`, `bridge_batch` (attach the parts), `bridge_playtest`.

---

## Look & world

### `bridge_visuals` — lighting, fog, post-FX, particles
*15 tools (0 read-only).* `add_light`, `set_fog`, `set_skybox`, `add_post_process`,
`add_envmap_probe` + `bake_reflections`, the one-call `apply_atmosphere` and `apply_post_fx_look`
mood presets, particles (`spawn_vpcf` for the reliable path), `add_render_target_camera`
(a secondary camera rendered to a texture and wired onto a Material — the CCTV / mirror /
portal-screen path), and `add_daynight_sun` (a DirectionalLight arc + color gradient +
optional SkyBox2D tint driven by `create_day_night_clock`'s `TimeOfDay`).

- `apply_atmosphere "horror"` for ambient + directional + fog + post-fx in one call.
- `add_light` a spot for a flashlight; `set_fog "gradient"` for mood haze.
- `spawn_vpcf` for a guaranteed-visible impact/sparks burst.
- `add_render_target_camera` for a security-camera monitor; `add_daynight_sun` to make the clock *visible* in the sky.

*Prompt:* "Light this room like a dim, foggy basement."

*Safety:* Mutating; refused in play mode. **Particle caveat:** the runtime `spawn_particle` /
`create_particle_effect` / `add_trail` / `add_beam` tools are experimental and **don't render
through the bridge** — use `spawn_vpcf`. An `add_envmap_probe` captures nothing until you
`bake_reflections`.

*Related:* `bridge_scaffold_polish` (`add_flicker_light`), `bridge_screenshot` (verify the look), `bridge_world`.

### `bridge_world` — terrain, forests, caves, placement
*16 tools (1 read-only).* Terrain sculpting and features (`add_terrain_hill`,
`add_terrain_clearing`, `add_terrain_trail`, `sculpt_terrain`, `build_terrain_mesh`,
`raycast_terrain`), forests (`add_forest_poi`, `add_forest_trail`, `paint_forest_density`,
`set_forest_seed`), caves (`add_cave_waypoint`), `place_along_path` for
fences/lampposts/rocks, and `add_water_body` (a `WaterVolume` physics volume with a trigger
footprint and an optional tinted surface — honest limit: swimmable water *physics*, not a
water shader).

- Raise hills and carve a trail, then `raycast_terrain` to place props on the surface.
- Paint a dense forest region, add clearings and trails between POIs, regenerate once.
- `place_along_path` a run of fence posts along a road.
- `add_water_body` to make a carved basin actually swimmable.

*Prompt:* "Build some rolling hills and a forest with a clearing in the middle."

*Safety:* Mutating; refused in play mode. Many tools default `rebuild=false` so you can batch
edits then regenerate once (forest gen is slow). **Requires MapBuilder / ForestGenerator /
CaveBuilder-style components** in the project (`invoke_button` works anywhere;
`build_terrain_mesh` is standalone).

*Related:* `bridge_visuals`, `bridge_gameobject` (`scatter_props`), `bridge_navigation` (bake after terrain).

### `bridge_material` — models & materials
*4 tools (0 read-only).* `assign_model`, `assign_material`, `create_material` (`.vmat`, KV1
format), and `set_material_property` (color, roughness, metallic, texture).

- Put a model on an object, assign a material, then tweak its roughness.
- `create_material` → `recompile_asset` → `assign_material` for a custom look.

*Prompt:* "Make this floor a dark, rough metal."

*Safety:* Mutating; refused in play mode. `create_material` writes a file — follow with
`recompile_asset` so the editor compiles it. Community model paths that aren't in *your*
project render as the ERROR mesh (see [BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #4).

*Related:* `bridge_asset` (`copy_asset_with_dependencies`), `bridge_gameobject` (`set_tint`, `replace_model`).

### `bridge_character` — Citizens
*12 tools (1 read-only).* Spawn and outfit Citizen characters and animate them: `spawn_citizen`,
`dress_citizen`, `pose_citizen`, `set_expression`, `set_bodygroup`, `equip_model`, `set_look_at`,
`play_animation` + `set_animgraph_param` + `list_animations`, `add_ragdoll`, and `add_lipsync`.

- Spawn a Citizen, dress it, and pose it holding a rifle.
- `set_look_at` so an NPC tracks the player; `add_lipsync` to drive facial morphs from a sound.
- `add_ragdoll` for physics-driven death (flops in play mode only).

*Prompt:* "Spawn a Citizen, put it in a hard hat, and have it wave."

*Safety:* Mutating (except `list_animations`). Many results only show in play mode
(`add_ragdoll`, `add_lipsync`) — verify with `capture_view` while playing, not the static pose.

*Related:* `bridge_npc` (give it a brain), `bridge_screenshot` (verify poses), `bridge_scaffold_polish`.

---

## Physics, play, and verification

### `bridge_physics` — bodies, colliders, traces
*5 tools (2 read-only).* `add_physics` (Rigidbody + collider), `add_collider`, `add_joint`,
`raycast`, and `physics_overlap`.

- Make a prop dynamic with `add_physics`, then watch it simulate in play mode.
- `raycast` for line-of-sight / placement; `physics_overlap` for "what's near this point".

*Prompt:* "Make these barrels physics objects and connect two of them with a joint."

*Safety:* `add_physics` / `add_collider` / `add_joint` mutate (refused in play mode); `raycast`
and `physics_overlap` are read-only. A `Default Surface not found` on a trace is a known
transient — `restart_editor` and retry.

*Related:* `bridge_vehicle`, `bridge_debug` (`debug_draw_ray` to see a raycast), `bridge_gameobject`.

### `bridge_playmode` — enter/exit play, runtime props
*5 tools (2 read-only).* `start_play`, `stop_play`, `is_playing`, and `get_runtime_property` /
`set_runtime_property` for live objects while playing.

- Enter play, tweak a value live with `set_runtime_property`, watch the effect, stop.
- `is_playing` to gate scene edits (trust the `gameFlag` field — `sessionPlaying` can read stale).

*Prompt:* "Start the game and set the player's move speed to 400 while it runs."

*Safety:* `start_play` / `stop_play` / `set_runtime_property` mutate. Runtime changes are
**discarded** when play stops — use `set_property` in edit mode to persist. Scene-mutating
tools refuse while playing.

*Related:* `bridge_playtest`, `bridge_screenshot` (`take_screenshot` = player view in play mode).

### `bridge_playtest` — scripted gameplay verification
*6 tools (2 read-only).* `playtest` (a scripted step list run in play mode with **in-frame
assertions**), `playtest_status`, `playtest_abort`, `simulate_input`, and the experimental
`drive_player` / `drive_player_status`.

- Walk forward and assert `Displacement > 50`; jump and assert `IsAirborne` the next frame.
- `playtest_abort` a stuck run; the partial transcript stays readable.
- `drive_player` to inject sustained look/move/held-action when `simulate_input` (one frame) isn't enough.

*Prompt:* "Play the game, walk the player forward, and confirm they actually moved."

*Safety:* `playtest` / `playtest_abort` / `simulate_input` / `drive_player` mutate runtime state
(require play mode). Verifies that mechanics **fire**, not that the game **feels** right — a
human playtest is still the final word ([BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #1).

*Related:* `bridge_playmode`, `bridge_screenshot`, `bridge_validation`.

### `bridge_navigation` — navmesh & pathing
*2 tools (1 read-only).* `bake_navmesh` (async; the editor shows a progress bar) and
`get_navmesh_path` (query a walkable route between two points).

- Bake the navmesh so `NavMeshAgent`s can path; validate a route is reachable.
- Check spawn/patrol-point connectivity with `get_navmesh_path`.

*Prompt:* "Bake the navmesh and check there's a path from the spawn to the exit."

*Safety:* `bake_navmesh` mutates (editor op, async — give it a moment). `get_navmesh_path` is
read-only.

*Related:* `bridge_npc`, `bridge_world` (bake after terrain changes).

---

## Inspection, validation & workflow

### `bridge_discovery` — reflect over the API
*5 tools (all read-only).* `describe_type`, `search_types`, `get_method_signature`,
`list_libraries`, and `find_in_project`. Use these **before** writing C# against an unfamiliar
type — reflection is ground truth.

- `describe_type "PlayerController"` to see its real members before you touch it.
- `list_libraries` to discover what you can build *on* (e.g. `fish.scc`, `facepunch.playercontroller`).
- `find_in_project` to grep for how the project already does something.

*Prompt:* "What methods does the NavMeshAgent expose on this SDK?"

*Safety:* All read-only — run freely, no prompts.

*Related:* everything — this is the "check before you build" toolset. Pairs with `bridge_component`, `bridge_networking`, `bridge_scaffold_gameplay`.

### `bridge_validation` — lints & health checks
*9 tools (all read-only).* `find_broken_references`, `scene_validate`, `networking_lint`,
`razor_lint`, `sandbox_lint`, `inspect_networked_object`, `save_inspect`, `services_query`,
and `validate_project`.

- `find_broken_references` before a screenshot — missing models, dead refs, missing prefab files.
- `networking_lint` to catch unguarded `[Sync]` mutators and money-as-plain-`[Sync]` exploits.
- `sandbox_lint` to catch whitelist violations (`Array.Clone()`, `System.Net`) before they compile-fail.

*Prompt:* "Scan the project for broken references and networking footguns."

*Safety:* All read-only. This is the "catch the silent breakage" toolset — run it in the
Validate step of the loop.

*Related:* `bridge_networking`, `bridge_ui` (`razor_lint`), `bridge_project` (`get_compile_errors` is lifeline-side).

### `bridge_workflow` — scene checkpoints (the agent-side undo)
*3 tools (2 read-only).* `checkpoint_scene` (snapshot every root GameObject to temp storage
outside the project), `list_checkpoints`, and `restore_checkpoint` (rebuild the scene from a
snapshot). The practical answer to the engine's addon-inaccessible undo.

- Checkpoint before a risky batch edit; roll back if it goes wrong.
- Browse `list_checkpoints` and restore a specific one by id.

*Prompt:* "Checkpoint the scene before you delete those objects."

*Safety:* `checkpoint_scene` / `list_checkpoints` are read-only; **`restore_checkpoint` is
destructive** — it replaces the scene's entire contents and requires an explicit id (never
guesses). Both mutating-side ops are refused in play mode. The scene file on disk is untouched
until `save_scene`.

*Related:* `bridge_batch` (checkpoint before a big batch), `bridge_scene` (`save_scene` to persist).

### `bridge_debug` — health, console, profiler, debug draw
*11 tools (2 read-only).* `get_bridge_status` (call FIRST in a session), `restart_editor`,
`console_run`, `get_profiler_stats`, `set_time_scale`, the debug-draw primitives
(`debug_draw_line` / `_ray` / `_box` / `_sphere` / `debug_clear`), and `frame_camera`.

- `get_bridge_status` to confirm the bridge is alive and versions align.
- `set_time_scale 0.1` to watch a fast interaction frame-by-frame in play mode.
- `debug_draw_sphere` to visualize an NPC's hearing range or a blast radius.

*Prompt:* "Draw the trigger volume's bounds so I can see it, then screenshot it."

*Safety:* `get_bridge_status` / `get_profiler_stats` are read-only; the rest mutate editor
state. Edit-mode debug-draw gizmos are **not** captured by `take_screenshot` — use
`capture_view` in play mode to see them.

*Related:* `bridge_physics` (visualize traces), `bridge_project` (`restart_editor` is here), the lifeline.

### `bridge_project` — project files, scripts, config
*14 tools (6 read-only).* `describe_project` (one-call orientation), `get_project_info` /
`get_project_config` / `set_project_config`, file I/O (`read_file`, `write_file`,
`list_project_files`), C# script authoring (`create_script`, `edit_script`, `delete_script`),
`trigger_hotload`, `ensure_input_action`, `set_project_thumbnail`, and `get_package_details`.

- `describe_project` as the very first read of a session.
- `create_script` → `trigger_hotload` → attach; `edit_script` for in-place find/replace.
- `ensure_input_action` so a generated game's custom verbs resolve in play mode.

*Prompt:* "Describe the project, then add an input action called 'Grapple' bound to G."

*Safety:* The `get_*` / `read_*` / `list_*` / `describe_project` tools are read-only; the rest
mutate (write files, edit config). `write_file` / `delete_script` silently overwrite/delete —
`read_file` first if unsure. Input config is read at project load — `restart_editor` for a new
action to take effect in play mode.

*Related:* `bridge_discovery`, `bridge_scaffold_gameplay`, `bridge_validation`.

### `bridge_asset` — asset library & packages
*6 tools (3 read-only).* `search_assets`, `list_asset_library`, `get_asset_info`,
`install_asset` (pull a community package by ident), `copy_asset_with_dependencies` (copy a
model + its full dependency closure), and `recompile_asset`.

- `search_assets` / `list_asset_library` to find a model path to `spawn_model` (native built-in).
- `install_asset "facepunch.flatgrass"` to add a package dependency.
- `copy_asset_with_dependencies` to bring a community model local *with* its materials/textures.

*Prompt:* "Find a barrel model in the library and tell me its path."

*Safety:* `search_assets` / `list_asset_library` / `get_asset_info` are read-only;
`install_asset` / `copy_asset_with_dependencies` / `recompile_asset` mutate. `copy_asset…`
**refuses to write under core engine trees** (shadowing them causes an infinite recompile loop —
[BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #5). A code-library install needs `restart_editor`, not
just a hotload.

*Related:* `bridge_material`, `bridge_gameobject`.

### `bridge_audio` — sounds
*5 tools (1 read-only).* `list_sounds`, `create_sound_event` (author a `.sound` wired to a
`.vsnd`), `assign_sound` (attach a `SoundPointComponent`), `play_sound_preview` (test in the
editor without play mode), and `add_tts_voice` (a `Sandbox.Speech.Synthesizer` voice
component — call `Say()` from game code; `enableVisemeData` exposes the live viseme stream).

- `create_sound_event` then `assign_sound` to attach it to an object.
- `play_sound_preview` to audition a sound quickly.
- `add_tts_voice` for dynamic NPC barks/announcers with no recorded audio.

*Prompt:* "Attach a looping ambient hum to the generator."

*Safety:* `list_sounds` is read-only; the rest mutate. Looping lives on the
`SoundPointComponent`, not the `.sound` event. `play_sound_preview` is fire-and-forget (no stop
control). TTS is audio-only **by design** — `LipSync` consumes a `BaseSoundComponent`, not the
`SoundHandle` TTS returns; drive mouths yourself from the exposed viseme data if you need them.

*Related:* `bridge_character` (`add_lipsync` binds a sound), `bridge_scaffold_polish`.

### `bridge_moviemaker` — cutscene authoring, playback & gameplay recording
*10 tools (1 read-only).* Playback: `list_movies`, `add_movie_player` (wire a `MoviePlayer` +
optional `.movie` resource), `play_movie`, and `stop_movie`. Authoring: `author_movie_clip`
(bake a `.movie` cutscene from a declarative shot list — edit mode only, no dock, no
real-time waiting; a temp or borrowed camera is restored exactly afterwards). Recording:
`record_gameplay_clip` (record live gameplay in play mode — whole scene by default, or
targeted objects via `ids`), `stop_gameplay_recording` (writes a compiled `.movie` asset,
immediately loadable by `list_movies` / `play_movie`, with optional MoviePlayer wiring),
`gameplay_recording_status` (poll), `record_playtest` (a scripted playtest AND a recording
of the same run in one call — a failing playtest arrives with replayable footage), and
`create_killcam` (a rolling-buffer killcam scaffold: the last N seconds of a target's
gameplay, replayed through a chase cam on `TriggerReplay()`). The bridge **authors (from
shot lists), records, wires, and plays** movies — hand-keyframing stays in the editor's
Movie Maker dock.

- `list_movies` first — an empty list means bake one (`author_movie_clip`), record one, or author one in the dock.
- `add_movie_player` with `playOnStart` for an intro cinematic; `play_movie` from a trigger.
- `record_gameplay_clip` → play the moment → `stop_gameplay_recording` → replay it with `play_movie` — a replay pipeline in three calls; `create_killcam` packages the same idea as a game feature.
- `record_playtest` for regression footage: verdict + clip from one call.

*Prompt:* "Record the next ten seconds of gameplay and replay it as a cutscene."

*Safety:* `list_movies` is read-only; `add_movie_player` / `author_movie_clip` /
`create_killcam` mutate (refused in play mode); `play_movie` / `stop_movie` /
`record_playtest` work during play. Clips genuinely advance only in play mode, and
recording requires it. The recorder auto-advances with game time — the bridge monitors only
(see [BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #13).

*Related:* `bridge_scaffold_polish` (`create_cutscene_director` is the zero-asset
alternative), `bridge_playtest` (`record_playtest` composes its step schema).

### `bridge_screenshot` — inline PNG capture
*4 tools (1 read-only).* Hand-written. Every capture returns the **PNG inline as an image
block** — no temp-file path to read back. `take_screenshot` (main camera / player view in play
mode), `capture_view` (auto-frame an object or a free position+lookAt camera), `screenshot_from`
(historical alias of `capture_view`), and `screenshot_orbit` (N angles in one call).

- `capture_view {id}` to aim a shot at exactly what you just built.
- `screenshot_orbit` to see all sides of a model in one response.

*Prompt:* "Screenshot the car from the front and read the result."

*Safety:* `take_screenshot` is read-only; `capture_view` / `screenshot_from` / `screenshot_orbit`
move a temporary/editor camera and so are mutating. This is the tool that closes the
build → look → adjust loop — the agent sees the image directly.

*Related:* every visual toolset (`bridge_visuals`, `bridge_material`, `bridge_character`, `bridge_world`).

---

## The lifeline — editor-down diagnostics

**7 tools, stdio, outside the editor.** The native server dies with the editor; the lifeline
doesn't. Run it as a second server entry (`npx -y sbox-mcp-server@2 --lifeline`) and reach for
it the moment port 7269 goes silent.

| Tool | What it does |
|---|---|
| `read_log` | Tail / filter `sbox-dev.log` (e.g. filter `"Error \|"` to surface a whitelist rejection hidden by the broken-reference cascade). |
| `get_compile_errors` | Surface the latest C# compile failure — the thing that most often explains a dead session. |
| `search_docs` | Search the official `Facepunch/sbox-docs` guides. |
| `get_doc_page` | Fetch a specific doc page. |
| `list_doc_categories` | Browse the doc categories. |
| `run_self_test` | Self-diagnostic of the bridge/server. |
| `get_bridge_status` | Connection + version summary (also on the native surface). |

*Prompt:* "The editor just crashed — read the log and tell me why."

*Safety:* Read-only by nature (they read files / fetch docs). No editor needed, which is the
whole point. `execute_csharp` is **not** in the lifeline set (it needs a live editor).

*Related:* [TROUBLESHOOTING.md](TROUBLESHOOTING.md) entry 5 (editor crashed/hung),
[BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md).

---

## Not on the bridge surface (native built-ins serve them)

Six tool names were dropped from the bridge because the native server ships built-ins with the
**same names and semantics** — your workflows keep the names:
`spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component`. The native
server also gives you `spawn_models` (batch), `scene_tree`, `find_game_objects`,
`get_game_object` / `set_game_object`, `add_component` / `set_component`, the `asset_*` family,
`play_start` / `play_stop`, `read_console`, and `compile_status` for free. See
[V2-MIGRATION.md](V2-MIGRATION.md).
