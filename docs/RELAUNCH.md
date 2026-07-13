# The v2.0.0 "Native" Relaunch

> The s&box Claude Bridge goes native. The full toolset now lives on **s&box's own
> editor MCP server** — no file IPC, no Node on the main path, inline screenshots,
> live tool discovery, and honest errors. A serious toolset that helps you build
> faster inside s&box.

**This is the hub page.** From here:

- **[AGENT-GUIDE.md](AGENT-GUIDE.md)** — how an AI agent should actually work the platform (the discover → inspect → checkpoint → modify → validate → test loop, with worked examples).
- **[ECOSYSTEM.md](ECOSYSTEM.md)** — plain-English tour of all 28 toolsets + the lifeline, with example prompts.
- **[FAQ.md](FAQ.md)** — do my old workflows still work, what is MCP, can an agent modify my project, what's coming next.
- **[TOOLSETS.md](TOOLSETS.md)** — the generated, authoritative inventory (every tool, its toolset, read-only status).
- **[V2-MIGRATION.md](V2-MIGRATION.md)** — the upgrade guide if you're coming from v1.x.
- **[../INSTALL.md](../INSTALL.md)** · **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** · **[BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md)** · **[ADDING-A-TOOL.md](ADDING-A-TOOL.md)** · **[../CHANGELOG.md](../CHANGELOG.md)**

---

## What v2.0.0 is

s&box now ships a **native MCP server inside the editor** — on by default at
`http://127.0.0.1:7269/mcp` (**Editor → Preferences → MCP Server**), loopback-only,
streamable HTTP. v2.0.0 moves the bridge onto it.

The bridge's tools are no longer a Node process shuttling JSON files through a temp
directory. They are **`[McpTool]` methods the engine discovers itself** — grouped into
described `bridge_*` toolsets, with XML docs as the schema, `[McpTool.ReadOnly]` hints,
and hotload = live re-registration. You connect Claude Code once and everything the
addon exposes shows up through the server's search.

**The current surface** (v2.0.0 "Native" plus the v2.1.0 Tier-2 + gameplay-recording + cinematic waves — see the CHANGELOG's `[2.1.0]`):

| | Count |
|---|---|
| Tools on the native surface | **262** |
| Toolsets (`bridge_*`) | **28** |
| Read-only tools (no permission prompt) | **53** |
| Lifeline tools (editor-down diagnostics) | **7** |
| Handed to native built-ins (same names) | **6** |
| **Total tools** | **275** (262 native + 7 lifeline + 6 built-in-served) / **267 handlers** |

The generated **[TOOLSETS.md](TOOLSETS.md)** is the authoritative inventory — read it,
don't memorize it. Agents browse it live with `list_toolsets` / `describe_toolset` and
find individual tools with `search_tools`.

---

## Why the rebuild

Four things got better. They're the whole reason v2 exists.

### 1. Transport

v1 talked to the editor by **writing `req_*.json` / `res_*.json` files into a temp
directory and polling every 50 ms** — because s&box's sandboxed game code blocks
`System.Net`, so no sockets. That constraint is gone: the native server is hosted by the
**editor process itself**, outside the sandbox. So v2 is **streamable HTTP** — no polling
latency, no BOM bugs, no atomic-rename dance, and no two-sides-resolved-different-temp-dirs
class of failure. **Node.js is no longer required** on the main path (only for the optional
lifeline).

### 2. Discovery

v1 handed the client a **flat list of ~200 tools**. v2 hands it a handful of entry points
(`search_tools`, `call_tool`, `call_tools`, `list_toolsets`, `describe_toolset`) and lets
the agent **find what it needs live**:

```
search_tools "flicker light"   → add_flicker_light  (bridge_scaffold_polish)
call_tool  {name: "add_flicker_light", arguments: {lightId: "..."}}
```

28 clean, described toolsets instead of a wall of names. Hotload re-registers new tools
within seconds of a clean compile.

### 3. Honesty

A tool description is a promise. v2 shipped a **description sweep**: 156 quality-gate
warnings driven to **zero**, and **~20 dishonest descriptions corrected** — tools that
claimed to do things their handler didn't (e.g. `focus_object` never actually moved the
camera; several scaffold params were silently ignored). Every one is now flagged
truthfully. And errors are **real thrown tool errors** now — the handler's own message,
readable — not `{ error: "..." }` payloads buried inside a "successful" response.

### 4. Safety

Working *with* an AI agent inside a live editor needs guardrails, and v2 has a real set:

- **Read-only hints.** 53 tools carry `[McpTool.ReadOnly]` — the client can run them
  without a permission prompt, and they promise never to change your project, scene, or
  editor state.
- **Play-mode guards.** Scene-mutating tools refuse while the game is running, with a
  clear error, so an edit can't get lost against a live runtime scene.
- **Dry-run discipline.** The batch tools take `dryRun: true` — they report exactly what
  *would* change, on every target, without touching anything.
- **Scene checkpoints.** `checkpoint_scene` snapshots the whole scene before risky work;
  `restore_checkpoint` rolls it back. (More on this below — it's the honest answer to a
  real engine limitation.)

---

## What creators can now do

Concrete wins that didn't exist, or didn't work, before v2.

- **Orient in two calls.** `describe_project` gives one-call project orientation (identity,
  open scene, file lists, code footprint, custom components, installed libraries).
  `describe_scene` gives one-call scene orientation (component histogram, cameras, lights,
  tags, content bounds). Two reads and the agent knows where it is.
- **Checkpoint before risky edits.** `checkpoint_scene` → do the scary batch operation →
  `restore_checkpoint` if it went wrong. Live-verified resurrecting a 317-root scene from
  a snapshot. The scene file on disk stays untouched until you `save_scene`.
- **Real prefabs.** `create_prefab` now writes a **full engine serialization** — every
  component with its property values, all children, the same JSON the editor writes — and
  `instantiate_prefab` genuinely recreates the tree (engine `Clone` for registered prefabs,
  guid-remapped deserialize for fresh files, so repeat instantiations never collide). The
  old handler wrote a minimal descriptor that dropped both.
- **Batch with a safety net.** `batch_set_property`, `batch_add_component`, `batch_delete`,
  `batch_reparent` — one property or op across many objects in one call, each with
  `dryRun: true` to validate first. Set the tint on 40 props, add a collider to every crate,
  regroup a level section — and see the plan before you commit.
- **Drive a car.** `create_vehicle_controller` makes any Rigidbody prop drivable: 4-corner
  raycast suspension, a built-in hidden-driver seat (press E), a chase camera, and
  angular-velocity steering. `tune_vehicle` applies arcade / drift / offroad / race presets;
  `create_seat_system` and `create_physics_grab_tool` round out the physics-interaction kit.

Plus the whole existing craft layer — scaffolds for gameplay systems, networking
primitives, cinematics, UI, terrain and forests, the playtest harness — is unchanged and
carries every name it had. See **[ECOSYSTEM.md](ECOSYSTEM.md)** for the full tour.

---

## How AI-assisted s&box development works on it

The bridge doesn't build your game *for* you in one shot; it closes the **build → look →
adjust** loop tightly enough that an agent can iterate the way a developer does. The
recommended loop (fully worked in **[AGENT-GUIDE.md](AGENT-GUIDE.md)**):

1. **Inspect** — `describe_project` / `describe_scene`, then `get_scene_hierarchy` /
   `find_objects` for the specifics.
2. **Plan** — decide the edits; research unfamiliar s&box types with `describe_type`
   (reflection is the source of truth, not training data).
3. **Checkpoint** — `checkpoint_scene` before anything risky or batch-shaped.
4. **Modify** — create/edit scripts, hotload, wire components, compose the scene.
5. **Validate** — `find_broken_references`, `scene_validate`, the lints
   (`networking_lint` / `razor_lint` / `sandbox_lint`), `compile_status`.
6. **Test** — the `playtest` harness for scripted in-frame assertions, and screenshots
   (`take_screenshot` / `capture_view` / `screenshot_orbit`) that arrive **as inline PNGs
   the agent looks at directly** — no file to read back.
7. **Summarize** — report what changed and what still needs a human.

The screenshot loop is the heart of it. Because the agent *sees* what it built, it stops
guessing about visual outcomes and starts correcting them.

---

## Honest limitations (these stay honest)

- **No auto-undo for bridge mutations.** The engine's public snapshot APIs
  (`FullUndoSnapshot`, `UndoSystem.Snapshot`) are verified **inert** on current builds —
  the built-in tools' undo uses an internal mechanism addons can't reach. This is an
  engine-watch item. **The answer is scene checkpoints** (`checkpoint_scene` /
  `restore_checkpoint`), plus the habit of saving early (`save_scene`).
- **The bridge can't fully "play" your game.** It can author, wire, and script-verify
  mechanics (the `playtest` harness asserts that controls *fire* in-frame), but there is no
  analog input synthesis and no substitute for a human at the keyboard for **feel**. A
  drivable car *compiles and drives* under the gate; whether it **feels** right needs a
  human playtest. See [BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #1.
- **The native server dies with the editor.** When s&box crashes or hangs, nothing served
  over port 7269 can tell you why. That's what the **lifeline** is for (below).

---

## What comes next (roadmap — planned, not shipped)

Tracked in **[TOOL_BACKLOG.md](TOOL_BACKLOG.md)**. Labeled **planned** on purpose — none of
this is in v2.0.0:

- **v2.1.0 retirement of the legacy path.** The v1.x file-IPC transport and the full stdio
  TS tool layer (everything except the lifeline) are compiled-in and functional through
  v2.0.x as a fallback for older engine builds, and **retire in v2.1.0** once the native
  path proves out across the Asset Library user base.
- **Remaining Tier-2 scaffolds** — ✅ **largely done**: the v2.1.0 waves
  (2026-07-12/13) complete the Tier-2 backlog by theme — economy & saves, stats &
  achievements, round-flow & UI, world & render, AI & systems, plus the cinematic wave
  (lipsync dialogue, camera effects, clip authoring, recorded playtests, killcams) — with
  the few leftovers deliberately skipped for verified reasons (see TOOL_BACKLOG.md).
- **Multiplayer test harness** — spawn N local clients, drive each via `playtest`, assert
  sync. **Likely unblocked** (docs-audit finding 2026-07-13): the official
  testing-multiplayer docs now describe "Join via new instance" plus `connect local` /
  `reconnect` console commands, so the engine's local test path appears to have shipped —
  one live verification session away; the top next-wave candidate (see TOOL_BACKLOG.md).
- **Typed DTO returns** for hot read tools (`get_scene_hierarchy`, `find_objects`,
  `get_bridge_status`) → `outputSchema` + `structuredContent`, so agents plan around
  fields instead of parsing text.
- **`MovieRecorder`** — ✅ **shipped in v2.1.0**: the engine's
  `MovieRecorder` reached the shipping build (2026-07-12) and the bridge covers it end to
  end — `record_gameplay_clip` / `stop_gameplay_recording` / `gameplay_recording_status`,
  E2E-proven recording live gameplay and replaying it through `play_movie`.

---

## The lifeline (forever)

The old stdio server doesn't disappear — it becomes the **lifeline**:
`npx -y sbox-mcp-server@2 --lifeline` exposes the **7 editor-down tools** (`read_log`,
`get_compile_errors`, `search_docs`, `get_doc_page`, `list_doc_categories`, `run_self_test`,
`get_bridge_status`). Because it runs **outside** the editor, it keeps working when the
native server is dead — it's how you answer "why did the editor crash." Recommended as a
second server entry alongside the native one; the plugin wires both for you.

---

*Built by [sboxskins.gg](https://sboxskins.gg). Source-available (no redistribution) —
see [LICENSE](../LICENSE) and [NOTICE](../NOTICE).*
