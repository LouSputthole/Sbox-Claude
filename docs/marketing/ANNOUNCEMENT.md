# v2.0.0 "Native" — launch announcement (channel-ready variants)

Ground-truth numbers: **232 native tools · 28 toolsets · 53 read-only · 7 lifeline tools · source-available (no redistribution) · built by sboxskins.gg.**
Connect: `claude mcp add --transport http sbox http://127.0.0.1:7269/mcp`
Plugin: `/plugin marketplace add LouSputthole/Sbox-Claude` → `/plugin install sbox-claude`
Docs: https://sboxskins.gg/claudebridge · GitHub: https://github.com/LouSputthole/Sbox-Claude

---

## (a) Long form — website / GitHub release (~440 words)

### s&box Claude Bridge v2.0.0 "Native" — the editor is now an AI-agent workspace

The s&box editor ships a native MCP server now — on by default, loopback-only, port 7269. So the Claude Bridge stops being a stack of file-IPC scripts and becomes what it was always meant to be: a structured automation platform living *inside* the editor. v2.0.0 moves the bridge's full tool surface onto that native transport — **232 tools across 28 described toolsets** (53 of them read-only), streamed over HTTP instead of polled off disk.

What that buys you, concretely:

- **Claude sees what it builds.** `take_screenshot`, `capture_view`, `screenshot_from`, and `screenshot_orbit` return the PNG *inline* in the tool result. No temp-file path to read back — the image is right there, so the build → screenshot → fix loop actually closes.
- **Agents discover tools live.** `search_tools` finds the right one; `list_toolsets` / `describe_toolset` browse the 28 groups; `call_tool` / `call_tools` run them (batch several in one round trip). No 200-tool flat list to scroll.
- **Checkpoint the scene, then take risks.** `checkpoint_scene` snapshots every root object to temp storage; `restore_checkpoint` rolls it all back. Batch-retint 40 props with a dry-run first, hate the result, roll back — this is the agent-side undo (live-verified resurrecting a 317-root scene).
- **Real prefabs, real batch ops.** `create_prefab` writes full serialization and `instantiate_prefab` truly rebuilds the tree with GUID remap. `batch_set_property` / `batch_delete` / `batch_add_component` all validate with `dryRun: true` before they touch anything.
- **Drive a car, run a playtest.** `create_vehicle_controller` turns any rigidbody prop into a raycast car with a built-in driver seat; `tune_vehicle` applies arcade / drift / offroad / race presets. The playtest harness runs a scripted loop in play mode and asserts the result *in-frame*.
- **No Node.js on the main path.** The editor hosts the server. Node is only needed for the optional lifeline — a slim diagnostics server that keeps answering logs and compile errors when the editor crashes and takes the native server down with it.

Tool names are unchanged from v1.x; upgrading is one command plus an addon update. Six tools whose names collided with native built-ins (`spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component`) now defer to Facepunch's identical implementations — your workflows keep the same names.

Connect: `claude mcp add --transport http sbox http://127.0.0.1:7269/mcp`
Or install the Claude Code plugin (wires both servers plus the cookbook brain, specialist agent, and screenshot workflow): `/plugin marketplace add LouSputthole/Sbox-Claude` → `/plugin install sbox-claude`

Source-available (no redistribution). Built by sboxskins.gg. Full docs: **sboxskins.gg/claudebridge**

---

## (b) Discord announcement (~150 words, emoji-light)

**s&box Claude Bridge v2.0.0 "Native" is live** 🚀

The s&box editor now ships a built-in MCP server, so the bridge runs natively *inside* the editor — no more file polling, no Node.js on the main path. The full surface is **232 tools across 28 toolsets**.

What's new:
- Inline screenshots — Claude sees what it builds, right in the tool result
- Scene checkpoints — snapshot before risky edits, roll back if you hate it
- Real prefabs + dry-run batch edits (preview before you commit)
- Drivable vehicles (arcade / drift / offroad / race presets) and a play-mode playtest harness
- A lifeline server that still answers logs + compile errors when the editor crashes

Connect in one command:
`claude mcp add --transport http sbox http://127.0.0.1:7269/mcp`

Or grab the plugin (brain + agent + workflow):
`/plugin marketplace add LouSputthole/Sbox-Claude`

Tool names are unchanged from v1.x. Docs → sboxskins.gg/claudebridge

---

## (c) Twitter / X

### Single post (<280 chars)

s&box Claude Bridge v2.0.0 "Native" is out. The editor's built-in MCP server now hosts 232 tools — inline screenshots, scene checkpoints + rollback, real prefabs, dry-run batch edits, and drivable cars. No Node.js on the main path.

sboxskins.gg/claudebridge

### 3-tweet thread

**1/** s&box Claude Bridge v2.0.0 "Native" is live. The s&box editor now ships its own MCP server, so the bridge runs natively inside the editor — 232 tools over streamable HTTP, no Node.js on the main path. 🧵

**2/** What that unlocks: Claude sees its screenshots inline, checkpoints the scene before risky edits and rolls back, builds real prefabs, and validates batch edits with a dry-run first. Plus drivable cars and a play-mode playtest harness that asserts in-frame.

**3/** Connect in one command:
`claude mcp add --transport http sbox http://127.0.0.1:7269/mcp`
Or grab the plugin for the cookbook brain + specialist agent. Source-available.
Docs → sboxskins.gg/claudebridge

---

## (d) s&box community forum post (developer-to-developer)

**Claude Bridge v2.0.0 — now running on the editor's native MCP server**

Since the editor started shipping a built-in MCP server (on by default, Editor → Preferences → MCP Server, loopback port 7269), it made no sense to keep polling files off disk. So v2 moves the whole bridge onto it.

The bridge's tools are now `[McpTool]` static methods the engine's ToolRegistry auto-discovers — 232 of them across 28 described toolsets. Hotload = live re-registration, so a new tool shows up in `search_tools` seconds after a clean compile. Screenshots come back as inline image blocks instead of temp-file paths, read-only tools carry `[McpTool.ReadOnly]` so clients can skip the permission prompt, and errors are real thrown tool errors instead of `{error}` payloads hidden inside a success.

A few things worth calling out for anyone else building on the editor MCP server:

- Scene-mutating tools still refuse during play mode.
- There's no engine auto-undo reachable from an addon (the public snapshot APIs are inert on current builds), so the bridge ships its own: `checkpoint_scene` / `restore_checkpoint` snapshot and restore every root object. That's the pattern I'd recommend until a public per-edit undo hook lands.
- Six tools (`spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component`) collided with native built-ins and now defer to Facepunch's implementations — same names, same semantics.

Connect: `claude mcp add --transport http sbox http://127.0.0.1:7269/mcp`
The old stdio server survives as `--lifeline` for editor-down diagnostics (logs, compile errors, docs) when the editor takes the native server down with it.

Source-available (no redistribution). Feedback and issues welcome: github.com/LouSputthole/Sbox-Claude
