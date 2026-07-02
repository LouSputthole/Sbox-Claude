# Installation Guide

There is **one** correct place to install the Claude Bridge: inside your s&box **project's** `Libraries/` folder. s&box's global `addons/` folder is built-in only and **will not compile custom C#** — if anything tells you to install there, ignore it. The installer below handles this for you.

Total setup time: ~5 minutes.

## Prerequisites

- **s&box** installed via Steam
- **Node.js 18+** ([download](https://nodejs.org/))
- **Claude Code** ([setup guide](https://docs.anthropic.com/en/docs/claude-code))
- An s&box **project** you intend to use the bridge with (create one in s&box first if you don't have one yet)

---

## Install (recommended — uses the installer script)

### Windows (PowerShell)

```powershell
git clone https://github.com/lousputthole/sbox-claude.git
cd sbox-claude

# Auto-detects your project if you have exactly one in Documents\s&box projects
.\install.ps1

# ...or pass it explicitly:
.\install.ps1 -ProjectPath "C:\path\to\your\sbox\project"

# Useful flags:
.\install.ps1 -ListProjects        # show projects, then exit
.\install.ps1 -RemoveStaleAddons   # also delete any old install under <sbox>/addons/
```

### Linux / WSL / macOS

```bash
git clone https://github.com/lousputthole/sbox-claude.git
cd sbox-claude

./install.sh                                # auto-detect
./install.sh /path/to/your/sbox/project     # explicit
./install.sh --list                         # show projects
./install.sh --remove-stale                 # also clean old addons-folder installs
```

The installer copies two files into `<your-project>/Libraries/claudebridge/`:

- `claudebridge.sbproj` (library manifest)
- `Editor/MyEditorMenu.cs` (the bridge itself)

s&box will auto-generate the matching `.csproj` files on next launch.

### Build the MCP server

```bash
cd sbox-mcp-server
npm install
npm run build
```

### Register with Claude Code (one-time)

```bash
claude mcp add sbox -- node /full/path/to/sbox-claude/sbox-mcp-server/dist/index.js
```

Or, if the published npm package is available:

```bash
claude mcp add sbox -- npx sbox-mcp-server
```

---

## Manual install (fallback, if the installer can't find your project)

1. Open s&box and load your project.
2. Find your project folder (default: `Documents\s&box projects\<yourgame>`).
3. Create `Libraries\claudebridge\Editor\` inside that project.
4. Copy `sbox-bridge-addon\claudebridge.sbproj` from this repo into `Libraries\claudebridge\`.
5. Copy `sbox-bridge-addon\Editor\MyEditorMenu.cs` from this repo into `Libraries\claudebridge\Editor\`.
6. Restart s&box.

> Do **not** copy `claudebridge.editor.csproj` — that file has hard-coded paths to s&box on a specific machine. s&box will regenerate a fresh one against your local install on next launch.

---

## Verify it's working

1. Start (or restart) s&box and load your project.
2. **View → Claude Bridge** opens the dock (optional — the bridge's frame handler is static and runs whether or not the dock is open; the dock is just a status readout).
3. In Claude Code, ask:

```
"Check the bridge status."
```

You should get back: `connected: true` and a non-zero `handlerCount` matching your addon version (`get_bridge_status` reports both and warns on drift). (That's the count of C# handlers inside the editor; the MCP server exposes a few more tools total — a handful, like `read_log` / `get_compile_errors` / the docs-search tools, run MCP-server-side and don't need an editor handler.) Then try:

```
"What project is open in s&box?"
```

If both work, you're set. If anything fails, jump to `TROUBLESHOOTING.md`.

---

## Updating

### Update the MCP server

```bash
cd sbox-claude
git pull
cd sbox-mcp-server
npm install
npm run build
```

Restart any open Claude Code sessions so they pick up the new server.

### Update the bridge addon

Re-run the installer — it overwrites the project copy:

```powershell
.\install.ps1     # Windows
./install.sh      # Linux/Mac
```

Then in s&box, ask Claude to call `trigger_hotload` (or restart s&box if hotload gets stuck).

---

## Configuration

The bridge communicates over **file-based IPC** — the MCP server writes
`req_*.json` and the addon writes `res_*.json` in a shared temp directory. There
is no network socket; `SBOX_BRIDGE_HOST` / `SBOX_BRIDGE_PORT` are cosmetic (shown
only in `get_bridge_status`).

### Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `SBOX_BRIDGE_IPC_DIR` | `<os tmpdir>/sbox-bridge-ipc` | IPC directory. Must match on both sides. |
| `SBOX_BRIDGE_HOST` | `127.0.0.1` | Legacy/cosmetic — display only |
| `SBOX_BRIDGE_PORT` | `29015` | Legacy/cosmetic — display only |

### Fixing a "connected but every call times out" hang

This almost always means the MCP server (Node) and the s&box addon (C#) resolved
**different** temp directories — Node uses `os.tmpdir()` (reads `TEMP` first), C#
uses `Path.GetTempPath()` (reads `TMP` first), and on some machines those differ.

1. In s&box, open **Editor → Claude Bridge → Status** (or check the editor
   console for `[SboxBridge] Bridge … IPC at <dir>`). Note that directory.
2. Point the MCP server at the same directory:
   ```bash
   claude mcp add sbox --env SBOX_BRIDGE_IPC_DIR="<that dir>" -- npx sbox-mcp-server
   ```

The addon side resolves its directory from `Path.GetTempPath()` only and does not
honor an env override (to stay inside the s&box sandbox), so realign from the
MCP-server side.

---

## Uninstall

### Remove the MCP server from Claude Code

```bash
claude mcp remove sbox
```

### Remove the bridge addon from your project

Delete `<your-project>/Libraries/claudebridge/`.

---

## Troubleshooting

See **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** for diagnoses and fixes for the common failure modes: install-in-wrong-folder, `tool.frame` error spam, project save corruption, dock-closed timeouts, and more.

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
