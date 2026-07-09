# sbox-claude — Claude Code Plugin

The complete toolkit for building s&box games by talking to Claude.

📖 **Full docs:** [sboxskins.gg/claudebridge](https://sboxskins.gg/claudebridge) — overview, setup, changelog, troubleshooting & FAQ.

This plugin bundles:

| Component | What it does |
|---|---|
| **MCP server registration** (`sbox` = the editor's NATIVE MCP server over HTTP, plus the `sbox-lifeline` stdio server for editor-down diagnostics) | 232 native tools across 28 toolsets to drive the s&box editor — GameObjects, scripts, scenes, components, physics, networking, UI, world-gen, lighting & atmosphere, characters, lipsync, scene layout, navmesh & spatial queries, particles, animation, NPC brains, playable-game scaffolds, game-feel juice (camera shake / flicker lights / floating combat text), networking & scene inspection/lint, save & services queries, scatter & object utilities, self-diagnosis, console/C# execution, live docs search, type discovery, debug-draw visualization, play-mode time-scale & profiler, and a playtest harness that runs a scripted gameplay loop and asserts the result in-frame |
| **Skill: `sbox-build-feature`** | Codifies the screenshot-driven iteration workflow — bridge check, brainstorm gate, API research, hotload verify, screenshot read. Prevents the "guess and check" loop |
| **Skill: `sbox-api`** | Schema-grounded s&box API knowledge — Unity→s&box translation table, the Ten Rules, and curated component/UI/networking/physics references. Stops Unity-pattern hallucination; repointed to verify signatures via the bridge's live `describe_type`. Adapted from [claude-sbox](https://github.com/gavogavogavo/claude-sbox) (MIT © David Ryan) |
| **Skill: `sbox-cookbook`** | A master **router** of code-grounded recipes mined from 51 current (2026) open-source s&box games + the modern engine repos -- **11 engine**, **18 system**, and **20 genre** references. Ask "how do I build a tycoon / an inventory / a save system?" and it routes to a grounded how-to |
| **Skill: `sbox-scaffold-game`** | Turns one ask into a playable starter scene (first-person preset) |
| **Skill: `sbox-setup`** | A ~30-second onboarding wizard — verifies the bridge, detects your installed libraries, recommends a first move |
| **Agent: `sbox-game-dev`** | Optional specialist for handing off self-contained game-dev tasks |

## What this plugin does NOT include

This plugin gives Claude the **MCP server side** of the bridge. To actually drive the s&box editor, you also need the **bridge addon** installed in your s&box **project's** `Libraries/` folder. From v2.0.0 the addon's tools are served by **s&box's built-in editor MCP server** (`http://127.0.0.1:7269/mcp`, on by default) — the plugin registers that endpoint plus the `sbox-lifeline` stdio server for editor-down diagnostics.

**Install the bridge addon separately** — see the [main repo's INSTALL.md](https://github.com/LouSputthole/Sbox-Claude/blob/main/INSTALL.md). The 30-second version:

```powershell
git clone https://github.com/LouSputthole/Sbox-Claude.git
cd Sbox-Claude
.\install.ps1 -RemoveStaleAddons      # Windows
./install.sh --remove-stale            # Linux/Mac/WSL
```

## Install the plugin

Once Claude Code's plugin marketplace catalogs this entry, install with:

```
/plugin marketplace add LouSputthole/Sbox-Claude
/plugin install sbox-claude
```

For local development you can also point Claude at the plugin directory directly:

```
claude --plugin-dir /path/to/Sbox-Claude/plugins/sbox-claude
```

After install, restart your Claude Code session and run `/reload-plugins` if you make local changes.

## Verify it's working

In a new Claude Code session, ask:

```
Check the bridge status.
```

Claude should call `search_tools` / `get_bridge_status` through the native server and report the `bridge_*` toolsets. `connected: true` with a healthy `handlerCount` means the addon is compiled and live.

If the endpoint doesn't answer: s&box isn't running, or the MCP server is disabled (Editor → Preferences → MCP Server).
If `search_tools` finds no `bridge_*` toolsets: the bridge addon isn't installed in your project's `Libraries/` (see above) or isn't compiling.

## Using the skill

The `sbox-build-feature` skill activates whenever Claude is about to make a non-trivial change to an s&box project. You can also invoke it explicitly:

```
/sbox-claude:sbox-build-feature
```

The skill enforces:
1. Confirm bridge alive before doing anything
2. Brainstorm complex features before coding
3. Research the s&box API via `describe_type` before guessing
4. Bite-sized edits, one file at a time
5. Hotload + log scan after every change
6. **Screenshot + read it yourself** for any visual change

Plus a list of common s&box gotchas (MathF not available, Cloud assets not persistent, Citizen bone names case-sensitive, CitizenAnimationHelper.IkRightHand works at runtime, etc.).

## Using the agent

For larger self-contained tasks, hand off to the specialist:

```
Use the sbox-game-dev agent to build a survival-stamina system with HUD bar, depletion on sprint, regen when idle, and red flash when low.
```

The agent runs the `sbox-build-feature` skill as its default workflow.

## What's bundled vs. fetched

- The main tool surface is served by the **editor itself** (native MCP server) — nothing to fetch
- The `sbox-lifeline` stdio server is fetched from npm on first use via `npx -y sbox-mcp-server@2.0.0 --lifeline` (the only part that needs Node)
- The skills and agent are bundled with the plugin
- The bridge **addon** (the s&box-side C# code) is **not bundled** — install it from the s&box Asset Library or via the install script (see above)

## Version compatibility

- This plugin is **v2.0.0**. The `sbox` entry points at the editor's native MCP server (no version to pin — it ships with the engine); the `sbox-lifeline` pin (`sbox-mcp-server@2.0.0`) is in the plugin's `.mcp.json`. Keep the bridge **addon** at a matching `2.x` (`BridgeVersion` `2.0.0`) — `get_bridge_status` reports the live addon version.
- File IPC and the full stdio server remain functional through v2.0.x as a fallback for older engine builds; they retire in v2.1.0.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `mcp__sbox__*` tools not available in Claude | Plugin not installed or session not reloaded | `/reload-plugins`, restart Claude Code |
| Port 7269 doesn't answer / tools time out | s&box not running, MCP server disabled, or a stale HTTP.sys registration holds the port | Open s&box with your project; check Editor → Preferences → MCP Server; restart the editor if the log shows `[MCP] Couldn't start MCP server on port 7269` |
| `Couldn't add project` on s&box startup | Project has both a local-dev `Libraries/claudebridge/` AND an asset-library-installed `Libraries/sboxskinsgg.claudebridge/` claiming the same compiler name | Either set the local one's `Org` to `local`, or remove the asset-library copy. See `TROUBLESHOOTING.md` |
| `get_compile_errors` / `read_log` not found on the native server | They live on the **lifeline** server (editor-down diagnostics) | Ensure the `sbox-lifeline` entry is registered (`/reload-plugins`) |
| Compile error in s&box editor that nothing in your `.cs` files explains | Hot-load cache is stuck | Touch the file and re-hotload, or restart s&box |

For deeper issues see the main repo's [TROUBLESHOOTING.md](https://github.com/LouSputthole/Sbox-Claude/blob/main/TROUBLESHOOTING.md).

## License

Source-available (no redistribution). Same as the bridge. You may use and locally modify it to build your own games, but you may not redistribute, fork, repackage, or re-host it, and the "s&box Claude Bridge" / "sboxskins.gg" name and branding may not be reused — see the repo's [LICENSE](https://github.com/LouSputthole/Sbox-Claude/blob/main/LICENSE) and [NOTICE](https://github.com/LouSputthole/Sbox-Claude/blob/main/NOTICE).

## Credits

Built by [sboxskins.gg](https://sboxskins.gg). The `sbox-api` skill is adapted from [claude-sbox](https://github.com/gavogavogavo/claude-sbox) by **David Ryan** (MIT). Bridge bootstrap-crash fix by [@FurkanZhlp](https://github.com/FurkanZhlp). Original bug reports by [@Jmcasavant](https://github.com/Jmcasavant) and [@dvd900](https://github.com/dvd900).
