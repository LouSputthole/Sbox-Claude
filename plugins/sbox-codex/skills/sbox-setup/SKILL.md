---
name: sbox-setup
description: First-run onboarding for the s&box Codex Bridge. Run when a user first connects the bridge or asks how to get started — it verifies the connection, detects their installed libraries, recommends what to build with, and points them to help + feedback. Keep it warm and brief.
---

# s&box Bridge — Setup & Welcome

A short, friendly orientation for someone who just connected the bridge. A few beats, not an interrogation — and adapt to what the user says. If they already know what they want, skip the tour and just build.

## When to run
- It's clearly the user's first session with the bridge, or they just connected it.
- They ask "how do I start?", "what can this do?", or run `/sbox-setup`.

## The beats

**1. Welcome**
> 👋 Thanks for using the s&box Codex Bridge — let's get you oriented in about 30 seconds.

**2. Confirm the bridge is live**
Call `mcp__sbox__get_bridge_status`. If it's not connected, stop and help fix it first:
- IPC-dir mismatch → set `SBOX_BRIDGE_IPC_DIR` on both sides (see `TROUBLESHOOTING.md`).
- The **Claude Bridge dock must be open/visible** in s&box, or the frame loop won't run.
- Make sure s&box is running with the addon installed.

**3. Detect their libraries**
Call `mcp__sbox__list_libraries` and summarize in plain language. Call out the useful ones:
- A character controller — `fish.scc` (Shrimple) or `facepunch.playercontroller` → "I can wire up player movement with this, no code from scratch."
- World/build tools — splines, roads, interiors, tree/asset browsers → mention they're on hand.
- `claudebridge` — that's me, the bridge itself.

**4. Recommend a first move**
Based on what's installed and whether the scene is empty (peek with `get_scene_hierarchy` if useful), offer 2–3 concrete starts, e.g.:
- "Spawn a controllable player" — using an installed controller if there is one.
- "Block out a test scene — ground, a light or two, a few props."
- "Set the mood — `apply_atmosphere`, fog, a skybox."

**5. Help + feedback**
- **Troubleshooting:** I can read my own errors (`read_log`, `get_compile_errors`), and there's a full `TROUBLESHOOTING.md`. Just ask me here anytime — that's what I'm for.
- **Bugs / feedback:** GitHub issues — https://github.com/LouSputthole/Sbox-Claude/issues
- Built by **sboxskins.gg**.

**6. Hand off**
> What do you want to build first?

## Notes
- Use `screenshot_from` to *show* results — `take_screenshot` is locked to the scene's Main Camera, so it won't be aimed at what you changed.
- This is a guide, not a script. Read the room: a returning power user doesn't need the welcome.
