# sbox-mcp-server

MCP Server for the s&box game engine. Lets Claude Code build s&box games through conversation — **200+ tools** for scenes, scripts, GameObjects, components, assets, materials, audio, physics, UI, networking, publishing, world-gen, lighting & atmosphere, characters, lipsync, scene layout, navmesh & spatial queries, particles, animation, NPC brains, playable-game scaffolds, gameplay playtesting, networking & scene inspection/lint, save & services queries, self-diagnosis, console/C# execution, live docs search, and type discovery.

## Fastest install — the Claude Code plugin

If you use Claude Code, the easiest install is the companion plugin. It registers this MCP server automatically, ships the workflow + recipe skills (`sbox-build-feature`, `sbox-api`, `sbox-cookbook`, `sbox-scaffold-game`, `sbox-setup`), and includes the `sbox-game-dev` specialist agent.

```
/plugin marketplace add LouSputthole/Sbox-Claude
/plugin install sbox-claude
```

You still need to install the s&box-side **bridge addon** into your project's `Libraries/` folder (see step 1 below). The plugin handles the Claude side; the addon handles the s&box side.

## Manual install — three steps

### 1. Install the bridge addon in s&box

The bridge addon runs inside the s&box editor and receives commands from this MCP server. It MUST live inside a project's `Libraries/` folder — putting it in s&box's global `addons/` will silently fail to compile.

```powershell
git clone https://github.com/LouSputthole/Sbox-Claude.git
cd Sbox-Claude
.\install.ps1 -RemoveStaleAddons      # Windows, auto-detects your s&box project
./install.sh --remove-stale            # Linux/Mac/WSL
```

See [INSTALL.md](https://github.com/LouSputthole/Sbox-Claude/blob/main/INSTALL.md) for the full guide and manual fallback.

### 2. Register the MCP server with Claude Code

```bash
claude mcp add sbox -- npx sbox-mcp-server
```

This is the bare command — equivalent to what the plugin's `.mcp.json` does for you.

### 3. Open s&box

Open your project. The bridge starts automatically. Verify with:

```
Check the bridge status.
```

You should see `connected: true` with a healthy `handlerCount`. (That's the editor-side handler count; the server exposes a few more tools total — a handful run MCP-server-side and need no editor handler.)

## How it works

```
Claude Code → (stdio) → sbox-mcp-server → (file IPC) → bridge addon → s&box editor
```

Communication uses file-based IPC through `%TEMP%/sbox-bridge-ipc/`. The MCP server writes request JSON files, the bridge addon (running inside s&box) polls and processes on the main editor thread, then writes response files back. WebSocket is not used — s&box's sandboxed C# environment blocks `System.Net`.

## Tools

`get_bridge_status` reports the `handlerCount` — that's the C# handlers compiled inside the editor. A handful of tools run **MCP-server-side** and need no editor handler (`read_log`, `get_compile_errors`, `execute_csharp`, `search_docs`, `get_doc_page`, `list_doc_categories`, `run_self_test`, `screenshot_orbit`). They read the log / hotload-eval / fetch docs / orchestrate other tools directly, so most keep working even when the editor has crashed or stalled.

| Category | Tools |
|----------|-------|
| **Project** | get_project_info, list_project_files, read_file, write_file |
| **Scripts** | create_script, edit_script, delete_script, trigger_hotload |
| **Scenes** | list_scenes, load_scene, save_scene, create_scene |
| **GameObjects** | create/delete/duplicate/rename, set_parent/enabled/transform |
| **Components** | get/set_property, get_all_properties, list_available, add_component, set_prefab_ref |
| **Hierarchy** | get_scene_hierarchy (with `maxDepth` + `rootId`), get/select/focus_object |
| **Assets** | search_assets, list_asset_library, install_asset, get_asset_info |
| **Materials** | assign_model, create/assign_material, set_material_property |
| **Audio** | list_sounds, create_sound_event, assign_sound, play_sound_preview |
| **Play Mode** | start/stop_play, is_playing |
| **Runtime** | get/set_runtime_property, take_screenshot |
| **Editor** | undo, redo |
| **Prefabs** | create/instantiate_prefab, list_prefabs, get_prefab_info |
| **Physics** | add_physics, add_collider, add_joint, raycast |
| **UI** | create_razor_ui, add_screen_panel, add_world_panel |
| **Templates** | create_player/npc_controller, create_game_manager, create_trigger_zone |
| **Networking** | network_helper, configure/status, spawn, ownership, sync, RPCs, lobby/event templates |
| **Publishing** | project_config, validate, thumbnail, package_details |
| **World gen** | invoke_button, list_component_buttons, raycast_terrain, build_terrain_mesh |
| **Map edit** | add_terrain_hill/clearing/trail, clear_terrain_features, sculpt_terrain |
| **Caves / Forest** | add_cave_waypoint, clear_cave_path, add_forest_poi/trail, set_forest_seed, clear_forest_pois, paint_forest_density |
| **Placement** | place_along_path |
| **Discovery** | describe_type, search_types, get_method_signature, find_in_project |
| **Status** | get_bridge_status |
| **Visual & atmosphere** *(v1.4.0)* | add_light, set_fog, add_post_process, set_skybox, add_envmap_probe, apply_atmosphere, apply_post_fx_look |
| **Characters** *(v1.4.0)* | spawn_model, spawn_citizen, dress_citizen, set_bodygroup, pose_citizen, equip_model, set_look_at, add_ragdoll, set_expression |
| **Scene & level** *(v1.4.0)* | snap_to_ground, align_objects, distribute_objects, grid_duplicate, measure_distance |
| **Environment** *(v1.4.0)* | scatter_props, randomize_transforms, group_objects |
| **Object utilities** *(v1.4.0)* | find_objects, set_tint, replace_model, set_tags |
| **VFX** *(v1.4.0, experimental)* | spawn_particle, create_particle_effect, add_trail, add_beam — compile but do **not** render through the bridge; use `spawn_vpcf` (below) for visible particles |
| **Diagnostics** *(v1.5.0, MCP-server-side)* | read_log, get_compile_errors — read `sbox-dev.log` directly; work even when the editor has crashed |
| **Camera** *(v1.5.0)* | screenshot_from (**aim a shot at any object/point** — `take_screenshot` is fixed to the Main Camera), frame_camera (move the editor viewport) |
| **Navigation** *(v1.5.0)* | bake_navmesh, get_navmesh_path |
| **Spatial** *(v1.5.0)* | physics_overlap (volume counterpart to raycast) |
| **Reflections** *(v1.5.0)* | bake_reflections (a placed EnvmapProbe captures nothing until baked) |
| **Particles** *(v1.5.0)* | spawn_vpcf — compiled `.vpcf` via LegacyParticleSystem, the **supported** particle path |
| **Console / Exec** *(v1.5.0)* | console_run, execute_csharp *(experimental)* |
| **Object utilities** *(v1.5.0)* | remove_component, get_tags |
| **Docs search** *(v1.5.0, MCP-server-side)* | search_docs, get_doc_page, list_doc_categories — official `Facepunch/sbox-docs` |
| **Inspection & validation** *(v1.9.0)* | inspect_networked_object (per-object `Network.*` + every component's `[Sync]` fields/values), networking_lint (static scan for `[Sync]`/RPC footguns), scene_validate (no-camera / stray root Rigidbody / trigger-vs-trace), save_inspect (list/read/diff `FileSystem.Data` saves), services_query (`Sandbox.Services` stats + leaderboards), simulate_input (drive named input actions in play mode) |
| **Editor & self-test** *(v1.5.1–v1.7)* | restart_editor (self-restart + reconnect — closes the C#-edit loop), list_libraries, recompile_asset, run_self_test (8-check end-to-end health gate), capture_view (**renders the RUNNING play scene** from any camera — the play-mode eyes) |
| **Animation & verification** *(v1.6.0)* | list_animations, play_animation, set_animgraph_param, get_bounds, screenshot_orbit (multi-angle orbit shots of any object) |
| **Call & input** *(v1.10.0)* | invoke_method (call a component method **with args**), ensure_input_action (add a `.sbproj` input action), drive_player / drive_player_status (play-mode input driver) |
| **Gameplay scaffolds** *(v1.7–v1.13)* | set_component_reference, add_component_to_new_object, create_objective_system, create_health_system, create_pickup, create_trigger_zone, create_economy_wallet, create_round_phase_machine, create_day_night_clock, create_interactable, create_weighted_loot_table, create_save_system, create_leaderboard_panel, create_inventory, create_stat_modifier_system, create_placement_mode |
| **NPC brains** *(v1.7.0)* | create_npc_brain (FSM: Idle/Patrol/Wander/Chase/Search/Flee/Ambush + FOV/LOS/hearing), place_patrol_route, assign_patrol_route, create_npc_spawner, simulate_npc_perception |
| **Lints & asset utils** *(v1.12.0)* | sandbox_lint (whitelist pre-compile scan), razor_lint (Razor/SCSS transpiler footguns), copy_asset_with_dependencies (asset + full dependency closure) |
| **Meta & debug-draw** *(v1.14–v1.15)* | set_time_scale (pause/slow-mo/fast-forward), get_profiler_stats (FPS/frame/GPU/allocations), debug_draw_line/ray/box/sphere, debug_clear |
| **Playtest harness** *(v1.17)* | playtest (scripted gameplay loop in PLAY MODE with **in-frame assertions** — move/look/action/jump/set/wait/capture/assert), playtest_status |
| **Lipsync** *(v1.18.0)* | add_lipsync — wires `Sandbox.LipSync` (new in the 2026-07-01 engine update) to a citizen/model: `SkinnedModelRenderer` + a `SoundPointComponent` bound to a `.sound` path, facial morphs animating while the sound plays |
| **Gameplay scaffolds** *(v1.18.0)* | create_round_state_machine (multi-state round manager + abstract `RoundState` lifecycle base, host-authoritative with late-joiner reconcile), add_interaction_station (one-occupant `IPressable` station, host-routed claims, reservation grace window, level gate), create_event_director (weighted-interval pacing/AI director, dedupe, `MaxActive` cap, timed self-destruct), create_save_slots (multi-slot save manager, versioned, optional GUID scene reconciliation — companion to create_save_system) |

## Working with Claude effectively

Three disciplines prevent the iteration-loop trap:

1. **After visual changes, see the result — and aim the camera.** `take_screenshot` renders from the scene's Main Camera (one fixed angle), so it often won't show the thing you just changed. Use **`screenshot_from`** to point the camera at the target object/point, then read the PNG. Claude is a multimodal model — guessing about visual outcomes from code alone produces long iteration loops.
2. **Before writing code that touches an unfamiliar s&box type, call `describe_type` or `search_types`.** Reflection is the source of truth; training data goes stale across SDK versions.
3. **When something breaks, read the log instead of guessing.** `get_compile_errors` surfaces the latest C# compile failures and `read_log` tails `sbox-dev.log` — both MCP-server-side, so they work even if the editor crashed.

The companion plugin's `sbox-build-feature` skill encodes this workflow plus the common gotchas. If you're not using the plugin, the same rules apply manually.

## Requirements

- **Node.js 18+**
- **s&box** with the bridge addon installed in your project's `Libraries/` folder
- **Claude Code**

## Documentation

- [Main README](https://github.com/LouSputthole/Sbox-Claude/blob/main/README.md) — full project overview
- [INSTALL.md](https://github.com/LouSputthole/Sbox-Claude/blob/main/INSTALL.md) — install + manual fallback
- [TROUBLESHOOTING.md](https://github.com/LouSputthole/Sbox-Claude/blob/main/TROUBLESHOOTING.md) — common failures and fixes
- [CHANGELOG.md](https://github.com/LouSputthole/Sbox-Claude/blob/main/CHANGELOG.md) — release history
- [Plugin README](https://github.com/LouSputthole/Sbox-Claude/blob/main/plugins/sbox-claude/README.md) — Claude Code plugin docs

## License

**AGPL-3.0-or-later** — see [LICENSE](../LICENSE) and [NOTICE](../NOTICE) for details. The code is open under AGPL; the "s&box Claude Bridge" / "sboxskins.gg" name and branding may not be reused to pass a fork off as the original.

Copyright (c) 2026 [sboxskins.gg](https://sboxskins.gg)
