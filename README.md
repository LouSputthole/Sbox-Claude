# s&box Claude Bridge

> Let non-coders build s&box games through conversation with Claude Code.

## What This Does

Claude Code connects to the s&box editor in real-time. You describe what you want — Claude writes the C# scripts, builds the scenes, and iterates until it works.

```
You: "Make me a horror game where I explore an abandoned hospital with a flashlight"
Claude: *creates scripts, builds scene, configures lighting, adds player controller*
```

## Architecture

```
┌──────────────┐     stdio      ┌───────────────┐   file IPC     ┌──────────────┐
│  Claude Code │ ◄────────────► │  MCP Server   │ ◄────────────► │ Bridge Addon │
│              │                │  (Node.js)    │   %TEMP%/      │  (in s&box)  │
└──────────────┘                └───────────────┘                └──────┬───────┘
                                                                       │
                                                                       ▼
                                                                ┌──────────────┐
                                                                │ s&box Editor │
                                                                │  (Source 2)  │
                                                                └──────────────┘
```

Communication uses **file-based IPC** through `%TEMP%/sbox-bridge-ipc/` — the MCP server writes request JSON files, the bridge addon (running inside s&box) polls for them, processes on the main editor thread, and writes response files back.

## Quick Start

### 1. Create the Bridge Library in s&box

1. Open s&box with your project
2. Open **Library Manager** (in the editor)
3. Create a new library called **"claudebridge"**
4. Copy `sbox-bridge-addon/Editor/MyEditorMenu.cs` into the library's `Editor/` folder
5. Restart s&box

### 2. Build the MCP Server

```bash
git clone https://github.com/lousputthole/sbox-claude.git
cd sbox-claude/sbox-mcp-server
npm install
npm run build
```

### 3. Connect Claude Code

```bash
claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
```

### 4. Open the Bridge Dock

In s&box, go to **View → Claude Bridge** to open the dock panel. This is required for scene-modifying operations to work (they must run on the main editor thread).

### 5. Start Building

```
"Create a first-person player controller with WASD movement and mouse look"
"Add a cube at position 0,0,100 and give it a box model"
"What scenes are in the project?"
"Create a new script called EnemyAI with patrol behavior"
```

## Available Tools (78 active, 89 defined)

### Working & Tested
| Category | Tools |
|----------|-------|
| **Project & Files** | `get_project_info`, `list_project_files`, `read_file`, `write_file` |
| **Scripts** | `create_script`, `edit_script`, `delete_script` |
| **Scenes** | `list_scenes`, `load_scene`, `save_scene`, `create_scene` |
| **GameObjects** | `create_gameobject`, `delete_gameobject`, `duplicate_gameobject`, `rename_gameobject`, `set_parent`, `set_enabled`, `set_transform` |
| **Hierarchy** | `get_scene_hierarchy`, `get_selected_objects`, `select_object`, `focus_object` |
| **Components** | `get_property`, `set_property`, `get_all_properties`, `list_available_components`, `add_component_with_properties` |
| **Play Mode** | `start_play`, `stop_play`, `is_playing`, `get_runtime_property`, `set_runtime_property` |
| **Assets** | `search_assets`, `get_asset_info`, `assign_model`, `create_material`, `assign_material` |
| **Audio** | `list_sounds`, `create_sound_event`, `assign_sound`, `play_sound_preview` |
| **Prefabs** | `create_prefab`, `instantiate_prefab`, `list_prefabs`, `get_prefab_info` |
| **Physics** | `add_physics`, `add_collider`, `add_joint`, `raycast` |
| **Materials** | `set_material_property` |
| **Templates** | `create_player_controller`, `create_npc_controller`, `create_game_manager`, `create_trigger_zone` |
| **UI** | `create_razor_ui`, `add_screen_panel`, `add_world_panel` |
| **Editor** | `undo`, `redo`, `take_screenshot`, `trigger_hotload` |
| **Networking** | `network_spawn`, `add_sync_property`, `add_rpc_method`, `create_networked_player`, `create_lobby_manager`, `create_network_events`, `add_network_helper`, `configure_network`, `get_network_status`, `set_ownership` |
| **Publishing** | `get_project_config`, `set_project_config`, `validate_project`, `set_project_thumbnail`, `get_package_details`, `install_asset`, `list_asset_library` |
| **Diagnostics** | `get_bridge_status` |

### Not Implementable (no s&box API exists)
`pause_play`, `resume_play`, `get_console_output`, `get_compile_errors`, `clear_console`, `build_project`, `get_build_status`, `clean_build`, `export_project`, `prepare_publish`

## Technical Notes

- **No WebSocket**: s&box's sandboxed C# doesn't allow `System.Net`. We use file-based IPC instead.
- **Main thread required**: Scene APIs must run on the editor's main thread. A `[Dock]` widget with `[EditorEvent.Frame]` processes queued requests.
- **Addon location**: Must be in the project's `Libraries/` folder, NOT the global `addons/` folder.
- **API reference**: Download the full type schema from `sbox.game/api` for the definitive API.

## Development

```bash
# Build MCP Server
cd sbox-mcp-server && npm install && npm run build

# Test IPC manually (PowerShell):
echo '{"id":"test","command":"get_project_info","params":{}}' > $env:TEMP\sbox-bridge-ipc\req_test.json
cat $env:TEMP\sbox-bridge-ipc\res_test.json
```

See [CLAUDE.md](CLAUDE.md) for detailed architecture docs, verified APIs, and lessons learned.

## Credits

Built by [sboxskins.gg](https://sboxskins.gg) — the s&box community marketplace.

## License

**GPL-3.0** — see [LICENSE](LICENSE) for details.

This means you can freely use Claude Bridge in your s&box games (free or commercial). You can modify it for your own use. But if you redistribute a modified version of the bridge itself, you must keep it open source under GPL-3.0 and credit sboxskins.gg.

Copyright (c) 2026 [sboxskins.gg](https://sboxskins.gg)
