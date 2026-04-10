using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Handler interface for bridge commands.
/// </summary>
public interface IBridgeHandler
{
	Task<object> Execute( JsonElement parameters );
}

/// <summary>
/// Claude Bridge — file-based IPC server for MCP integration.
/// </summary>
public static class ClaudeBridge
{
	private static readonly Dictionary<string, IBridgeHandler> _handlers = new();
	private static bool _running;
	private static string _ipcDir;
	private static Timer _pollTimer;

	static ClaudeBridge()
	{
		Log.Info( "[SboxBridge] Initializing..." );
		RegisterHandlers();
		StartBridge();
	}

	[Menu( "Editor", "Claude Bridge/Status", "smart_toy" )]
	public static void ShowStatus()
	{
		var msg = _running
			? $"Running\nIPC: {_ipcDir}\nHandlers: {_handlers.Count}"
			: "Not running";
		EditorUtility.DisplayDialog( "Claude Bridge", msg );
	}

	static void StartBridge()
	{
		if ( _running ) return;

		try
		{
			_ipcDir = Path.Combine( Path.GetTempPath(), "sbox-bridge-ipc" );
			Directory.CreateDirectory( _ipcDir );

			var statusPath = Path.Combine( _ipcDir, "status.json" );
			File.WriteAllText( statusPath, JsonSerializer.Serialize( new
			{
				running = true,
				startedAt = DateTime.UtcNow.ToString( "o" ),
				handlerCount = _handlers.Count
			} ) );

			_running = true;

			// Use a Timer only to read request files from disk (IO is thread-safe)
			// But queue the actual processing for the main thread
			_pollTimer = new Timer( ReadRequestFiles, null, 500, 50 );

			Log.Info( $"[SboxBridge] Bridge started — {_handlers.Count} handlers, IPC at {_ipcDir}" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SboxBridge] Failed to start: {ex.Message}" );
		}
	}

	// Pending requests read from disk, to be processed on main thread
	static readonly Queue<(string responseId, string json)> _pendingRequests = new();
	static readonly object _queueLock = new();

	static void RegisterHandlers()
	{
		// ── Batch 1: File / project basics ──────────────────────────────
		Register( "get_project_info",    new GetProjectInfoHandler() );
		Register( "list_project_files",  new ListProjectFilesHandler() );
		Register( "read_file",           new ReadFileHandler() );
		Register( "write_file",          new WriteFileHandler() );
		Register( "create_script",       new CreateScriptHandler() );
		Register( "edit_script",         new EditScriptHandler() );
		Register( "delete_script",       new DeleteScriptHandler() );
		Register( "list_scenes",         new ListScenesHandler() );

		// ── Batch 2: Scene file operations ──────────────────────────────
		Register( "load_scene",          new LoadSceneHandler() );
		Register( "save_scene",          new SaveSceneHandler() );
		Register( "create_scene",        new CreateSceneHandler() );

		// ── Batch 3: GameObject CRUD ─────────────────────────────────────
		Register( "create_gameobject",   new CreateGameObjectHandler() );
		Register( "delete_gameobject",   new DeleteGameObjectHandler() );
		Register( "duplicate_gameobject",new DuplicateGameObjectHandler() );
		Register( "rename_gameobject",   new RenameGameObjectHandler() );
		Register( "set_parent",          new SetParentHandler() );
		Register( "set_enabled",         new SetEnabledHandler() );
		Register( "set_transform",       new SetTransformHandler() );
		Register( "get_scene_hierarchy", new GetSceneHierarchyHandler() );
		Register( "get_selected_objects",new GetSelectedObjectsHandler() );
		Register( "select_object",       new SelectObjectHandler() );
		Register( "focus_object",        new FocusObjectHandler() );

		// ── Batch 4: Components ──────────────────────────────────────────
		Register( "get_property",                   new GetPropertyHandler() );
		Register( "get_all_properties",             new GetAllPropertiesHandler() );
		Register( "set_property",                   new SetPropertyHandler() );
		Register( "list_available_components",      new ListAvailableComponentsHandler() );
		Register( "add_component_with_properties",  new AddComponentWithPropertiesHandler() );

		// ── Batch 5: Play mode ───────────────────────────────────────────
		Register( "start_play",          new StartPlayHandler() );
		Register( "stop_play",           new StopPlayHandler() );
		// pause_play / resume_play — no API found, omitted
		Register( "is_playing",          new IsPlayingHandler() );
		Register( "get_runtime_property",new GetRuntimePropertyHandler() );
		Register( "set_runtime_property",new SetRuntimePropertyHandler() );

		// ── Batch 6: Assets ──────────────────────────────────────────────
		Register( "search_assets",       new SearchAssetsHandler() );
		Register( "get_asset_info",      new GetAssetInfoHandler() );
		Register( "assign_model",        new AssignModelHandler() );
		Register( "create_material",     new CreateMaterialHandler() );
		Register( "assign_material",     new AssignMaterialHandler() );
		Register( "set_material_property", new SetMaterialPropertyHandler() );

		// ── Batch 7: Audio ───────────────────────────────────────────────
		Register( "list_sounds",         new ListSoundsHandler() );
		Register( "create_sound_event",  new CreateSoundEventHandler() );
		Register( "assign_sound",        new AssignSoundHandler() );
		Register( "play_sound_preview",  new PlaySoundPreviewHandler() );

		// ── Batch 8: Prefabs ─────────────────────────────────────────────
		Register( "create_prefab",       new CreatePrefabHandler() );
		Register( "instantiate_prefab",  new InstantiatePrefabHandler() );
		Register( "list_prefabs",        new ListPrefabsHandler() );
		Register( "get_prefab_info",     new GetPrefabInfoHandler() );

		// ── Batch 9: Physics ─────────────────────────────────────────────
		Register( "add_physics",         new AddPhysicsHandler() );
		Register( "add_collider",        new AddColliderHandler() );
		Register( "add_joint",           new AddJointHandler() );
		Register( "raycast",             new RaycastHandler() );

		// ── Batch 10: Code templates ─────────────────────────────────────
		Register( "create_player_controller", new CreatePlayerControllerHandler() );
		Register( "create_npc_controller",    new CreateNpcControllerHandler() );
		Register( "create_game_manager",      new CreateGameManagerHandler() );
		Register( "create_trigger_zone",      new CreateTriggerZoneHandler() );

		// ── Batch 11: UI ─────────────────────────────────────────────────
		Register( "create_razor_ui",     new CreateRazorUIHandler() );
		Register( "add_screen_panel",    new AddScreenPanelHandler() );
		Register( "add_world_panel",     new AddWorldPanelHandler() );

		// ── Batch 11b: Undo/Redo ─────────────────────────────────────────
		Register( "undo",                new UndoHandler() );
		Register( "redo",                new RedoHandler() );

		// ── Batch 12: Networking ─────────────────────────────────────────
		Register( "add_network_helper",  new AddNetworkHelperHandler() );
		Register( "configure_network",   new ConfigureNetworkHandler() );
		Register( "get_network_status",  new GetNetworkStatusHandler() );
		Register( "set_ownership",       new SetOwnershipHandler() );
		Register( "network_spawn",            new NetworkSpawnHandler() );
		Register( "add_sync_property",        new AddSyncPropertyHandler() );
		Register( "add_rpc_method",           new AddRpcMethodHandler() );
		Register( "create_networked_player",  new CreateNetworkedPlayerHandler() );
		Register( "create_lobby_manager",     new CreateLobbyManagerHandler() );
		Register( "create_network_events",    new CreateNetworkEventsHandler() );

		// ── Batch 13: Publishing / config ────────────────────────────────
		Register( "get_project_config",  new GetProjectConfigHandler() );
		Register( "set_project_config",  new SetProjectConfigHandler() );
		Register( "validate_project",    new ValidateProjectHandler() );
		Register( "set_project_thumbnail",new SetProjectThumbnailHandler() );
		Register( "get_package_details", new GetPackageDetailsHandler() );
		Register( "install_asset",       new InstallAssetHandler() );
		Register( "list_asset_library",  new ListAssetLibraryHandler() );

		// ── Batch 14: Console / diagnostics ─────────────────────────────
		// get_console_output / get_compile_errors / clear_console — LogCapture not available, omitted
		Register( "take_screenshot",     new TakeScreenshotHandler() );
		Register( "trigger_hotload",     new TriggerHotloadHandler() );

		Log.Info( $"[SboxBridge] Registered {_handlers.Count} handlers" );
	}

	static void Register( string name, IBridgeHandler handler )
	{
		_handlers[name] = handler;
	}

	/// <summary>
	/// Runs on a timer thread — only reads files from disk and queues them.
	/// </summary>
	static void ReadRequestFiles( object state )
	{
		if ( !_running || _ipcDir == null ) return;

		try
		{
			var files = Directory.GetFiles( _ipcDir, "req_*.json" );
			foreach ( var reqFile in files )
			{
				try
				{
					var json = File.ReadAllText( reqFile, Encoding.UTF8 );
					File.Delete( reqFile );

					var fileName = Path.GetFileNameWithoutExtension( reqFile );
					var responseId = fileName.Substring( 4 );

					lock ( _queueLock )
					{
						_pendingRequests.Enqueue( (responseId, json) );
					}
				}
				catch ( IOException ) { }
				catch ( Exception ex )
				{
					Log.Warning( $"[SboxBridge] Read error: {ex.Message}" );
				}
			}
		}
		catch { }
	}

	/// <summary>
	/// Called on the main thread by BridgePoller widget.
	/// Processes queued requests where scene APIs are safe to call.
	/// </summary>
	public static void ProcessPendingOnMainThread()
	{
		while ( true )
		{
			(string responseId, string json) item;
			lock ( _queueLock )
			{
				if ( _pendingRequests.Count == 0 ) break;
				item = _pendingRequests.Dequeue();
			}

			string response;
			try { response = ProcessRequest( item.json ).GetAwaiter().GetResult(); }
			catch ( Exception ex ) { response = MakeError( null, $"Processing error: {ex.Message}" ); }

			try
			{
				var responsePath = Path.Combine( _ipcDir, $"res_{item.responseId}.json" );
				File.WriteAllText( responsePath, response, Encoding.UTF8 );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[SboxBridge] Write error: {ex.Message}" );
			}
		}
	}

	static async Task<string> ProcessRequest( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		var id = root.TryGetProperty( "id", out var idProp ) ? idProp.GetString() : null;
		var command = root.TryGetProperty( "command", out var cmdProp ) ? cmdProp.GetString() : null;

		if ( string.IsNullOrEmpty( id ) )
			return MakeError( null, "Missing 'id'" );
		if ( string.IsNullOrEmpty( command ) )
			return MakeError( id, "Missing 'command'" );

		// Built-in status command
		if ( command == "get_bridge_status" )
		{
			return JsonSerializer.Serialize( new
			{
				id, success = true,
				data = new
				{
					connected = true,
					running = _running,
					handlerCount = _handlers.Count,
					registeredCommands = _handlers.Keys.ToArray()
				}
			} );
		}

		if ( _handlers.TryGetValue( command, out var handler ) )
		{
			try
			{
				var paramsElement = root.TryGetProperty( "params", out var p ) ? p : default;
				var result = await handler.Execute( paramsElement );
				return JsonSerializer.Serialize( new { id, success = true, data = result } );
			}
			catch ( Exception ex )
			{
				return MakeError( id, $"Handler error: {ex.Message}" );
			}
		}

		return MakeError( id, $"Unknown command: {command}" );
	}

	static string MakeError( string id, string message )
	{
		return JsonSerializer.Serialize( new { id, success = false, error = message } );
	}

	// ── Shared helpers ────────────────────────────────────────────────────
	internal static Vector3 ParseVector3( JsonElement e )
	{
		float x = e.TryGetProperty( "x", out var ex ) ? ex.GetSingle() : 0f;
		float y = e.TryGetProperty( "y", out var ey ) ? ey.GetSingle() : 0f;
		float z = e.TryGetProperty( "z", out var ez ) ? ez.GetSingle() : 0f;
		return new Vector3( x, y, z );
	}

	internal static Rotation ParseRotation( JsonElement e )
	{
		float pitch = e.TryGetProperty( "pitch", out var ep ) ? ep.GetSingle() : 0f;
		float yaw   = e.TryGetProperty( "yaw",   out var ey ) ? ey.GetSingle() : 0f;
		float roll  = e.TryGetProperty( "roll",  out var er ) ? er.GetSingle() : 0f;
		return Rotation.From( pitch, yaw, roll );
	}

	internal static object SerializeGo( GameObject go )
	{
		return new
		{
			id       = go.Id.ToString(),
			name     = go.Name,
			enabled  = go.Enabled,
			parent   = go.Parent?.Id.ToString(),
			position = new { go.WorldPosition.x, go.WorldPosition.y, go.WorldPosition.z },
			rotation = new { pitch = go.WorldRotation.Pitch(), yaw = go.WorldRotation.Yaw(), roll = go.WorldRotation.Roll() },
			scale    = new { go.WorldScale.x, go.WorldScale.y, go.WorldScale.z },
			components = go.Components.GetAll().Select( c => c.GetType().Name ).ToArray(),
			childCount = go.Children.Count
		};
	}

	internal static object SerializeGoTree( GameObject go )
	{
		return new
		{
			id         = go.Id.ToString(),
			name       = go.Name,
			enabled    = go.Enabled,
			components = go.Components.GetAll().Select( c => c.GetType().Name ).ToArray(),
			children   = go.Children.Select( c => SerializeGoTree( c ) ).ToArray()
		};
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 1 — File / project basics (unchanged)
// ═══════════════════════════════════════════════════════════════════

public class GetProjectInfoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var project = Project.Current;
		return Task.FromResult<object>( new
		{
			name       = project.Config.Title,
			org        = project.Config.Org,
			ident      = project.Config.Ident,
			type       = project.Config.Type,
			path       = project.GetRootPath(),
			assetsPath = project.GetAssetsPath()
		} );
	}
}

public class ListProjectFilesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var dir       = p.TryGetProperty( "path", out var d ) ? d.GetString() : "";
		var extension = p.TryGetProperty( "extension",  out var e ) ? e.GetString() : null;
		var recursive = !p.TryGetProperty( "recursive", out var rec ) || rec.GetBoolean();

		var searchDir = string.IsNullOrEmpty( dir )
			? rootPath
			: Path.Combine( rootPath, dir );

		if ( !Directory.Exists( searchDir ) )
			return Task.FromResult<object>( new { error = $"Directory not found: {dir}", files = Array.Empty<string>() } );

		var files = Directory.GetFiles( searchDir, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.Where( f => extension == null || f.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
			.Take( 500 )
			.ToArray();

		return Task.FromResult<object>( new { path = dir, count = files.Length, files } );
	}
}

public class ReadFileHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );
		return Task.FromResult<object>( new { path = filePath, content, length = content.Length } );
	}
}

public class WriteFileHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var content  = p.GetProperty( "content" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		File.WriteAllText( fullPath, content );
		return Task.FromResult<object>( new { path = filePath, written = true, length = content.Length } );
	}
}

public class CreateScriptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.GetProperty( "name" ).GetString();
		var template  = p.TryGetProperty( "template",  out var t ) ? t.GetString() : "component";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName  = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath  = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var className = Path.GetFileNameWithoutExtension( fileName );
		var code = template switch
		{
			"component" => $"using Sandbox;\n\npublic sealed class {className} : Component\n{{\n\tprotected override void OnUpdate()\n\t{{\n\t}}\n}}\n",
			"raw"       => p.TryGetProperty( "content", out var c ) ? c.GetString() : $"// {className}\n",
			_           => $"using Sandbox;\n\npublic sealed class {className} : Component\n{{\n}}\n",
		};

		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { path = $"{directory}/{fileName}", created = true, className } );
	}
}

public class EditScriptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );

		if ( p.TryGetProperty( "find", out var find ) && p.TryGetProperty( "replace", out var replace ) )
		{
			var findStr    = find.GetString();
			var replaceStr = replace.GetString();
			if ( !content.Contains( findStr ) )
				return Task.FromResult<object>( new { error = $"Text not found: {findStr}" } );

			content = content.Replace( findStr, replaceStr );
			File.WriteAllText( fullPath, content );
			return Task.FromResult<object>( new { path = filePath, edited = true, operation = "find_replace" } );
		}

		if ( p.TryGetProperty( "content", out var newContent ) )
		{
			File.WriteAllText( fullPath, newContent.GetString() );
			return Task.FromResult<object>( new { path = filePath, edited = true, operation = "overwrite" } );
		}

		return Task.FromResult<object>( new { error = "Provide 'find'/'replace' or 'content'" } );
	}
}

public class DeleteScriptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		File.Delete( fullPath );
		return Task.FromResult<object>( new { path = filePath, deleted = true } );
	}
}

public class ListScenesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var scenes = Directory.GetFiles( rootPath, "*.scene", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.ToArray();

		return Task.FromResult<object>( new { count = scenes.Length, scenes } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 2 — Scene file operations
// ═══════════════════════════════════════════════════════════════════

public class LoadSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scenePath = p.GetProperty( "path" ).GetString();
		var rootPath  = Project.Current.GetRootPath();

		// Try as relative path first, then absolute
		var fullPath = Path.IsPathRooted( scenePath )
			? scenePath
			: Path.GetFullPath( Path.Combine( rootPath, scenePath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Scene file not found: {scenePath}" } );

		try
		{
			// SceneFile is the resource type for .scene files
			var sceneFile = ResourceLibrary.Get<SceneFile>( scenePath );
			if ( sceneFile != null )
			{
				EditorScene.OpenScene( sceneFile );
				return Task.FromResult<object>( new { loaded = true, path = scenePath } );
			}
			return Task.FromResult<object>( new { error = "Could not load scene resource. Try using a path relative to the assets folder." } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to load scene: {ex.Message}" } );
		}
	}
}

public class SaveSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			EditorScene.SaveSession();
			return Task.FromResult<object>( new { saved = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to save scene: {ex.Message}" } );
		}
	}
}

public class CreateSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var name     = p.GetProperty( "name" ).GetString();
		var rootPath = Project.Current.GetRootPath();
		var subdir   = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Scenes";

		var fileName = name.EndsWith( ".scene" ) ? name : $"{name}.scene";
		var fullPath = Path.Combine( rootPath, subdir, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Scene already exists: {subdir}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		// Minimal valid s&box scene JSON
		var sceneJson = JsonSerializer.Serialize( new
		{
			__version = 0,
			__referencedFiles = Array.Empty<string>(),
			GameObjects = Array.Empty<object>()
		}, new JsonSerializerOptions { WriteIndented = true } );

		File.WriteAllText( fullPath, sceneJson );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 3 — GameObject CRUD
// ═══════════════════════════════════════════════════════════════════

public class CreateGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "GameObject";

		var go = scene.CreateObject( true );
		go.Name = name;

		if ( p.TryGetProperty( "position", out var pos ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( pos );

		if ( p.TryGetProperty( "rotation", out var rot ) )
			go.WorldRotation = ClaudeBridge.ParseRotation( rot );

		if ( p.TryGetProperty( "scale", out var scl ) )
			go.WorldScale = ClaudeBridge.ParseVector3( scl );

		if ( p.TryGetProperty( "parentId", out var pid ) && Guid.TryParse( pid.GetString(), out var parentGuid ) )
		{
			var parent = scene.Directory.FindByGuid( parentGuid );
			if ( parent != null )
				go.SetParent( parent, keepWorldPosition: true );
		}

		if ( p.TryGetProperty( "tags", out var tags ) && tags.ValueKind == JsonValueKind.Array )
		{
			foreach ( var tag in tags.EnumerateArray() )
				go.Tags.Add( tag.GetString() );
		}

		return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

public class DeleteGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var name = go.Name;
		go.Destroy();
		return Task.FromResult<object>( new { deleted = true, id, name } );
	}
}

public class DuplicateGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var clone = go.Clone();

		if ( p.TryGetProperty( "offset", out var off ) )
			clone.WorldPosition = go.WorldPosition + ClaudeBridge.ParseVector3( off );

		if ( p.TryGetProperty( "name", out var nm ) )
			clone.Name = nm.GetString();

		return Task.FromResult<object>( new { duplicated = true, original = id, gameObject = ClaudeBridge.SerializeGo( clone ) } );
	}
}

public class RenameGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var oldName = go.Name;
		go.Name = p.GetProperty( "name" ).GetString();
		return Task.FromResult<object>( new { renamed = true, id, oldName, newName = go.Name } );
	}
}

public class SetParentHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid child GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var keepWorld = !p.TryGetProperty( "keepWorldPosition", out var kw ) || kw.GetBoolean();

		// parentId == null → detach to root
		if ( p.TryGetProperty( "parentId", out var pid ) && pid.ValueKind != JsonValueKind.Null )
		{
			if ( !Guid.TryParse( pid.GetString(), out var parentGuid ) )
				return Task.FromResult<object>( new { error = "Invalid parent GUID" } );

			var parent = scene.Directory.FindByGuid( parentGuid );
			if ( parent == null )
				return Task.FromResult<object>( new { error = $"Parent not found: {pid.GetString()}" } );

			go.SetParent( parent, keepWorld );
			return Task.FromResult<object>( new { parented = true, id, parentId = pid.GetString() } );
		}

		go.SetParent( null, keepWorld );
		return Task.FromResult<object>( new { parented = true, id, parentId = (string)null } );
	}
}

public class SetEnabledHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var enabled = p.GetProperty( "enabled" ).GetBoolean();
		go.Enabled = enabled;
		return Task.FromResult<object>( new { id, enabled } );
	}
}

public class SetTransformHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var local = p.TryGetProperty( "local", out var lc ) && lc.GetBoolean();

		if ( p.TryGetProperty( "position", out var pos ) )
		{
			if ( local ) go.LocalPosition = ClaudeBridge.ParseVector3( pos );
			else         go.WorldPosition = ClaudeBridge.ParseVector3( pos );
		}

		if ( p.TryGetProperty( "rotation", out var rot ) )
		{
			if ( local ) go.LocalRotation = ClaudeBridge.ParseRotation( rot );
			else         go.WorldRotation = ClaudeBridge.ParseRotation( rot );
		}

		if ( p.TryGetProperty( "scale", out var scl ) )
		{
			if ( local ) go.LocalScale = ClaudeBridge.ParseVector3( scl );
			else         go.WorldScale  = ClaudeBridge.ParseVector3( scl );
		}

		return Task.FromResult<object>( new { transformed = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

public class GetSceneHierarchyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var roots = scene.Children
			.Select( go => ClaudeBridge.SerializeGoTree( go ) )
			.ToArray();

		return Task.FromResult<object>( new
		{
			sceneName = scene.Name,
			objectCount = scene.GetAllObjects( true ).Count(),
			hierarchy = roots
		} );
	}
}

public class GetSelectedObjectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var selected = SceneEditorSession.Active.Selection
			.OfType<GameObject>()
			.Select( go => ClaudeBridge.SerializeGo( go ) )
			.ToArray();

		return Task.FromResult<object>( new { count = selected.Length, selected } );
	}
}

public class SelectObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var add = p.TryGetProperty( "addToSelection", out var at ) && at.GetBoolean();
		if ( add )
			SceneEditorSession.Active.Selection.Add( go );
		else
			SceneEditorSession.Active.Selection.Set( go );

		return Task.FromResult<object>( new { selected = true, id } );
	}
}

public class FocusObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		// No dedicated focus API — select the object so the editor highlights it
		SceneEditorSession.Active.Selection.Set( go );
		return Task.FromResult<object>( new { focused = true, id, note = "Object selected in editor (no separate focus API)" } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 4 — Components
// ═══════════════════════════════════════════════════════════════════

public class GetPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var componentType = p.TryGetProperty( "component", out var ct ) ? ct.GetString() : null;
		var propertyName  = p.GetProperty( "property" ).GetString();

		var component = FindComponent( go, componentType );
		if ( component == null )
			return Task.FromResult<object>( new { error = $"Component not found: {componentType}" } );

		try
		{
			var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
			if ( propDesc == null )
				return Task.FromResult<object>( new { error = $"Property not found: {propertyName}" } );

			var value = propDesc.GetValue( component );
			return Task.FromResult<object>( new { id, component = component.GetType().Name, property = propertyName, value = value?.ToString() } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to get property: {ex.Message}" } );
		}
	}

	static Component FindComponent( GameObject go, string typeName )
	{
		if ( string.IsNullOrEmpty( typeName ) )
			return go.Components.GetAll().FirstOrDefault();

		return go.Components.GetAll()
			.FirstOrDefault( c => c.GetType().Name.Equals( typeName, StringComparison.OrdinalIgnoreCase ) );
	}
}

public class GetAllPropertiesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var result = new List<object>();
		foreach ( var component in go.Components.GetAll() )
		{
			var typeName = component.GetType().Name;
			var typeDesc = Game.TypeLibrary.GetType( typeName );
			var props = new List<object>();

			if ( typeDesc != null )
			{
				foreach ( var propDesc in typeDesc.Properties )
				{
					try
					{
						var value = propDesc.GetValue( component );
						props.Add( new { name = propDesc.Name, type = propDesc.PropertyType?.Name, value = value?.ToString() } );
					}
					catch { props.Add( new { name = propDesc.Name, type = propDesc.PropertyType?.Name, value = "<error>" } ); }
				}
			}

			result.Add( new { component = typeName, properties = props } );
		}

		return Task.FromResult<object>( new { id, components = result } );
	}
}

public class SetPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var componentType = p.TryGetProperty( "component", out var ct ) ? ct.GetString() : null;
		var propertyName  = p.GetProperty( "property" ).GetString();
		var valueStr      = p.GetProperty( "value" ).GetString();

		var component = go.Components.GetAll()
			.FirstOrDefault( c => string.IsNullOrEmpty( componentType ) ||
			                      c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );

		if ( component == null )
			return Task.FromResult<object>( new { error = $"Component not found: {componentType}" } );

		try
		{
			var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
			if ( propDesc == null )
				return Task.FromResult<object>( new { error = $"Property not found: {propertyName}" } );

			// Attempt type-safe conversion
			var propType = propDesc.PropertyType;
			object typedValue = propType?.Name switch
			{
				"Single"  or "float"  => float.Parse( valueStr ),
				"Double"  or "double" => double.Parse( valueStr ),
				"Int32"   or "int"    => int.Parse( valueStr ),
				"Boolean" or "bool"   => bool.Parse( valueStr ),
				"String"  or "string" => valueStr,
				_                     => valueStr
			};

			propDesc.SetValue( component, typedValue );
			return Task.FromResult<object>( new { set = true, id, component = component.GetType().Name, property = propertyName, value = valueStr } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set property: {ex.Message}" } );
		}
	}
}

public class ListAvailableComponentsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var filter = p.TryGetProperty( "filter", out var f ) ? f.GetString() : null;

		var types = Game.TypeLibrary.GetTypes<Component>()
			.Where( t => !t.IsAbstract )
			.Where( t => filter == null || t.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
			.Select( t => new { name = t.Name, title = t.Title, description = t.Description, fullName = t.FullName } )
			.OrderBy( t => t.name )
			.ToArray();

		return Task.FromResult<object>( new { count = types.Length, components = types } );
	}
}

public class AddComponentWithPropertiesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var typeName = p.GetProperty( "component" ).GetString();
		var typeDesc = Game.TypeLibrary.GetType( typeName );
		if ( typeDesc == null )
			return Task.FromResult<object>( new { error = $"Component type not found: {typeName}" } );

		try
		{
			var component = go.Components.Create( typeDesc );
			if ( component == null )
				return Task.FromResult<object>( new { error = "Failed to create component instance" } );

			// Apply optional property overrides
			if ( p.TryGetProperty( "properties", out var props ) && props.ValueKind == JsonValueKind.Object )
			{
				foreach ( var prop in props.EnumerateObject() )
				{
					try
					{
						var pd = typeDesc.Properties.FirstOrDefault( pp => pp.Name == prop.Name );
						if ( pd != null )
						{
							var propType = pd.PropertyType;
							object typedValue = propType?.Name switch
							{
								"Single"  or "float"  => float.Parse( prop.Value.GetString() ),
								"Double"  or "double" => double.Parse( prop.Value.GetString() ),
								"Int32"   or "int"    => int.Parse( prop.Value.GetString() ),
								"Boolean" or "bool"   => prop.Value.ValueKind == JsonValueKind.True,
								_                     => prop.Value.GetString()
							};
							pd.SetValue( component, typedValue );
						}
					}
					catch { /* best-effort property set */ }
				}
			}

			return Task.FromResult<object>( new { added = true, id, component = typeName } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add component: {ex.Message}" } );
		}
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 5 — Play mode
// ═══════════════════════════════════════════════════════════════════

public class StartPlayHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			SceneEditorSession.Active?.SetPlaying( SceneEditorSession.Active.Scene );
			return Task.FromResult<object>( new { started = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to start play: {ex.Message}" } );
		}
	}
}

public class StopPlayHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			SceneEditorSession.Active?.StopPlaying();
			return Task.FromResult<object>( new { stopped = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to stop play: {ex.Message}" } );
		}
	}
}

public class IsPlayingHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		return Task.FromResult<object>( new
		{
			isPlaying = Game.IsPlaying,
			isPaused  = Game.IsPaused
		} );
	}
}

public class GetRuntimePropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "Game is not currently playing" } );

		// Reuse GetPropertyHandler logic
		return new GetPropertyHandler().Execute( p );
	}
}

public class SetRuntimePropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "Game is not currently playing" } );

		return new SetPropertyHandler().Execute( p );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 6 — Assets
// ═══════════════════════════════════════════════════════════════════

public class SearchAssetsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var query     = p.TryGetProperty( "query",     out var q ) ? q.GetString() : null;
		var extension = p.TryGetProperty( "extension", out var e ) ? e.GetString() : null;

		var files = Directory.GetFiles( rootPath, "*.*", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.Where( f => extension == null || f.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
			.Where( f => query     == null || f.Contains( query, StringComparison.OrdinalIgnoreCase ) )
			.Take( 200 )
			.ToArray();

		return Task.FromResult<object>( new { count = files.Length, assets = files } );
	}
}

public class GetAssetInfoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Asset not found: {filePath}" } );

		var info = new FileInfo( fullPath );
		return Task.FromResult<object>( new
		{
			path      = filePath,
			name      = info.Name,
			extension = info.Extension,
			size      = info.Length,
			modified  = info.LastWriteTimeUtc.ToString( "o" )
		} );
	}
}

public class AssignModelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var modelPath = p.GetProperty( "model" ).GetString();
		var model = Model.Load( modelPath );
		if ( model == null )
			return Task.FromResult<object>( new { error = $"Model not found: {modelPath}" } );

		var renderer = go.GetOrAddComponent<ModelRenderer>();
		renderer.Model = model;
		return Task.FromResult<object>( new { assigned = true, id, model = modelPath } );
	}
}

public class CreateMaterialHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var name     = p.GetProperty( "name" ).GetString();
		var subdir   = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Materials";

		var fileName = name.EndsWith( ".vmat" ) ? name : $"{name}.vmat";
		var fullPath = Path.Combine( rootPath, subdir, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Material already exists: {subdir}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var shader = p.TryGetProperty( "shader", out var sh ) ? sh.GetString() : "shaders/simple.shader";
		var vmat = $"// THIS FILE IS AUTO-GENERATED\n\"Layer0\"\n{{\n\tshader \"{shader}\"\n\n\tF_SELF_ILLUM 0\n\n\tTextureColor \"materials/default/default.tga\"\n}}\n";

		File.WriteAllText( fullPath, vmat );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath } );
	}
}

public class AssignMaterialHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var materialPath = p.GetProperty( "material" ).GetString();
		var material = Material.Load( materialPath );
		if ( material == null )
			return Task.FromResult<object>( new { error = $"Material not found: {materialPath}" } );

		var renderer = go.GetComponent<ModelRenderer>();
		if ( renderer == null )
			return Task.FromResult<object>( new { error = "No ModelRenderer on GameObject" } );

		renderer.MaterialOverride = material;
		return Task.FromResult<object>( new { assigned = true, id, material = materialPath } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 7 — Audio
// ═══════════════════════════════════════════════════════════════════

public class ListSoundsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var sounds = Directory.GetFiles( rootPath, "*.sound", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.ToArray();

		return Task.FromResult<object>( new { count = sounds.Length, sounds } );
	}
}

public class CreateSoundEventHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var name     = p.GetProperty( "name" ).GetString();
		var subdir   = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Sounds";

		var fileName = name.EndsWith( ".sound" ) ? name : $"{name}.sound";
		var fullPath = Path.Combine( rootPath, subdir, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Sound already exists: {subdir}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var volume = p.TryGetProperty( "volume", out var v ) ? v.GetSingle() : 1.0f;
		var soundJson = JsonSerializer.Serialize( new
		{
			__version  = 0,
			Sounds     = Array.Empty<object>(),
			Volume     = volume,
			Pitch      = 1.0f,
			Attenuation = 1.0f
		}, new JsonSerializerOptions { WriteIndented = true } );

		File.WriteAllText( fullPath, soundJson );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 8 — Prefabs
// ═══════════════════════════════════════════════════════════════════

public class CreatePrefabHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var rootPath = Project.Current.GetRootPath();

		// If "path" is given use it directly, otherwise fall back to name+directory
		string fullPath;
		if ( p.TryGetProperty( "path", out var pathProp ) )
		{
			var prefabRelPath = pathProp.GetString();
			fullPath = Path.GetFullPath( Path.Combine( rootPath, prefabRelPath ) );
		}
		else
		{
			var name   = p.TryGetProperty( "name", out var n ) ? n.GetString() : go.Name;
			var subdir = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Prefabs";
			var fileName = name.EndsWith( ".prefab" ) ? name : $"{name}.prefab";
			fullPath = Path.Combine( rootPath, subdir, fileName );
		}
		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		// Serialize a minimal prefab descriptor referencing the GameObject
		var prefabJson = JsonSerializer.Serialize( new
		{
			__version  = 0,
			RootObject = new
			{
				Id         = go.Id.ToString(),
				Name       = go.Name,
				Enabled    = go.Enabled,
				Components = go.Components.GetAll().Select( c => new { Type = c.GetType().Name } ).ToArray()
			}
		}, new JsonSerializerOptions { WriteIndented = true } );

		File.WriteAllText( fullPath, prefabJson );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath, sourceId = id } );
	}
}

public class InstantiatePrefabHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var prefabPath = p.GetProperty( "path" ).GetString();
		var rootPath   = Project.Current.GetRootPath();
		var fullPath   = Path.GetFullPath( Path.Combine( rootPath, prefabPath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Prefab not found: {prefabPath}" } );

		try
		{
			// Read the prefab to get the name
			var json      = File.ReadAllText( fullPath );
			using var doc = JsonDocument.Parse( json );
			var prefabName = doc.RootElement
				.TryGetProperty( "RootObject", out var ro ) &&
				ro.TryGetProperty( "Name", out var nm )
				? nm.GetString()
				: Path.GetFileNameWithoutExtension( prefabPath );

			// Create a new GO mirroring the prefab descriptor
			var go = scene.CreateObject( true );
			go.Name = prefabName;

			if ( p.TryGetProperty( "position", out var pos ) )
				go.WorldPosition = ClaudeBridge.ParseVector3( pos );

			if ( p.TryGetProperty( "rotation", out var rot ) )
				go.WorldRotation = ClaudeBridge.ParseRotation( rot );

			return Task.FromResult<object>( new
			{
				instantiated = true,
				prefab       = prefabPath,
				gameObject   = ClaudeBridge.SerializeGo( go ),
				note         = "Basic instantiation — full prefab resource loading requires s&box prefab asset pipeline"
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to instantiate prefab: {ex.Message}" } );
		}
	}
}

public class ListPrefabsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var prefabs = Directory.GetFiles( rootPath, "*.prefab", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.ToArray();

		return Task.FromResult<object>( new { count = prefabs.Length, prefabs } );
	}
}

public class GetPrefabInfoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath   = Project.Current.GetRootPath();
		var prefabPath = p.GetProperty( "path" ).GetString();
		var fullPath   = Path.GetFullPath( Path.Combine( rootPath, prefabPath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Prefab not found: {prefabPath}" } );

		var content = File.ReadAllText( fullPath );
		var info    = new FileInfo( fullPath );
		return Task.FromResult<object>( new
		{
			path     = prefabPath,
			name     = info.Name,
			size     = info.Length,
			modified = info.LastWriteTimeUtc.ToString( "o" ),
			content
		} );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 9 — Physics
// ═══════════════════════════════════════════════════════════════════

public class AddPhysicsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var rb = go.GetOrAddComponent<Rigidbody>();

		if ( p.TryGetProperty( "gravity", out var g ) ) rb.Gravity      = g.GetBoolean();
		if ( p.TryGetProperty( "mass",    out var m ) ) rb.MassOverride = m.GetSingle();

		var colliderType = p.TryGetProperty( "collider", out var ct ) ? ct.GetString() : "box";
		var added = new List<string> { "Rigidbody" };

		switch ( colliderType.ToLower() )
		{
			case "sphere":
				var sphere = go.GetOrAddComponent<SphereCollider>();
				if ( p.TryGetProperty( "radius", out var r ) ) sphere.Radius = r.GetSingle();
				added.Add( "SphereCollider" );
				break;
			case "capsule":
				go.GetOrAddComponent<CapsuleCollider>();
				added.Add( "CapsuleCollider" );
				break;
			default: // "box"
				var box = go.GetOrAddComponent<BoxCollider>();
				if ( p.TryGetProperty( "scale", out var s ) ) box.Scale = ClaudeBridge.ParseVector3( s );
				added.Add( "BoxCollider" );
				break;
		}

		return Task.FromResult<object>( new { physicsAdded = true, id, components = added } );
	}
}

public class AddColliderHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var colliderType = p.TryGetProperty( "type", out var ct ) ? ct.GetString() : "box";
		var isTrigger    = p.TryGetProperty( "isTrigger", out var it ) && it.GetBoolean();

		string addedType;
		switch ( colliderType.ToLower() )
		{
			case "sphere":
				var sphere = go.GetOrAddComponent<SphereCollider>();
				if ( p.TryGetProperty( "radius", out var r ) ) sphere.Radius = r.GetSingle();
				sphere.IsTrigger = isTrigger;
				addedType = "SphereCollider";
				break;
			case "capsule":
				var cap = go.GetOrAddComponent<CapsuleCollider>();
				cap.IsTrigger = isTrigger;
				addedType = "CapsuleCollider";
				break;
			case "mesh":
				var mesh = go.GetOrAddComponent<HullCollider>();
				mesh.IsTrigger = isTrigger;
				addedType = "HullCollider";
				break;
			default: // "box"
				var box = go.GetOrAddComponent<BoxCollider>();
				if ( p.TryGetProperty( "scale", out var s ) ) box.Scale = ClaudeBridge.ParseVector3( s );
				box.IsTrigger = isTrigger;
				addedType = "BoxCollider";
				break;
		}

		return Task.FromResult<object>( new { added = true, id, collider = addedType, isTrigger } );
	}
}

public class RaycastHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var start = ClaudeBridge.ParseVector3( p.GetProperty( "start" ) );
		var end   = ClaudeBridge.ParseVector3( p.GetProperty( "end" ) );

		try
		{
			var tr = scene.Trace.Ray( start, end ).Run();

			return Task.FromResult<object>( new
			{
				hit          = tr.Hit,
				hitPosition  = tr.Hit ? new { tr.HitPosition.x, tr.HitPosition.y, tr.HitPosition.z } : null,
				normal       = tr.Hit ? new { tr.Normal.x, tr.Normal.y, tr.Normal.z } : null,
				distance     = tr.Distance,
				gameObjectId = tr.GameObject?.Id.ToString(),
				gameObjectName = tr.GameObject?.Name
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Raycast failed: {ex.Message}" } );
		}
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 10 — Code templates
// ═══════════════════════════════════════════════════════════════════

public class CreatePlayerControllerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "PlayerController";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	[Property] public float MoveSpeed {{ get; set; }} = 200f;
	[Property] public float JumpForce {{ get; set; }} = 400f;

	private CharacterController _controller;

	protected override void OnStart()
	{{
		_controller = GetOrAddComponent<CharacterController>();
	}}

	protected override void OnUpdate()
	{{
		if ( _controller == null ) return;

		var move = new Vector3(
			Input.AnalogMove.x,
			0,
			Input.AnalogMove.y
		) * MoveSpeed;

		if ( _controller.IsOnGround && Input.Pressed( ""jump"" ) )
			_controller.Punch( Vector3.Up * JumpForce );

		_controller.Accelerate( move );
		_controller.ApplyFriction( 10f );
		_controller.Move();
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateNpcControllerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NpcController";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	[Property] public float MoveSpeed    {{ get; set; }} = 100f;
	[Property] public float DetectRadius {{ get; set; }} = 500f;
	[Property] public GameObject Target  {{ get; set; }}

	private NavMeshAgent _agent;

	protected override void OnStart()
	{{
		_agent = GetOrAddComponent<NavMeshAgent>();
	}}

	protected override void OnUpdate()
	{{
		if ( Target == null || _agent == null ) return;

		float dist = Vector3.DistanceBetween( WorldPosition, Target.WorldPosition );
		if ( dist < DetectRadius )
			_agent.MoveTo( Target.WorldPosition );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateGameManagerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "GameManager";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component, Component.INetworkListener
{{
	public static {className} Instance {{ get; private set; }}

	[Property] public int MaxPlayers {{ get; set; }} = 16;
	[Property] public string GameState {{ get; set; }} = ""waiting"";

	protected override void OnStart()
	{{
		Instance = this;
		Log.Info( $""[{className}] Started. State: {{GameState}}"" );
	}}

	protected override void OnDestroy()
	{{
		if ( Instance == this ) Instance = null;
	}}

	public void OnActive( Connection channel )
	{{
		Log.Info( $""[{className}] Player connected: {{channel.DisplayName}}"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateTriggerZoneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "TriggerZone";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component, Component.ITriggerListener
{{
	[Property] public string TriggerTag {{ get; set; }} = ""player"";

	protected override void OnStart()
	{{
		var collider = GetOrAddComponent<BoxCollider>();
		collider.IsTrigger = true;
	}}

	public void OnTriggerEnter( Collider other )
	{{
		if ( other.GameObject.Tags.Has( TriggerTag ) )
			OnPlayerEnter( other.GameObject );
	}}

	public void OnTriggerExit( Collider other )
	{{
		if ( other.GameObject.Tags.Has( TriggerTag ) )
			OnPlayerExit( other.GameObject );
	}}

	private void OnPlayerEnter( GameObject player )
	{{
		Log.Info( $""[{className}] {{player.Name}} entered trigger"" );
	}}

	private void OnPlayerExit( GameObject player )
	{{
		Log.Info( $""[{className}] {{player.Name}} exited trigger"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 11 — UI
// ═══════════════════════════════════════════════════════════════════

public class CreateRazorUIHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.GetProperty( "name" ).GetString();
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "UI";

		var fileName = name.EndsWith( ".razor" ) ? name : $"{name}.razor";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var componentName = Path.GetFileNameWithoutExtension( fileName );
		var razor = $@"@using Sandbox;
@using Sandbox.UI;

@namespace {componentName}

<root class=""{componentName.ToLower()}"">
	<div class=""container"">
		<label>@Title</label>
	</div>
</root>

@code {{
	[Property] public string Title {{ get; set; }} = ""{componentName}"";
}}
";
		File.WriteAllText( fullPath, razor );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath, componentName } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 12 — Networking
// ═══════════════════════════════════════════════════════════════════

public class NetworkSpawnHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		try
		{
			go.NetworkSpawn();
			return Task.FromResult<object>( new { spawned = true, id } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"NetworkSpawn failed: {ex.Message}" } );
		}
	}
}

public class AddSyncPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath     = Project.Current.GetRootPath();
		var filePath     = p.GetProperty( "path" ).GetString();
		var propertyName = p.GetProperty( "propertyName" ).GetString();
		var propertyType = p.TryGetProperty( "propertyType", out var ptProp ) ? ptProp.GetString() ?? "float" : "float";
		var defaultValue = p.TryGetProperty( "defaultValue", out var dvProp ) ? dvProp.GetString() : null;
		var fullPath     = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );

		// Find the property declaration and add [Sync] above it if not already present
		var propPattern = $"public ";
		var propIndex   = content.IndexOf( $"public ", StringComparison.Ordinal );

		// More targeted: find the specific property
		var searchStr = $"public.*{propertyName}";
		var lines     = content.Split( '\n' ).ToList();
		bool modified = false;

		for ( int i = 0; i < lines.Count; i++ )
		{
			if ( lines[i].Contains( propertyName ) && lines[i].Contains( "public" ) && lines[i].Contains( "{" ) )
			{
				if ( i > 0 && lines[i - 1].TrimStart().StartsWith( "[Sync]" ) )
				{
					return Task.FromResult<object>( new { error = $"Property '{propertyName}' already has [Sync]" } );
				}

				var indent = new string( '\t', lines[i].TakeWhile( c => c == '\t' ).Count() );
				lines.Insert( i, $"{indent}[Sync]" );
				modified = true;
				break;
			}
		}

		if ( !modified )
			return Task.FromResult<object>( new { error = $"Property '{propertyName}' not found in file" } );

		File.WriteAllText( fullPath, string.Join( '\n', lines ) );
		return Task.FromResult<object>( new { added = true, path = filePath, property = propertyName, attribute = "[Sync]" } );
	}
}

public class AddRpcMethodHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath   = Project.Current.GetRootPath();
		var filePath   = p.GetProperty( "path" ).GetString();
		var methodName = p.TryGetProperty( "methodName", out var m ) ? m.GetString() : "MyRpc";
		var rpcType    = p.TryGetProperty( "rpcType", out var rt ) ? rt.GetString() : "Broadcast";
		var fullPath   = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );

		// Insert new RPC method before the last closing brace of the class
		var lastBrace = content.LastIndexOf( '}' );
		if ( lastBrace < 0 )
			return Task.FromResult<object>( new { error = "Could not find closing brace in file" } );

		var rpcAttr = rpcType.ToLower() switch
		{
			"owner"  => "[Rpc.Owner]",
			"host"   => "[Rpc.Host]",
			_        => "[Rpc.Broadcast]"
		};

		var methodCode = $"\n\t{rpcAttr}\n\tpublic void {methodName}()\n\t{{\n\t\t// TODO: implement RPC\n\t}}\n";
		content = content.Insert( lastBrace, methodCode );
		File.WriteAllText( fullPath, content );

		return Task.FromResult<object>( new { added = true, path = filePath, method = methodName, attribute = rpcAttr } );
	}
}

public class CreateNetworkedPlayerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NetworkedPlayer";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	[Sync] public string PlayerName {{ get; set; }}
	[Sync] public int    Health     {{ get; set; }} = 100;

	[Property] public float MoveSpeed {{ get; set; }} = 200f;

	private CharacterController _controller;

	protected override void OnStart()
	{{
		_controller = GetOrAddComponent<CharacterController>();

		if ( IsProxy ) return;

		PlayerName = Connection.Local.DisplayName;
		Health     = 100;
	}}

	protected override void OnUpdate()
	{{
		if ( IsProxy || _controller == null ) return;

		var move = new Vector3(
			Input.AnalogMove.x,
			0,
			Input.AnalogMove.y
		) * MoveSpeed;

		_controller.Accelerate( move );
		_controller.ApplyFriction( 10f );
		_controller.Move();
	}}

	[Rpc.Broadcast]
	public void TakeDamage( int amount )
	{{
		Health -= amount;
		if ( Health <= 0 )
			Log.Info( $""{{PlayerName}} died!"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateLobbyManagerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "LobbyManager";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;
using System.Collections.Generic;

public sealed class {className} : Component, Component.INetworkListener
{{
	public static {className} Instance {{ get; private set; }}

	[Sync] public int PlayerCount {{ get; private set; }}

	[Property] public int     MaxPlayers  {{ get; set; }} = 16;
	[Property] public string  LobbyState  {{ get; set; }} = ""waiting"";

	private readonly List<Connection> _players = new();

	protected override void OnStart()
	{{
		Instance = this;
	}}

	protected override void OnDestroy()
	{{
		if ( Instance == this ) Instance = null;
	}}

	public void OnActive( Connection channel )
	{{
		_players.Add( channel );
		PlayerCount = _players.Count;
		Log.Info( $""[{className}] {{channel.DisplayName}} joined. Players: {{PlayerCount}}/{{MaxPlayers}}"" );

		if ( PlayerCount >= MaxPlayers )
			StartGame();
	}}

	public void OnDisconnected( Connection channel )
	{{
		_players.Remove( channel );
		PlayerCount = _players.Count;
	}}

	private void StartGame()
	{{
		LobbyState = ""playing"";
		Log.Info( $""[{className}] Game starting!"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateNetworkEventsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NetworkEvents";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	/// <summary>Broadcasts a named event to all connected clients.</summary>
	[Rpc.Broadcast]
	public void SendEvent( string eventName, string payload )
	{{
		Log.Info( $""[{className}] Event '{{eventName}}' received with payload: {{payload}}"" );
		OnNetworkEvent( eventName, payload );
	}}

	/// <summary>Sends an event only to the host.</summary>
	[Rpc.Host]
	public void SendEventToHost( string eventName, string payload )
	{{
		Log.Info( $""[{className}] Host received event '{{eventName}}'"" );
		OnNetworkEvent( eventName, payload );
	}}

	private void OnNetworkEvent( string eventName, string payload )
	{{
		// Dispatch locally — extend this switch to handle specific events
		switch ( eventName )
		{{
			case ""player_scored"":
				Log.Info( $""Player scored: {{payload}}"" );
				break;
			default:
				Log.Info( $""Unhandled event: {{eventName}}"" );
				break;
		}}
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 13 — Publishing / config
// ═══════════════════════════════════════════════════════════════════

public class GetProjectConfigHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var sbproj   = Directory.GetFiles( rootPath, "*.sbproj", SearchOption.TopDirectoryOnly ).FirstOrDefault();

		if ( sbproj == null )
			return Task.FromResult<object>( new { error = ".sbproj file not found in project root" } );

		var content = File.ReadAllText( sbproj );
		return Task.FromResult<object>( new
		{
			path    = Path.GetRelativePath( rootPath, sbproj ).Replace( '\\', '/' ),
			content,
			project = new
			{
				title = Project.Current.Config.Title,
				org   = Project.Current.Config.Org,
				ident = Project.Current.Config.Ident,
				type  = Project.Current.Config.Type
			}
		} );
	}
}

public class SetProjectConfigHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var sbproj   = Directory.GetFiles( rootPath, "*.sbproj", SearchOption.TopDirectoryOnly ).FirstOrDefault();

		if ( sbproj == null )
			return Task.FromResult<object>( new { error = ".sbproj file not found in project root" } );

		var content = File.ReadAllText( sbproj );

		// Apply find/replace pairs from the "changes" object
		if ( p.TryGetProperty( "changes", out var changes ) && changes.ValueKind == JsonValueKind.Object )
		{
			foreach ( var change in changes.EnumerateObject() )
			{
				// Replace JSON string values by key name pattern
				var searchPattern = $"\"{change.Name}\":";
				var idx = content.IndexOf( searchPattern, StringComparison.OrdinalIgnoreCase );
				if ( idx >= 0 )
				{
					// find the value start
					var valueStart = content.IndexOf( '"', idx + searchPattern.Length );
					var valueEnd   = content.IndexOf( '"', valueStart + 1 );
					if ( valueStart >= 0 && valueEnd > valueStart )
					{
						content = content.Substring( 0, valueStart + 1 )
						        + change.Value.GetString()
						        + content.Substring( valueEnd );
					}
				}
			}
			File.WriteAllText( sbproj, content );
		}
		else if ( p.TryGetProperty( "content", out var newContent ) )
		{
			File.WriteAllText( sbproj, newContent.GetString() );
		}
		else
		{
			return Task.FromResult<object>( new { error = "Provide 'changes' object or 'content' string" } );
		}

		return Task.FromResult<object>( new { updated = true, path = Path.GetRelativePath( rootPath, sbproj ).Replace( '\\', '/' ) } );
	}
}

public class ValidateProjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var issues   = new List<string>();
		var checks   = new List<object>();

		// Check for .sbproj
		var sbproj = Directory.GetFiles( rootPath, "*.sbproj", SearchOption.TopDirectoryOnly ).FirstOrDefault();
		var hasSbproj = sbproj != null;
		checks.Add( new { check = "sbproj_exists", pass = hasSbproj, detail = hasSbproj ? sbproj : "No .sbproj found" } );
		if ( !hasSbproj ) issues.Add( "Missing .sbproj file" );

		// Check for at least one scene
		var sceneCount = Directory.GetFiles( rootPath, "*.scene", SearchOption.AllDirectories ).Length;
		checks.Add( new { check = "has_scenes", pass = sceneCount > 0, detail = $"{sceneCount} scene(s) found" } );
		if ( sceneCount == 0 ) issues.Add( "No .scene files found" );

		// Check project ident
		var hasIdent = !string.IsNullOrEmpty( Project.Current.Config.Ident );
		checks.Add( new { check = "has_ident", pass = hasIdent, detail = hasIdent ? Project.Current.Config.Ident : "No ident set" } );
		if ( !hasIdent ) issues.Add( "Project Ident not set" );

		// Check project title
		var hasTitle = !string.IsNullOrEmpty( Project.Current.Config.Title );
		checks.Add( new { check = "has_title", pass = hasTitle, detail = hasTitle ? Project.Current.Config.Title : "No title set" } );
		if ( !hasTitle ) issues.Add( "Project Title not set" );

		var valid = issues.Count == 0;
		return Task.FromResult<object>( new { valid, issueCount = issues.Count, issues, checks } );
	}
}

public class SetProjectThumbnailHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath   = Project.Current.GetRootPath();
		var sourcePath = p.GetProperty( "sourcePath" ).GetString();
		var fullSource = Path.GetFullPath( Path.Combine( rootPath, sourcePath ) );

		if ( !File.Exists( fullSource ) )
			return Task.FromResult<object>( new { error = $"Source image not found: {sourcePath}" } );

		var ext  = Path.GetExtension( fullSource ).ToLower();
		if ( ext != ".png" && ext != ".jpg" && ext != ".jpeg" )
			return Task.FromResult<object>( new { error = "Thumbnail must be a .png or .jpg file" } );

		var thumbDest = Path.Combine( rootPath, "thumb.png" );
		File.Copy( fullSource, thumbDest, overwrite: true );

		return Task.FromResult<object>( new { set = true, thumbnail = "thumb.png" } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 15 — New handlers (joints, sound, UI panels, undo/redo,
//             networking helpers, packages, assets, screenshot, hotload)
// ═══════════════════════════════════════════════════════════════════

public class AddJointHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var jointType = p.TryGetProperty( "type", out var jt ) ? jt.GetString() : "fixed";

		// Resolve optional target body
		GameObject targetGo = null;
		if ( p.TryGetProperty( "targetId", out var tid ) && Guid.TryParse( tid.GetString(), out var targetGuid ) )
			targetGo = scene.Directory.FindByGuid( targetGuid );

		try
		{
			string addedType;
			switch ( jointType?.ToLower() )
			{
				case "spring":
				{
					var joint = go.AddComponent<SpringJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					if ( p.TryGetProperty( "frequency", out var freq ) ) joint.Frequency = freq.GetSingle();
					if ( p.TryGetProperty( "damping",   out var damp ) ) joint.Damping   = damp.GetSingle();
					addedType = "SpringJoint";
					break;
				}
				case "hinge":
				{
					var joint = go.AddComponent<HingeJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					addedType = "HingeJoint";
					break;
				}
				case "slider":
				{
					var joint = go.AddComponent<SliderJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					addedType = "SliderJoint";
					break;
				}
				default: // "fixed"
				{
					var joint = go.AddComponent<FixedJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					addedType = "FixedJoint";
					break;
				}
			}
			return Task.FromResult<object>( new { added = true, id, joint = addedType, targetId = targetGo?.Id.ToString() } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add joint: {ex.Message}" } );
		}
	}
}

public class AssignSoundHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var soundPath  = p.GetProperty( "sound" ).GetString();
		var playOnStart = p.TryGetProperty( "playOnStart", out var pos ) && pos.GetBoolean();

		try
		{
			var spc = go.GetOrAddComponent<SoundPointComponent>();

			// Load the SoundEvent from the path and assign it
			var soundEvent = ResourceLibrary.Get<SoundEvent>( soundPath );
			if ( soundEvent != null )
				spc.SoundEvent = soundEvent;

			if ( playOnStart )
				spc.StartSound();

			return Task.FromResult<object>( new
			{
				assigned    = true,
				id,
				sound       = soundPath,
				soundLoaded = soundEvent != null,
				playOnStart
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to assign sound: {ex.Message}" } );
		}
	}
}

public class PlaySoundPreviewHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var eventName = p.GetProperty( "sound" ).GetString();
		var volume    = p.TryGetProperty( "volume", out var v ) ? v.GetSingle() : 1.0f;

		try
		{
			var handle = Sound.Play( eventName );
			return Task.FromResult<object>( new { playing = true, sound = eventName, volume } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to play sound: {ex.Message}" } );
		}
	}
}

public class SetMaterialPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var renderer = go.GetComponent<ModelRenderer>();
		if ( renderer == null )
			return Task.FromResult<object>( new { error = "No ModelRenderer on GameObject" } );

		var propertyName = p.GetProperty( "property" ).GetString();
		var value        = p.GetProperty( "value" );

		try
		{
			// Ensure we have a mutable material override
			var mat = renderer.MaterialOverride;
			if ( mat == null )
				return Task.FromResult<object>( new { error = "No MaterialOverride set — assign a material first via assign_material" } );

			// Apply the property based on the JSON value kind
			switch ( value.ValueKind )
			{
				case JsonValueKind.Number:
					mat.Set( propertyName, value.GetSingle() );
					break;
				case JsonValueKind.True:
				case JsonValueKind.False:
					mat.Set( propertyName, value.GetBoolean() ? 1f : 0f );
					break;
				case JsonValueKind.Object:
					// Try to interpret as Color (r,g,b,a) or Vector3 (x,y,z)
					if ( value.TryGetProperty( "r", out var cr ) )
					{
						float r = cr.GetSingle();
						float g = value.TryGetProperty( "g", out var cg ) ? cg.GetSingle() : 0f;
						float b = value.TryGetProperty( "b", out var cb ) ? cb.GetSingle() : 0f;
						float a = value.TryGetProperty( "a", out var ca ) ? ca.GetSingle() : 1f;
						mat.Set( propertyName, new Color( r, g, b, a ) );
					}
					else
					{
						float x = value.TryGetProperty( "x", out var vx ) ? vx.GetSingle() : 0f;
						float y = value.TryGetProperty( "y", out var vy ) ? vy.GetSingle() : 0f;
						float z = value.TryGetProperty( "z", out var vz ) ? vz.GetSingle() : 0f;
						mat.Set( propertyName, new Vector3( x, y, z ) );
					}
					break;
				default:
					mat.Set( propertyName, value.GetString() );
					break;
			}

			return Task.FromResult<object>( new { set = true, id, property = propertyName } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set material property: {ex.Message}" } );
		}
	}
}

public class AddScreenPanelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var name   = p.TryGetProperty( "name",   out var n  ) ? n.GetString()  : "Screen Panel";
		var zIndex = p.TryGetProperty( "zIndex", out var zi ) ? zi.GetInt32()  : 0;

		// Resolve optional parent
		GameObject parentGo = null;
		if ( p.TryGetProperty( "parent", out var par ) && Guid.TryParse( par.GetString(), out var parGuid ) )
			parentGo = scene.Directory.FindByGuid( parGuid );

		try
		{
			var go = scene.CreateObject( true );
			go.Name = name;

			if ( parentGo != null )
				go.SetParent( parentGo, false );

			var panel = go.AddComponent<ScreenPanel>();
			panel.ZIndex = zIndex;

			// Optionally add a named panel component type
			if ( p.TryGetProperty( "panelComponent", out var pc ) )
			{
				var typeName = pc.GetString();
				if ( !string.IsNullOrEmpty( typeName ) )
				{
					var typeDesc = Game.TypeLibrary.GetType( typeName );
					if ( typeDesc != null )
						go.Components.Create( typeDesc );
				}
			}

			return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add ScreenPanel: {ex.Message}" } );
		}
	}
}

public class AddWorldPanelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var name          = p.TryGetProperty( "name",          out var n   ) ? n.GetString()    : "World Panel";
		var lookAtCamera  = p.TryGetProperty( "lookAtCamera",  out var lac ) && lac.GetBoolean();

		// Resolve optional parent
		GameObject parentGo = null;
		if ( p.TryGetProperty( "parent", out var par ) && Guid.TryParse( par.GetString(), out var parGuid ) )
			parentGo = scene.Directory.FindByGuid( parGuid );

		try
		{
			var go = scene.CreateObject( true );
			go.Name = name;

			if ( parentGo != null )
				go.SetParent( parentGo, false );

			if ( p.TryGetProperty( "position", out var pos ) )
				go.WorldPosition = ClaudeBridge.ParseVector3( pos );

			if ( p.TryGetProperty( "rotation", out var rot ) )
				go.WorldRotation = ClaudeBridge.ParseRotation( rot );

			if ( p.TryGetProperty( "worldScale", out var ws ) )
				go.WorldScale = ClaudeBridge.ParseVector3( ws );

			var panel = go.AddComponent<WorldPanel>();
			panel.LookAtCamera = lookAtCamera;

			// Optionally add a named panel component type
			if ( p.TryGetProperty( "panelComponent", out var pc ) )
			{
				var typeName = pc.GetString();
				if ( !string.IsNullOrEmpty( typeName ) )
				{
					var typeDesc = Game.TypeLibrary.GetType( typeName );
					if ( typeDesc != null )
						go.Components.Create( typeDesc );
				}
			}

			return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add WorldPanel: {ex.Message}" } );
		}
	}
}

public class UndoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			SceneEditorSession.Active?.UndoSystem?.Undo();
			return Task.FromResult<object>( new { undone = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Undo failed: {ex.Message}" } );
		}
	}
}

public class RedoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			SceneEditorSession.Active?.UndoSystem?.Redo();
			return Task.FromResult<object>( new { redone = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Redo failed: {ex.Message}" } );
		}
	}
}

public class AddNetworkHelperHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var name = p.TryGetProperty( "name", out var n ) ? n.GetString() : null;
		if ( name != null ) go.Name = name;

		try
		{
			var helper = go.GetOrAddComponent<NetworkHelper>();
			helper.StartServer = true;

			return Task.FromResult<object>( new { added = true, id, component = "NetworkHelper" } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add NetworkHelper: {ex.Message}" } );
		}
	}
}

public class ConfigureNetworkHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			// Networking.MaxPlayers is read-only — set via lobby config
			if ( p.TryGetProperty( "lobbyName",   out var ln ) ) Networking.ServerName  = ln.GetString();

			return Task.FromResult<object>( new
			{
				configured   = true,
				maxPlayers   = Networking.MaxPlayers,
				serverName   = Networking.ServerName
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to configure network: {ex.Message}" } );
		}
	}
}

public class GetNetworkStatusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			return Task.FromResult<object>( new
			{
				isActive      = Networking.IsActive,
				isHost        = Networking.IsHost,
				isClient      = Networking.IsClient,
				isConnecting  = Networking.IsConnecting,
				maxPlayers    = Networking.MaxPlayers,
				serverName    = Networking.ServerName
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to get network status: {ex.Message}" } );
		}
	}
}

public class SetOwnershipHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var connectionId = p.TryGetProperty( "connectionId", out var cid ) ? cid.GetString() : null;

		try
		{
			if ( string.IsNullOrEmpty( connectionId ) )
			{
				go.Network.DropOwnership();
				return Task.FromResult<object>( new { ownershipDropped = true, id } );
			}
			else
			{
				// Find connection by steam ID or display name
				var conn = Connection.All.FirstOrDefault( c =>
					c.SteamId.ToString() == connectionId ||
					c.Id.ToString()      == connectionId );

				if ( conn == null )
					return Task.FromResult<object>( new { error = $"Connection not found: {connectionId}" } );

				go.Network.AssignOwnership( conn );
				return Task.FromResult<object>( new { ownershipAssigned = true, id, connectionId } );
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set ownership: {ex.Message}" } );
		}
	}
}

public class GetPackageDetailsHandler : IBridgeHandler
{
	public async Task<object> Execute( JsonElement p )
	{
		var ident = p.GetProperty( "ident" ).GetString();

		try
		{
			var pkg = await Package.FetchAsync( ident, false );
			if ( pkg == null )
				return new { error = $"Package not found: {ident}" };

			return new
			{
				fullIdent   = pkg.FullIdent,
				title       = pkg.Title,
				summary     = pkg.Summary,
				description = pkg.Description,
				org         = pkg.Org
			};
		}
		catch ( Exception ex )
		{
			return new { error = $"Failed to fetch package: {ex.Message}" };
		}
	}
}

public class InstallAssetHandler : IBridgeHandler
{
	public async Task<object> Execute( JsonElement p )
	{
		var ident = p.GetProperty( "ident" ).GetString();

		try
		{
			var asset = await AssetSystem.InstallAsync( ident, true );
			if ( asset == null )
				return new { error = $"Failed to install asset: {ident}" };

			return new
			{
				installed     = true,
				ident,
				name          = asset.Name,
				path          = asset.Path,
				relativePath  = asset.RelativePath
			};
		}
		catch ( Exception ex )
		{
			return new { error = $"Failed to install asset: {ex.Message}" };
		}
	}
}

public class ListAssetLibraryHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var query      = p.TryGetProperty( "query",      out var q  ) ? q.GetString()  : null;
		var typeFilter = p.TryGetProperty( "type",       out var tf ) ? tf.GetString() : null;
		var maxResults = p.TryGetProperty( "maxResults", out var mr ) ? mr.GetInt32()  : 200;

		try
		{
			var assets = AssetSystem.All
				.Where( a => query == null || a.Name.Contains( query, StringComparison.OrdinalIgnoreCase ) )
				.Where( a => typeFilter == null || a.AssetType?.ToString().Contains( typeFilter, StringComparison.OrdinalIgnoreCase ) == true )
				.Take( maxResults )
				.Select( a => new
				{
					name         = a.Name,
					path         = a.Path,
					relativePath = a.RelativePath,
					assetType    = a.AssetType?.ToString()
				} )
				.ToArray();

			return Task.FromResult<object>( new { count = assets.Length, assets } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to list asset library: {ex.Message}" } );
		}
	}
}

public class TakeScreenshotHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var path = p.TryGetProperty( "path", out var pt ) ? pt.GetString() : null;

		try
		{
			EditorScene.TakeHighResScreenshot( 1920, 1080 );
			return Task.FromResult<object>( new
			{
				taken = true,
				note  = "Screenshot taken via EditorScene.TakeHighResScreenshot(1920, 1080)",
				path  = path ?? "<default editor location>"
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to take screenshot: {ex.Message}" } );
		}
	}
}

public class TriggerHotloadHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		return Task.FromResult<object>( new
		{
			message = "Hotload is automatic in s&box when files change. Save a .cs file to trigger recompilation.",
			note    = "No manual hotload API is available. Modify a script file to trigger a hotload."
		} );
	}
}

// ═══════════════════════════════════════════════════════════════════
// Main-thread poller — ensures scene APIs run on the editor thread
// ═══════════════════════════════════════════════════════════════════

[Dock( "Editor", "Claude Bridge", "smart_toy" )]
public class BridgePoller : Widget
{
	public BridgePoller( Widget parent ) : base( parent )
	{
		MinimumSize = 64;
	}

	[EditorEvent.Frame]
	public void OnFrame()
	{
		ClaudeBridge.ProcessPendingOnMainThread();
	}
}
