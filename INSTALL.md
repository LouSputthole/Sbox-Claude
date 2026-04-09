# Installation Guide

This guide walks you through setting up the s&box Claude Bridge from scratch. Total setup time: ~5 minutes.

## Prerequisites

- **s&box** installed via Steam
- **Node.js 18+** installed ([download](https://nodejs.org/))
- **Claude Code** installed ([setup guide](https://docs.anthropic.com/en/docs/claude-code))

## Quick Install (Recommended)

### Windows (PowerShell)

```powershell
# 1. Clone the repo
git clone https://github.com/lousputthole/sbox-claude.git
cd sbox-claude

# 2. Run the installer — auto-detects your s&box install
.\install.ps1

# 3. Connect Claude Code (one-time)
claude mcp add sbox -- npx sbox-mcp-server
```

### Linux / WSL

```bash
# 1. Clone the repo
git clone https://github.com/lousputthole/sbox-claude.git
cd sbox-claude

# 2. Run the installer
./install.sh

# 3. Connect Claude Code (one-time)
claude mcp add sbox -- npx sbox-mcp-server
```

### If auto-detect fails

Pass your s&box path manually:

```powershell
# Windows
.\install.ps1 -SboxPath "D:\SteamLibrary\steamapps\common\sbox"

# Linux/WSL
./install.sh /mnt/d/SteamLibrary/steamapps/common/sbox
```

## Manual Install

If you prefer to install manually (or the installer doesn't work for your setup):

### Step 1: Copy the Bridge Addon

Copy the entire `sbox-bridge-addon/` folder into your s&box addons directory:

| Platform | Typical addons path |
|----------|-------------------|
| Windows (Steam) | `C:\Program Files\Steam\steamapps\common\sbox\addons\` |
| Windows (custom) | `D:\SteamLibrary\steamapps\common\sbox\addons\` |
| Linux (Steam) | `~/.steam/steam/steamapps/common/sbox/addons/` |

The result should look like:
```
sbox/
  addons/
    sbox-bridge-addon/
      sbox-bridge-addon.sbproj
      Code/
        Core/
        Commands/
      Assets/
```

### Step 2: Connect Claude Code

Run this once in your terminal:

```bash
claude mcp add sbox -- npx sbox-mcp-server
```

This registers the MCP server with Claude Code. It will auto-start whenever you open a Claude Code session.

## Verify It's Working

1. **Start s&box** — open the editor with any project
2. **Open Claude Code** — start a new session
3. **Test the connection:**
   ```
   You: "Check if the bridge is connected"
   Claude: *calls get_bridge_status* → shows connected, latency, etc.
   ```
4. **Try a command:**
   ```
   You: "What project is open in s&box?"
   Claude: *calls get_project_info* → shows project name, path, etc.
   ```

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `SBOX_BRIDGE_HOST` | `127.0.0.1` | Bridge WebSocket host |
| `SBOX_BRIDGE_PORT` | `29015` | Bridge WebSocket port |

### Custom Port

If port 29015 is in use, change it on both sides:

**MCP Server side:**
```bash
claude mcp add sbox --env SBOX_BRIDGE_PORT=29016 -- npx sbox-mcp-server
```

**Bridge Addon side:** Edit `BridgeAddon.cs` line in `OnEditorLoaded()`:
```csharp
_ = BridgeServer.Start(29016);
```

## Updating

### Update the MCP Server

```bash
npx sbox-mcp-server@latest
```

Or if using a local install, pull and rebuild:
```bash
cd sbox-claude/sbox-mcp-server
git pull
npm install
npm run build
```

### Update the Bridge Addon

Run the installer again — it detects the existing install and replaces it:

```powershell
.\install.ps1     # Windows
./install.sh      # Linux
```

## Uninstall

### Remove MCP Server from Claude Code

```bash
claude mcp remove sbox
```

### Remove Bridge Addon from s&box

Delete the `sbox-bridge-addon/` folder from your s&box addons directory.

## Troubleshooting

### "Cannot connect to s&box Bridge"
- Is s&box running? The editor must be open
- Is a project loaded? Open a project in s&box first
- Check the port: run `get_bridge_status` to see connection details
- Firewall: ensure localhost:29015 isn't blocked

### "npx: command not found"
- Install Node.js 18+ from [nodejs.org](https://nodejs.org/)
- Restart your terminal after installing

### "claude: command not found"
- Install Claude Code: see [setup guide](https://docs.anthropic.com/en/docs/claude-code)

### Installer can't find s&box
- Pass the path manually (see "If auto-detect fails" above)
- Check that s&box is installed via Steam and has been run at least once

### Bridge addon won't compile in s&box
- Check the s&box console for error messages
- Make sure the addon folder structure is correct (see Manual Install)
- Some s&box APIs may need adjustment for your SDK version — see `API-NOTE` comments in handler files
