# s&box Claude Bridge

> **Build s&box games by talking to Claude Code.** Describe what you want — Claude writes the C#, builds the scenes, wires up components, and iterates until it works.

<p>
<strong>v1.18.0</strong> · <strong>206 tools</strong> · <strong>197 handlers</strong> · Source-available (no redistribution) · built by <a href="https://sboxskins.gg">sboxskins.gg</a>
</p>

<p>📖 <strong>Full docs:</strong> <a href="https://sboxskins.gg/claudebridge">sboxskins.gg/claudebridge</a> — <a href="https://sboxskins.gg/claudebridge/plugin">setup</a> · <a href="https://sboxskins.gg/claudebridge/changelog">changelog</a> · <a href="https://sboxskins.gg/claudebridge/troubleshooting">troubleshooting</a> · <a href="https://sboxskins.gg/claudebridge/faq">FAQ</a></p>

```
You:    "Make a horror game where I explore an abandoned hospital with a flashlight."
Claude: *creates scripts, builds the scene, sets the lighting and fog, adds a player
         controller, takes a screenshot, reads it, fixes the angle, and shows you.*
```

Claude Code connects to the **live s&box editor** through a file-based bridge. It can create GameObjects, write and hotload scripts, compose scenes, sculpt terrain, set up networking and UI, drive characters, bake navmesh, read its own compile errors, and — crucially — **screenshot what it built and look at it** so it can close the build-and-check loop instead of guessing.

---

## How it works

There are **two halves**. Both must be installed, and both must be on **matching versions**.

```
┌──────────────┐   stdio    ┌──────────────┐   file IPC    ┌───────────────┐
│  Claude Code │ ◄────────► │  MCP Server  │ ◄───────────► │ Editor Addon  │
│              │            │ (npm, TS/Node)│  %TEMP%/      │ (C#, in s&box)│
└──────────────┘            └──────────────┘ sbox-bridge-  └───────┬───────┘
                                              ipc/                  │
                                                                    ▼
                                                            ┌───────────────┐
                                                            │  s&box Editor │
                                                            │   (Source 2)  │
                                                            └───────────────┘
```

| Half | What it is | Where it lives |
|---|---|---|
| **MCP server** | TypeScript/Node program that exposes the bridge's full toolset to Claude Code over stdio | npm package `sbox-mcp-server` (or run from source) |
| **Editor addon** | C# editor library that runs *inside* s&box and actually executes the work | installed via the s&box **Library Manager** (`sboxskinsgg.claudebridge`) into your **project's `Libraries/` folder** |

**Why file IPC and not a socket?** s&box's sandboxed C# blocks `System.Net` (no `HttpListener`, no WebSocket, no TCP). So the MCP server writes request JSON files into a shared temp dir, the addon's editor-frame loop picks them up, runs them on the main editor thread, and writes responses back. The MCP server polls for the reply. Simple, sandbox-safe, and the reason for two of the gotchas below.

> **A few tools run entirely MCP-server-side** and need no editor handler — `read_log`, `get_compile_errors`, `execute_csharp`, `search_docs`, `get_doc_page`, and `list_doc_categories`. They read the log file or fetch docs directly, so they **keep working even when the editor has crashed or stalled** — part of why the tool count is higher than the live handler count.

---

## Install

Pick the path with the least resistance for you. **Every path needs both halves** — the MCP server *and* the s&box editor addon.

### A. Claude Code plugin — easiest

The plugin registers the MCP server for you (pinned to `sbox-mcp-server@1.18.0`, fetched via `npx` on first use) and ships the workflow skills, the onboarding wizard, and the specialist agent.

1. **Add the marketplace + install the plugin** (in Claude Code):
   ```
   /plugin marketplace add LouSputthole/Sbox-Claude
   /plugin install sbox-claude
   ```
2. **Install the editor addon** via the s&box **Library Manager** — open **Editor → Library Manager** (this is *not* the Asset Browser; the bridge is a **Library**, not a content asset, so it won't show up there), search for **`sboxskinsgg.claudebridge`**, and install it *into your project*. It lands in `<your-project>/Libraries/`.
3. **Open s&box**, open your project, and open the **View → Claude Bridge** dock. Leave it open (see the note below).
4. **Verify** in a new Claude Code session: *"Check the bridge status."* You want `connected: true` and a non-zero `handlerCount`.

### B. npm + manual MCP registration

If you don't use the plugin, register the server yourself.

1. **Register the MCP server** (one-time):
   ```bash
   claude mcp add sbox -- npx -y sbox-mcp-server@latest
   ```
2. **Install the editor addon** via the s&box **Library Manager** (`sboxskinsgg.claudebridge`) into your project — same as path A, step 2.
3. **Open s&box**, open the **View → Claude Bridge** dock, keep it visible.
4. **Verify:** ask Claude to *"check the bridge status."*

### C. Fully manual / from source

For hacking on the bridge itself, or if you'd rather not use the Library Manager.

1. **Clone and build the MCP server:**
   ```bash
   git clone https://github.com/LouSputthole/Sbox-Claude.git
   cd Sbox-Claude/sbox-mcp-server
   npm install
   npm run build
   ```
2. **Register the built server** with Claude Code:
   ```bash
   claude mcp add sbox -- node /full/path/to/Sbox-Claude/sbox-mcp-server/dist/index.js
   ```
3. **Install the addon into your project.** Use the helper script from the repo root — it copies the addon to `<your-project>/Libraries/claudebridge/` (the correct location) and can clean up old wrong-location installs:
   ```powershell
   # Windows
   .\install.ps1                     # auto-detects your s&box project
   .\install.ps1 -RemoveStaleAddons  # also remove old <sbox>/addons/ installs
   ```
   ```bash
   # Linux / macOS / WSL
   ./install.sh                      # auto-detects
   ./install.sh --remove-stale       # also remove old installs
   ```
4. **Open s&box**, open the **View → Claude Bridge** dock, keep it visible, and verify.

> See **[INSTALL.md](INSTALL.md)** for the long-form guide and **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** for the 10 most common failure modes.

### Two install rules that bite everyone

- **The addon must live in your project's `Libraries/` folder — not s&box's global `addons/` folder.** The global folder is built-in-only and silently refuses to compile custom C#. The Library Manager install and the `install` scripts both put it in the right place.
- **The Claude Bridge dock must stay open.** The bridge's editor-frame loop (which drains the request queue *and* drives the heartbeat) only fires while the dock is visible. Close it and every tool call times out at 30s.

> **Connection issue / 30s hangs?** The single most common cause is the MCP server and the addon resolving **different** temp dirs (Node reads `TEMP`, C# reads `TMP`). Set **`SBOX_BRIDGE_IPC_DIR`** to the same absolute path on both sides to realign. The addon logs its resolved dir (`[SboxBridge] … IPC at <dir>`) and reports it in `get_bridge_status`.

---

## Tools & features

**The full toolset**, grouped by area below (the tool count runs a little above the live handler count — a handful of log/docs/exec tools run server-side). Highlights per area; the server's `--help` lists every tool, and `describe_type` / live reflection is always the source of truth for s&box APIs.

### Project, files & scripts
| Tool | Does |
|---|---|
| `get_project_info`, `list_project_files`, `read_file`, `write_file` | Inspect and edit project files (path-traversal-guarded) |
| `create_script`, `edit_script`, `delete_script` | Author C# components and classes |
| `trigger_hotload` | Recompile + hot-reload after a code edit |
| `recompile_asset` *(v1.5.2)* | Compile a project asset after you write/edit it (e.g. a material → `.vmat_c`) — pairs with `write_file` |

### Scenes, GameObjects & hierarchy
| Tool | Does |
|---|---|
| `list_scenes`, `load_scene`, `save_scene`, `create_scene` | Manage `.scene` files |
| `create_gameobject`, `delete_gameobject`, `duplicate_gameobject`, `rename_gameobject` | GameObject CRUD |
| `set_parent`, `set_enabled`, `set_transform` | Parenting, enable/disable, position/rotation/scale |
| `get_scene_hierarchy` (`maxDepth` + `rootId`), `get_selected_objects`, `select_object`, `focus_object` | Traverse a subtree without dumping the whole scene |

### Components
| Tool | Does |
|---|---|
| `list_available_components` | Discover component types in the SDK |
| `add_component_with_properties`, `remove_component` | Add/remove components |
| `get_property`, `set_property`, `get_all_properties` | Read/write component properties (edit-mode) |
| `set_prefab_ref` | Assign a prefab GameObject to a component property |

### Physics & spatial
| Tool | Does |
|---|---|
| `add_physics`, `add_collider`, `add_joint` | Rigidbodies, box/sphere/capsule/hull colliders, joints |
| `raycast` | Cast a ray, get the hit |
| `physics_overlap` | Sphere/box volume query — *what's in this region* (the volume counterpart to `raycast`) |

### UI
`create_razor_ui` (Razor `.razor` panel + scss), `add_screen_panel`, `add_world_panel` — screen-space HUDs and in-world panels.

### Networking
| Tool | Does |
|---|---|
| `add_network_helper`, `configure_network`, `get_network_status`, `network_spawn`, `set_ownership` | Stand up multiplayer, spawn networked objects, transfer ownership |
| `add_sync_property`, `add_rpc_method` | Annotate a property `[Sync]`; generate an RPC stub *(honest schemas — they do exactly this)* |
| `create_networked_player`, `create_lobby_manager`, `create_network_events` | Scaffolds for networked play |

### Inspection & validation *(v1.9.0)*
| Tool | Does |
|---|---|
| `inspect_networked_object` | Dump a single object's `Network.*` state plus every component's `[Sync]` fields (flags + live values) — *see exactly what replicates* |
| `networking_lint` | Static scan for multiplayer footguns: unguarded `[Sync]` mutators, money/health/score as plain `[Sync]`, `List`/`Dictionary` as `[Sync]`, and `[Rpc.Host]` methods that never re-check `Rpc.Caller` |
| `scene_validate` | Catch scene-setup footguns — no camera, stray root `Rigidbody`s, `IsTrigger`-vs-trace mismatches |
| `save_inspect` | List / read / diff the project's `FileSystem.Data` save files |
| `services_query` | Read `Sandbox.Services` stats + leaderboards |
| `simulate_input` | Drive named input actions in play mode (press/hold a bound action without a keyboard) |

### Materials & audio
| Tool | Does |
|---|---|
| `assign_model`, `create_material`, `assign_material`, `set_material_property` | Models + materials (see Known Issues re: `create_material`) |
| `list_sounds`, `create_sound_event`, `assign_sound`, `play_sound_preview` | Sound events and playback |

### Prefabs & templates
`create_prefab`, `instantiate_prefab`, `list_prefabs`, `get_prefab_info` for prefabs; `create_player_controller`, `create_npc_controller`, `create_game_manager`, `create_trigger_zone`, `create_round_state_machine`, `add_interaction_station`, `create_event_director`, `create_save_slots` *(v1.18.0)* for ready-made gameplay scaffolds.

### Lighting & atmosphere
| Tool | Does |
|---|---|
| `add_light` | Directional / point / spot / ambient (intensity = colour magnitude; `brightness` scales RGB, >1 for HDR) |
| `set_fog`, `add_post_process`, `set_skybox`, `add_envmap_probe` | Haze, bloom/tonemapping/vignette/DoF, skybox, reflection probes |
| `apply_atmosphere`, `apply_post_fx_look` | One-call presets (`horror-night`, `foggy-dawn`, `warm-interior`, `overcast`) |
| `bake_reflections` | Bake all `EnvmapProbe`s — *a placed probe captures nothing until baked* |

### Characters & models
| Tool | Does |
|---|---|
| `spawn_model`, `spawn_citizen` | Any model (with tint); an animated Citizen that idles in-editor |
| `dress_citizen`, `set_bodygroup`, `equip_model`, `set_expression` | Clothing, bodygroups, attached props, facial morphs |
| `pose_citizen`, `set_look_at`, `add_ragdoll` | Hold/move/sit/crouch poses, gaze tracking, ragdoll physics |
| `add_lipsync` *(v1.18.0)* | Wire `Sandbox.LipSync` to a citizen/model — `SkinnedModelRenderer` + a `SoundPointComponent` bound to a `.sound` path; facial morphs animate while the sound plays |

### Scene layout & environment
| Tool | Does |
|---|---|
| `snap_to_ground`, `align_objects`, `distribute_objects`, `grid_duplicate`, `measure_distance` | Compose and arrange objects |
| `scatter_props`, `randomize_transforms`, `group_objects` | Seeded scatter, natural variation, reparent under a centroid |
| `find_objects`, `set_tint`, `replace_model`, `set_tags`, `get_tags` | Query by name/type/tag (composable — feed GUIDs into align/distribute/group/delete) |

### Terrain & world generation
| Tool | Does |
|---|---|
| `invoke_button`, `list_component_buttons` | **The keystone** — press any `[Button]` on any component by label or method name |
| `add_terrain_hill`, `add_terrain_clearing`, `add_terrain_trail`, `clear_terrain_features`, `sculpt_terrain`, `build_terrain_mesh` | Heightmap features + a direct raise/lower/flatten/smooth brush |
| `raycast_terrain` | Sample surface height at a world XY to place props on the ground |
| `add_cave_waypoint`, `clear_cave_path` | Cave tunnel paths |
| `add_forest_poi`, `add_forest_trail`, `set_forest_seed`, `clear_forest_pois`, `paint_forest_density` | Procedural forest clearings, trails, density painting, re-rolls |
| `place_along_path` | Drop model instances along a curve with spacing/jitter/scale |

> The named map tools work on **any project** whose components follow the `[Property] List<Feature>` + `[Button]`-to-rebuild convention — they find the component by reflection, mutate the list, and re-press the button. No hard dependency on any specific game's code. See [CHANGELOG.md](CHANGELOG.md) `[1.1.0]` for the convention.

### Navigation
`bake_navmesh` (enable + bake `NavMesh.BakeNavMesh`, async) and `get_navmesh_path` (query a walkable route via `GetSimplePath`; returns the path or `reachable:false`).

### Particles
`spawn_vpcf` — play a compiled `.vpcf` via `LegacyParticleSystem`. **This is the supported particle path.** The experimental runtime tools `spawn_particle`, `create_particle_effect`, `add_trail`, `add_beam` compile and build the right component graph but **do not render visibly through the bridge** (see Known Issues).

### Verification, diagnostics & self-restart
| Tool | Does |
|---|---|
| `take_screenshot` | Render the scene's **Main Camera** (one fixed angle) |
| `screenshot_from` | **Aim a screenshot at any object/point**, capture, and restore — the habit that makes the whole authoring layer screenshot-verifiable |
| `frame_camera` | Move the editor *viewport* to focus an object/point |
| `read_log`, `get_compile_errors` *(server-side)* | Tail/filter `sbox-dev.log` and surface C# compile failures — **work even when the editor has crashed**, so Claude can debug itself |
| `restart_editor` *(v1.5.1)* | Restart the s&box editor and wait for the bridge to reconnect — **closes the C#-edit → recompile loop** so addon changes apply without a manual restart |
| `playtest` / `playtest_status` *(v1.17.0)* | **Run a scripted gameplay loop in play mode and assert the result in-frame** — `move`/`look`/`action`/`jump`/`set`/`wait`/`capture`/`assert`, with a `Displacement` check and transient-state catches (a jump's airborne frame). The only way to verify a *playable loop* (not just a static scene), since transient state is gone by the time a separate call lands. Poll `playtest_status` for the per-step pass/fail transcript |

### Console & C# execution
`console_run` (run an s&box ConCmd via `ConsoleSystem.Run`) and `execute_csharp` *(experimental)* — compile + run a C# snippet in the unsandboxed editor context (temp `[ConCmd]` → hotload → run → read result from the log → clean up).

### Discovery & library detection
| Tool | Does |
|---|---|
| `describe_type`, `search_types`, `get_method_signature` | Live `Game.TypeLibrary` reflection — **the source of truth** for s&box APIs (call `describe_type "MeshComponent"` before writing code that touches it) |
| `find_in_project` | Grep the project for usage examples |
| `list_libraries` *(v1.5.1)* | List installed s&box libraries/addons so Claude can **build on what you already have** (e.g. drive a character controller instead of writing movement from scratch — see Integrations) |

### Documentation search
`search_docs`, `get_doc_page`, `list_doc_categories` *(server-side)* — search the official **`Facepunch/sbox-docs`** guides (git-tree cached + raw Markdown), so Claude can consult real docs without leaving the session.

### Publishing & status
`get_project_config`, `set_project_config`, `validate_project`, `set_project_thumbnail`, `get_package_details` for shipping; `get_bridge_status` for the connection health-check (IPC dir, heartbeat age, real round-trip result, build version).

> **Not implemented** (no s&box editor API exists): `pause_play`, `resume_play`, `get_console_output`, `clear_console`, `build_project`, `get_build_status`, `clean_build`, `export_project`, `prepare_publish`. Removed from the surface in v1.3.0. For console output, use `read_log` instead.

---

## Integrations

- **Claude Code plugin** — bundles the MCP server config, the `sbox-build-feature` workflow skill, the `sbox-api` schema-grounded API skill, the `sbox-cookbook` recipe router, the `sbox-scaffold-game` starter-scene skill, the `sbox-setup` onboarding wizard, and the `sbox-game-dev` specialist agent. (See the next section.)
- **The s&box engine** — the file-IPC editor addon runs inside the editor and executes everything on the main thread.
- **Installed-library detection & leverage** — `list_libraries` reads your project's `Libraries/` and each `.sbproj`, so Claude can discover and *drive what you already have* rather than reinventing it. If you have the **Shrimple Character Controller** (`fish.scc`) or **`facepunch.playercontroller`**, it can wire up player movement via `add_component_with_properties` instead of writing a controller from scratch. The `sbox-setup` wizard surfaces this on first connect.
- **s&box Cloud assets** — reference cloud models/textures/sounds (note: Cloud-only assets are ephemeral across restarts — prefer local files for anything permanent).
- **Facepunch docs** — `search_docs` queries the official `Facepunch/sbox-docs` repo so API guidance comes from the source, not stale memory.

---

## The Claude plugin

**`sbox-claude`** is the recommended way to use the bridge from Claude Code. It bundles:

| Piece | What it is |
|---|---|
| **MCP server config** | `.mcp.json` pins `sbox-mcp-server@1.18.0` and fetches it via `npx -y` on first use — no manual registration, no version drift |
| **Skill: `sbox-build-feature`** | The screenshot-driven build workflow: confirm the bridge is alive → brainstorm non-trivial features → research the API with `describe_type` → bite-sized edits → hotload + scan the log → **screenshot and read it yourself**. Plus a table of s&box gotchas (`MathF` doesn't exist in the sandbox; Cloud assets aren't persistent; Citizen bone names are case-sensitive; `CitizenAnimationHelper.IkRightHand` drives IK at runtime; `Color` properties want `"r, g, b, a"` strings; etc.) |
| **Skill: `sbox-api`** | Schema-grounded s&box API knowledge — the Unity→s&box translation table, the Ten Rules, and curated component/UI/networking/physics references, so Claude stops hallucinating Unity patterns |
| **Skill: `sbox-cookbook`** *(v1.9.0)* | A master **router** indexing code-grounded recipes mined from **27 current (2026) open-source s&box games** plus the modern engine repos. Its `references/` hold **11 engine** references (networking-authority, architecture, components-lifecycle, player-controller, ui-razor, combat-weapons, input-interaction, physics-traces-movement, worldgen-rendering, performance-threading, data-assets), **15 systems** (inventory, economy-currency, shop-vendor, save-persistence, progression-upgrades, gacha-loot, leaderboards-services, idle-offline, building-placement, crafting, dialogue, round-match, spawning-waves, anti-cheat, level-design), and **14 genre recipes** (tycoon-idle, shopkeeper, document-sim, roleplay, sandbox-voxel, social-hub, platformer-obstacle, deathmatch-arena, card-battler, survival-horror, gacha-crawler, puzzle, vehicles, party-microgame). Ask "how do I build a tycoon / an inventory / a save system?" and it routes you to a grounded how-to |
| **Skill: `sbox-scaffold-game`** | Turns one ask into a playable starter scene (first-person preset) by orchestrating the scaffold tools |
| **Skill: `sbox-setup`** | A warm ~30-second onboarding wizard. It greets you on first connect, verifies the bridge, **detects your installed libraries** (`list_libraries`), recommends a concrete first move, and points you to help + feedback |
| **Agent: `sbox-game-dev`** | A specialist sub-agent for self-contained game-dev tasks; it runs `sbox-build-feature` as its default workflow |

**Day to day:** install the plugin → open s&box with the addon + dock → start a Claude Code session and it greets you and detects your setup → just ask it to build things. Run **`/sbox-setup`** anytime to re-orient, or invoke **`/sbox-claude:sbox-build-feature`** to force the disciplined workflow. See [`plugins/sbox-claude/README.md`](plugins/sbox-claude/README.md) for the full plugin docs.

---

## Quickstart — your first 5 minutes

Once both halves are installed and s&box is open with the **Claude Bridge** dock visible:

1. **Confirm the connection.** Ask: *"Check the bridge status."* Claude calls `get_bridge_status` — you want `connected: true`, a healthy `handlerCount`, and a fresh heartbeat. (Timeout? See the `SBOX_BRIDGE_IPC_DIR` note above and [TROUBLESHOOTING.md](TROUBLESHOOTING.md).)
2. **Get oriented.** Run **`/sbox-setup`** — it detects your libraries and suggests a first move.
3. **Spawn something.** *"Add a cube at 0, 0, 100 and put a box model on it."* or *"Spawn a Citizen and have it idle."*
4. **See it.** *"Screenshot it from the front."* Claude uses `screenshot_from` to aim the camera at what you just made, then reads the PNG and tells you what it sees. That loop — build → aim → screenshot → read → adjust — is the whole game.

Then ask for the real thing: *"Create a first-person player controller with WASD and mouse look,"* or *"Set a horror-night mood with fog and a flickering light."*

---

## Troubleshooting & feedback

- **Stuck?** Start with **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** — wrong install location, dock-closed timeouts, IPC-dir mismatch, stale `.csproj` paths, `create_material` workaround, color formatting, and more. Claude can also read its own errors: ask it to check the log (`read_log` / `get_compile_errors`) even when the editor is unresponsive.
- **Bugs & feature requests:** **[github.com/LouSputthole/Sbox-Claude/issues](https://github.com/LouSputthole/Sbox-Claude/issues)**.
- **Deeper docs:** [CLAUDE.md](CLAUDE.md) (architecture, verified APIs, lessons learned) and [CHANGELOG.md](CHANGELOG.md) (full feature history).

### Known issues (short list)
- **Particles:** runtime `ParticleEffect` tools don't render through the bridge — use `spawn_vpcf`. No flame `.vpcf` currently ships in a bridge-loadable form (under investigation).
- **`take_screenshot`** is locked to the Main Camera and ignores its `path` arg — use `screenshot_from` to aim, then read the newest file in `<sbox>/screenshots/`.
- **`is_playing.sessionPlaying`** can read stale after a restart — trust the `gameFlag` field.
- **`create_material`** has a dictionary-key bug — workaround: write the `.vmat` via `write_file` (see TROUBLESHOOTING.md).
- **`execute_csharp`** is experimental (hotload latency; briefly recompiles the editor assembly).

---

## License

**Source-available (no redistribution)** — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

You can freely use the Claude Bridge to build your s&box games — free or commercial — and modify it locally for your own use. You may **not** redistribute, fork, mirror, repackage, or re-host the bridge or its source, publish a derivative of it, or offer it to others as a service. The code you create *with* the bridge (your games and their source) is yours and is not restricted.

> **Branding & trademark.** The **"s&box Claude Bridge"** / **"sboxskins.gg"** name, logos, and branding are trademarks of sboxskins.gg and are *not* licensed for reuse. See [NOTICE](NOTICE).

> **Prior versions.** Releases before this license took effect were AGPL-3.0-or-later and remain available under that license; this and all later versions are governed by the source-available license above.

Built by **[sboxskins.gg](https://sboxskins.gg)** — the s&box community marketplace.

Copyright © 2026 [sboxskins.gg](https://sboxskins.gg)
