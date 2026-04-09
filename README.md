# Sbox-Claude

Let non-coders build s&box games through conversation with Claude Code.

## What This Does

Claude Code connects to the s&box editor in real-time. You describe what you want — Claude writes the C# scripts, builds the scenes, reads the console errors, and iterates until it works. s&box hotloads everything instantly.

```
You: "Make me a horror game where I explore an abandoned hospital with a flashlight"
Claude: *creates scripts, builds scene, configures lighting, adds player controller*
```

## Architecture

```
┌──────────────┐     stdio      ┌───────────────┐   WebSocket    ┌──────────────┐
│  Claude Code │ ◄────────────► │  MCP Server   │ ◄────────────► │ Bridge Addon │
│              │                │  (Node.js)    │    :29015      │  (in s&box)  │
└──────────────┘                └───────────────┘                └──────┬───────┘
                                                                       │
                                                                       ▼
                                                                ┌──────────────┐
                                                                │ s&box Editor │
                                                                │  (Source 2)  │
                                                                └──────────────┘
```

## Quick Start

### 1. Install the Bridge Addon in s&box

Copy the `sbox-bridge-addon/` folder into your s&box addons directory. When s&box loads, it will compile the addon and start the WebSocket server on port 29015.

### 2. Connect Claude Code (one command)

```bash
claude mcp add sbox -- npx sbox-mcp-server
```

That's it. No cloning, no building. `npx` downloads and runs the server automatically.

### 3. Start Building

Open s&box, open a project, and start talking to Claude:

```
"Create a first-person player controller with WASD movement and mouse look"
"Add a flashlight to the player that toggles with F"
"What compile errors are there? Fix them"
"Create a new scene called level_01 with a camera and lights"
"Search for a table model and place it in the scene"
"Enter play mode and take a screenshot"
```

## Alternative Setup

<details>
<summary>Manual install (from source)</summary>

```bash
git clone https://github.com/lousputthole/sbox-claude.git
cd sbox-claude/sbox-mcp-server
npm install
npm run build
claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
```

</details>

<details>
<summary>JSON config (for Claude Code settings)</summary>

Add to `~/.claude/claude_desktop_config.json` or your project's `.mcp.json`:

```json
{
  "mcpServers": {
    "sbox": {
      "command": "npx",
      "args": ["sbox-mcp-server"],
      "env": {
        "SBOX_BRIDGE_HOST": "127.0.0.1",
        "SBOX_BRIDGE_PORT": "29015"
      }
    }
  }
}
```

</details>

## Available Tools (53)

### Project & Files
| Tool | What It Does |
|------|-------------|
| `get_project_info` | Returns project path, name, type, dependencies |
| `list_project_files` | Browse file tree, filter by directory/extension |
| `read_file` | Read any project file contents |
| `write_file` | Create/overwrite files, auto-creates directories |

### Script Management
| Tool | What It Does |
|------|-------------|
| `create_script` | Generate C# component with boilerplate or raw content |
| `edit_script` | Find/replace, insert, append, delete lines in scripts |
| `delete_script` | Remove script files from project |
| `trigger_hotload` | Force recompile + hotload scripts |

### Console & Errors
| Tool | What It Does |
|------|-------------|
| `get_console_output` | Read buffered log entries by severity |
| `get_compile_errors` | Get diagnostics with file, line, column, message |
| `clear_console` | Clear the log buffer |

### Scenes
| Tool | What It Does |
|------|-------------|
| `list_scenes` | Find all .scene files in project |
| `load_scene` | Open a scene in the editor |
| `save_scene` | Save the current scene |
| `create_scene` | New scene with optional camera, light, ground |

### GameObjects
| Tool | What It Does |
|------|-------------|
| `create_gameobject` | Create with name, position, rotation, scale, parent |
| `delete_gameobject` | Remove by GUID |
| `duplicate_gameobject` | Clone with all components, optional offset |
| `rename_gameobject` | Change display name |
| `set_parent` | Reparent (or move to root with null) |
| `set_enabled` | Enable/disable object |
| `set_transform` | Set position/rotation/scale (world or local) |

### Components
| Tool | What It Does |
|------|-------------|
| `get_property` | Read a single component property value |
| `set_property` | Write a component property (editor mode) |
| `get_all_properties` | Dump all properties as JSON |
| `list_available_components` | Browse all component types (built-in + custom) |
| `add_component_with_properties` | Add component and set properties in one call |

### Hierarchy & Selection
| Tool | What It Does |
|------|-------------|
| `get_scene_hierarchy` | Full scene tree with GUIDs, components, positions |
| `get_selected_objects` | What the user has selected in editor |
| `select_object` | Programmatically select an object |
| `focus_object` | Move editor camera to look at object |

### Assets
| Tool | What It Does |
|------|-------------|
| `search_assets` | Search project assets by name/type |
| `list_asset_library` | Browse community asset packages |
| `install_asset` | Add community package to project |
| `get_asset_info` | Detailed asset metadata |

### Materials & Models
| Tool | What It Does |
|------|-------------|
| `assign_model` | Set model on ModelRenderer (auto-creates component) |
| `create_material` | New .vmat with shader + properties |
| `assign_material` | Apply material to renderer slot |
| `set_material_property` | Change color, roughness, texture, etc. |

### Audio
| Tool | What It Does |
|------|-------------|
| `list_sounds` | Find sound assets in project |
| `create_sound_event` | New .sound with volume, pitch, falloff settings |
| `assign_sound` | Attach sound to SoundPointComponent |
| `play_sound_preview` | Preview a sound in the editor |

### Play Mode & Testing
| Tool | What It Does |
|------|-------------|
| `start_play` | Enter play mode — run the game in editor |
| `stop_play` | Exit play mode — return to editor |
| `pause_play` | Pause running game |
| `resume_play` | Resume paused game |
| `is_playing` | Check state: playing / paused / stopped |
| `get_runtime_property` | Read component property during play mode |
| `set_runtime_property` | Write component property during play mode |
| `take_screenshot` | Capture editor viewport as PNG |

### Editor
| Tool | What It Does |
|------|-------------|
| `undo` | Undo last editor action |
| `redo` | Redo last undone action |

### Diagnostics
| Tool | What It Does |
|------|-------------|
| `get_bridge_status` | Connection health, latency, registered commands |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `SBOX_BRIDGE_HOST` | `127.0.0.1` | Bridge WebSocket host |
| `SBOX_BRIDGE_PORT` | `29015` | Bridge WebSocket port |

## Troubleshooting

**Bridge won't connect** — Is s&box running with the Bridge Addon? Check `get_bridge_status`. Default port is 29015.

**Commands timeout (30s)** — The editor may be frozen (compiling, loading). Try `get_bridge_status` — if latency is -1, restart the MCP server.

**Compile errors after script edit** — Run `get_compile_errors`, fix with `edit_script`, `trigger_hotload`, verify with `get_compile_errors` again.

**Play mode not working** — Check `is_playing` first. Runtime property tools require `start_play` first. `stop_play` discards all runtime changes.

## Roadmap

- **Phase 1** ✅ Foundation — project awareness, scripts, console, scenes (15 tools)
- **Phase 2** ✅ Scene Building — GameObjects, components, hierarchy, selection (15 tools)
- **Phase 3** ✅ Assets & Resources — asset browser, materials, models, audio (12 tools)
- **Phase 4** ✅ Play & Test — play mode, runtime debugging, screenshots, undo/redo (11 tools)
- **Phase 5** 🔲 Game Logic — prefabs, player controllers, AI templates, UI systems
- **Phase 6** 🔲 Multiplayer — networking, RPCs, local testing
- **Phase 7** 🔲 Publishing — build, export, Steam Workshop

## License

MIT
