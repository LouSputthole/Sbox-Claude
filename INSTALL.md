# Installation Guide (v2.0.0)

v2 connects Claude Code to **s&box's native editor MCP server** — the editor hosts the transport, so there is no Node server to build and no file IPC to align. **Node.js is only needed for the optional lifeline server.**

There is still **one** correct place to install the bridge addon: inside your s&box **project's** `Libraries/` folder. s&box's global `addons/` folder is built-in only and **will not compile custom C#** — if anything tells you to install there, ignore it.

Total setup time: ~3 minutes.

## Prerequisites

- **s&box** installed via Steam, on a build that ships the native MCP server (July 2026 or later — check **Editor → Preferences → MCP Server** exists)
- **Claude Code** ([setup guide](https://docs.anthropic.com/en/docs/claude-code))
- An s&box **project** you intend to use the bridge with (create one in s&box first if you don't have one yet)
- **Node.js 18+** — *only* if you want the optional lifeline server (step 4)

---

## Step 1 — Install the editor addon (`claudebridge`)

1. Open s&box and load your project.
2. Open **Editor → Library Manager** (this is *not* the Asset Browser — the bridge is a **Library**, not a content asset, so it won't show up there).
3. Search for **`sboxskinsgg.claudebridge`** and install it *into your project*.

It lands in `<your-project>/Libraries/` — the correct location. You can also find it in the s&box **Asset Library** on [sbox.game](https://sbox.game).

## Step 2 — Confirm the native MCP server is on

Open **Editor → Preferences → MCP Server**. It is **on by default**, serving `http://127.0.0.1:7269/mcp` (loopback only).

> **Non-default port?** If you change the port in preferences, use your port in the URL in step 3.

## Step 3 — Connect Claude Code

### Option A — the Claude Code plugin (recommended)

In Claude Code:

```
/plugin marketplace add LouSputthole/Sbox-Claude
/plugin install sbox-claude
```

From v2.0.0 the plugin's `.mcp.json` wires **both servers** for you — `sbox` (the native HTTP endpoint) and `sbox-lifeline` (the editor-down diagnostics server) — plus the workflow skills (`sbox-build-feature`, `sbox-api`, `sbox-cookbook`, `sbox-scaffold-game`), the `sbox-setup` onboarding wizard, and the `sbox-game-dev` specialist agent. Skip to **Verify** below.

### Option B — manual registration

Register the native server (one-time):

```bash
claude mcp add --transport http sbox http://127.0.0.1:7269/mcp
```

## Step 4 (optional, recommended) — Add the lifeline server

The native server lives inside the editor process and **dies with it**. The lifeline is a slim stdio server that answers "why did the editor crash" when nothing else can — `read_log`, `get_compile_errors`, `search_docs`, `get_doc_page`, `list_doc_categories`, `run_self_test`, `get_bridge_status`:

```bash
claude mcp add sbox-lifeline -- npx -y sbox-mcp-server@2 --lifeline
```

This is the only step that requires Node.js. Plugin users already have it (Option A wires it).

---

## Verify it's working

1. Start (or restart) s&box and load your project.
2. In a new Claude Code session, ask:

```
"Check the bridge status."
```

You should see the `bridge_*` toolsets discoverable (Claude finds them via `search_tools`) and a healthy handler count from `get_bridge_status`. Then try:

```
"What project is open in s&box?"
```

(`describe_project` gives one-call orientation.) If both work, you're set. If anything fails, jump to **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** — the first two entries cover the port-7269 failure modes.

### Next steps

Now that you're connected:

- **[docs/RELAUNCH.md](docs/RELAUNCH.md)** — what v2.0.0 is, why the rebuild, and what you can now build.
- **[docs/ECOSYSTEM.md](docs/ECOSYSTEM.md)** — a plain-English tour of all 28 toolsets, with example prompts.
- **[docs/AGENT-GUIDE.md](docs/AGENT-GUIDE.md)** — how an agent works the platform (the inspect → checkpoint → modify → validate → test loop).
- **[docs/FAQ.md](docs/FAQ.md)** — do my old workflows still work, can an agent modify my project, what's next.

---

## Updating

- **Editor addon:** update the `claudebridge` library through the **Library Manager** (or re-install from the Asset Library). New `[McpTool]` methods hot-register within seconds of a clean compile — no re-registration needed on the Claude side.
- **Lifeline:** `npx -y sbox-mcp-server@2` always fetches the latest 2.x on first use, so it updates itself.
- **Plugin:** update through Claude Code's plugin manager.

---

## From source (hacking on the bridge)

For working on the bridge itself:

```powershell
# Windows
git clone https://github.com/LouSputthole/Sbox-Claude.git
cd Sbox-Claude

# Copy the addon into your project's Libraries/ (auto-detects your s&box project)
.\install.ps1
.\install.ps1 -ProjectPath "C:\path\to\your\sbox\project"   # explicit
.\install.ps1 -ListProjects                                  # show projects, then exit
.\install.ps1 -RemoveStaleAddons                             # also delete old <sbox>/addons/ installs
```

```bash
# Linux / WSL / macOS
git clone https://github.com/LouSputthole/Sbox-Claude.git
cd Sbox-Claude

./install.sh                                # auto-detect
./install.sh /path/to/your/sbox/project     # explicit
./install.sh --list                         # show projects
./install.sh --remove-stale                 # also clean old addons-folder installs
```

The addon's `[McpTool]` wrappers are discovered by the engine automatically once the library compiles — connect Claude Code exactly as in step 3. **Gotcha:** the Libraries file-watcher is unreliable for externally-edited `.cs` files — after syncing addon code, use `restart_editor` rather than expecting a hotload (see [docs/BRIDGE_GOTCHAS.md](docs/BRIDGE_GOTCHAS.md) #9).

---

## Legacy fallback — file IPC (v2.0.x only)

The v1.x file-IPC transport and the full stdio TS server remain **compiled-in and functional through v2.0.x** as a fallback for older engine builds that don't ship the native MCP server. They are not registered by default and **retire in v2.1.0**.

If you must use the fallback:

```bash
cd sbox-mcp-server
npm install
npm run build
claude mcp add sbox -- node /full/path/to/Sbox-Claude/sbox-mcp-server/dist/index.js
```

The fallback communicates via `req_*.json` / `res_*.json` files in a shared temp directory (`<os tmpdir>/sbox-bridge-ipc`). If calls time out, the usual cause is the MCP server (Node reads `TEMP`) and the addon (C# reads `TMP`) resolving **different** temp dirs — set `SBOX_BRIDGE_IPC_DIR` on the MCP-server side to the directory the addon logs (`[SboxBridge] … IPC at <dir>`). Full legacy diagnostics live in the root [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

---

## Uninstall

### Remove the MCP servers from Claude Code

```bash
claude mcp remove sbox
claude mcp remove sbox-lifeline
```

(Plugin users: uninstall the `sbox-claude` plugin instead.)

### Remove the bridge addon from your project

Delete `<your-project>/Libraries/claudebridge/` (or remove it via the Library Manager).

---

## Troubleshooting

See **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** for the v2 failure modes: port 7269 not answering, the stale HTTP.sys registration, bridge tools missing from `search_tools`, external addon edits never recompiling, editor-down diagnosis via the lifeline, play-mode refusals, and modal-dialog stalls. Engine limitations you work *around* (not fix) are in **[docs/BRIDGE_GOTCHAS.md](docs/BRIDGE_GOTCHAS.md)**.

---

## Publishing to the s&box Asset Library (maintainers only)

The bridge can be published to the s&box Asset Library so users can install it with one click from inside the editor.

### Prerequisites

1. A Steam account with s&box access
2. An organization registered on [sbox.game](https://sbox.game)

### Steps

1. Update `Org` and `Ident` in `sbox-bridge-addon/claudebridge.sbproj` to match your sbox.game organization.
2. Open the bridge as its own project in s&box editor (open the `claudebridge.sbproj` directly).
3. **Edit → Publish Project**, add a thumbnail and description, set visibility to Public, **Publish**.

The addon is then available in the in-editor Asset Library under the chosen name.
