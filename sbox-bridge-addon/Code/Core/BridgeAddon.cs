using Sandbox;

namespace SboxBridge;

/// <summary>
/// Main entry point for the s&box Bridge Addon.
/// Starts the WebSocket server and registers all command handlers.
/// </summary>
[Title( "Claude Bridge" )]
[Description( "Enables Claude Code to interact with the s&box editor via MCP" )]
public class BridgeAddon
{
	/// <summary>
	/// Called when the addon is loaded by s&box.
	/// Registers all handlers and starts the Bridge server.
	/// </summary>
	[Event( "editor.loaded" )]
	public static void OnEditorLoaded()
	{
		RegisterHandlers();
		_ = BridgeServer.Start();
	}

	/// <summary>
	/// Called when the addon is unloaded.
	/// </summary>
	[Event( "editor.unloaded" )]
	public static void OnEditorUnloaded()
	{
		BridgeServer.Stop();
	}

	/// <summary>
	/// Register every command handler with the Bridge server.
	/// The string key passed to <see cref="BridgeServer.RegisterHandler"/> must match
	/// the MCP tool name exactly (snake_case, e.g. "create_gameobject") because the
	/// MCP server forwards requests using the same name as the command key.
	/// </summary>
	private static void RegisterHandlers()
	{
		// Phase 1.1 — Project Awareness
		BridgeServer.RegisterHandler( "get_project_info", new GetProjectInfoHandler() );
		BridgeServer.RegisterHandler( "list_project_files", new ListProjectFilesHandler() );
		BridgeServer.RegisterHandler( "read_file", new ReadFileHandler() );
		BridgeServer.RegisterHandler( "write_file", new WriteFileHandler() );

		// Phase 1.2 — Script Management
		BridgeServer.RegisterHandler( "create_script", new CreateScriptHandler() );
		BridgeServer.RegisterHandler( "edit_script", new EditScriptHandler() );
		BridgeServer.RegisterHandler( "delete_script", new DeleteScriptHandler() );
		BridgeServer.RegisterHandler( "trigger_hotload", new TriggerHotloadHandler() );

		// Phase 1.3 — Console & Error Feedback
		BridgeServer.RegisterHandler( "get_console_output", new GetConsoleOutputHandler() );
		BridgeServer.RegisterHandler( "get_compile_errors", new GetCompileErrorsHandler() );
		BridgeServer.RegisterHandler( "clear_console", new ClearConsoleHandler() );

		// Phase 1.4 — Scene File Operations
		BridgeServer.RegisterHandler( "list_scenes", new ListScenesHandler() );
		BridgeServer.RegisterHandler( "load_scene", new LoadSceneHandler() );
		BridgeServer.RegisterHandler( "save_scene", new SaveSceneHandler() );
		BridgeServer.RegisterHandler( "create_scene", new CreateSceneHandler() );

		// Phase 2.1 — GameObject Lifecycle
		BridgeServer.RegisterHandler( "create_gameobject", new CreateGameObjectHandler() );
		BridgeServer.RegisterHandler( "delete_gameobject", new DeleteGameObjectHandler() );
		BridgeServer.RegisterHandler( "duplicate_gameobject", new DuplicateGameObjectHandler() );
		BridgeServer.RegisterHandler( "rename_gameobject", new RenameGameObjectHandler() );
		BridgeServer.RegisterHandler( "set_parent", new SetParentHandler() );
		BridgeServer.RegisterHandler( "set_enabled", new SetEnabledHandler() );
		BridgeServer.RegisterHandler( "set_transform", new SetTransformHandler() );

		// Phase 2.2 — Component Operations
		BridgeServer.RegisterHandler( "get_property", new GetPropertyHandler() );
		BridgeServer.RegisterHandler( "get_all_properties", new GetAllPropertiesHandler() );
		BridgeServer.RegisterHandler( "list_available_components", new ListAvailableComponentsHandler() );
		BridgeServer.RegisterHandler( "add_component_with_properties", new AddComponentWithPropertiesHandler() );

		// Phase 2.3 — Hierarchy & Selection
		BridgeServer.RegisterHandler( "get_scene_hierarchy", new GetSceneHierarchyHandler() );
		BridgeServer.RegisterHandler( "get_selected_objects", new GetSelectedObjectsHandler() );
		BridgeServer.RegisterHandler( "select_object", new SelectObjectHandler() );
		BridgeServer.RegisterHandler( "focus_object", new FocusObjectHandler() );

		// Phase 3.1 — Asset Browser
		BridgeServer.RegisterHandler( "search_assets", new SearchAssetsHandler() );
		BridgeServer.RegisterHandler( "list_asset_library", new ListAssetLibraryHandler() );
		BridgeServer.RegisterHandler( "install_asset", new InstallAssetHandler() );
		BridgeServer.RegisterHandler( "get_asset_info", new GetAssetInfoHandler() );

		// Phase 3.2 — Materials & Models
		BridgeServer.RegisterHandler( "assign_model", new AssignModelHandler() );
		BridgeServer.RegisterHandler( "create_material", new CreateMaterialHandler() );
		BridgeServer.RegisterHandler( "assign_material", new AssignMaterialHandler() );
		BridgeServer.RegisterHandler( "set_material_property", new SetMaterialPropertyHandler() );

		// Phase 3.3 — Audio
		BridgeServer.RegisterHandler( "list_sounds", new ListSoundsHandler() );
		BridgeServer.RegisterHandler( "create_sound_event", new CreateSoundEventHandler() );
		BridgeServer.RegisterHandler( "assign_sound", new AssignSoundHandler() );
		BridgeServer.RegisterHandler( "play_sound_preview", new PlaySoundPreviewHandler() );

		// Phase 4.1 — Play Mode Control
		BridgeServer.RegisterHandler( "start_play", new StartPlayHandler() );
		BridgeServer.RegisterHandler( "stop_play", new StopPlayHandler() );
		BridgeServer.RegisterHandler( "pause_play", new PausePlayHandler() );
		BridgeServer.RegisterHandler( "resume_play", new ResumePlayHandler() );
		BridgeServer.RegisterHandler( "is_playing", new IsPlayingHandler() );

		// Phase 4.2 — Runtime Debugging
		BridgeServer.RegisterHandler( "set_property", new SetPropertyHandler() );
		BridgeServer.RegisterHandler( "get_runtime_property", new GetRuntimePropertyHandler() );
		BridgeServer.RegisterHandler( "set_runtime_property", new SetRuntimePropertyHandler() );
		BridgeServer.RegisterHandler( "take_screenshot", new TakeScreenshotHandler() );

		// Phase 4.3 — Editor Undo/Redo
		BridgeServer.RegisterHandler( "undo", new UndoHandler() );
		BridgeServer.RegisterHandler( "redo", new RedoHandler() );

		Log.Info( "[SboxBridge] All Phase 1–4 command handlers registered (53 tools)" );
	}
}
