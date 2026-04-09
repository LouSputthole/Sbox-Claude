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

		Log.Info( "[SboxBridge] All Phase 1 + Phase 2 command handlers registered" );
	}
}
