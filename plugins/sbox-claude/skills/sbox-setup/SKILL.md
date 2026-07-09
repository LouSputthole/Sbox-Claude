---
name: sbox-setup
description: First-run onboarding for the s&box Claude Bridge. Run when a user first connects the bridge or asks how to get started — it verifies the connection, detects their installed libraries, recommends what to build with, and points them to help + feedback. Keep it warm and brief.
---

# s&box Bridge — Setup & Welcome

A short, friendly orientation for someone who just connected the bridge. A few beats, not an interrogation — and adapt to what the user says. If they already know what they want, skip the tour and just build.

## When to run
- It's clearly the user's first session with the bridge, or they just connected it.
- They ask "how do I start?", "what can this do?", or run `/sbox-setup`.

## The beats

**1. Welcome**
> 👋 Thanks for using the s&box Claude Bridge — let's get you oriented in about 30 seconds.

**2. Confirm the native server is live**
The editor ships its own MCP server, on by default at `http://127.0.0.1:7269/mcp`. Quickest check: call `mcp__sbox__search_tools` (or `list_toolsets`) — if it answers, you're connected. (A raw `initialize`/`list_toolsets` probe against the endpoint proves the same thing.) Not registered with Claude Code yet? One line:

```
claude mcp add --transport http sbox http://127.0.0.1:7269/mcp
```

Recommended second entry — the **lifeline** keeps `read_log` / `get_compile_errors` / docs search working when the editor is down (the native server dies with the editor):

```
claude mcp add sbox-lifeline -- npx -y sbox-mcp-server@2 --lifeline
```

If the endpoint doesn't answer, stop and help fix it first:
- Check **Editor → Preferences → MCP Server** — it should be on (it's the default), port 7269.
- Editor log says `[MCP] Couldn't start MCP server on port 7269`? A stale HTTP.sys registration from a dying editor instance is holding the port — restart the editor once the stale process exits.
- Make sure s&box is running with the `claudebridge` library installed — that's what puts the bridge tools on the server.

**3. Detect their libraries**
Call `list_libraries` (via `call_tool`) and summarize in plain language. Call out the useful ones:
- A character controller — `fish.scc` (Shrimple) or `facepunch.playercontroller` → "I can wire up player movement with this, no code from scratch."
- World/build tools — splines, roads, interiors, tree/asset browsers → mention they're on hand.
- `claudebridge` — that's me, the bridge itself.

**4. Recommend a first move**
Based on what's installed and whether the scene is empty (peek with `get_scene_hierarchy` if useful), offer 2–3 concrete starts, e.g.:
- "Spawn a controllable player" — using an installed controller if there is one.
- "Block out a test scene — ground, a light or two, a few props."
- "Set the mood — `apply_atmosphere`, fog, a skybox."

**5. Help + feedback**
- **Troubleshooting:** I can read my own errors (`read_log`, `get_compile_errors` — they live on the lifeline server, so they work even when the editor is down), and there's a full `TROUBLESHOOTING.md`. Just ask me here anytime — that's what I'm for.
- **Bugs / feedback:** GitHub issues — https://github.com/LouSputthole/Sbox-Claude/issues
- Built by **sboxskins.gg**.

**6. Hand off**
> What do you want to build first?

## Notes
- Use `capture_view` (or its alias `screenshot_from`) to *show* results framed on what you changed — `take_screenshot` is the main camera's view (the player's view in play mode), so it may not be aimed at it. Screenshots come back **inline in the tool result** — just look at the returned image.
- This is a guide, not a script. Read the room: a returning power user doesn't need the welcome.
