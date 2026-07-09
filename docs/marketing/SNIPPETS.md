# Reusable copy blocks — s&box Claude Bridge v2.0.0 "Native"

Grounded, drop-in copy for the site, README, store listings, and social. All claims are live-verified.
Canonical numbers: **232 native tools · 28 toolsets · 53 read-only · 7 lifeline tools · source-available (no redistribution)**.

---

## One-sentence description

The s&box Claude Bridge turns the s&box editor into an AI-agent workspace — 232 native tools, served by the editor's built-in MCP server, that let Claude (or any AI) inspect, build, validate, and playtest s&box projects from conversation.

## Short paragraph

The s&box Claude Bridge is a source-available editor automation platform for s&box. Its 232 tools run natively inside the editor via s&box's built-in MCP server, so an AI agent can create GameObjects, write and hotload C#, compose scenes, wire networking and UI, and — crucially — screenshot what it built and look at it. v2.0.0 "Native" adds scene checkpoints, real prefabs, dry-run batch edits, drivable vehicles, and a play-mode playtest harness.

## Long paragraph

The s&box editor now ships a native MCP server, and the Claude Bridge runs on it — 232 tools across 28 described toolsets, streamed over local HTTP instead of polled off disk. That makes the editor a structured automation platform, not a pile of scripts: an AI agent discovers tools live (`search_tools`), builds a scene, and then *sees its own work* — screenshots come back as inline images, so the build → screenshot → fix loop actually closes. It can checkpoint the whole scene before a risky batch edit and roll back if it goes wrong, round-trip real prefabs, validate bulk edits with a dry-run before committing, make a prop drivable, and run a scripted playtest that asserts the result in-frame. No Node.js on the main path — the editor hosts the server; an optional lifeline keeps answering logs and compile errors even when the editor crashes.

## GitHub README intro line

> **Build s&box games by talking to Claude Code.** Describe what you want — Claude writes the C#, builds the scene, wires up components, screenshots its own work, and iterates until it works. 232 native tools, served by s&box's built-in editor MCP server.

---

## Website hero

**Headline:** Build s&box games by talking to Claude

**Subheadline:** Your s&box editor is now an AI-agent workspace. 232 native tools let Claude — or any AI — write the C#, build the scene, wire the components, screenshot its own work, and fix what's wrong. You describe what you want; it builds it and checks it.

**CTA (primary):** Get started → (links to plugin & setup)
**CTA (secondary / concrete):**
- Connect any MCP client: `claude mcp add --transport http sbox http://127.0.0.1:7269/mcp`
- Or the full experience: `/plugin marketplace add LouSputthole/Sbox-Claude` → `/plugin install sbox-claude`

---

## Feature-card blurbs

**Native MCP transport**
Runs on s&box's built-in editor MCP server — streamable HTTP on loopback port 7269, on by default. No 50 ms file polling, no Node.js on the main path, and hotload re-registers tools live.

**Live tool discovery**
`search_tools` finds the right tool across 28 described toolsets; `list_toolsets` / `describe_toolset` browse them; `call_tool` / `call_tools` run them and batch several into one round trip. No 200-tool flat list to scroll.

**Inline screenshots**
`take_screenshot`, `screenshot_from`, and `screenshot_orbit` return the PNG *inside* the tool result. Claude sees what it built immediately — no file path to read back — so the build → screenshot → fix loop closes.

**Scene checkpoints & rollback**
`checkpoint_scene` snapshots every root object before a risky batch; `restore_checkpoint` rolls the whole scene back. Checkpoint, batch-edit 40 props, hate it, roll back — the agent-side undo.

**Real prefabs**
`create_prefab` writes full engine serialization; `instantiate_prefab` rebuilds the entire tree with GUID remap, so repeat instantiations never collide. `get_prefab_info` returns a structured tree summary, not a raw JSON dump.

**Dry-run batch ops**
`batch_set_property`, `batch_delete`, and `batch_add_component` retint or restructure many objects in one call — each with `dryRun: true` to preview current values and catch problems before anything is applied.

**Playtest harness**
`playtest` runs a scripted step list in play mode — move, look, jump, wait, assert — and evaluates each assertion *in-frame*, so it can catch transient state like a jump's airborne frame. A gameplay loop becomes verifiable, not just a static scene.

**Drivable vehicles**
`create_vehicle_controller` turns any rigidbody prop into a raycast car with 4-corner suspension and a built-in driver seat; `tune_vehicle` applies arcade / drift / offroad / race handling presets. (Handling *feel* is worth a human tuning pass.)

---

## Roadmap blurb

**What's next (planned).** A few things are on the roadmap, clearly not yet shipped: **(planned)** finishing the remaining corpus scaffolds mined from the 51-game reference set; **(planned)** a local-loopback multiplayer test harness — spawn N clients, drive each, assert they stay in sync — which lands when s&box's loopback multi-instance socket reaches the shipping build; and **(planned)** typed, structured tool outputs. Everything described elsewhere in this doc is live and verified today; these are labeled planned on purpose.
