# s&box Claude Bridge

> **Build s&box games by talking to Claude Code.** Describe what you want — Claude writes the C#, builds the scenes, wires up components, and iterates until it works.

<p>
<strong>v2.2.0</strong> · <strong>273 native tools</strong> · <strong>286 total tools / 278 handlers</strong> · <strong>28 toolsets</strong> (+ 7 lifeline tools) · Source-available (no independent redistribution; official channels authorized) · built by <a href="https://sboxskins.gg">sboxskins.gg</a>
</p>

<p>📖 <strong>Full docs:</strong> <a href="https://sboxskins.gg/claudebridge">sboxskins.gg/claudebridge</a> — <a href="https://sboxskins.gg/claudebridge/plugin">setup</a> · <a href="https://sboxskins.gg/claudebridge/changelog">changelog</a> · <a href="https://sboxskins.gg/claudebridge/troubleshooting">troubleshooting</a> · <a href="https://sboxskins.gg/claudebridge/faq">FAQ</a></p>

```
You:    "Make a horror game where I explore an abandoned hospital with a flashlight."
Claude: *creates scripts, builds the scene, sets the lighting and fog, adds a player
         controller, takes a screenshot — sees the PNG right in the tool result —
         fixes the angle, and shows you.*
```

Claude Code connects to the **live s&box editor** through the editor's **native MCP server**. It can create GameObjects, write and hotload scripts, compose scenes, sculpt terrain, set up networking and UI, drive characters, bake navmesh, read its own compile errors, and — crucially — **screenshot what it built and look at it** so it can close the build-and-check loop instead of guessing.

> ### 🚀 New in v2.0.0 — the "Native" relaunch
> The bridge now runs on s&box's own editor MCP server. Start here:
> **[docs/RELAUNCH.md](docs/RELAUNCH.md)** (what changed and why) ·
> **[docs/AGENT-GUIDE.md](docs/AGENT-GUIDE.md)** (how an agent works the platform) ·
> **[docs/ECOSYSTEM.md](docs/ECOSYSTEM.md)** (plain-English tour of all 28 toolsets) ·
> **[docs/FAQ.md](docs/FAQ.md)** (do my old workflows still work, can an agent modify my project, what's next).

---

## What's new in v2.2.0

- **Rotation and vector arguments just work now.** The native MCP gate stringifies structured
  arguments; every rotation/vector parse path now unwraps stringified JSON and accepts comma
  strings (`"12,45,7"`), arrays, and objects. No more workarounds through `set_game_object`.
- **The multiplayer test harness is live.** `start_multiplayer_test` boots real `sbox.exe
  -joinlocal` clients into a private hidden lobby; `multiplayer_test_status` tracks connections
  and client processes truthfully (dead clients report dead); `stop_multiplayer_test` cleans up.
  Capacity checks, overlapping-run prevention, false-positive join guards, failed-start rollback.
- **First-party Codex distribution.** The **[s&box Codex Bridge](plugins/sbox-codex-bridge/)**
  ships from this repo: same 286 tools, native editor MCP, lifeline diagnostics, API brain,
  cookbook, and workflow skills — packaged for OpenAI Codex and generated from the Claude
  plugin by `scripts/gen-codex-plugin.mjs` to enforce parity.
- Prefab `[Property]` values are documented as **authoritative over code defaults** — tuning
  flows through prefab edits (see the `sbox-build-feature` gotchas reference).

---

## What's new in v2.0.0

v2 moves the bridge off the file-IPC transport and onto **s&box's built-in editor MCP server** (shipped in the editor since July 2026, on by default). The headline wins:

- **Inline PNG screenshots** — `take_screenshot`, `capture_view`, `screenshot_from`, and `screenshot_orbit` return the image *inside the tool result*. Claude sees it directly; there is no file path to read back.
- **Permission-free reads** — read-only tools carry the `[McpTool.ReadOnly]` hint, so clients can run them without permission prompts.
- **Live tool discovery** — agents find tools with `search_tools` and browse the 28 described toolsets with `list_toolsets` / `describe_toolset`, instead of scrolling a 200-tool flat list.
- **Real error semantics** — failures are actual tool errors (thrown, readable), not `{ error }` payloads buried inside successful responses.
- **New wave-1 tools** — `find_broken_references` (scene health scan), `batch_set_property` with `dryRun: true` validation (bulk edits across many objects), and `describe_project` (one-call project orientation).
- **No Node.js on the main path** — the editor hosts the server; Node is only needed for the optional lifeline.

Upgrading from v1.x? **[docs/V2-MIGRATION.md](docs/V2-MIGRATION.md)** is the migration guide — tool names are unchanged, and the six removals are 1:1 built-in replacements.

---

## How it works

```
┌──────────────┐  streamable HTTP   ┌─────────────────────┐  ToolRegistry  ┌───────────────────────┐
│  Claude Code │ ◄────────────────► │  native MCP server  │ ─────────────► │  [McpTool] wrappers   │
│              │  127.0.0.1:7269    │ (inside the editor) │   discovery    │ (claudebridge addon)  │
└──────────────┘                    └─────────────────────┘                └───────────┬───────────┘
                                                                                       ▼
                                                                          bridge handler dispatch
                                                                          (C#, main editor thread)
```

| Piece | What it is | Where it lives |
|---|---|---|
| **Native MCP server** | Facepunch's editor-hosted MCP server — streamable HTTP, loopback-only, on by default (**Editor → Preferences → MCP Server**, port 7269) | ships with s&box |
| **Editor addon** | The bridge: a C# editor library whose 273 tools are `[McpTool]` methods the engine auto-discovers, each dispatching into the bridge's handler layer on the main editor thread | installed via the s&box **Asset Library / Library Manager** (`sboxskinsgg.claudebridge`) into your **project's `Libraries/` folder** |
| **Lifeline server** *(optional)* | A slim stdio server for editor-down diagnostics — the native server dies with the editor; the lifeline doesn't | npm package `sbox-mcp-server@2`, run with `--lifeline` |

**Invocation pattern.** The native server exposes a handful of entry points — `search_tools`, `call_tool`, `call_tools`, `list_toolsets`, `describe_toolset` — and discovers everything else live from the addon:

```
search_tools "flicker light"       → finds add_flicker_light (bridge_scaffold_polish)
call_tool  {name: "add_flicker_light", arguments: {lightId: "..."}}
call_tools [...]                   → several calls, one round trip
```

Hotload = live re-registration: new `[McpTool]` methods appear in `search_tools` within seconds of a clean compile.

> **Legacy fallback (deprecated in v2.1.0).** The v1.x file-IPC transport and full stdio TS server remain compiled-in for compatibility with older engine builds, but they are not registered by default and may be removed in a later release. Legacy-transport issues are covered by the root [TROUBLESHOOTING.md](TROUBLESHOOTING.md); native-v2 issues live in **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)**.

---

## Install

Three steps. Node.js is **not** required (only for the optional lifeline).

> **Using Codex instead of Claude Code?** Install the first-party **[s&box Codex Bridge](plugins/sbox-codex-bridge/)** from this repository. It packages the same bridge, MCP servers, API brain, cookbook, and workflows for Codex:
>
> ```bash
> codex plugin marketplace add LouSputthole/Sbox-Claude --ref main
> codex plugin add sbox-codex-bridge@sboxskins
> ```
>
> Start a new Codex session after installation. The plugin README covers prerequisites, updates, uninstalling, and editor-addon setup.

1. **Install the editor addon** — get the **`claudebridge`** library from the s&box **Asset Library** (or **Editor → Library Manager**, search **`sboxskinsgg.claudebridge`**) and install it *into your project*. It lands in `<your-project>/Libraries/` — the one correct location; the global `addons/` folder silently refuses to compile custom C#.
2. **Connect Claude Code** to the editor's native MCP server (on by default — **Editor → Preferences → MCP Server**):
   ```bash
   claude mcp add --transport http sbox http://127.0.0.1:7269/mcp
   ```
3. **(Optional, recommended) Add the lifeline** — the editor-down diagnostics server. The native server dies with the editor; the lifeline answers "why did the editor crash" when nothing else can:
   ```bash
   claude mcp add sbox-lifeline -- npx -y sbox-mcp-server@2.2.0 --lifeline
   ```

**Prefer the plugin?** `/plugin marketplace add LouSputthole/Sbox-Claude` then `/plugin install sbox-claude` — from v2.0.0 the plugin's `.mcp.json` wires **both** servers (native + lifeline) for you and ships the workflow skills. You still install the editor addon (step 1).

> See **[INSTALL.md](INSTALL.md)** for the step-by-step guide and **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** for the known failure modes (port 7269 not answering, tools missing from `search_tools`, and friends).

---

## Tools & features

**273 native tools across 28 `bridge_*` toolsets**, plus the **7 lifeline tools**. The full generated inventory — every tool, its toolset, and its read-only status — is **[docs/TOOLSETS.md](docs/TOOLSETS.md)**. Upgraders: **[docs/V2-MIGRATION.md](docs/V2-MIGRATION.md)**.

| Toolset | What it covers |
|---|---|
| `bridge_asset` | Asset library search, package install, metadata, dependency-closure copies, recompiles |
| `bridge_audio` | `.sound` events, sound components, editor previews, TTS voices (`Sandbox.Speech.Synthesizer`, viseme data exposed) |
| `bridge_batch` | Bulk operations across many GameObjects — `batch_set_property` with `dryRun` validation |
| `bridge_character` | Spawn/dress/pose citizens, animations, animgraph params, ragdolls, lipsync, attachments |
| `bridge_component` | Add/configure/inspect components, properties, cross-component refs, invoke methods and `[Button]`s |
| `bridge_debug` | Bridge health, `restart_editor`, console commands, profiler stats, time scale, debug draw |
| `bridge_discovery` | Live API reflection — `describe_type`, `search_types`, method signatures, `list_libraries`, project grep |
| `bridge_gameobject` | GameObject lifecycle, hierarchy, transforms, tags, selection, bulk layout |
| `bridge_material` | Models, `.vmat` authoring, material assignment and properties |
| `bridge_moviemaker` | `Sandbox.MovieMaker` playback *and recording* — `.movie` clips, `MoviePlayer` wiring, record live gameplay to a replayable clip (`record_gameplay_clip`) |
| `bridge_navigation` | Navmesh baking + walkable-path queries |
| `bridge_networking` | Multiplayer setup + codegen — `[Sync]`, RPCs (broadcast/host/targeted), ownership, host migration, bounded local multi-instance tests |
| `bridge_npc` | NPC brains, spawners, patrol routes, perception simulation, utility AI, daily schedule brains |
| `bridge_physics` | Rigidbodies, colliders, joints, raycasts, overlap queries |
| `bridge_playmode` | Enter/exit play mode, play state, runtime property read/write |
| `bridge_playtest` | Scripted gameplay verification with in-frame assertions (`playtest`), `drive_player`, `simulate_input` |
| `bridge_prefab` | Create/instantiate/inspect prefabs, wire prefab refs |
| `bridge_project` | Project info/config, file read/write, C# script authoring, hotload, input actions — incl. `describe_project` |
| `bridge_scaffold_gameplay` | Compile-verified gameplay codegen: controllers, health, inventory, saves (signed + meta-progression), economy (wallets, ledgers, idle), loot (incl. data-asset tables), achievements & leaderboard stats, Elo, rounds & map votes, needs systems, event buses, interaction… |
| `bridge_scaffold_polish` | Game feel: camera shake, flicker lights, combat text, combo meters, nametags, cutscenes, dialogue, round-timer HUDs |
| `bridge_scene` | Create and load `.scene` files |
| `bridge_screenshot` | Inline-PNG screenshots: main camera, framed object, free camera, multi-angle orbit |
| `bridge_ui` | Razor UI panels — screen-space HUDs, world panels, `PanelComponent` scaffolds, `add_panel_buildhash` (patch a BuildHash override into an existing panel) |
| `bridge_validation` | Lints and health checks: networking footguns, whitelist scans, Razor footguns, scene validation, `find_broken_references` |
| `bridge_visuals` | Lighting, fog, post-FX, skyboxes, envmap probes, `.vpcf` particles, atmosphere presets, render-target cameras (CCTV/mirrors), day-night sun arcs |
| `bridge_world` | Terrain sculpting, forests, caves, trails, path-based placement, swimmable water volumes |

**Six tools moved to the native built-ins.** `spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, and `remove_component` were dropped from the bridge surface because the native server ships built-ins with the **same names and semantics** — your workflows keep the same names; Facepunch's implementations serve them. The native server also gives you `spawn_models` (batch), `scene_tree`, `find_game_objects`, `get_game_object`/`set_game_object`, `add_component`/`set_component`, the `asset_*` family, `play_start`/`play_stop`, `read_console`, and `compile_status` for free.

**The 7 lifeline tools** (stdio, editor-down diagnostics): `read_log`, `get_compile_errors`, `search_docs`, `get_doc_page`, `list_doc_categories`, `run_self_test`, `get_bridge_status`. They read the log file / fetch docs directly, so they **keep working even when the editor has crashed or stalled**.

### Conventions

- Vectors/rotations as comma strings: `"x,y,z"`, `"pitch,yaw,roll"` (JSON objects still accepted).
- Objects and components are referenced by **GUID** (from `get_scene_hierarchy` / `find_objects`).
- **Scene-mutating tools refuse during play mode** — stop play first (`play_stop`). Auto-undo for bridge mutations is not available: the engine's public snapshot APIs are inert on current builds (engine-watch item).
- `describe_type` / live reflection is always the source of truth for s&box APIs.

---

## Integrations

- **Claude Code plugin** — bundles both MCP server entries, the `sbox-build-feature` workflow skill, the `sbox-api` schema-grounded API skill, the `sbox-cookbook` recipe router, the `sbox-scaffold-game` starter-scene skill, the `sbox-setup` onboarding wizard, and the `sbox-game-dev` specialist agent. (See the next section.)
- **Codex plugin** — packages the same MCP entries and knowledge/workflow bundle as the first-party **[s&box Codex Bridge](plugins/sbox-codex-bridge/)** (`sbox-codex-bridge`). It is generated from the Claude plugin by `scripts/gen-codex-plugin.mjs` to enforce parity.
- **The s&box engine** — the native MCP server hosts the transport; the claudebridge addon's `[McpTool]` methods run everything on the main editor thread.
- **Installed-library detection & leverage** — `list_libraries` reads your project's `Libraries/` and each `.sbproj`, so Claude can discover and *drive what you already have* rather than reinventing it. If you have the **Shrimple Character Controller** (`fish.scc`) or **`facepunch.playercontroller`**, it can wire up player movement via `add_component_with_properties` instead of writing a controller from scratch. The `sbox-setup` wizard surfaces this on first connect.
- **s&box Cloud assets** — reference cloud models/textures/sounds (note: Cloud-only assets are ephemeral across restarts — prefer local files for anything permanent).
- **Facepunch docs** — `search_docs` (lifeline) queries the official `Facepunch/sbox-docs` repo so API guidance comes from the source, not stale memory.

---

## The Claude plugin

**`sbox-claude`** is the recommended way to use the bridge from Claude Code. It bundles:

| Piece | What it is |
|---|---|
| **MCP server config** | `.mcp.json` registers **both servers** from v2.0.0 — `sbox` (the native HTTP endpoint at `http://127.0.0.1:7269/mcp`) and `sbox-lifeline` (`npx -y sbox-mcp-server@2.2.0 --lifeline`) — no manual registration, no version drift |
| **Skill: `sbox-build-feature`** | The screenshot-driven build workflow: confirm the bridge is alive → brainstorm non-trivial features → research the API with `describe_type` → bite-sized edits → hotload + scan the log → **screenshot and read the inline PNG**. Plus a table of s&box gotchas (Cloud assets aren't persistent; Citizen bone names are case-sensitive; `CitizenAnimationHelper.IkRightHand` drives IK at runtime; `Color` properties want `"r, g, b, a"` strings; etc.) |
| **Skill: `sbox-api`** | Schema-grounded s&box API knowledge — the Unity→s&box translation table, the Ten Rules, and curated component/UI/networking/physics references, so Claude stops hallucinating Unity patterns |
| **Skill: `sbox-cookbook`** | A master **router** indexing code-grounded recipes mined from **51 public-source s&box games** plus the modern engine repos: engine references (networking-authority, architecture, player-controller, ui-razor, and more), systems (inventory, economy, saves, progression, gacha, leaderboards, building, crafting, dialogue, rounds, waves, anti-cheat…), and genre recipes (tycoon, shopkeeper, survival-horror, deathmatch, platformer, card-battler, social-hub…). Ask "how do I build a tycoon / an inventory / a save system?" and it routes you to a grounded how-to |
| **Skill: `sbox-scaffold-game`** | Turns one ask into a playable starter scene (first-person preset) by orchestrating the scaffold tools |
| **Skill: `sbox-setup`** | A warm ~30-second onboarding wizard. It greets you on first connect, verifies the bridge, **detects your installed libraries** (`list_libraries`), recommends a concrete first move, and points you to help + feedback |
| **Agent: `sbox-game-dev`** | A specialist sub-agent for self-contained game-dev tasks; it runs `sbox-build-feature` as its default workflow |

**Day to day:** install the plugin → install the addon → open s&box → start a Claude Code session and it greets you and detects your setup → just ask it to build things. Run **`/sbox-setup`** anytime to re-orient, or invoke **`/sbox-claude:sbox-build-feature`** to force the disciplined workflow. See [`plugins/sbox-claude/README.md`](plugins/sbox-claude/README.md) for the full plugin docs.

---

## Quickstart — your first 5 minutes

Once the addon is installed and s&box is open with your project:

1. **Confirm the connection.** Ask: *"Check the bridge status."* — you want the bridge toolsets visible and a healthy handler count. (Port not answering? See [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).)
2. **Get oriented.** Run **`/sbox-setup`** — it detects your libraries and suggests a first move. Or ask *"describe the project"* (`describe_project` gives one-call orientation).
3. **Spawn something.** *"Add a cube at 0, 0, 100 and put a box model on it."* or *"Spawn a Citizen and have it idle."*
4. **See it.** *"Screenshot it from the front."* Claude uses `screenshot_from` to aim the camera at what you just made — and the PNG arrives **inline in the tool result**, so it sees the image immediately and tells you what needs fixing. That loop — build → aim → screenshot → look → adjust — is the whole game.

Then ask for the real thing: *"Create a first-person player controller with WASD and mouse look,"* or *"Set a horror-night mood with fog and a flickering light."*

---

## Troubleshooting & feedback

- **Stuck?** Start with **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** — port 7269 not answering, stale HTTP.sys registrations, bridge tools missing from `search_tools`, external addon edits not recompiling, play-mode refusals, modal-dialog stalls. For engine limitations you work *around* rather than fix, see **[docs/BRIDGE_GOTCHAS.md](docs/BRIDGE_GOTCHAS.md)**. Claude can also read its own errors even when the editor is dead: the lifeline's `read_log` / `get_compile_errors`.
- **Bugs & feature requests:** **[github.com/LouSputthole/Sbox-Claude/issues](https://github.com/LouSputthole/Sbox-Claude/issues)**.
- **Deeper docs:** [docs/RELAUNCH.md](docs/RELAUNCH.md) (the v2 relaunch hub), [docs/AGENT-GUIDE.md](docs/AGENT-GUIDE.md) (the agent working loop), [docs/ECOSYSTEM.md](docs/ECOSYSTEM.md) (toolset tour), [docs/FAQ.md](docs/FAQ.md), [CLAUDE.md](CLAUDE.md) (architecture, verified APIs, lessons learned), [CHANGELOG.md](CHANGELOG.md) (full feature history), and [docs/ADDING-A-TOOL.md](docs/ADDING-A-TOOL.md) (a new tool = one static method + XML docs).

### Known issues (short list)
- **Particles:** runtime `ParticleEffect` tools don't render through the bridge — use `spawn_vpcf`. No flame `.vpcf` currently ships in a bridge-loadable form (under investigation).
- **`take_screenshot`** captures the Main Camera (the player view in play mode) — use `screenshot_from` / `capture_view` to frame a target, or `screenshot_orbit` for several angles in one call.
- **`is_playing.sessionPlaying`** can read stale after a restart — trust the `gameFlag` field.
- **`execute_csharp`** is experimental (hotload latency; briefly recompiles the editor assembly).
- **No auto-undo** for bridge mutations — the engine's public snapshot APIs are inert on current builds. Save early, save often (`save_scene`).

---

## License

**Source-available (no independent redistribution; official channels authorized)** — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

You can freely use the Claude or Codex Bridge to build your s&box games — free or commercial — and modify it locally for your own use. Except for GitHub's built-in public-repository rights described in the LICENSE, you may **not** independently redistribute, mirror, repackage, or re-host the bridge or its source, publish a derivative of it, or offer it to others as a service. The code you create *with* the bridge (your games and their source) is yours and is not restricted.

> **Branding & trademark.** The **"s&box Claude Bridge"** / **"sboxskins.gg"** name, logos, and branding are trademarks of sboxskins.gg and are *not* licensed for reuse. See [NOTICE](NOTICE).

> **Prior versions.** Releases before this license took effect were AGPL-3.0-or-later and remain available under that license; this and all later versions are governed by the source-available license above.

Built by **[sboxskins.gg](https://sboxskins.gg)** — the s&box community marketplace.

Copyright © 2026 [sboxskins.gg](https://sboxskins.gg)
