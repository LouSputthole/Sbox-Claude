using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
	// UTF-8 without BOM — Node.js JSON.parse rejects the BOM prefix
	private static readonly Encoding _utf8NoBom = new UTF8Encoding( false );

	// Bridge build version — surfaced in status.json + the Status menu so a
	// marketplace-addon-vs-MCP-server skew is visible at a glance.
	private const string BridgeVersion = "1.20.0";

	// status.json doubles as a heartbeat. _startedAtIso is stamped once at start;
	// the heartbeat timestamp is refreshed from the frame loop at most once per
	// HeartbeatIntervalMs so a closed/stalled editor reads as disconnected.
	private static string _startedAtIso;
	private static DateTime _lastHeartbeatUtc = DateTime.MinValue;
	private const double HeartbeatIntervalMs = 1000;

	// Set on the first editor frame after bootstrap, so we don't re-initialize on every frame.
	private static bool _initialized;

	static ClaudeBridge()
	{
		// Static ctor must stay empty. TypeLibrary is explicitly disabled while
		// PackageLoader.AddAssembly runs static constructors. Even a Log.Info call
		// here dispatches to the menu addon's ConsoleOverlay, which constructs a
		// Panel — Panel..ctor() then accesses TypeLibrary and throws:
		//
		//   InvalidOperationException: TypeLibrary is currently inaccessible.
		//   Reason: Disabled during static constructors.
		//
		// That crashes editor bootstrap on current s&box builds and makes any
		// project depending on this addon unopenable. Real init runs on the
		// first editor frame instead (see OnEditorFrame). Fix originally
		// reported and patched in PR #6 by @FurkanZhlp.
	}

	[Menu( "Editor", "Claude Bridge/Status", "smart_toy" )]
	public static void ShowStatus()
	{
		var msg = _running
			? $"Running v{BridgeVersion}\nIPC: {_ipcDir}\nHandlers: {_handlers.Count}"
			: "Not running";
		EditorUtility.DisplayDialog( "Claude Bridge", msg );
	}

	// Resolved via Path.GetTempPath() only. Reading SBOX_BRIDGE_IPC_DIR here would
	// need Environment.GetEnvironmentVariable, which may be blocked by the s&box
	// sandbox (env vars are an info-leak vector for untrusted game code) and would
	// fail at compile time. The dir is LOGGED below and written into status.json,
	// so to fix a Node-vs-C# temp split you point the MCP server at this dir via
	// its SBOX_BRIDGE_IPC_DIR env var instead.
	static string ResolveIpcDir()
	{
		return Path.Combine( Path.GetTempPath(), "sbox-bridge-ipc" );
	}

	/// <summary>
	/// Write status.json. This doubles as a HEARTBEAT: the `heartbeat` field is
	/// refreshed from the editor frame loop, so the MCP server can tell a live
	/// editor from a closed/crashed/frame-stalled one. A write-once status file
	/// used to leave the bridge reporting "connected" forever after the first run.
	/// </summary>
	static void WriteStatus( bool running )
	{
		if ( _ipcDir == null ) return;
		try
		{
			var statusPath = Path.Combine( _ipcDir, "status.json" );
			File.WriteAllText( statusPath, JsonSerializer.Serialize( new
			{
				running,
				version = BridgeVersion,
				startedAt = _startedAtIso,
				heartbeat = DateTime.UtcNow.ToString( "o" ),
				handlerCount = _handlers.Count,
				ipcDir = _ipcDir
			} ), _utf8NoBom );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SboxBridge] Status write error: {ex.Message}" );
		}
	}

	static void StartBridge()
	{
		if ( _running ) return;

		try
		{
			_ipcDir = ResolveIpcDir();
			Directory.CreateDirectory( _ipcDir );

			_startedAtIso = DateTime.UtcNow.ToString( "o" );
			_running = true;
			WriteStatus( true );
			_lastHeartbeatUtc = DateTime.UtcNow;

			// execute_csharp writes a temp Editor/__Exec_*.cs, hotloads, runs, then deletes it.
			// If that snippet fails to COMPILE, the whole editor assembly (local.<project>.editor —
			// including this bridge) breaks, so the bridge can't service the delete_script cleanup
			// and the bad file leaks, poisoning every subsequent compile. Sweep any leftovers on
			// startup (the bridge only reaches here once the assembly compiled clean again) so a
			// prior failure can't keep the project broken. (Fix 5)
			SweepStaleExecFiles();

			// Use a Timer only to read request files from disk (IO is thread-safe)
			// But queue the actual processing for the main thread
			_pollTimer = new Timer( ReadRequestFiles, null, 500, 50 );

			Log.Info( $"[SboxBridge] Bridge v{BridgeVersion} started — {_handlers.Count} handlers, IPC at {_ipcDir}" );
			Log.Info( "[SboxBridge] s&box Claude Bridge by sboxskins.gg — https://sboxskins.gg" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SboxBridge] Failed to start: {ex.Message}" );
		}
	}

	/// <summary>
	/// Delete any leftover execute_csharp temp files (Editor/__Exec_*.cs) from the project's
	/// Editor folder. These are written by execute_csharp and normally deleted after the run,
	/// but a compile failure can take the bridge down mid-cleanup and leave the file behind —
	/// where it breaks local.&lt;project&gt;.editor on every subsequent compile. Best-effort: a
	/// file we can't delete (locked/perms) is logged once and skipped, never throws. (Fix 5)
	/// </summary>
	internal static void SweepStaleExecFiles()
	{
		try
		{
			var editorDir = Path.Combine( Project.Current.GetRootPath(), "Editor" );
			if ( !Directory.Exists( editorDir ) ) return;

			foreach ( var f in Directory.GetFiles( editorDir, "__Exec_*.cs" ) )
			{
				try { File.Delete( f ); Log.Info( $"[SboxBridge] Swept stale exec temp file: {Path.GetFileName( f )}" ); }
				catch ( Exception ex ) { Log.Warning( $"[SboxBridge] Could not delete stale exec file '{Path.GetFileName( f )}': {ex.Message}" ); }
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SboxBridge] Exec-temp sweep failed: {ex.Message}" );
		}
	}

	// Pending requests read from disk, to be processed on main thread
	static readonly Queue<(string responseId, string json)> _pendingRequests = new();
	static readonly object _queueLock = new();

	static void RegisterHandlers()
	{
		// ── Batch 1: File / project basics ──────────────────────────────
		Register( "get_project_info",    () => new GetProjectInfoHandler() );
		Register( "list_project_files",  () => new ListProjectFilesHandler() );
		Register( "read_file",           () => new ReadFileHandler() );
		Register( "write_file",          () => new WriteFileHandler() );
		Register( "create_script",       () => new CreateScriptHandler() );
		Register( "edit_script",         () => new EditScriptHandler() );
		Register( "delete_script",       () => new DeleteScriptHandler() );
		Register( "list_scenes",         () => new ListScenesHandler() );

		// ── Batch 2: Scene file operations ──────────────────────────────
		Register( "load_scene",          () => new LoadSceneHandler() );
		Register( "save_scene",          () => new SaveSceneHandler() );
		Register( "create_scene",        () => new CreateSceneHandler() );

		// ── Batch 3: GameObject CRUD ─────────────────────────────────────
		Register( "create_gameobject",   () => new CreateGameObjectHandler() );
		Register( "delete_gameobject",   () => new DeleteGameObjectHandler() );
		Register( "duplicate_gameobject",() => new DuplicateGameObjectHandler() );
		Register( "rename_gameobject",   () => new RenameGameObjectHandler() );
		Register( "set_parent",          () => new SetParentHandler() );
		Register( "set_enabled",         () => new SetEnabledHandler() );
		Register( "set_transform",       () => new SetTransformHandler() );
		Register( "get_scene_hierarchy", () => new GetSceneHierarchyHandler() );
		Register( "get_selected_objects",() => new GetSelectedObjectsHandler() );
		Register( "select_object",       () => new SelectObjectHandler() );
		Register( "focus_object",        () => new FocusObjectHandler() );

		// ── Batch 4: Components ──────────────────────────────────────────
		Register( "get_property",                   () => new GetPropertyHandler() );
		Register( "get_all_properties",             () => new GetAllPropertiesHandler() );
		Register( "set_property",                   () => new SetPropertyHandler() );
		Register( "set_prefab_ref",                 () => new SetPrefabRefHandler() );
		Register( "list_available_components",      () => new ListAvailableComponentsHandler() );
		Register( "add_component_with_properties",  () => new AddComponentWithPropertiesHandler() );

		// ── Batch 5: Play mode ───────────────────────────────────────────
		Register( "start_play",          () => new StartPlayHandler() );
		Register( "stop_play",           () => new StopPlayHandler() );
		// pause_play / resume_play — no API found, omitted
		Register( "is_playing",          () => new IsPlayingHandler() );
		Register( "get_runtime_property",() => new GetRuntimePropertyHandler() );
		Register( "set_runtime_property",() => new SetRuntimePropertyHandler() );
		// Unity carry-over (debugviz wave) — NOT scene-mutating: must run while playing.
		Register( "set_time_scale",      () => new SetTimeScaleHandler() );
		Register( "get_profiler_stats",  () => new GetProfilerStatsHandler() );
		// debug-draw (Unity carry-over wave 2) — gizmos in edit, DebugOverlay in play; NOT scene-mutating-gated.
		Register( "debug_draw_line",     () => new DebugDrawLineHandler() );
		Register( "debug_draw_ray",      () => new DebugDrawRayHandler() );
		Register( "debug_draw_box",      () => new DebugDrawBoxHandler() );
		Register( "debug_draw_sphere",   () => new DebugDrawSphereHandler() );
		Register( "debug_clear",         () => new DebugClearHandler() );
		// playtest harness (gameplay verification) — async frame-loop step runner; NOT scene-mutating (runs in play).
		Register( "playtest",            () => new PlaytestHandler() );
		Register( "playtest_status",     () => new PlaytestStatusHandler() );

		// ── Batch 6: Assets ──────────────────────────────────────────────
		Register( "search_assets",       () => new SearchAssetsHandler() );
		Register( "get_asset_info",      () => new GetAssetInfoHandler() );
		Register( "assign_model",        () => new AssignModelHandler() );
		Register( "create_material",     () => new CreateMaterialHandler() );
		Register( "assign_material",     () => new AssignMaterialHandler() );
		Register( "set_material_property", () => new SetMaterialPropertyHandler() );

		// ── Batch 7: Audio ───────────────────────────────────────────────
		Register( "list_sounds",         () => new ListSoundsHandler() );
		Register( "create_sound_event",  () => new CreateSoundEventHandler() );
		Register( "assign_sound",        () => new AssignSoundHandler() );
		Register( "play_sound_preview",  () => new PlaySoundPreviewHandler() );

		// ── Batch 8: Prefabs ─────────────────────────────────────────────
		Register( "create_prefab",       () => new CreatePrefabHandler() );
		Register( "instantiate_prefab",  () => new InstantiatePrefabHandler() );
		Register( "list_prefabs",        () => new ListPrefabsHandler() );
		Register( "get_prefab_info",     () => new GetPrefabInfoHandler() );

		// ── Batch 9: Physics ─────────────────────────────────────────────
		Register( "add_physics",         () => new AddPhysicsHandler() );
		Register( "add_collider",        () => new AddColliderHandler() );
		Register( "add_joint",           () => new AddJointHandler() );
		Register( "raycast",             () => new RaycastHandler() );

		// ── Batch 10: Code templates ─────────────────────────────────────
		Register( "create_player_controller", () => new CreatePlayerControllerHandler() );
		Register( "create_npc_controller",    () => new CreateNpcControllerHandler() );
		Register( "create_game_manager",      () => new CreateGameManagerHandler() );
		Register( "create_trigger_zone",      () => new CreateTriggerZoneHandler() );

		// ── Batch 11: UI ─────────────────────────────────────────────────
		Register( "create_razor_ui",     () => new CreateRazorUIHandler() );
		Register( "add_screen_panel",    () => new AddScreenPanelHandler() );
		Register( "add_world_panel",     () => new AddWorldPanelHandler() );

		// ── Batch 11b: Undo/Redo ─────────────────────────────────────────
		Register( "undo",                () => new UndoHandler() );
		Register( "redo",                () => new RedoHandler() );

		// ── Batch 12: Networking ─────────────────────────────────────────
		Register( "add_network_helper",  () => new AddNetworkHelperHandler() );
		Register( "configure_network",   () => new ConfigureNetworkHandler() );
		Register( "get_network_status",  () => new GetNetworkStatusHandler() );
		Register( "set_ownership",       () => new SetOwnershipHandler() );
		Register( "network_spawn",            () => new NetworkSpawnHandler() );
		Register( "add_sync_property",        () => new AddSyncPropertyHandler() );
		Register( "add_rpc_method",           () => new AddRpcMethodHandler() );
		Register( "create_networked_player",  () => new CreateNetworkedPlayerHandler() );
		Register( "create_lobby_manager",     () => new CreateLobbyManagerHandler() );
		Register( "create_network_events",    () => new CreateNetworkEventsHandler() );

		// ── Batch 13: Publishing / config ────────────────────────────────
		Register( "get_project_config",  () => new GetProjectConfigHandler() );
		Register( "set_project_config",  () => new SetProjectConfigHandler() );
		Register( "validate_project",    () => new ValidateProjectHandler() );
		Register( "set_project_thumbnail",() => new SetProjectThumbnailHandler() );
		Register( "get_package_details", () => new GetPackageDetailsHandler() );
		Register( "install_asset",       () => new InstallAssetHandler() );
		Register( "list_asset_library",  () => new ListAssetLibraryHandler() );

		// ── Batch 14: Console / diagnostics ─────────────────────────────
		// get_console_output / get_compile_errors / clear_console — LogCapture not available, omitted
		Register( "take_screenshot",     () => new TakeScreenshotHandler() );
		Register( "trigger_hotload",     () => new TriggerHotloadHandler() );

		// ── Batch 15: Terrain / Map building ────────────────────────────
		Register( "build_terrain_mesh",       () => new BuildTerrainMeshHandler() );
		Register( "invoke_button",            () => new InvokeButtonHandler() );
		Register( "invoke_method",            () => new InvokeMethodHandler() );
		Register( "list_component_buttons",   () => new ListComponentButtonsHandler() );
		Register( "raycast_terrain",          () => new RaycastTerrainHandler() );
		Register( "add_terrain_hill",         () => new AddTerrainHillHandler() );
		Register( "add_terrain_clearing",     () => new AddTerrainClearingHandler() );
		Register( "add_terrain_trail",        () => new AddTerrainTrailHandler() );
		Register( "clear_terrain_features",   () => new ClearTerrainFeaturesHandler() );
		Register( "add_cave_waypoint",        () => new AddCaveWaypointHandler() );
		Register( "clear_cave_path",          () => new ClearCavePathHandler() );
		Register( "add_forest_poi",           () => new AddForestPOIHandler() );
		Register( "add_forest_trail",         () => new AddForestTrailHandler() );
		Register( "set_forest_seed",          () => new SetForestSeedHandler() );
		Register( "clear_forest_pois",        () => new ClearForestPOIsHandler() );
		Register( "sculpt_terrain",           () => new SculptTerrainHandler() );
		Register( "paint_forest_density",     () => new PaintForestDensityHandler() );
		Register( "place_along_path",         () => new PlaceAlongPathHandler() );

		// ── Batch 16: Coding / type discovery ───────────────────────────
		Register( "describe_type",            () => new DescribeTypeHandler() );
		Register( "search_types",             () => new SearchTypesHandler() );
		Register( "get_method_signature",     () => new GetMethodSignatureHandler() );
		Register( "find_in_project",          () => new FindInProjectHandler() );

		// ── Batch 17: Visual & atmosphere ───────────────────────────────
		Register( "add_light",                () => new AddLightHandler() );
		Register( "set_fog",                  () => new SetFogHandler() );
		Register( "add_post_process",         () => new AddPostProcessHandler() );
		Register( "set_skybox",               () => new SetSkyboxHandler() );
		Register( "apply_atmosphere",         () => new ApplyAtmosphereHandler() );
		Register( "apply_post_fx_look",       () => new ApplyPostFxLookHandler() );
		Register( "add_envmap_probe",         () => new AddEnvmapProbeHandler() );

		// ── Batch 18: VFX / particles ───────────────────────────────────
		Register( "spawn_particle",           () => new SpawnParticleHandler() );
		Register( "add_trail",                () => new AddTrailHandler() );
		Register( "add_beam",                 () => new AddBeamHandler() );
		Register( "create_particle_effect",   () => new CreateParticleEffectHandler() );

		// ── Batch 19: Characters & models ───────────────────────────────
		Register( "spawn_model",              () => new SpawnModelHandler() );
		Register( "spawn_citizen",            () => new SpawnCitizenHandler() );
		Register( "dress_citizen",            () => new DressCitizenHandler() );
		Register( "set_bodygroup",            () => new SetBodygroupHandler() );
		Register( "pose_citizen",             () => new PoseCitizenHandler() );

		// ── Batch 20: Character polish ──────────────────────────────────
		Register( "equip_model",              () => new EquipModelHandler() );
		Register( "set_look_at",              () => new SetLookAtHandler() );
		Register( "add_ragdoll",              () => new AddRagdollHandler() );
		Register( "set_expression",           () => new SetExpressionHandler() );

		// ── Batch 21: Scene & level building ────────────────────────────
		Register( "snap_to_ground",           () => new SnapToGroundHandler() );
		Register( "align_objects",            () => new AlignObjectsHandler() );
		Register( "distribute_objects",       () => new DistributeObjectsHandler() );
		Register( "grid_duplicate",           () => new GridDuplicateHandler() );
		Register( "measure_distance",         () => new MeasureDistanceHandler() );

		// ── Batch 22: Environment & props ───────────────────────────────
		Register( "scatter_props",            () => new ScatterPropsHandler() );
		Register( "randomize_transforms",     () => new RandomizeTransformsHandler() );
		Register( "group_objects",            () => new GroupObjectsHandler() );

		// ── Batch 23: Object utilities & queries ────────────────────────
		Register( "find_objects",             () => new FindObjectsHandler() );
		Register( "set_tint",                 () => new SetTintHandler() );
		Register( "replace_model",            () => new ReplaceModelHandler() );
		Register( "set_tags",                 () => new SetTagsHandler() );

		// ── Batch 24: Bridge superpowers (editor) ───────────────────────
		Register( "frame_camera",             () => new FrameCameraHandler() );

		// ── Batch 25: screenshot aiming + component/tag gaps ────────────
		Register( "screenshot_from",          () => new ScreenshotFromHandler() );
		Register( "remove_component",         () => new RemoveComponentHandler() );
		Register( "get_tags",                 () => new GetTagsHandler() );

		// ── Batch 26: console ───────────────────────────────────────────
		Register( "console_run",              () => new ConsoleRunHandler() );

		// ── Batch 27: navigation ────────────────────────────────────────
		Register( "bake_navmesh",             () => new BakeNavMeshHandler() );
		Register( "get_navmesh_path",         () => new GetNavMeshPathHandler() );

		// ── Batch 28: spatial query + reflection bake ───────────────────
		Register( "physics_overlap",          () => new PhysicsOverlapHandler() );
		Register( "bake_reflections",         () => new BakeReflectionsHandler() );

		// ── Batch 29: real particles (.vpcf) ────────────────────────────
		Register( "spawn_vpcf",               () => new SpawnVpcfHandler() );

		// ── Batch 30: editor lifecycle ──────────────────────────────────
		Register( "restart_editor",           () => new RestartEditorHandler() );

		// ── Batch 31: library discovery ─────────────────────────────────
		Register( "list_libraries",           () => new ListLibrariesHandler() );

		// ── Batch 32: asset compile ─────────────────────────────────────
		Register( "recompile_asset",          () => new RecompileAssetHandler() );

		// ── Batch 33: animation + bounds ────────────────────────────────
		Register( "list_animations",          () => new ListAnimationsHandler() );
		Register( "play_animation",           () => new PlayAnimationHandler() );
		Register( "set_animgraph_param",      () => new SetAnimgraphParamHandler() );
		Register( "get_bounds",               () => new GetBoundsHandler() );

		// ── Batch 34: play-mode eyes ────────────────────────────────────
		Register( "capture_view",             () => new CaptureViewHandler() );

		// ── Batch 35: playable game scaffolds (Phase 1) ─────────────────
		Register( "set_component_reference",     () => new SetComponentReferenceHandler() );
		Register( "add_component_to_new_object", () => new AddComponentToNewObjectHandler() );
		Register( "create_objective_system",     () => new CreateObjectiveSystemHandler() );
		Register( "create_health_system",        () => new CreateHealthSystemHandler() );
		Register( "create_pickup",               () => new CreatePickupHandler() );
		Register( "create_economy_wallet",       () => new CreateEconomyWalletHandler() );
		Register( "create_round_phase_machine",  () => new CreateRoundPhaseMachineHandler() );
		Register( "create_day_night_clock",      () => new CreateDayNightClockHandler() );
		Register( "create_interactable",         () => new CreateInteractableHandler() );
		Register( "create_weighted_loot_table",  () => new CreateWeightedLootTableHandler() );
		Register( "create_save_system",          () => new CreateSaveSystemHandler() );
		Register( "create_leaderboard_panel",    () => new CreateLeaderboardPanelHandler() );
		Register( "create_inventory",            () => new CreateInventoryHandler() );
		Register( "create_stat_modifier_system", () => new CreateStatModifierSystemHandler() );
		Register( "create_placement_mode",       () => new CreatePlacementModeHandler() );

		// ── Batch 36: NPC brains ────────────────────────────────────────
		Register( "create_npc_brain",         () => new CreateNpcBrainHandler() );
		Register( "place_patrol_route",       () => new PlacePatrolRouteHandler() );
		Register( "assign_patrol_route",      () => new AssignPatrolRouteHandler() );
		Register( "create_npc_spawner",       () => new CreateNpcSpawnerHandler() );
		Register( "simulate_npc_perception",  () => new SimulateNpcPerceptionHandler() );

		// ── Batch 37: Inspection & validation (mined from 27 shipped games) ──
		Register( "inspect_networked_object", () => new InspectNetworkedObjectHandler() );
		Register( "networking_lint",          () => new NetworkingLintHandler() );
		Register( "sandbox_lint",             () => new SandboxLintHandler() );
		Register( "razor_lint",              () => new RazorLintHandler() );
		Register( "scene_validate",           () => new SceneValidateHandler() );
		Register( "save_inspect",             () => new SaveInspectHandler() );
		Register( "services_query",           () => new ServicesQueryHandler() );
		Register( "simulate_input",           () => new SimulateInputHandler() );

		// ── Batch 38: Input actions (project config) ────────────────────
		Register( "ensure_input_action",      () => new EnsureInputActionHandler() );

		// ── Batch 39: Play-mode input driver (sustained / analog) ───────
		Register( "drive_player",             () => new DrivePlayerHandler() );
		Register( "drive_player_status",      () => new DrivePlayerStatusHandler() );

		// ── Batch 40: Asset utilities ────────────────────────────────────
		Register( "copy_asset_with_dependencies", () => new CopyAssetWithDependenciesHandler() );

		// ── Batch 43: v1.18.0 — LipSync (new engine component) + community scaffolds ──
		Register( "add_lipsync",                  () => new AddLipSyncHandler() );
		Register( "create_round_state_machine",   () => new CreateRoundStateMachineHandler() );
		Register( "add_interaction_station",      () => new AddInteractionStationHandler() );
		Register( "create_event_director",        () => new CreateEventDirectorHandler() );
		Register( "create_save_slots",            () => new CreateSaveSlotsHandler() );

		// ── Batch 44: v1.19.0 — Game Feel pack (juice scaffolds) ────────
		Register( "create_camera_shake",          () => new CreateCameraShakeHandler() );
		Register( "add_flicker_light",            () => new AddFlickerLightHandler() );
		Register( "create_floating_combat_text",  () => new CreateFloatingCombatTextHandler() );

		// ── Batch 45: v1.20.0 — MovieMaker / cutscene family ────────────
		Register( "list_movies",                  () => new ListMoviesHandler() );
		Register( "add_movie_player",             () => new AddMoviePlayerHandler() );
		Register( "play_movie",                   () => new PlayMovieHandler() );
		Register( "stop_movie",                   () => new StopMovieHandler() );

		// ── Batch 46: v1.20.0 — Networking primitives pack (Track B) ────
		Register( "create_host_rpc_action",       () => new CreateHostRpcActionHandler() );
		Register( "add_targeted_rpc",             () => new AddTargetedRpcHandler() );
		Register( "create_local_player_resolver", () => new CreateLocalPlayerResolverHandler() );
		Register( "add_host_migration_recovery",  () => new AddHostMigrationRecoveryHandler() );

		// ── Batch 47: v1.20.0 — Interaction + carry pack (Tracks E/F) ───
		Register( "add_interaction_prompt",       () => new AddInteractionPromptHandler() );
		Register( "create_hold_to_confirm",       () => new CreateHoldToConfirmHandler() );
		Register( "create_carry_system",          () => new CreateCarrySystemHandler() );

		// ── Batch 48: v1.20.0 — Loot / Economy variants (Track D) ───────
		Register( "create_gacha_drop_table",      () => new CreateGachaDropTableHandler() );
		Register( "create_currency_pickup",       () => new CreateCurrencyPickupHandler() );
		Register( "create_offline_progress",      () => new CreateOfflineProgressHandler() );

		// ── Batch 49: v1.20.0 — UI / feedback pack (Track C) ────────────
		Register( "create_worldpanel_ui",         () => new CreateWorldPanelUiHandler() );
		Register( "create_proxy_nametag",         () => new CreateProxyNametagHandler() );
		Register( "create_combo_meter",           () => new CreateComboMeterHandler() );

		// ── Batch 50: v1.20.0 — Cinematics & Dialogue (hand-authored) ───
		Register( "create_cutscene_director",     () => new CreateCutsceneDirectorHandler() );
		Register( "create_dialogue_system",       () => new CreateDialogueSystemHandler() );

		Log.Info( $"[SboxBridge] Registered {_handlers.Count} handlers" );
	}

	public static int HandlerCount => _handlers.Count;

	// Commands that mutate the scene/disk — refused while in play mode to avoid save corruption
	private static readonly HashSet<string> _sceneMutatingCommands = new()
	{
		"add_light", "set_fog", "add_post_process", "set_skybox", "apply_atmosphere", "apply_post_fx_look", "add_envmap_probe",
		"spawn_particle", "add_trail", "add_beam", "create_particle_effect",
		"spawn_model", "spawn_citizen", "dress_citizen", "set_bodygroup", "pose_citizen",
		"equip_model", "set_look_at", "add_ragdoll", "set_expression",
		"set_animgraph_param", "play_animation",
		"set_component_reference", "add_component_to_new_object",
		"create_objective_system", "create_health_system", "create_pickup", "create_economy_wallet", "create_round_phase_machine", "create_day_night_clock", "create_interactable", "create_weighted_loot_table", "create_save_system", "create_leaderboard_panel", "create_inventory", "create_stat_modifier_system", "create_placement_mode",
		"create_npc_brain", "place_patrol_route", "assign_patrol_route", "create_npc_spawner",
		"snap_to_ground", "align_objects", "distribute_objects", "grid_duplicate",
		"scatter_props", "randomize_transforms", "group_objects",
		"set_tint", "replace_model", "set_tags",
		"remove_component",
		"create_gameobject", "delete_gameobject", "duplicate_gameobject", "rename_gameobject",
		"set_parent", "set_transform", "set_enabled",
		"add_component_with_properties", "set_property", "set_prefab_ref",
		"create_script", "edit_script", "delete_script", "trigger_hotload",
		"create_scene", "load_scene", "save_scene",
		"assign_model", "create_material", "assign_material", "set_material_property",
		"create_sound_event", "assign_sound",
		"create_prefab", "instantiate_prefab",
		"add_physics", "add_collider", "add_joint",
		"create_player_controller", "create_npc_controller", "create_game_manager", "create_trigger_zone",
		"create_razor_ui", "add_screen_panel", "add_world_panel",
		"network_spawn", "add_sync_property", "add_rpc_method", "create_networked_player",
		"create_lobby_manager", "create_network_events", "add_network_helper",
		"configure_network", "set_ownership",
		"build_terrain_mesh", "add_terrain_hill", "add_terrain_clearing", "add_terrain_trail",
		"clear_terrain_features", "sculpt_terrain",
		"add_cave_waypoint", "clear_cave_path",
		"add_forest_poi", "add_forest_trail", "set_forest_seed", "clear_forest_pois",
		"paint_forest_density", "place_along_path",
		"undo", "redo",
		"set_project_config", "set_project_thumbnail",
		"ensure_input_action",
		"copy_asset_with_dependencies",
		"add_lipsync", "create_round_state_machine", "add_interaction_station", "create_event_director", "create_save_slots",
		"create_camera_shake", "add_flicker_light", "create_floating_combat_text",
		"add_movie_player",
		"create_host_rpc_action", "add_targeted_rpc", "create_local_player_resolver", "add_host_migration_recovery",
		"add_interaction_prompt", "create_hold_to_confirm", "create_carry_system",
		"create_gacha_drop_table", "create_currency_pickup", "create_offline_progress",
		"create_worldpanel_ui", "create_proxy_nametag", "create_combo_meter",
		"create_cutscene_director", "create_dialogue_system",
	};

	internal static bool IsSceneMutating( string command ) => _sceneMutatingCommands.Contains( command );

	static void Register( string name, Func<IBridgeHandler> factory )
	{
		try
		{
			var handler = factory?.Invoke();
			if ( handler == null )
			{
				Log.Warning( $"[SboxBridge] Handler factory for '{name}' returned null — tool unavailable" );
				return;
			}
			_handlers[name] = handler;
		}
		catch ( Exception ex )
		{
			// One bad handler must not take down the whole bridge. Log and continue.
			Log.Warning( $"[SboxBridge] Failed to register '{name}': {ex.GetType().Name}: {ex.Message}" );
		}
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
	/// Drains the request queue on the main editor thread. Required because scene
	/// APIs (CreateObject, AddComponent, Destroy, etc.) are NOT thread-safe and
	/// must run on the same thread as the rest of the editor.
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
				// Atomic write: temp + rename so the MCP poller can never read a half-written response.
				var tmpPath = responsePath + ".tmp";
				File.WriteAllText( tmpPath, response, _utf8NoBom );
				File.Move( tmpPath, responsePath, true );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[SboxBridge] Write error: {ex.Message}" );
			}
		}
	}

	// Dedups frame-handler error logs so a broken init doesn't spam 60×/sec.
	private static string _lastFrameError;

	/// <summary>
	/// Editor frame tick. Fires every editor frame regardless of UI state.
	///
	/// Does two things:
	/// 1. On the very first frame, runs the bridge initialization (logging,
	///    handler registration, IPC startup) that can't safely run from the
	///    static constructor. By the first editor frame, bootstrap has finished
	///    and TypeLibrary is accessible again. PR #6 by @FurkanZhlp.
	/// 2. On every frame, drains the IPC request queue. Used to live on the
	///    BridgePoller Widget, which meant RPCs only processed while the dock
	///    was open — see GitHub issue #2. Moved here so the bridge works
	///    whether or not the user opens the dock panel.
	/// </summary>
	[EditorEvent.Frame]
	public static void OnEditorFrame()
	{
		if ( !_initialized )
		{
			_initialized = true;
			try
			{
				Log.Info( "[SboxBridge] Initializing..." );
				RegisterHandlers();
				StartBridge();
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[SboxBridge] Init failed: {ex}" );
				return;
			}
		}

		// Refresh the liveness heartbeat (throttled). Driven from the frame loop
		// on purpose: if frames stop firing (editor closed or stalled) the
		// heartbeat goes stale within seconds and the MCP server reports
		// "disconnected" instead of a permanent false-positive.
		if ( _running && (DateTime.UtcNow - _lastHeartbeatUtc).TotalMilliseconds >= HeartbeatIntervalMs )
		{
			_lastHeartbeatUtc = DateTime.UtcNow;
			WriteStatus( true );
		}

		try
		{
			ProcessPendingOnMainThread();
		}
		catch ( Exception ex )
		{
			var msg = $"{ex.GetType().Name}: {ex.Message}";
			if ( msg != _lastFrameError )
			{
				_lastFrameError = msg;
				Log.Warning( $"[SboxBridge] Frame handler error (logged once per unique message): {ex}" );
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
					version = BridgeVersion,
					handlerCount = _handlers.Count,
					registeredCommands = _handlers.Keys.ToArray()
				}
			} );
		}

		// Refuse scene-mutating commands while in play mode. Mutations during play can
		// corrupt the .scene file when serializer state and editor state diverge.
		if ( IsSceneMutating( command ) && Game.IsPlaying )
		{
			return JsonSerializer.Serialize( new
			{
				id,
				success = false,
				error = $"'{command}' is not allowed while play mode is active. Stop play first (stop_play) and try again."
			} );
		}

		// Set prefab reference (inline — handles GameObject properties that set_property can't)
		if ( command == "set_prefab_ref" )
		{
			try
			{
				var sceneRef = SceneEditorSession.Active?.Scene;
				if ( sceneRef == null )
					return JsonSerializer.Serialize( new { id, success = false, error = "No active scene" } );

				var paramsEl = root.GetProperty( "params" );
				var targetIdStr = paramsEl.GetProperty( "id" ).GetString();
				if ( !Guid.TryParse( targetIdStr, out var targetGuid ) )
					return JsonSerializer.Serialize( new { id, success = false, error = "Invalid target GUID" } );

				var targetGo = sceneRef.Directory.FindByGuid( targetGuid );
				if ( targetGo == null )
					return JsonSerializer.Serialize( new { id, success = false, error = "Target GameObject not found" } );

				var componentType = paramsEl.GetProperty( "component" ).GetString();
				var propertyName = paramsEl.GetProperty( "property" ).GetString();
				var prefabPath = paramsEl.GetProperty( "prefabPath" ).GetString();

				var comp = targetGo.Components.GetAll()
					.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );
				if ( comp == null )
					return JsonSerializer.Serialize( new { id, success = false, error = $"Component not found: {componentType}" } );

				var prefabFile = ResourceLibrary.Get<PrefabFile>( prefabPath );
				if ( prefabFile == null )
					return JsonSerializer.Serialize( new { id, success = false, error = $"Prefab not found: {prefabPath}" } );

				GameObject prefabGo = null;
				try { prefabGo = SceneUtility.GetPrefabScene( prefabFile ); }
				catch ( Exception ex ) { return JsonSerializer.Serialize( new { id, success = false, error = $"GetPrefabScene failed: {ex.Message}" } ); }

				if ( prefabGo == null )
					return JsonSerializer.Serialize( new { id, success = false, error = "Prefab scene is null" } );

				var tDesc = Game.TypeLibrary.GetType( comp.GetType().Name );
				var prop = tDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
				if ( prop == null )
					return JsonSerializer.Serialize( new { id, success = false, error = $"Property not found: {propertyName}" } );

				prop.SetValue( comp, prefabGo );
				return JsonSerializer.Serialize( new { id, success = true, data = new { wired = propertyName, prefab = prefabPath } } );
			}
			catch ( Exception ex )
			{
				return MakeError( id, $"set_prefab_ref error: {ex.Message}" );
			}
		}

		if ( _handlers.TryGetValue( command, out var handler ) )
		{
			try
			{
				var paramsElement = root.TryGetProperty( "params", out var p ) ? p : default;
				var result = await handler.Execute( paramsElement );
				if ( TryGetHandlerError( result, out var handlerErr ) )
					return JsonSerializer.Serialize( new { id, success = false, error = handlerErr } );
				return JsonSerializer.Serialize( new { id, success = true, data = result } );
			}
			catch ( Exception ex )
			{
				return MakeError( id, $"Handler error: {ex.Message}" );
			}
		}

		return MakeError( id, $"Unknown command: {command}" );
	}

	// Many handlers signal failure by returning an object with a non-empty
	// `error` string property instead of throwing. Detect that via reflection
	// so the dispatch envelope reports success=false (audit P1).
	static bool TryGetHandlerError( object result, out string err )
	{
		err = null;
		if ( result == null )
			return false;

		var errStr = result.GetType().GetProperty( "error" )?.GetValue( result ) as string;
		if ( string.IsNullOrEmpty( errStr ) )
			return false;

		err = errStr;
		return true;
	}

	static string MakeError( string id, string message )
	{
		return JsonSerializer.Serialize( new { id, success = false, error = message } );
	}

	// ── Shared helpers ────────────────────────────────────────────────────
	internal static Vector3 ParseVector3( JsonElement e )
	{
		// Non-objects (a "x,y,z" string, a [x,y,z] array, a bare number) route through
		// ParseVector3Flexible so EVERY vector param honors the union the TS schema
		// advertises. Fixes raycast / physics_overlap / screenshot_from / capture_view
		// (and every other ParseVector3 caller) throwing "requires an element of type
		// 'Object' … target … 'String'" when handed the comma-string form.
		if ( e.ValueKind != JsonValueKind.Object )
			return ParseVector3Flexible( e );

		float x = e.TryGetProperty( "x", out var ex ) ? ex.GetSingle() : 0f;
		float y = e.TryGetProperty( "y", out var ey ) ? ey.GetSingle() : 0f;
		float z = e.TryGetProperty( "z", out var ez ) ? ez.GetSingle() : 0f;
		return new Vector3( x, y, z );
	}

	/// <summary>
	/// Flexible Vector3 parse for the cross-language contract: a vector value may arrive as
	///   • an object  {"x":..,"y":..,"z":..}   (the canonical form — same as ParseVector3)
	///   • a single number  5                  → uniform Vector3(5,5,5)  (mainly for scale)
	///   • an array   [x,y,z]                   → component-wise
	///   • a comma string  "x,y,z"             → component-wise
	/// C# is the source of truth for parsing (the TS schema accepts the union and passes the
	/// value through unchanged). Missing components default to <paramref name="uniformFallback"/>'s
	/// behaviour: an object missing a key falls back to 0 (matches ParseVector3); a single
	/// number fills all three axes. Used by set_transform / create_gameobject scale so a bare
	/// number or a string no longer silently fails the way ParseVector3 alone did.
	/// </summary>
	internal static Vector3 ParseVector3Flexible( JsonElement e )
	{
		switch ( e.ValueKind )
		{
			case JsonValueKind.Object:
				return ParseVector3( e );

			case JsonValueKind.Number:
			{
				// A single number means uniform scale on every axis.
				var u = e.GetSingle();
				return new Vector3( u, u, u );
			}

			case JsonValueKind.Array:
			{
				var f = ExtractFloats( e.GetRawText() );
				if ( f.Length == 1 ) return new Vector3( f[0], f[0], f[0] ); // [5] => uniform
				return new Vector3( f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f, f.Length > 2 ? f[2] : 0f );
			}

			case JsonValueKind.String:
			{
				var f = ExtractFloats( e.GetString() );
				if ( f.Length == 1 ) return new Vector3( f[0], f[0], f[0] ); // "5" => uniform
				return new Vector3( f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f, f.Length > 2 ? f[2] : 0f );
			}

			default:
				return Vector3.Zero;
		}
	}

	/// <summary>
	/// Resolve a GameObject by its id string. Fast path: persisted-scene GUID lookup
	/// (scene.Directory.FindByGuid). Fallback: scan the live scene tree by runtime .Id,
	/// so runtime-spawned objects — which are never added to the scene Directory — are
	/// addressable too (fixes set_runtime_property / get_runtime_property on spawned objects).
	/// Returns null if the id is unparseable or no match exists.
	/// </summary>
	internal static GameObject ResolveGameObject( Scene scene, string idString )
	{
		if ( scene == null || string.IsNullOrEmpty( idString ) ) return null;
		if ( !Guid.TryParse( idString, out var guid ) ) return null;

		var go = scene.Directory.FindByGuid( guid );
		if ( go != null ) return go;

		// Runtime-spawned objects aren't in the Directory — fall back to a scene scan by .Id.
		foreach ( var obj in scene.GetAllObjects( true ) )
		{
			if ( obj != null && obj.Id == guid ) return obj;
		}
		return null;
	}

	/// <summary>
	/// Resolve a user-supplied relative path against the project root and verify
	/// it stays inside the root (separator-safe containment). Returns false with a
	/// generic error on escape attempts (audit P2 — path traversal).
	/// </summary>
	internal static bool TryResolveProjectPath( string userPath, out string fullPath, out string error )
	{
		var root = Project.Current.GetRootPath();
		fullPath = Path.GetFullPath( Path.Combine( root, userPath ?? "" ) );

		var rootBoundary = root.TrimEnd( '/', '\\' ) + Path.DirectorySeparatorChar;
		if ( string.Equals( fullPath, root, StringComparison.OrdinalIgnoreCase )
			|| fullPath.StartsWith( rootBoundary, StringComparison.OrdinalIgnoreCase ) )
		{
			error = null;
			return true;
		}

		error = "Path outside project root denied";
		return false;
	}

	/// <summary>
	/// Sanitize an arbitrary string into a valid C# identifier for use as a class
	/// name in generated code (audit P2 — code injection via the `name` param).
	/// Keeps only [A-Za-z0-9_], prefixes '_' if the first kept char is a digit, and
	/// falls back to <paramref name="fallback"/> if nothing valid remains.
	/// </summary>
	internal static string SanitizeIdentifier( string raw, string fallback = "GeneratedComponent" )
	{
		if ( string.IsNullOrEmpty( raw ) )
			return fallback;

		var sb = new System.Text.StringBuilder( raw.Length );
		foreach ( var c in raw )
		{
			if ( ( c >= 'A' && c <= 'Z' ) || ( c >= 'a' && c <= 'z' ) || ( c >= '0' && c <= '9' ) || c == '_' )
				sb.Append( c );
		}

		if ( sb.Length == 0 )
			return fallback;

		if ( sb[0] >= '0' && sb[0] <= '9' )
			sb.Insert( 0, '_' );

		return sb.ToString();
	}

	internal static Rotation ParseRotation( JsonElement e )
	{
		float pitch = e.TryGetProperty( "pitch", out var ep ) ? ep.GetSingle() : 0f;
		float yaw   = e.TryGetProperty( "yaw",   out var ey ) ? ey.GetSingle() : 0f;
		float roll  = e.TryGetProperty( "roll",  out var er ) ? er.GetSingle() : 0f;
		return Rotation.From( pitch, yaw, roll );
	}

	internal static Vector2 ParseVector2( JsonElement e )
	{
		float x = e.TryGetProperty( "x", out var ex ) ? ex.GetSingle() : 0f;
		float y = e.TryGetProperty( "y", out var ey ) ? ey.GetSingle() : 0f;
		return new Vector2( x, y );
	}

	internal static Color ParseColor( JsonElement e )
	{
		float r = e.TryGetProperty( "r", out var er ) ? er.GetSingle() : 0f;
		float g = e.TryGetProperty( "g", out var eg ) ? eg.GetSingle() : 0f;
		float b = e.TryGetProperty( "b", out var eb ) ? eb.GetSingle() : 0f;
		float a = e.TryGetProperty( "a", out var ea ) ? ea.GetSingle() : 1f;
		return new Color( r, g, b, a );
	}

	/// <summary>
	/// Flatten a JSON value into the string form CoercePropertyAndSet expects:
	/// scalars as-is, arrays joined with commas (so [0,0,200] -> "0,0,200"), objects as raw JSON.
	/// </summary>
	internal static string ElementToValueString( JsonElement el )
	{
		switch ( el.ValueKind )
		{
			case JsonValueKind.String: return el.GetString();
			case JsonValueKind.Number: return el.GetRawText();
			case JsonValueKind.True:   return "true";
			case JsonValueKind.False:  return "false";
			case JsonValueKind.Null:   return "null";
			case JsonValueKind.Array:  return string.Join( ",", el.EnumerateArray().Select( ElementToValueString ) );
			default:                   return el.GetRawText();
		}
	}

	/// <summary>
	/// Extract the numeric components from a value string for building value types.
	/// Accepts "1,2,3", "1 2 3", "[1,2,3]", or a JSON object like {"x":1,"y":2,"z":3}
	/// / {"r":1,"g":0,"b":0,"a":1}. Needed because PropertyDescription.SetValue does
	/// NOT auto-parse a string into Vector2/3, Color, or Rotation (it silently no-ops).
	/// </summary>
	internal static float[] ExtractFloats( string s )
	{
		if ( string.IsNullOrWhiteSpace( s ) ) return System.Array.Empty<float>();
		var ms = System.Text.RegularExpressions.Regex.Matches( s, @"-?\d+(?:\.\d+)?" );
		var arr = new float[ms.Count];
		for ( int i = 0; i < ms.Count; i++ )
			arr[i] = float.Parse( ms[i].Value, System.Globalization.CultureInfo.InvariantCulture );
		return arr;
	}

	/// <summary>
	/// Coerce a string value to a component property's real type and SET it.
	///
	/// WHY THIS EXISTS: the old set_property / add_component_with_properties type
	/// switch only handled float/double/int/bool/string and passed every other
	/// type's value through as a raw STRING. s&box's PropertyDescription.SetValue
	/// auto-parses a string into value types (Vector3, Color, Rotation, Angles…),
	/// so those "just worked" — but it does NOT parse a string into a Model /
	/// Material / Texture / SoundEvent / prefab / GameObject / Component REFERENCE.
	/// Those silently stayed null, yet the handler still reported success=true, and
	/// the null then serialized into the saved .scene. (Verified: assign_model and
	/// set_component_reference persist fine; only the string-coercion path was broken.)
	///
	/// This routes asset/resource refs through ResourceLibrary.Get&lt;T&gt; (the same
	/// path the scene deserializer uses) and object/component refs through the active
	/// scene directory by GUID, so the CORRECT typed object is assigned and therefore
	/// serializes. Value types/enums/strings keep the old (working) behavior.
	///
	/// Returns true on a successful set. Returns false with <paramref name="error"/>
	/// set when a non-empty ref/asset/guid value could not be resolved — callers
	/// surface that as success=false instead of a false "set":true.
	/// </summary>
	/// <param name="propType">The property's declared type (propDesc.PropertyType).</param>
	/// <param name="setValue">Closure that performs propDesc.SetValue( target, v ).</param>
	/// <param name="propName">The property name (for error messages only).</param>
	/// <remarks>
	/// Takes the property via (type + setter closure + name) rather than a
	/// PropertyDescription parameter ON PURPOSE: the rest of the addon never NAMES
	/// the s&box reflection types (they use `var`) because their namespace isn't
	/// guaranteed importable in this assembly. This keeps that invariant.
	/// </remarks>
	internal static bool CoercePropertyAndSet( Type propType, Action<object> setValue, string propName, string valueStr, out string error )
	{
		error = null;
		if ( propType == null ) { error = "Could not resolve property type"; return false; }

		// Treat empty / "null" as "clear the property" (assign default/null).
		bool wantsNull = valueStr == null || valueStr == "null";

		try
		{
			// 1. string — pass through (covers string props AND lets s&box coerce
			//    value-type strings like "1,0,0,1" Color / "0,0,200" Vector3 / enums).
			if ( propType == typeof( string ) )
			{
				setValue( valueStr );
				return true;
			}

			// 2. primitives — explicit parse (invariant, same as before).
			switch ( propType.Name )
			{
				case "Single":  case "float":  setValue( float.Parse( valueStr, System.Globalization.CultureInfo.InvariantCulture ) );  return true;
				case "Double":  case "double": setValue( double.Parse( valueStr, System.Globalization.CultureInfo.InvariantCulture ) ); return true;
				case "Int32":   case "int":    setValue( int.Parse( valueStr ) );    return true;
				case "Int64":                  setValue( long.Parse( valueStr ) );   return true;
				case "Boolean": case "bool":   setValue( bool.Parse( valueStr ) );   return true;
			}

			// 3. enum — parse by name (case-insensitive).
			if ( propType.IsEnum )
			{
				if ( wantsNull ) { setValue( Activator.CreateInstance( propType ) ); return true; }
				setValue( Enum.Parse( propType, valueStr, ignoreCase: true ) );
				return true;
			}

			// 4. GameObject reference — resolve the string as a scene GUID.
			if ( propType == typeof( GameObject ) )
			{
				if ( wantsNull ) { setValue( null ); return true; }
				var scene = SceneEditorSession.Active?.Scene;
				if ( scene == null ) { error = "No active scene to resolve GameObject reference"; return false; }
				if ( !Guid.TryParse( valueStr, out var goGuid ) )
				{ error = $"Property '{propName}' is a GameObject reference; value must be a GameObject GUID (got '{valueStr}'). Use set_component_reference."; return false; }
				var refGo = scene.Directory.FindByGuid( goGuid );
				if ( refGo == null ) { error = $"GameObject not found for GUID '{valueStr}'"; return false; }
				setValue( refGo );
				return true;
			}

			// 5. Component reference — resolve the GUID's GameObject, pull the component.
			if ( typeof( Component ).IsAssignableFrom( propType ) )
			{
				if ( wantsNull ) { setValue( null ); return true; }
				var scene = SceneEditorSession.Active?.Scene;
				if ( scene == null ) { error = "No active scene to resolve Component reference"; return false; }
				if ( !Guid.TryParse( valueStr, out var cGuid ) )
				{ error = $"Property '{propName}' is a {propType.Name} reference; value must be a GameObject GUID (got '{valueStr}'). Use set_component_reference."; return false; }
				var cGo = scene.Directory.FindByGuid( cGuid );
				if ( cGo == null ) { error = $"GameObject not found for GUID '{valueStr}'"; return false; }
				var comp = cGo.Components.GetAll().FirstOrDefault( c => propType.IsAssignableFrom( c.GetType() ) );
				if ( comp == null ) { error = $"GameObject '{cGo.Name}' has no component assignable to '{propType.Name}'"; return false; }
				setValue( comp );
				return true;
			}

			// 6. Resource / GameResource (Model, Material, Texture, SoundEvent,
			//    PrefabFile, custom GameResources…) — load by path via the generic
			//    ResourceLibrary.Get<T>, the same accessor the scene deserializer uses.
			if ( IsResourceType( propType ) )
			{
				if ( wantsNull ) { setValue( null ); return true; }
				var loaded = LoadResource( propType, valueStr );
				if ( loaded == null )
				{ error = $"Could not load {propType.Name} from path '{valueStr}' (check the asset path)."; return false; }
				setValue( loaded );
				return true;
			}

			// 7. Common value types — parse explicitly. PropertyDescription.SetValue
			//    does NOT auto-parse a string into these (verified: it silently no-ops),
			//    so build the typed value from the numbers in the string and set THAT.
			//    ExtractFloats accepts "x,y,z", "[..]", or a JSON object form.
			if ( propType == typeof( Vector3 ) )
			{
				var f = ExtractFloats( valueStr );
				setValue( new Vector3( f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f, f.Length > 2 ? f[2] : 0f ) );
				return true;
			}
			if ( propType == typeof( Vector2 ) )
			{
				var f = ExtractFloats( valueStr );
				setValue( new Vector2( f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f ) );
				return true;
			}
			if ( propType == typeof( Color ) )
			{
				var f = ExtractFloats( valueStr );
				setValue( new Color( f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f, f.Length > 2 ? f[2] : 0f, f.Length > 3 ? f[3] : 1f ) );
				return true;
			}
			if ( propType == typeof( Rotation ) )
			{
				var f = ExtractFloats( valueStr );
				setValue( Rotation.From( f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f, f.Length > 2 ? f[2] : 0f ) );
				return true;
			}

			// 8. Anything else (Angles, Transform, BBox…): hand the string to s&box's
			//    setter as a last resort (may parse some types, no-op others).
			setValue( valueStr );
			return true;
		}
		catch ( Exception ex )
		{
			error = $"Failed to set '{propName}' ({propType.Name}): {ex.Message}";
			return false;
		}
	}

	/// <summary>True if the type is an s&box asset/resource (Resource or GameResource subtype).</summary>
	internal static bool IsResourceType( Type t )
	{
		for ( var b = t; b != null; b = b.BaseType )
			if ( b.Name == "Resource" || b.Name == "GameResource" )
				return true;
		return false;
	}

	/// <summary>Load an asset of <paramref name="resourceType"/> from a project path via ResourceLibrary.Get&lt;T&gt;.</summary>
	internal static object LoadResource( Type resourceType, string path )
	{
		try
		{
			// ResourceLibrary.Get<T> is generic with two overloads, Get<T>(string) and
			// Get<T>(int). Type.GetMethod(name, Type[]) can't bind a generic method by
			// arg types, so find it by hand: the open generic Get with a single string param.
			var generic = typeof( ResourceLibrary )
				.GetMethods( BindingFlags.Public | BindingFlags.Static )
				.FirstOrDefault( m => m.Name == "Get"
					&& m.IsGenericMethodDefinition
					&& m.GetParameters().Length == 1
					&& m.GetParameters()[0].ParameterType == typeof( string ) );
			if ( generic == null ) return null;
			return generic.MakeGenericMethod( resourceType ).Invoke( null, new object[] { path } );
		}
		catch { return null; }
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
		return SerializeGoTree( go, 0, int.MaxValue );
	}

	/// <summary>
	/// Serialize a GameObject and its descendants, capped at <paramref name="maxDepth"/>
	/// levels of recursion. depth=0 is the root being serialized; once depth reaches
	/// maxDepth, children are returned as an empty array instead of being recursed into.
	/// Critical for large scenes — without this cap the JSON payload overflows token
	/// budgets (see GitHub issue #4).
	/// </summary>
	internal static object SerializeGoTree( GameObject go, int depth, int maxDepth )
	{
		return new
		{
			id         = go.Id.ToString(),
			name       = go.Name,
			enabled    = go.Enabled,
			components = go.Components.GetAll().Select( c => c.GetType().Name ).ToArray(),
			children   = depth >= maxDepth
				? Array.Empty<object>()
				: go.Children.Select( c => SerializeGoTree( c, depth + 1, maxDepth ) ).ToArray()
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

		if ( !ClaudeBridge.TryResolveProjectPath( dir, out var searchDir, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr, files = Array.Empty<string>() } );

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
		var filePath = p.GetProperty( "path" ).GetString();
		if ( !ClaudeBridge.TryResolveProjectPath( filePath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );
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
		var filePath = p.GetProperty( "path" ).GetString();
		var content  = p.GetProperty( "content" ).GetString();
		if ( !ClaudeBridge.TryResolveProjectPath( filePath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		File.WriteAllText( fullPath, content );
		return Task.FromResult<object>( new { path = filePath, written = true, length = content.Length } );
	}
}

public class CreateScriptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var name      = p.GetProperty( "name" ).GetString();
		var template  = p.TryGetProperty( "template",  out var t ) ? t.GetString() : "component";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName  = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );
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
		var filePath = p.GetProperty( "path" ).GetString();
		if ( !ClaudeBridge.TryResolveProjectPath( filePath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );
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
		var filePath = p.GetProperty( "path" ).GetString();
		if ( !ClaudeBridge.TryResolveProjectPath( filePath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );
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
		var assetsPath = Project.Current.GetAssetsPath();

		// Try as relative path first, then absolute
		var fullPath = Path.IsPathRooted( scenePath )
			? scenePath
			: Path.GetFullPath( Path.Combine( rootPath, scenePath ) );

		// Callers also pass an assets-relative path (e.g. "scenes/foo.scene"); resolve that too.
		if ( !File.Exists( fullPath ) )
		{
			var altFull = Path.IsPathRooted( scenePath )
				? scenePath
				: Path.GetFullPath( Path.Combine( assetsPath, scenePath ) );
			if ( File.Exists( altFull ) ) fullPath = altFull;
		}

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Scene file not found: {scenePath}" } );

		try
		{
			// ResourceLibrary.Get<SceneFile> wants the path RELATIVE TO THE ASSETS FOLDER
			// (e.g. "scenes/minimal.scene"), NOT the project-root-relative or absolute path.
			// Build that from the resolved full path so any accepted input form works.
			string assetRel = Path.GetRelativePath( assetsPath, fullPath ).Replace( '\\', '/' );

			// SceneFile is the resource type for .scene files. Try the assets-relative path
			// first, then fall back to the raw input (covers paths already in that form).
			var sceneFile = ResourceLibrary.Get<SceneFile>( assetRel )
				?? ResourceLibrary.Get<SceneFile>( scenePath );

			// Fallback: ResourceLibrary.Get returns null if the .scene isn't currently
			// registered (observed after switching between scenes — the resource for an
			// inactive scene can be evicted). Resolve it through the AssetSystem instead,
			// registering the file if needed, then load its SceneFile resource. This makes
			// load_scene reliable regardless of the live ResourceLibrary registration state.
			if ( sceneFile == null )
			{
				try
				{
					var asset = AssetSystem.FindByPath( assetRel ) ?? AssetSystem.RegisterFile( fullPath );
					// RegisterFile returns null for some types (e.g. a freshly-written .scene) —
					// compile it from source text, then re-resolve the now-registered asset.
					if ( asset == null )
					{
						AssetSystem.CompileResource( assetRel, File.ReadAllText( fullPath ) );
						asset = AssetSystem.FindByPath( assetRel );
					}
					sceneFile = asset?.LoadResource( typeof( SceneFile ) ) as SceneFile;
					sceneFile ??= ResourceLibrary.Get<SceneFile>( assetRel );
				}
				catch { /* fall through to the error below */ }
			}

			if ( sceneFile != null )
			{
				EditorScene.OpenScene( sceneFile );
				return Task.FromResult<object>( new { loaded = true, path = assetRel } );
			}
			return Task.FromResult<object>( new { error = $"Could not load scene resource '{assetRel}'. The .scene file exists but could not be resolved via ResourceLibrary or AssetSystem." } );
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
		var assetsPath = Project.Current.GetAssetsPath();

		// Accept "path" (preferred — e.g. 'scenes/level_01.scene') or legacy name(+directory).
		string rel = null;
		if ( p.TryGetProperty( "path", out var pe ) && !string.IsNullOrWhiteSpace( pe.GetString() ) )
			rel = pe.GetString();
		else if ( p.TryGetProperty( "name", out var ne ) && !string.IsNullOrWhiteSpace( ne.GetString() ) )
		{
			var subdir = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "scenes";
			rel = Path.Combine( subdir, ne.GetString() );
		}
		if ( string.IsNullOrWhiteSpace( rel ) )
			return Task.FromResult<object>( new { error = "path is required (e.g. 'scenes/level_01.scene')" } );

		// Normalize: forward slashes, drop a leading 'Assets/', ensure the .scene extension.
		rel = rel.Replace( '\\', '/' ).TrimStart( '/' );
		if ( rel.StartsWith( "assets/", StringComparison.OrdinalIgnoreCase ) )
			rel = rel.Substring( "assets/".Length );
		if ( !rel.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) )
			rel += ".scene";

		// Scenes MUST live under the project's Assets/ folder to be resolvable by
		// ResourceLibrary/AssetSystem — root under Assets, NOT the project root.
		var fullPath = Path.GetFullPath( Path.Combine( assetsPath, rel ) );
		var assetsBoundary = assetsPath.TrimEnd( '/', '\\' ) + Path.DirectorySeparatorChar;
		if ( !fullPath.StartsWith( assetsBoundary, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Scene path must stay inside the project's Assets folder" } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Scene already exists: {rel}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		// Minimal valid s&box scene JSON. (includeDefaults — camera/light/ground — is not
		// generated here yet; add them via create_gameobject/add_light after loading.)
		var sceneJson = JsonSerializer.Serialize( new
		{
			__version = 0,
			__referencedFiles = Array.Empty<string>(),
			GameObjects = Array.Empty<object>()
		}, new JsonSerializerOptions { WriteIndented = true } );

		File.WriteAllText( fullPath, sceneJson );

		// Register the new scene with the AssetSystem so load_scene works immediately
		// (RegisterFile returns null for a fresh .scene, so fall back to CompileResource).
		bool registered = false;
		try
		{
			var asset = Editor.AssetSystem.RegisterFile( fullPath );
			if ( asset != null ) { asset.Compile( true ); registered = true; }
			else registered = Editor.AssetSystem.CompileResource( rel, sceneJson );
		}
		catch { /* best-effort — load_scene also self-registers as a fallback */ }

		return Task.FromResult<object>( new { created = true, path = rel, registered } );
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
			go.WorldScale = ClaudeBridge.ParseVector3Flexible( scl ); // object / number(uniform) / string (Fix 1/7)

		// Honor the parent id so the new object can be created directly under a parent instead
		// of always landing at the scene root. keepWorldPosition:false → the object adopts the
		// parent's local space (the position/rotation/scale params above are treated as the
		// world transform pre-parent, then re-based into the parent). Accept either "parentId"
		// (the set_parent idiom) or "parent" (what the TS create_gameobject schema sends). (Fix 2)
		JsonElement pidEl = default;
		bool havePid = ( p.TryGetProperty( "parentId", out pidEl ) || p.TryGetProperty( "parent", out pidEl ) )
			&& pidEl.ValueKind == JsonValueKind.String;
		if ( havePid && Guid.TryParse( pidEl.GetString(), out var parentGuid ) )
		{
			var parent = scene.Directory.FindByGuid( parentGuid );
			if ( parent != null )
				go.SetParent( parent, keepWorldPosition: false );
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

		// go.Clone() resolves its destination from the AMBIENT active scene (Game.ActiveScene),
		// which is null in edit mode — so a bare Clone() throws "No Active Scene" even though the
		// editor scene exists. Push the editor scene as the active scope for the clone so it lands
		// in the same scene add_component/set_property mutate. (Fix 3)
		GameObject clone;
		using ( scene.Push() )
		{
			clone = go.Clone();
		}

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
			// Scale accepts an object {x,y,z}, a comma string "x,y,z", an array, OR a single
			// number (uniform). The old ParseVector3 only read object keys, so a bare number
			// or a "1,1,1" string silently became (0,0,0) and collapsed the object. (Fix 1/7)
			if ( local ) go.LocalScale = ClaudeBridge.ParseVector3Flexible( scl );
			else         go.WorldScale  = ClaudeBridge.ParseVector3Flexible( scl );
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

		// Honor the maxDepth parameter documented in the MCP tool schema (default 10).
		// Without this cap the payload overflows Claude's per-tool-result token budget
		// on any non-trivial scene (GitHub issue #4).
		var maxDepth = p.TryGetProperty( "maxDepth", out var md ) && md.ValueKind == JsonValueKind.Number
			? md.GetInt32()
			: 10;
		if ( maxDepth < 0 ) maxDepth = 0;

		// Optional: start traversal from a specific GameObject by GUID instead of from
		// the scene roots. Useful for drilling into one subtree without dumping the
		// entire scene tree first (GitHub issue #4 bonus suggestion).
		if ( p.TryGetProperty( "rootId", out var rootProp ) && rootProp.ValueKind == JsonValueKind.String )
		{
			var idStr = rootProp.GetString();
			if ( !string.IsNullOrEmpty( idStr ) )
			{
				if ( !Guid.TryParse( idStr, out var rootGuid ) )
					return Task.FromResult<object>( new { error = $"Invalid rootId: {idStr}" } );

				var root = scene.Directory.FindByGuid( rootGuid );
				if ( root == null )
					return Task.FromResult<object>( new { error = $"rootId not found in scene: {idStr}" } );

				return Task.FromResult<object>( new
				{
					sceneName = scene.Name,
					rootId = idStr,
					maxDepth,
					hierarchy = new[] { ClaudeBridge.SerializeGoTree( root, 0, maxDepth ) }
				} );
			}
		}

		var roots = scene.Children
			.Select( go => ClaudeBridge.SerializeGoTree( go, 0, maxDepth ) )
			.ToArray();

		return Task.FromResult<object>( new
		{
			sceneName = scene.Name,
			objectCount = scene.GetAllObjects( true ).Count(),
			maxDepth,
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
		var go = ClaudeBridge.ResolveGameObject( scene, id );
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

/// <summary>
/// Sets a GameObject-typed property on a component to a loaded prefab.
/// Use this when you need to assign a prefab reference that set_property can't handle.
/// </summary>
public class SetPrefabRefHandler : IBridgeHandler
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

		var componentType = p.GetProperty( "component" ).GetString();
		var propertyName = p.GetProperty( "property" ).GetString();
		var prefabPath = p.GetProperty( "prefabPath" ).GetString();

		var component = go.Components.GetAll()
			.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );
		if ( component == null )
			return Task.FromResult<object>( new { error = $"Component not found: {componentType}" } );

		// Load the prefab
		var prefabFile = ResourceLibrary.Get<PrefabFile>( prefabPath );
		if ( prefabFile == null )
			return Task.FromResult<object>( new { error = $"Prefab not found: {prefabPath}" } );

		// Get the GameObject from the prefab scene
		GameObject prefabGo = null;
		try
		{
			prefabGo = SceneUtility.GetPrefabScene( prefabFile );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to get prefab scene: {ex.Message}" } );
		}

		if ( prefabGo == null )
			return Task.FromResult<object>( new { error = "Prefab scene GameObject is null" } );

		try
		{
			var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
			if ( propDesc == null )
				return Task.FromResult<object>( new { error = $"Property not found: {propertyName}" } );

			propDesc.SetValue( component, prefabGo );
			return Task.FromResult<object>( new { set = true, id, component = componentType, property = propertyName, prefabPath } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set prefab ref: {ex.Message}" } );
		}
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
		var go = ClaudeBridge.ResolveGameObject( scene, id );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var componentType = p.TryGetProperty( "component", out var ct ) ? ct.GetString() : null;
		var propertyName  = p.GetProperty( "property" ).GetString();
		var valueEl       = p.GetProperty( "value" );

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

			// Value-type given as a JSON object ({x,y,z} / {r,g,b,a}) — parse to the typed
			// value and set it directly so it reliably applies (s&box's string auto-parse
			// can silently no-op for some value types). Scalars/strings/arrays/refs fall
			// through to the audited string-coercion path below.
			var pt = propDesc.PropertyType;
			if ( valueEl.ValueKind == JsonValueKind.Object )
			{
				object typed = null;
				if ( pt == typeof( Vector3 ) )       typed = ClaudeBridge.ParseVector3( valueEl );
				else if ( pt == typeof( Vector2 ) )  typed = ClaudeBridge.ParseVector2( valueEl );
				else if ( pt == typeof( Color ) )    typed = ClaudeBridge.ParseColor( valueEl );
				else if ( pt == typeof( Rotation ) ) typed = ClaudeBridge.ParseRotation( valueEl );

				if ( typed != null )
				{
					propDesc.SetValue( component, typed );
					return Task.FromResult<object>( new { set = true, id, component = component.GetType().Name, property = propertyName, value = valueEl.ToString() } );
				}
			}

			// Type-aware set: handles primitives, enums, value-type strings (Color/Vector3),
			// AND asset/object references (Model/Material/GameObject/Component) which the old
			// raw-string path silently dropped to null. Reports success=false on a bad ref/path
			// instead of a false "set":true.
			var valueStr = ClaudeBridge.ElementToValueString( valueEl );
			if ( !ClaudeBridge.CoercePropertyAndSet( pt, v => propDesc.SetValue( component, v ), propDesc.Name, valueStr, out var setErr ) )
				return Task.FromResult<object>( new { error = setErr } );

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

			// Apply optional property overrides. Routes through the shared type-aware
			// coercion so asset/object references (Model/Material/GameObject/Component)
			// are loaded/resolved to the correct typed value and actually persist —
			// the old raw-string path silently dropped them to null. Best-effort per
			// property (a single bad value never aborts the add); failures are reported.
			var appliedProps = new List<string>();
			var failedProps  = new List<object>();
			if ( p.TryGetProperty( "properties", out var props ) && props.ValueKind == JsonValueKind.Object )
			{
				foreach ( var prop in props.EnumerateObject() )
				{
					var pd = typeDesc.Properties.FirstOrDefault( pp => pp.Name == prop.Name );
					if ( pd == null ) { failedProps.Add( new { property = prop.Name, error = "property not found" } ); continue; }

					// Normalize the JSON token to a string the coercer understands.
					string valStr = prop.Value.ValueKind switch
					{
						JsonValueKind.String => prop.Value.GetString(),
						JsonValueKind.True   => "true",
						JsonValueKind.False  => "false",
						JsonValueKind.Null   => "null",
						_                    => prop.Value.GetRawText()
					};

					if ( ClaudeBridge.CoercePropertyAndSet( pd.PropertyType, v => pd.SetValue( component, v ), pd.Name, valStr, out var perr ) )
						appliedProps.Add( prop.Name );
					else
						failedProps.Add( new { property = prop.Name, error = perr } );
				}
			}

			return Task.FromResult<object>( new
			{
				added = true, id, component = typeName,
				appliedProperties = appliedProps,
				failedProperties  = failedProps.Count > 0 ? failedProps : null
			} );
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

/// <summary>
/// Tracks editor play-mode state since Game.IsPlaying isn't reliable in editor context.
/// </summary>
public static class PlayState
{
	public static bool IsPlaying;
}

public class StartPlayHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var session = SceneEditorSession.Active;
		if ( session == null )
			return Task.FromResult<object>( new { error = "No active scene session" } );

		// Try the safe path first — matches what the editor Play button does.
		// This serializes the scene to catch any invalid state before actually playing.
		try
		{
			EditorScene.Play( session );
			PlayState.IsPlaying = true;
			return Task.FromResult<object>( new { started = true, method = "EditorScene.Play" } );
		}
		catch ( Exception editorEx )
		{
			// Fall back to direct SetPlaying. This skips scene serialization, which
			// is a workaround but can leave the editor in a half-play state if the
			// scene has invalid components. Only use if EditorScene.Play fails.
			try
			{
				session.SetPlaying( session.Scene );
				PlayState.IsPlaying = true;
				return Task.FromResult<object>( new
				{
					started = true,
					method = "SetPlaying (fallback)",
					editorErrorSkipped = editorEx.Message
				} );
			}
			catch ( Exception ex )
			{
				return Task.FromResult<object>( new
				{
					error = $"Failed both paths. Editor: {editorEx.Message} | Direct: {ex.Message}"
				} );
			}
		}
	}
}

public class StopPlayHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		// StartPlayHandler enters play via the editor's own EditorScene.Play(session) path.
		// The symmetric teardown is the STATIC EditorScene.Stop() — it tears down the play
		// GameSession and restores the editor scene the same way the editor's Stop button
		// does. The previous code only called the instance-level SceneEditorSession.StopPlaying(),
		// which is the lower half of that and can leave Game.IsPlaying (the engine "gameFlag")
		// stuck true — which then blocks scene-mutating commands. We now prefer EditorScene.Stop(),
		// fall back to StopPlaying(), then verify gameFlag and re-stop once if it's still set.
		string method = null;
		string warning = null;
		try
		{
			try
			{
				EditorScene.Stop();
				method = "EditorScene.Stop";
			}
			catch ( Exception editorEx )
			{
				SceneEditorSession.Active?.StopPlaying();
				method = "SceneEditorSession.StopPlaying (fallback)";
				warning = $"EditorScene.Stop failed, used StopPlaying fallback: {editorEx.Message}";
			}

			// Always clear the tracked flag regardless of which path stopped play.
			PlayState.IsPlaying = false;

			// Verify the engine actually left play. If gameFlag is still set, try the other
			// teardown call once more — this is the documented engine-state quirk.
			bool stillPlaying = false;
			try { stillPlaying = Game.IsPlaying; } catch { }

			if ( stillPlaying )
			{
				try { SceneEditorSession.Active?.StopPlaying(); } catch { }
				try { EditorScene.Stop(); } catch { }
				PlayState.IsPlaying = false;
				try { stillPlaying = Game.IsPlaying; } catch { }
				if ( stillPlaying )
					warning = ( warning == null ? "" : warning + " " ) +
						"Game.IsPlaying still reports true immediately after stop (engine clears it on a later frame). " +
						"The tracked flag is cleared; if a scene edit is still blocked, retry it next frame or restart the editor.";
			}

			return Task.FromResult<object>( new
			{
				stopped = true,
				method,
				gameFlag = SafeGameIsPlaying(),
				tracked = PlayState.IsPlaying,
				warning
			} );
		}
		catch ( Exception ex )
		{
			// Even on a hard failure, clear our tracked flag so is_playing isn't wedged on it.
			PlayState.IsPlaying = false;
			return Task.FromResult<object>( new { error = $"Failed to stop play: {ex.Message}", method, tracked = PlayState.IsPlaying } );
		}
	}

	static bool SafeGameIsPlaying()
	{
		try { return Game.IsPlaying; } catch { return false; }
	}
}

public class IsPlayingHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		// Check multiple signals: our tracked flag, Game.IsPlaying, and whether
		// the active scene is a game scene vs. editor scene.
		var tracked = PlayState.IsPlaying;
		var gameFlag = Game.IsPlaying;

		// Editor scene and game scene diverge during play mode
		bool sessionPlaying = false;
		try
		{
			var session = SceneEditorSession.Active;
			if ( session != null && Game.ActiveScene != null )
			{
				sessionPlaying = Game.ActiveScene != session.Scene;
			}
		}
		catch { }

		// sessionPlaying (Game.ActiveScene != session.Scene) reads stale after a restart,
		// so it's diagnostic-only; the authoritative flag is the engine's Game.IsPlaying.
		var isPlaying = gameFlag || tracked;

		return Task.FromResult<object>( new
		{
			isPlaying,
			isPaused = Game.IsPaused,
			gameFlag,
			tracked,
			sessionPlaying
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
		var filePath = p.GetProperty( "path" ).GetString();
		if ( !ClaudeBridge.TryResolveProjectPath( filePath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

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
		// Accept "path" (preferred — matches the create_material tool) or legacy "name"(+"directory").
		// Previously this did p.GetProperty("name") which THREW KeyNotFoundException when the
		// tool sent "path" (the "dictionary key" bug). Now it reads either.
		string rel = null;
		if ( p.TryGetProperty( "path", out var pe ) && !string.IsNullOrWhiteSpace( pe.GetString() ) )
			rel = pe.GetString();
		else if ( p.TryGetProperty( "name", out var ne ) && !string.IsNullOrWhiteSpace( ne.GetString() ) )
		{
			var subdir = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "materials";
			rel = Path.Combine( subdir, ne.GetString() );
		}
		if ( string.IsNullOrWhiteSpace( rel ) )
			return Task.FromResult<object>( new { error = "path is required (e.g. 'materials/walls/brick.vmat')" } );
		if ( !rel.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) ) rel += ".vmat";

		if ( !ClaudeBridge.TryResolveProjectPath( rel, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );
		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Material already exists: {rel}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var shader = p.TryGetProperty( "shader", out var sh ) && !string.IsNullOrWhiteSpace( sh.GetString() ) ? sh.GetString() : "shaders/complex.shader";

		var sb = new System.Text.StringBuilder();
		sb.Append( "// THIS FILE IS AUTO-GENERATED\n\"Layer0\"\n{\n" );
		sb.Append( "\tshader \"" + shader + "\"\n\n" );
		int wrote = 0;
		if ( p.TryGetProperty( "properties", out var props ) && props.ValueKind == JsonValueKind.Object )
		{
			foreach ( var kv in props.EnumerateObject() )
			{
				string val = kv.Value.ValueKind switch
				{
					JsonValueKind.String => "\"" + kv.Value.GetString() + "\"",
					JsonValueKind.Number => kv.Value.GetRawText(),
					JsonValueKind.True   => "1",
					JsonValueKind.False  => "0",
					_ => null
				};
				if ( val != null ) { sb.Append( "\t" + kv.Name + " " + val + "\n" ); wrote++; }
			}
		}
		if ( wrote == 0 )
			sb.Append( "\tg_flMetalness 0.0\n\tg_flRoughness 1.0\n" );
		sb.Append( "}\n" );

		File.WriteAllText( fullPath, sb.ToString() );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath, shader, propertiesWritten = wrote } );
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
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( subdir, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

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
		string relPath;
		if ( p.TryGetProperty( "path", out var pathProp ) )
		{
			relPath = pathProp.GetString();
		}
		else
		{
			var name   = p.TryGetProperty( "name", out var n ) ? n.GetString() : go.Name;
			var subdir = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Prefabs";
			var fileName = name.EndsWith( ".prefab" ) ? name : $"{name}.prefab";
			relPath = Path.Combine( subdir, fileName );
		}

		if ( !ClaudeBridge.TryResolveProjectPath( relPath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

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
		if ( !ClaudeBridge.TryResolveProjectPath( prefabPath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

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
		var prefabPath = p.GetProperty( "path" ).GetString();
		if ( !ClaudeBridge.TryResolveProjectPath( prefabPath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

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
			var emsg = ex.Message ?? string.Empty;
			// "Default Surface not found" (and kin) = the surface registry isn't loaded into
			// this editor session yet (a known transient after certain state changes). A trace
			// can't rebuild it in-place, so surface a clear, actionable recovery hint instead of
			// a cryptic failure. (v1.10.0 auto-handled: detected + a restart recommendation.)
			if ( emsg.IndexOf( "Default Surface", StringComparison.OrdinalIgnoreCase ) >= 0
			  || ( emsg.IndexOf( "surface", StringComparison.OrdinalIgnoreCase ) >= 0
			    && emsg.IndexOf( "not found", StringComparison.OrdinalIgnoreCase ) >= 0 ) )
			{
				return Task.FromResult<object>( new {
					error       = "Scene trace failed: the surface registry isn't loaded (\"Default Surface not found\") — a known transient editor state. Call restart_editor to rebuild it, then retry the trace.",
					recoverable = true,
					recovery    = "restart_editor"
				} );
			}
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
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "PlayerController";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		// Movement mode — honor the advertised `type` enum. Default first_person (back-compat).
		var type = ( p.TryGetProperty( "type", out var tEl ) ? tEl.GetString() : "first_person" )
			?.ToLowerInvariant() switch
			{
				"third_person" => "third_person",
				"top_down"     => "top_down",
				_              => "first_person"
			};

		// Tunables — these were previously DEAD (hardcoded). Now they flow into the
		// generated [Property] defaults. Invariant-culture formatting so a comma-decimal
		// locale can't emit "300,0f" and break the generated code's compile.
		float moveSpeed   = ReadFloat( p, "moveSpeed",        300f );
		float jumpForce   = ReadFloat( p, "jumpForce",        350f );
		float sprintMult  = ReadFloat( p, "sprintMultiplier", 1.5f );

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

		var code = BuildControllerCode( className, type, moveSpeed, jumpForce, sprintMult );
		// Generated game code is SANDBOXED — write UTF-8 without BOM (the s&box compiler reads it).
		File.WriteAllText( fullPath, code, new UTF8Encoding( false ) );

		// ── Optional in-scene placement (opt-in; default behavior is file-only, back-compat). ──
		// The just-generated component type is NOT in the TypeLibrary until a hotload, so we
		// CANNOT attach it here in the same call. We build the player rig (GO + CharacterController
		// + optional Camera) and report a clear note telling the caller to trigger_hotload then
		// add_component_with_properties (component="<className>") on the returned GameObject.
		bool placeInScene = p.TryGetProperty( "placeInScene", out var pisEl ) && pisEl.ValueKind == JsonValueKind.True;
		object placed = null;
		string note = null;

		if ( placeInScene )
		{
			placed = BuildPlayerRig( p, className, type, out note );
		}

		return Task.FromResult<object>( new
		{
			created = true,
			path = $"{directory}/{fileName}",
			className,
			type,
			moveSpeed, jumpForce, sprintMultiplier = sprintMult,
			gameObject = placed,
			note
		} );
	}

	static float ReadFloat( JsonElement p, string key, float fallback )
	{
		if ( !p.TryGetProperty( key, out var e ) ) return fallback;
		if ( e.ValueKind == JsonValueKind.Number && e.TryGetSingle( out var f ) ) return f;
		if ( e.ValueKind == JsonValueKind.String
		     && float.TryParse( e.GetString(), System.Globalization.NumberStyles.Float,
		                        System.Globalization.CultureInfo.InvariantCulture, out var fs ) ) return fs;
		return fallback;
	}

	// ── Build the player rig: a GO at spawnPosition with a CharacterController and (for
	//    FP/TP) a child Camera. Reuses the same scene-mutation APIs the other handlers use.
	//    Does NOT attach the generated controller component (needs a hotload first).
	static object BuildPlayerRig( JsonElement p, string className, string type, out string note )
	{
		note = null;
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) { note = "No active scene to place into."; return null; }

		var go = scene.CreateObject( true );
		go.Name = className;
		go.Tags.Add( "player" );

		if ( p.TryGetProperty( "spawnPosition", out var sp ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( sp );

		// CharacterController is a built-in type — always in the TypeLibrary, safe to add now.
		try { go.AddComponent<CharacterController>(); }
		catch ( Exception ex ) { note = $"CharacterController add failed: {ex.Message}"; }

		// Camera: child for FP/TP at eye height; fixed overhead for top_down.
		bool createCamera = !p.TryGetProperty( "createCamera", out var cc ) || cc.ValueKind != JsonValueKind.False;
		if ( createCamera )
		{
			try
			{
				var camGo = scene.CreateObject( true );
				camGo.Name = "Camera";
				var cam = camGo.AddComponent<CameraComponent>();

				switch ( type )
				{
					case "top_down":
						// Fixed camera high above the spawn, looking straight down.
						camGo.WorldPosition = go.WorldPosition + Vector3.Up * 600f;
						camGo.WorldRotation = Rotation.From( 90f, 0f, 0f ); // pitch down
						break;
					case "third_person":
						camGo.SetParent( go, keepWorldPosition: false );
						camGo.LocalPosition = new Vector3( -150f, 0f, 80f ); // boom behind + above
						camGo.LocalRotation = Rotation.From( 10f, 0f, 0f );
						break;
					default: // first_person
						camGo.SetParent( go, keepWorldPosition: false );
						camGo.LocalPosition = new Vector3( 0f, 0f, 64f ); // eye height
						camGo.LocalRotation = Rotation.Identity;
						break;
				}
			}
			catch ( Exception ex )
			{
				note = ( note == null ? "" : note + " " ) + $"Camera setup failed: {ex.Message}";
			}
		}

		note = ( note == null ? "" : note + " " ) +
			$"Built the player rig (GameObject + CharacterController" + ( createCamera ? " + Camera" : "" ) +
			$"). The {className} controller is NOT attached yet — it's not in the TypeLibrary until a recompile. " +
			$"Next: trigger_hotload, then add_component_with_properties (id=this GameObject, component=\"{className}\").";

		return ClaudeBridge.SerializeGo( go );
	}

	// ── Generate the controller .cs by movement mode. EVERY API used here is sandbox-legal
	//    and was verified against the live type library:
	//      Input.AnalogMove (.x/.y), Input.AnalogLook (.yaw/.pitch), Input.Pressed/Down(string),
	//      CharacterController.IsOnGround/Punch/Accelerate/ApplyFriction(f)/Move(),
	//      Scene.Camera (CameraComponent), MathX (NOT System.Math). ApplyFriction(10f) matches
	//      the long-shipping FP template's proven single-arg call.
	static string BuildControllerCode( string className, string type, float moveSpeed, float jumpForce, float sprintMult )
	{
		string Inv( float v ) => v.ToString( System.Globalization.CultureInfo.InvariantCulture ) + "f";
		string ms = Inv( moveSpeed );
		string jf = Inv( jumpForce );
		string sm = Inv( sprintMult );

		switch ( type )
		{
			case "top_down":
				// Screen-relative WASD, no mouse-look, no jump. A fixed overhead camera frames the player.
				return $@"using Sandbox;

/// <summary>
/// {className} — TOP-DOWN movement. WASD moves the player on the XY plane relative to the
/// world (the camera looks straight down). Self-contained; pair it with an overhead Camera.
/// </summary>
public sealed class {className} : Component
{{
	[Property] public float MoveSpeed {{ get; set; }} = {ms};
	[Property] public float SprintMultiplier {{ get; set; }} = {sm};

	private CharacterController _controller;

	protected override void OnStart()
	{{
		_controller = GetOrAddComponent<CharacterController>();
	}}

	protected override void OnUpdate()
	{{
		if ( _controller is null ) return;

		// Screen-relative on the ground plane. AnalogMove.x = strafe, .y = forward.
		float speed = MoveSpeed * ( Input.Down( ""run"" ) ? SprintMultiplier : 1f );
		var wish = new Vector3( Input.AnalogMove.y, Input.AnalogMove.x, 0f ).Normal * speed;

		_controller.Accelerate( wish );
		_controller.ApplyFriction( 8f );
		_controller.Move();
	}}
}}
";

			case "third_person":
				// Camera-relative WASD + mouse yaw on the body + jump/sprint. Camera is a child boom.
				return $@"using Sandbox;

/// <summary>
/// {className} — THIRD-PERSON movement. Mouse yaw turns the body; WASD moves relative to the
/// body's facing; Space jumps; the 'run' action sprints. Put a Camera child behind/above the
/// player (a boom). Self-contained — no hard dependency on any other script.
/// </summary>
public sealed class {className} : Component
{{
	[Property] public float MoveSpeed {{ get; set; }} = {ms};
	[Property] public float JumpForce {{ get; set; }} = {jf};
	[Property] public float SprintMultiplier {{ get; set; }} = {sm};

	private CharacterController _controller;

	protected override void OnStart()
	{{
		_controller = GetOrAddComponent<CharacterController>();
	}}

	protected override void OnUpdate()
	{{
		if ( _controller is null ) return;

		// Mouse yaw turns the whole body (the camera boom is a child, so it follows).
		var ang = WorldRotation.Angles();
		ang.yaw += Input.AnalogLook.yaw;
		ang.pitch = 0f;
		ang.roll = 0f;
		WorldRotation = ang.ToRotation();

		// WASD relative to where the body faces.
		float speed = MoveSpeed * ( Input.Down( ""run"" ) ? SprintMultiplier : 1f );
		var wish = ( WorldRotation.Forward * Input.AnalogMove.x + WorldRotation.Left * Input.AnalogMove.y ).Normal * speed;

		if ( _controller.IsOnGround && Input.Pressed( ""jump"" ) )
			_controller.Punch( Vector3.Up * JumpForce );

		_controller.Accelerate( wish );
		_controller.ApplyFriction( 10f );
		_controller.Move();
	}}
}}
";

			default: // first_person
				// WASD relative to facing + full mouse-look (body yaw, camera pitch) + jump/sprint.
				return $@"using Sandbox;

/// <summary>
/// {className} — FIRST-PERSON movement. Mouse yaw turns the body, mouse pitch tilts the child
/// Camera (clamped); WASD moves relative to facing; Space jumps; the 'run' action sprints.
/// Put a Camera child at eye height. Self-contained.
/// </summary>
public sealed class {className} : Component
{{
	[Property] public float MoveSpeed {{ get; set; }} = {ms};
	[Property] public float JumpForce {{ get; set; }} = {jf};
	[Property] public float SprintMultiplier {{ get; set; }} = {sm};

	private CharacterController _controller;
	private float _pitch;

	protected override void OnStart()
	{{
		_controller = GetOrAddComponent<CharacterController>();
	}}

	protected override void OnUpdate()
	{{
		if ( _controller is null ) return;

		// Body yaw from the mouse.
		var ang = WorldRotation.Angles();
		ang.yaw += Input.AnalogLook.yaw;
		ang.pitch = 0f;
		ang.roll = 0f;
		WorldRotation = ang.ToRotation();

		// Camera pitch from the mouse (clamped). Scene.Camera is the active CameraComponent.
		_pitch = MathX.Clamp( _pitch + Input.AnalogLook.pitch, -89f, 89f );
		if ( Scene?.Camera is not null )
			Scene.Camera.WorldRotation = Rotation.From( _pitch, ang.yaw, 0f );

		// WASD relative to facing.
		float speed = MoveSpeed * ( Input.Down( ""run"" ) ? SprintMultiplier : 1f );
		var wish = ( WorldRotation.Forward * Input.AnalogMove.x + WorldRotation.Left * Input.AnalogMove.y ).Normal * speed;

		if ( _controller.IsOnGround && Input.Pressed( ""jump"" ) )
			_controller.Punch( Vector3.Up * JumpForce );

		_controller.Accelerate( wish );
		_controller.ApplyFriction( 10f );
		_controller.Move();
	}}
}}
";
		}
	}
}

public class CreateNpcControllerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NpcController";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

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
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "GameManager";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

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
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "TriggerZone";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

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
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var componentName = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );
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
		var filePath     = p.GetProperty( "path" ).GetString();
		var propertyName = p.GetProperty( "propertyName" ).GetString();
		var propertyType = p.TryGetProperty( "propertyType", out var ptProp ) ? ptProp.GetString() ?? "float" : "float";
		var defaultValue = p.TryGetProperty( "defaultValue", out var dvProp ) ? dvProp.GetString() : null;
		if ( !ClaudeBridge.TryResolveProjectPath( filePath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );

		// Build the attribute: optional SyncFlags (e.g. "Interpolate") for smoothly-changing values.
		var syncFlags = p.TryGetProperty( "syncFlags", out var sf ) ? sf.GetString() : null;
		var syncAttr  = string.IsNullOrWhiteSpace( syncFlags ) ? "[Sync]" : $"[Sync( SyncFlags.{syncFlags} )]";

		// Find the property declaration and add the attribute above it if not already present.
		var lines     = content.Split( '\n' ).ToList();
		bool modified = false;

		for ( int i = 0; i < lines.Count; i++ )
		{
			if ( lines[i].Contains( propertyName ) && lines[i].Contains( "public" ) && lines[i].Contains( "{" ) )
			{
				if ( i > 0 && lines[i - 1].TrimStart().StartsWith( "[Sync" ) )
				{
					return Task.FromResult<object>( new { error = $"Property '{propertyName}' already has [Sync]" } );
				}

				var indent = new string( '\t', lines[i].TakeWhile( c => c == '\t' ).Count() );
				lines.Insert( i, $"{indent}{syncAttr}" );
				modified = true;
				break;
			}
		}

		if ( !modified )
			return Task.FromResult<object>( new { error = $"Property '{propertyName}' not found in file" } );

		File.WriteAllText( fullPath, string.Join( '\n', lines ) );
		return Task.FromResult<object>( new { added = true, path = filePath, property = propertyName, attribute = syncAttr } );
	}
}

public class AddRpcMethodHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var filePath   = p.GetProperty( "path" ).GetString();
		var methodName = p.TryGetProperty( "methodName", out var m ) ? m.GetString() : "MyRpc";
		var rpcType    = p.TryGetProperty( "rpcType", out var rt ) ? rt.GetString() : "Broadcast";
		if ( !ClaudeBridge.TryResolveProjectPath( filePath, out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );
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

		var methodParams = p.TryGetProperty( "methodParams", out var mp ) ? ( mp.GetString() ?? "" ) : "";
		var methodCode = $"\n\t{rpcAttr}\n\tpublic void {methodName}( {methodParams} )\n\t{{\n\t\t// TODO: implement RPC\n\t}}\n";
		content = content.Insert( lastBrace, methodCode );
		File.WriteAllText( fullPath, content );

		return Task.FromResult<object>( new { added = true, path = filePath, method = methodName, attribute = rpcAttr, parameters = methodParams } );
	}
}

public class CreateNetworkedPlayerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NetworkedPlayer";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";
		float moveSpeed = p.TryGetProperty( "moveSpeed", out var msEl ) && msEl.TryGetSingle( out var msv ) ? msv : 200f;
		string moveSpeedStr = moveSpeed.ToString( System.Globalization.CultureInfo.InvariantCulture ) + "f";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	[Sync] public string PlayerName {{ get; set; }}
	[Sync] public int    Health     {{ get; set; }} = 100;

	[Property] public float MoveSpeed {{ get; set; }} = {moveSpeedStr};

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
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "LobbyManager";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

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
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NetworkEvents";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

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
		if ( !ClaudeBridge.TryResolveProjectPath( sourcePath, out var fullSource, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

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
			// Ensure we have a mutable material override — auto-create one from the default
			// shader if none is assigned, so callers don't need a separate assign_material step.
			var mat = renderer.MaterialOverride;
			bool autoCreatedMaterial = false;
			if ( mat == null )
			{
				mat = Material.Create( "auto_override", "shaders/complex.shader", true );
				if ( mat == null )
					return Task.FromResult<object>( new { error = "No MaterialOverride and failed to create a default material" } );
				renderer.MaterialOverride = mat;
				autoCreatedMaterial = true;
			}

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

			return Task.FromResult<object>( new { set = true, id, property = propertyName, autoCreatedMaterial } );
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

// ═══════════════════════════════════════════════════════════════════
// Batch 37 — Inspection & validation (mined from 27 shipped s&box games)
// The 3 self-contained handlers (networking_lint / scene_validate / save_inspect)
// are solid; inspect_networked_object / services_query / simulate_input touch
// drift-prone engine API — verify members via describe_type before relying on them.
// ═══════════════════════════════════════════════════════════════════

/// <summary>Per-object networking contract: Network.* state + each component's [Sync] fields and current values.</summary>
public class InspectNetworkedObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );
		var id = p.TryGetProperty( "id", out var idEl ) ? idEl.GetString() : null;
		if ( string.IsNullOrEmpty( id ) )
			return Task.FromResult<object>( new { error = "id is required" } );
		var go = ClaudeBridge.ResolveGameObject( scene, id );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );
		bool allProps = p.TryGetProperty( "allProps", out var ap ) && ap.ValueKind == JsonValueKind.True;

		object net;
		try
		{
			var n = go.Network;
			net = new
			{
				active        = n.Active,
				isProxy       = n.IsProxy,
				isOwner       = n.IsOwner,
				isCreator     = n.IsCreator,
				ownerId       = n.OwnerId.ToString(),
				ownerSteamId  = n.Owner?.SteamId.ToString(),
				ownerTransfer = n.OwnerTransfer.ToString(),
				orphaned      = n.NetworkOrphaned.ToString(),
				flags         = n.Flags.ToString()
			};
		}
		catch ( Exception ex ) { net = new { note = $"network state unavailable: {ex.Message}" }; }

		var components = new List<object>();
		foreach ( var c in go.Components.GetAll() )
		{
			var td = Game.TypeLibrary.GetType( c.GetType() );
			if ( td == null ) continue;
			var fields = new List<object>();
			foreach ( var prop in td.Properties )
			{
				bool isSync = false; string syncFlags = null;
				try
				{
					var attr = prop.GetCustomAttribute<Sandbox.SyncAttribute>();
					if ( attr != null ) { isSync = true; try { syncFlags = attr.Flags.ToString(); } catch { } }
				}
				catch { }
				if ( !isSync && !allProps ) continue;
				object val; try { val = prop.GetValue( c )?.ToString(); } catch { val = "<error>"; }
				fields.Add( new { name = prop.Name, type = prop.PropertyType?.Name, isSync, syncFlags, value = val } );
			}
			if ( fields.Count > 0 )
				components.Add( new { component = c.GetType().Name, fields } );
		}
		return Task.FromResult<object>( new { id = go.Id.ToString(), name = go.Name, network = net, components } );
	}
}

/// <summary>Static scan of the project's C# for the highest-frequency networking/authority bugs.</summary>
public class NetworkingLintHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( root ) )
			return Task.FromResult<object>( new { error = "No current project" } );
		var sub = p.TryGetProperty( "path", out var sp ) ? sp.GetString() : null;
		var scanDir = string.IsNullOrEmpty( sub ) ? root : Path.Combine( root, sub );
		if ( !Directory.Exists( scanDir ) )
			return Task.FromResult<object>( new { error = $"Path not found: {sub}" } );

		var findings = new List<object>();
		var files = Directory.GetFiles( scanDir, "*.cs", SearchOption.AllDirectories )
			.Where( f => f.IndexOf( "\\obj\\", StringComparison.OrdinalIgnoreCase ) < 0
					  && f.IndexOf( "/obj/", StringComparison.OrdinalIgnoreCase ) < 0
					  && f.IndexOf( "Libraries", StringComparison.OrdinalIgnoreCase ) < 0 )
			.ToList();

		var rxAuthField  = new System.Text.RegularExpressions.Regex( @"\[Sync\]\s*public", System.Text.RegularExpressions.RegexOptions.IgnoreCase );
		var rxAuthName   = new System.Text.RegularExpressions.Regex( @"\b(money|cash|coins?|balance|wallet|funds|health|score|prestige|gems?|tokens?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase );
		var rxCollection = new System.Text.RegularExpressions.Regex( @"\[Sync[^\]]*\]\s*public\s+(List\s*<|Dictionary\s*<)" );
		var rxNonRepl    = new System.Text.RegularExpressions.Regex( @"\[Sync[^\]]*\]\s*public\s+(Connection|GameObject)\b" );

		foreach ( var file in files )
		{
			string[] lines; try { lines = File.ReadAllLines( file ); } catch { continue; }
			var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
			for ( int i = 0; i < lines.Length; i++ )
			{
				var line = lines[i];
				if ( rxAuthField.IsMatch( line ) && rxAuthName.IsMatch( line ) )
					findings.Add( new { file = rel, line = i + 1, rule = "sync-authoritative-field", fix = "Cheat-sensitive field on plain [Sync] — clients can author it. Use [Sync(SyncFlags.FromHost)].", code = line.Trim() } );
				if ( rxCollection.IsMatch( line ) )
					findings.Add( new { file = rel, line = i + 1, rule = "sync-collection", fix = "[Sync] List/Dictionary doesn't replicate granularly. Use NetList<>/NetDictionary<>.", code = line.Trim() } );
				if ( rxNonRepl.IsMatch( line ) )
					findings.Add( new { file = rel, line = i + 1, rule = "sync-nonreplicable", fix = "Connection/GameObject can't be [Sync]'d (local handle). Sync a Guid and resolve it.", code = line.Trim() } );
			}
			var text = string.Join( "\n", lines );
			foreach ( System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches( text, @"\[Rpc\.Host\]" ) )
			{
				var seg = text.Substring( m.Index, Math.Min( 700, text.Length - m.Index ) );
				if ( !seg.Contains( "Rpc.Caller" ) && !seg.Contains( "HasAccess" ) && !seg.Contains( "IsHost" ) )
				{
					int ln = 1; for ( int k = 0; k < m.Index; k++ ) if ( text[k] == '\n' ) ln++;
					findings.Add( new { file = rel, line = ln, rule = "rpc-host-unchecked", fix = "Re-validate Rpc.Caller / ownership and re-clamp inside the [Rpc.Host] body — forged args bypass NetFlags.", code = "[Rpc.Host]" } );
				}
			}
		}
		return Task.FromResult<object>( new { scannedFiles = files.Count, findingCount = findings.Count, findings = findings.Take( 200 ).ToList() } );
	}
}

/// <summary>Static scan of project C# for s&box sandbox whitelist violations — detects banned APIs before they fail to compile.</summary>
public class SandboxLintHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( root ) )
			return Task.FromResult<object>( new { error = "No current project" } );

		var dirParam = p.TryGetProperty( "directory", out var dp ) && !string.IsNullOrWhiteSpace( dp.GetString() ) ? dp.GetString() : "Code";
		string scanDir;
		if ( !ClaudeBridge.TryResolveProjectPath( dirParam, out scanDir, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( !Directory.Exists( scanDir ) )
			return Task.FromResult<object>( new { error = $"Directory not found: {dirParam}" } );

		var findings = new List<object>();
		var files = Directory.GetFiles( scanDir, "*.cs", SearchOption.AllDirectories )
			.Where( f => f.IndexOf( "\\obj\\",  StringComparison.OrdinalIgnoreCase ) < 0
					  && f.IndexOf( "/obj/",    StringComparison.OrdinalIgnoreCase ) < 0
					  && f.IndexOf( "\\bin\\",  StringComparison.OrdinalIgnoreCase ) < 0
					  && f.IndexOf( "/bin/",    StringComparison.OrdinalIgnoreCase ) < 0
					  && f.IndexOf( "Libraries", StringComparison.OrdinalIgnoreCase ) < 0 )
			.ToList();

		// Detection table — each entry: (regex, advice)
		// These patterns match common sandbox violations. All regexes are case-sensitive
		// unless the real API is case-sensitive (it is in C#).
		// NOTE (verified live 2026-06-09): System.Math and System.MathF now COMPILE in
		// game code on the current SDK — the old "use MathX" rules were removed as stale.
		// Array.Clone() is still rejected ("System.Array.Clone() is not allowed when
		// whitelist is enabled" — confirmed via a live compile). GameObject.Clone() is a
		// legitimate s&box API, so the .Clone() rule is advisory, not a hard error.
		var rules = new (System.Text.RegularExpressions.Regex Rx, string Advice)[]
		{
			(
				new System.Text.RegularExpressions.Regex( @"\.Clone\(\)" ),
				"If this is an ARRAY .Clone() it will not compile (System.Array.Clone() is whitelist-blocked) — use .ToArray(). GameObject.Clone() and other s&box Clone APIs are fine; ignore this finding for those."
			),
			(
				new System.Text.RegularExpressions.Regex( @"System\.Net\b" ),
				"System.Net is blocked in the s&box sandbox — use Sandbox.Http for outbound HTTP requests"
			),
			(
				new System.Text.RegularExpressions.Regex( @"\bHttpListener\b|\bTcpListener\b|\bTcpClient\b" ),
				"Raw sockets (HttpListener/TcpListener/TcpClient) are blocked in the sandbox — use Sandbox.Http for outbound requests"
			),
			(
				new System.Text.RegularExpressions.Regex( @"System\.IO\.File\b|\bFile\.ReadAllText\b|\bFile\.WriteAllText\b" ),
				"System.IO.File is blocked in s&box game code — use FileSystem.Data (or FileSystem.Mounted) instead"
			),
			(
				new System.Text.RegularExpressions.Regex( @"\bSystem\.Threading\.Thread\b|new Thread\(" ),
				"Raw System.Threading.Thread is blocked in the sandbox — use async/Task or GameTask for async work"
			),
		};

		foreach ( var file in files )
		{
			string[] lines; try { lines = File.ReadAllLines( file ); } catch { continue; }
			var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
			for ( int i = 0; i < lines.Length; i++ )
			{
				var line = lines[i];
				foreach ( var (rx, advice) in rules )
				{
					var m = rx.Match( line );
					if ( m.Success )
						findings.Add( new { file = rel, line = i + 1, match = m.Value, advice } );
				}
			}
		}

		return Task.FromResult<object>( new { scanned = files.Count, findings, clean = findings.Count == 0 } );
	}
}

/// <summary>
/// Static scan of .razor and .razor.scss files for common Razor/stylesheet pitfalls:
/// switch expressions in @code (crash the transpiler), non-ASCII in @code
/// (crashes the transpiler), PanelComponent without BuildHash (panel never re-renders),
/// and root type-selector CSS rules (silently skipped by the stylesheet engine).
/// Returns { scanned, findings:[{file,line,match,advice}], clean } like sandbox_lint.
/// </summary>
public class RazorLintHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( root ) )
			return Task.FromResult<object>( new { error = "No current project" } );

		var dirParam = p.TryGetProperty( "directory", out var dp ) && !string.IsNullOrWhiteSpace( dp.GetString() ) ? dp.GetString() : "Code";
		string scanDir;
		if ( !ClaudeBridge.TryResolveProjectPath( dirParam, out scanDir, out var pathErr ) )
			return Task.FromResult<object>( new { error = pathErr } );

		if ( !Directory.Exists( scanDir ) )
			return Task.FromResult<object>( new { error = $"Directory not found: {dirParam}" } );

		var findings = new List<object>();

		// Collect .razor and .razor.scss files, skip obj/bin/Libraries (same as sandbox_lint).
		static bool IsSkipped( string f ) =>
			f.IndexOf( "\\obj\\",     StringComparison.OrdinalIgnoreCase ) >= 0 ||
			f.IndexOf( "/obj/",       StringComparison.OrdinalIgnoreCase ) >= 0 ||
			f.IndexOf( "\\bin\\",     StringComparison.OrdinalIgnoreCase ) >= 0 ||
			f.IndexOf( "/bin/",       StringComparison.OrdinalIgnoreCase ) >= 0 ||
			f.IndexOf( "Libraries",   StringComparison.OrdinalIgnoreCase ) >= 0;

		var razorFiles = Directory.GetFiles( scanDir, "*.razor", SearchOption.AllDirectories )
			.Where( f => !IsSkipped( f ) ).ToList();
		var scssFiles  = Directory.GetFiles( scanDir, "*.razor.scss", SearchOption.AllDirectories )
			.Where( f => !IsSkipped( f ) ).ToList();

		// Regex tools (all ASCII patterns, constructed once).
		var rxSwitchExpr     = new System.Text.RegularExpressions.Regex( @"[\w\)\]]\s+switch\s*\{" );
		var rxRootTypeSelector = new System.Text.RegularExpressions.Regex( @"^[A-Z][A-Za-z0-9_]*\s*\{", System.Text.RegularExpressions.RegexOptions.Multiline );

		foreach ( var file in razorFiles )
		{
			string text; try { text = File.ReadAllText( file ); } catch { continue; }
			string[] lines = text.Split( '\n' );
			var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );

			// Find @code { ... } block ranges using brace matching.
			var codeRanges = FindCodeBlockLines( lines );

			for ( int i = 0; i < lines.Length; i++ )
			{
				var line = lines[i];
				bool inCode = codeRanges.Any( r => i >= r.Start && i <= r.End );

				if ( inCode )
				{
					// Rule 1: switch expressions in @code.
					var m1 = rxSwitchExpr.Match( line );
					if ( m1.Success )
						findings.Add( new { file = rel, line = i + 1, match = m1.Value,
							advice = "switch EXPRESSIONS in @code can crash the Razor transpiler with no useful error - use if/else" } );

					// Rule 2: non-ASCII characters in @code.
					for ( int c = 0; c < line.Length; c++ )
					{
						if ( line[c] > '\x7F' )
						{
							findings.Add( new { file = rel, line = i + 1, match = line[c].ToString(),
								advice = "non-ASCII/emoji in @code can crash the Razor transpiler - move it to markup or data" } );
							break; // one finding per line is enough
						}
					}
				}
			}

			// Rule 3 (whole file): PanelComponent without BuildHash.
			if ( text.Contains( "inherits PanelComponent" ) && !text.Contains( "BuildHash" ) )
				findings.Add( new { file = rel, line = 1, match = "inherits PanelComponent",
					advice = "PanelComponent without BuildHash override - panel may not re-render when state changes (and remember it renders nothing without a ScreenPanel/WorldPanel host)" } );
		}

		// .razor.scss rules.
		foreach ( var file in scssFiles )
		{
			string text; try { text = File.ReadAllText( file ); } catch { continue; }
			string[] lines = text.Split( '\n' );
			var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );

			for ( int i = 0; i < lines.Length; i++ )
			{
				var line = lines[i];
				// Rule 4: root (column-0) type selector starting with uppercase.
				// The line must start at column 0 (no leading whitespace).
				if ( line.Length > 0 && line[0] != ' ' && line[0] != '\t' )
				{
					var m = rxRootTypeSelector.Match( line );
					if ( m.Success && m.Index == 0 )
						findings.Add( new { file = rel, line = i + 1, match = m.Value.TrimEnd( '{' ).Trim(),
							advice = "root type-selector rules are silently skipped by the stylesheet engine - use a class selector (.my-panel)" } );
				}
			}
		}

		int scanned = razorFiles.Count + scssFiles.Count;
		return Task.FromResult<object>( new { scanned, findings, clean = findings.Count == 0 } );
	}

	// Brace-match the @code { ... } blocks. Returns a list of (Start, End) line ranges (inclusive).
	static List<(int Start, int End)> FindCodeBlockLines( string[] lines )
	{
		var ranges = new List<(int, int)>();
		int depth  = 0;
		int start  = -1;
		bool inCode = false;

		for ( int i = 0; i < lines.Length; i++ )
		{
			var line = lines[i];
			if ( !inCode )
			{
				// Look for @code on this line (may be followed by { on the same line).
				int atCode = line.IndexOf( "@code", StringComparison.Ordinal );
				if ( atCode >= 0 )
				{
					inCode = true;
					start  = i;
					depth  = 0;
					// Count braces on the same line after @code.
					for ( int c = atCode + 5; c < line.Length; c++ )
					{
						if ( line[c] == '{' ) depth++;
						else if ( line[c] == '}' ) depth--;
					}
					if ( depth <= 0 && start >= 0 ) { ranges.Add( (start, i) ); inCode = false; start = -1; }
				}
			}
			else
			{
				foreach ( char ch in line )
				{
					if ( ch == '{' ) depth++;
					else if ( ch == '}' ) { depth--; if ( depth <= 0 ) break; }
				}
				if ( depth <= 0 ) { ranges.Add( (start, i) ); inCode = false; start = -1; depth = 0; }
			}
		}
		return ranges;
	}
}

/// <summary>Validate the active scene for common setup footguns (no camera, stray root rigidbodies, trigger-vs-trace).</summary>

// ═════════════════════════════════════════════════════════════════════
//  Batch 34 — PLAY-MODE EYES (v1.7.0)
//  capture_view renders a CameraComponent's view to a PNG via
//  CameraComponent.RenderToBitmap + Bitmap.ToPng. Unlike take_screenshot /
//  screenshot_from (EditorScene, EDIT-only), this renders a camera's view of
//  the ACTIVE scene — so during PLAY it captures the RUNNING game (incl. HUD
//  when renderUI=true). No pose  -> the live main camera (player POV).
//  position/id -> a temp camera (created + destroyed in one frame, never
//  disturbs the game's own camera). Saves a uniquely-named PNG to TEMP and
//  returns the absolute path (no 1-second filename collisions).
// ═════════════════════════════════════════════════════════════════════
public class CaptureViewHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		bool playing = Game.IsPlaying;
		var scene = playing ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		int w = p.TryGetProperty( "width", out var wEl ) ? wEl.GetInt32() : 1280;
		int h = p.TryGetProperty( "height", out var hEl ) ? hEl.GetInt32() : 720;
		if ( w < 16 ) w = 16; if ( w > 3840 ) w = 3840;
		if ( h < 16 ) h = 16; if ( h > 2160 ) h = 2160;
		bool renderUI = !( p.TryGetProperty( "renderUI", out var uiEl ) && uiEl.ValueKind == JsonValueKind.False );

		GameObject tempCam = null;
		try
		{
			CameraComponent cam;
			string framed;
			bool hasId = p.TryGetProperty( "id", out var idEl ) && Guid.TryParse( idEl.GetString(), out _ );
			bool hasPos = p.TryGetProperty( "position", out var posEl );

			if ( hasId || hasPos )
			{
				Vector3 camPos; Rotation camRot;
				if ( hasId )
				{
					Guid.TryParse( idEl.GetString(), out var guid );
					var t = scene.Directory.FindByGuid( guid );
					if ( t == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
					var box = t.GetBounds(); var c = box.Center; float sz = box.Size.Length; if ( sz < 1f ) sz = 128f;
					float dist = sz * 1.4f; if ( dist < 150f ) dist = 150f;
					camPos = c + new Vector3( -1f, -0.6f, 0.7f ).Normal * dist;
					camRot = Rotation.LookAt( ( c - camPos ).Normal, Vector3.Up );
					framed = t.Name;
				}
				else
				{
					camPos = ClaudeBridge.ParseVector3( posEl );
					if ( p.TryGetProperty( "lookAt", out var laEl ) )
						camRot = Rotation.LookAt( ( ClaudeBridge.ParseVector3( laEl ) - camPos ).Normal, Vector3.Up );
					else if ( p.TryGetProperty( "rotation", out var rotEl ) )
						camRot = CharacterHelpers.ParseRotation( rotEl );
					else camRot = Rotation.Identity;
					framed = $"({camPos.x:0.#},{camPos.y:0.#},{camPos.z:0.#})";
				}
				tempCam = scene.CreateObject( true );
				tempCam.Name = "__bridge_capture_cam";
				tempCam.WorldPosition = camPos;
				tempCam.WorldRotation = camRot;
				cam = tempCam.AddComponent<CameraComponent>();
				if ( p.TryGetProperty( "fov", out var fovEl ) ) cam.FieldOfView = fovEl.GetSingle();
			}
			else
			{
				cam = VisualHelpers.FindMainCamera( scene );
				if ( cam == null ) return Task.FromResult<object>( new { error = "No main camera in the scene. Pass position {x,y,z} or id to capture from a temporary camera." } );
				framed = playing ? "live main camera (player view)" : "main camera";
			}

			var bmp = new Bitmap( w, h );
			cam.RenderToBitmap( bmp, renderUI );
			byte[] png = bmp.ToPng();
			string path = System.IO.Path.Combine( System.IO.Path.GetTempPath(), $"bridge_capture_{System.Guid.NewGuid():N}.png" );
			System.IO.File.WriteAllBytes( path, png );

			return Task.FromResult<object>( new
			{
				captured = true,
				playing,
				framed,
				width = w,
				height = h,
				renderUI,
				path,
				note = playing
					? "Captured the RUNNING game. Read the PNG at 'path'."
					: "Captured the edit scene from a camera. Read the PNG at 'path'."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"capture_view failed: {ex.Message}" } );
		}
		finally
		{
			tempCam?.Destroy();
		}
	}
}

public class SceneValidateHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );
		var issues = new List<object>();
		var all = scene.GetAllObjects( true ).ToList();

		try
		{
			if ( scene.GetAllComponents<CameraComponent>().FirstOrDefault() == null )
				issues.Add( new { severity = "high", issue = "No CameraComponent in the scene — nothing renders in play unless a controller spawns one.", fix = "Add a GameObject with a CameraComponent, or a PlayerController that creates one." } );
		}
		catch { }
		try
		{
			var rbRoots = all.Where( o => o.Parent == scene && o.Components.Get<Rigidbody>() != null ).ToList();
			if ( rbRoots.Count > 1 )
				issues.Add( new { severity = "medium", issue = $"{rbRoots.Count} Rigidbodies at the scene root.", fix = "Give each dynamic body its own GameObject; a child Rigidbody also breaks collider binding to the root." } );
		}
		catch { }
		try
		{
			int triggers = scene.GetAllComponents<Collider>().Count( c => { try { return c.IsTrigger; } catch { return false; } } );
			if ( triggers > 0 )
				issues.Add( new { severity = "info", issue = $"{triggers} IsTrigger collider(s) — Scene.Trace ignores triggers by default.", fix = "Triggers fire via ITriggerListener, not traces; if a trace must hit them, configure tags/UseHitboxes." } );
		}
		catch { }

		return Task.FromResult<object>( new { objectCount = all.Count, issueCount = issues.Count, issues } );
	}
}

/// <summary>Inspect FileSystem.Data save files: list / read / diff.</summary>
public class SaveInspectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var action = p.TryGetProperty( "action", out var a ) ? a.GetString() : "list";
		var path   = p.TryGetProperty( "path", out var pp ) ? ( pp.GetString() ?? "" ) : "";
		try
		{
			var fs = Sandbox.FileSystem.Data;
			if ( action == "list" )
			{
				var dir = path;
				var dirs  = fs.FindDirectory( dir, "*", false ).ToList();
				var files = fs.FindFile( dir, "*", false ).Select( f =>
				{
					var full = string.IsNullOrEmpty( dir ) ? f : ( dir.TrimEnd( '/' ) + "/" + f );
					long size = 0; try { size = fs.FileSize( full ); } catch { }
					return new { name = f, path = full, size };
				} ).ToList();
				return Task.FromResult<object>( new { path = dir, directories = dirs, files } );
			}
			if ( action == "read" )
			{
				if ( !fs.FileExists( path ) )
					return Task.FromResult<object>( new { error = $"Save file not found: {path}" } );
				var json = fs.ReadAllText( path );
				return Task.FromResult<object>( new { path, length = json.Length, content = json.Length > 60000 ? json.Substring( 0, 60000 ) + "\n…[truncated]" : json } );
			}
			if ( action == "diff" )
			{
				var pathB = p.TryGetProperty( "pathB", out var pb ) ? pb.GetString() : null;
				if ( string.IsNullOrEmpty( pathB ) || !fs.FileExists( path ) || !fs.FileExists( pathB ) )
					return Task.FromResult<object>( new { error = "Both 'path' and 'pathB' must be existing save files for diff" } );
				var diffs = new List<object>();
				try
				{
					using var da = System.Text.Json.JsonDocument.Parse( fs.ReadAllText( path ) );
					using var db = System.Text.Json.JsonDocument.Parse( fs.ReadAllText( pathB ) );
					DiffJson( "", da.RootElement, db.RootElement, diffs );
				}
				catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"diff parse failed: {ex.Message}" } ); }
				return Task.FromResult<object>( new { a = path, b = pathB, diffCount = diffs.Count, diffs = diffs.Take( 200 ).ToList() } );
			}
			return Task.FromResult<object>( new { error = $"Unknown action: {action}" } );
		}
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"save_inspect failed: {ex.Message}" } ); }
	}

	static void DiffJson( string prefix, JsonElement a, JsonElement b, List<object> outp )
	{
		if ( a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object )
		{
			var keys = new HashSet<string>();
			foreach ( var pr in a.EnumerateObject() ) keys.Add( pr.Name );
			foreach ( var pr in b.EnumerateObject() ) keys.Add( pr.Name );
			foreach ( var k in keys )
			{
				bool ha = a.TryGetProperty( k, out var av );
				bool hb = b.TryGetProperty( k, out var bv );
				var key = string.IsNullOrEmpty( prefix ) ? k : prefix + "." + k;
				if ( !ha ) outp.Add( new { key, change = "added", value = bv.ToString() } );
				else if ( !hb ) outp.Add( new { key, change = "removed", value = av.ToString() } );
				else DiffJson( key, av, bv, outp );
			}
		}
		else if ( a.ToString() != b.ToString() )
		{
			outp.Add( new { key = prefix, change = "changed", a = a.ToString(), b = b.ToString() } );
		}
	}
}

/// <summary>Read Sandbox.Services stats / leaderboards. NOTE: verify the Services API via describe_type before trusting.</summary>
public class ServicesQueryHandler : IBridgeHandler
{
	public async Task<object> Execute( JsonElement p )
	{
		var action = p.TryGetProperty( "action", out var a ) ? a.GetString() : "stats";
		var name   = p.TryGetProperty( "name", out var nm ) ? nm.GetString() : null;
		int limit  = p.TryGetProperty( "limit", out var l ) ? l.GetInt32() : 10;
		try
		{
			if ( action == "leaderboard" )
			{
				if ( string.IsNullOrEmpty( name ) )
					return new { error = "leaderboard 'name' is required" };
				var board = Sandbox.Services.Leaderboards.Get( name );
				board.MaxEntries = limit;
				await board.Refresh( default );
				return new { board = name, displayName = board.DisplayName, totalEntries = board.TotalEntries, count = board.Entries?.Length ?? 0, entries = board.Entries?.Take( limit ).ToArray() };
			}
			// stats — needs the package ident; build it from the project config
			var cfg = Project.Current?.Config;
			var ident = cfg == null ? "" : ( string.IsNullOrEmpty( cfg.Org ) ? cfg.Ident : $"{cfg.Org}.{cfg.Ident}" );
			var ps = Sandbox.Services.Stats.GetLocalPlayerStats( ident );
			await ps.Refresh();
			if ( !string.IsNullOrEmpty( name ) )
			{
				var s = ps.Get( name );
				return new { ident, stat = name, value = s.Value, sum = s.Sum, min = s.Min, max = s.Max, lastValue = s.LastValue, valueString = s.ValueString };
			}
			return new { ident, note = "Pass 'name' to read a stat (Value/Sum/Min/Max/LastValue). For a board, action='leaderboard'." };
		}
		catch ( Exception ex ) { return new { error = $"services_query failed: {ex.Message}" }; }
	}
}

/// <summary>Synthesize player input during play. NOTE: input-injection API must be verified live via describe_type "Sandbox.Input".</summary>
public class SimulateInputHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "simulate_input requires play mode" } );
		var action = p.TryGetProperty( "action", out var a ) ? a.GetString() : null;
		var state  = p.TryGetProperty( "state", out var s ) ? s.GetString() : "press";
		if ( string.IsNullOrEmpty( action ) )
			return Task.FromResult<object>( new { error = "action is required — a named input action like 'jump', 'attack1', 'use'. (Analog move/look injection is not supported by Sandbox.Input.)" } );
		try
		{
			bool down = state != "release";
			Sandbox.Input.SetAction( action, down );
			return Task.FromResult<object>( new
			{
				action,
				state = down ? "down" : "up",
				note = "SetAction sets the action for the current input frame; for a sustained hold call it each tick. Analog (move/look) has no injection API — drive the controller directly or use a real device."
			} );
		}
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"simulate_input failed: {ex.Message}" } ); }
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
				relativePath  = asset.RelativePath,
				// A freshly-installed CODE library adds a PackageReference the running editor
				// hasn't compiled — trigger_hotload will NOT surface its types. (v1.10.0
				// auto-handled: the install path now tells the caller to restart.)
				restartRecommended = true,
				note = "If this installed a code LIBRARY (new PackageReference), trigger_hotload will NOT make its types available — call restart_editor so the new package compiles into the project."
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
		try
		{
			var root = Project.Current?.GetRootPath();
			if ( string.IsNullOrEmpty( root ) )
				return Task.FromResult<object>( new { error = "No current project" } );

			// s&box recompiles when it detects a source change, but it can miss files
			// edited directly on disk (via write_file/edit_script). Bumping the project's
			// .csproj timestamps nudges the watcher to re-scan and recompile. Skip
			// Libraries/ (packages) so we only touch this project's code.
			var touched = new List<string>();
			foreach ( var csproj in Directory.GetFiles( root, "*.csproj", SearchOption.AllDirectories ) )
			{
				if ( csproj.Replace( '\\', '/' ).Contains( "/Libraries/" ) ) continue;
				try { File.SetLastWriteTimeUtc( csproj, DateTime.UtcNow ); touched.Add( Path.GetRelativePath( root, csproj ).Replace( '\\', '/' ) ); }
				catch { }
			}

			return Task.FromResult<object>( new
			{
				triggered = touched.Count > 0,
				touched,
				note = touched.Count > 0
					? "Bumped .csproj timestamps to nudge a recompile. If changes still don't apply, enter+exit play mode or use restart_editor (the reliable path for externally-edited C#)."
					: "No project .csproj found to touch. Enter+exit play mode or use restart_editor to force a recompile.",
				packageNote = "A newly-added PackageReference (installed library dependency) is NEVER resolved by hotload — use restart_editor for new package dependencies."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"trigger_hotload failed: {ex.Message}" } );
		}
	}
}

// ═══════════════════════════════════════════════════════════════════
// Status dock — purely informational. The main-thread frame handler
// lives on ClaudeBridge.OnEditorFrame so RPCs are processed even when
// this dock is closed (GitHub issue #2).
// ═══════════════════════════════════════════════════════════════════

[Dock( "Editor", "Claude Bridge", "smart_toy" )]
public class BridgePoller : Widget
{
	public BridgePoller( Widget parent ) : base( parent )
	{
		MinimumSize = new Vector2( 200, 80 );
		WindowTitle = "Claude Bridge";

		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 4;

		var title = Layout.Add( new Label( "Claude Bridge", this ) );
		title.SetStyles( "font-size: 14px; font-weight: bold; color: white;" );

		var status = Layout.Add( new Label( $"Handlers: {ClaudeBridge.HandlerCount} | IPC Active", this ) );
		status.SetStyles( "font-size: 11px; color: #aaa;" );

		Layout.AddSpacingCell( 8 );

		var credit = Layout.Add( new Label( "A project by sboxskins.gg", this ) );
		credit.SetStyles( "font-size: 11px; color: #4fc3f7;" );

		var url = Layout.Add( new Label( "https://sboxskins.gg", this ) );
		url.SetStyles( "font-size: 10px; color: #888;" );
	}
}

/// <summary>
/// Generates a smooth heightmap terrain mesh via MCP.
/// Params: size (float), resolution (int), hills (array of {x,y,radius,height}),
///         clearings (array of {x,y,radius}), name (string)
/// </summary>
public class BuildTerrainMeshHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var size = p.TryGetProperty( "size", out var sz ) ? sz.GetSingle() : 9600f;
		var resolution = p.TryGetProperty( "resolution", out var res ) ? res.GetInt32() : 64;
		var name = p.TryGetProperty( "name", out var nm ) ? nm.GetString() : "Generated Terrain";

		// Parse hills: [{x, y, radius, height}]
		var hills = new System.Collections.Generic.List<(Vector2 pos, float radius, float height)>();
		if ( p.TryGetProperty( "hills", out var hillsArr ) && hillsArr.ValueKind == JsonValueKind.Array )
		{
			foreach ( var h in hillsArr.EnumerateArray() )
			{
				var hx = h.TryGetProperty( "x", out var hxp ) ? hxp.GetSingle() : 0;
				var hy = h.TryGetProperty( "y", out var hyp ) ? hyp.GetSingle() : 0;
				var hr = h.TryGetProperty( "radius", out var hrp ) ? hrp.GetSingle() : 500;
				var hh = h.TryGetProperty( "height", out var hhp ) ? hhp.GetSingle() : 100;
				hills.Add( (new Vector2( hx, hy ), hr, hh) );
			}
		}

		// Parse clearings: [{x, y, radius}]
		var clearings = new System.Collections.Generic.List<(Vector2 pos, float radius)>();
		if ( p.TryGetProperty( "clearings", out var clArr ) && clArr.ValueKind == JsonValueKind.Array )
		{
			foreach ( var c in clArr.EnumerateArray() )
			{
				var cx = c.TryGetProperty( "x", out var cxp ) ? cxp.GetSingle() : 0;
				var cy = c.TryGetProperty( "y", out var cyp ) ? cyp.GetSingle() : 0;
				var cr = c.TryGetProperty( "radius", out var crp ) ? crp.GetSingle() : 300;
				clearings.Add( (new Vector2( cx, cy ), cr) );
			}
		}

		var go = scene.CreateObject( true );
		go.Name = name;
		go.WorldPosition = Vector3.Zero;

		var mesh = go.AddComponent<MeshComponent>();
		// MeshComponent.Mesh is null on a freshly-added component — must assign a fresh PolygonMesh
		if ( mesh.Mesh == null ) mesh.Mesh = new PolygonMesh();
		var polyMesh = mesh.Mesh;

		var halfSize = size * 0.5f;
		var step = size / resolution;
		var stride = resolution + 1;

		// Generate heightmap vertices
		var handles = new HalfEdgeMesh.VertexHandle[stride * stride];
		for ( int z = 0; z <= resolution; z++ )
		{
			for ( int x = 0; x <= resolution; x++ )
			{
				var worldX = -halfSize + x * step;
				var worldY = -halfSize + z * step;
				var height = CalcHeight( worldX, worldY, hills, clearings );
				handles[z * stride + x] = polyMesh.AddVertex( new Vector3( worldX, worldY, height ) );
			}
		}

		// Generate quad faces
		int faceCount = 0;
		for ( int z = 0; z < resolution; z++ )
		{
			for ( int x = 0; x < resolution; x++ )
			{
				var tl = z * stride + x;
				var tr = tl + 1;
				var bl = (z + 1) * stride + x;
				var br = bl + 1;
				polyMesh.AddFace( new[] { handles[tl], handles[bl], handles[br], handles[tr] } );
				faceCount++;
			}
		}

		return Task.FromResult<object>( new
		{
			built = true,
			id = go.Id.ToString(),
			name = go.Name,
			vertices = handles.Length,
			faces = faceCount
		} );
	}

	private static float CalcHeight( float x, float y,
		System.Collections.Generic.List<(Vector2 pos, float radius, float height)> hills,
		System.Collections.Generic.List<(Vector2 pos, float radius)> clearings )
	{
		float height = 0f;
		var pos = new Vector2( x, y );

		// Hills with smooth cosine falloff
		foreach ( var (hillPos, radius, hillHeight) in hills )
		{
			var dist = Vector2.DistanceBetween( pos, hillPos );
			if ( dist < radius )
			{
				var t = dist / radius;
				var blend = (MathF.Cos( t * MathF.PI ) + 1f) * 0.5f;
				height += hillHeight * blend;
			}
		}

		// Flatten clearings
		foreach ( var (clearPos, clearRadius) in clearings )
		{
			var dist = Vector2.DistanceBetween( pos, clearPos );
			if ( dist < clearRadius )
			{
				var t = dist / clearRadius;
				var blend = (MathF.Cos( t * MathF.PI ) + 1f) * 0.5f;
				height = MathX.Lerp( height, 0f, blend );
			}
		}

		// Subtle noise
		var ix = (int)MathF.Floor( x * 0.001f );
		var iy = (int)MathF.Floor( y * 0.001f );
		var fx = x * 0.001f - ix;
		var fy = y * 0.001f - iy;
		fx = fx * fx * (3f - 2f * fx);
		fy = fy * fy * (3f - 2f * fy);
		float Hash( int px, int py ) { var n = px * 127 + py * 311; n = (n << 13) ^ n; return 1f - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824f; }
		var a = Hash( ix, iy ); var b = Hash( ix + 1, iy ); var c = Hash( ix, iy + 1 ); var d = Hash( ix + 1, iy + 1 );
		height += MathX.Lerp( MathX.Lerp( a, b, fx ), MathX.Lerp( c, d, fx ), fy ) * 25f;

		return height;
	}
}



// ════════════════════════════════════════════════════════════════════════
// New handlers — Map editing, sculpt, type discovery (Batch 15 + 16)
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared helpers for world-gen and reflection-driven handlers.
/// </summary>
internal static class WorldGenHelpers
{
	public static Component FindFirstComponent( string typeName, out string error )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) { error = "No active scene"; return null; }
		foreach ( var go in scene.GetAllObjects( false ) )
		{
			foreach ( var comp in go.Components.GetAll() )
			{
				if ( comp.GetType().Name == typeName ) { error = null; return comp; }
			}
		}
		error = $"No '{typeName}' component in the scene. Pass component='YourComponentName' to target a differently-named one, or use invoke_button to call a method by name."; return null;
	}

	public static Component ResolveComponent( JsonElement p, string defaultType, out string error )
	{
		string typeName = p.TryGetProperty( "component", out var c ) ? c.GetString() : defaultType;
		if ( p.TryGetProperty( "id", out var idEl ) && idEl.ValueKind == JsonValueKind.String )
		{
			var idStr = idEl.GetString();
			if ( !Guid.TryParse( idStr, out var guid ) ) { error = "Invalid GameObject GUID"; return null; }
			var scene = SceneEditorSession.Active?.Scene;
			if ( scene == null ) { error = "No active scene"; return null; }
			var go = scene.Directory.FindByGuid( guid );
			if ( go == null ) { error = "GameObject not found"; return null; }
			foreach ( var comp in go.Components.GetAll() )
			{
				if ( comp.GetType().Name == typeName ) { error = null; return comp; }
			}
			error = $"No component '{typeName}' on the given GameObject"; return null;
		}
		return FindFirstComponent( typeName, out error );
	}

	public static System.Collections.IList GetListProperty( Component comp, string propertyName, out string error, out Type elementType )
	{
		elementType = null;
		var prop = comp.GetType().GetProperty( propertyName );
		if ( prop == null ) { error = $"Property '{propertyName}' not found on {comp.GetType().Name}"; return null; }
		var val = prop.GetValue( comp );
		if ( val is System.Collections.IList list )
		{
			if ( prop.PropertyType.IsGenericType )
				elementType = prop.PropertyType.GetGenericArguments()[0];
			error = null;
			return list;
		}
		error = $"Property '{propertyName}' is not a list"; return null;
	}

	public static bool InvokeButton( Component comp, string buttonLabel, object[] args = null )
	{
		args ??= System.Array.Empty<object>();
		var type = comp.GetType();
		var methods = type.GetMethods( BindingFlags.Public | BindingFlags.Instance );

		// Strategy 1: ButtonAttribute label match (arg count must match)
		foreach ( var method in methods )
		{
			if ( method.GetParameters().Length != args.Length ) continue;
			foreach ( var attr in method.GetCustomAttributes( true ) )
			{
				if ( !attr.GetType().Name.Contains( "Button" ) ) continue;
				if ( AttributeStringMatches( attr, buttonLabel ) )
				{
					InvokeUnwrap( method, comp, args );
					return true;
				}
			}
		}

		// Strategy 2: exact method name
		foreach ( var method in methods )
		{
			if ( method.GetParameters().Length != args.Length ) continue;
			if ( method.Name == buttonLabel )
			{
				InvokeUnwrap( method, comp, args );
				return true;
			}
		}

		// Strategy 3: case-insensitive, ignore spaces
		var normalized = buttonLabel.Replace( " ", "" );
		foreach ( var method in methods )
		{
			if ( method.GetParameters().Length != args.Length ) continue;
			if ( string.Equals( method.Name, normalized, StringComparison.OrdinalIgnoreCase ) )
			{
				InvokeUnwrap( method, comp, args );
				return true;
			}
		}

		return false;
	}

	// Invoke a method and re-throw the inner exception (not the TargetInvocationException wrapper)
	// so callers see the real error message instead of "Exception has been thrown by the target of an invocation."
	private static void InvokeUnwrap( MethodInfo method, object target, object[] args = null )
	{
		try
		{
			var ps = method.GetParameters();
			object[] callArgs = null;
			if ( ps.Length > 0 )
			{
				callArgs = new object[ps.Length];
				for ( int i = 0; i < ps.Length; i++ )
					callArgs[i] = ConvertValue( ( args != null && i < args.Length ) ? args[i] : null, ps[i].ParameterType );
			}
			method.Invoke( target, callArgs );
		}
		catch ( TargetInvocationException tie )
		{
			var inner = tie.InnerException ?? tie;
			throw new Exception( $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}" );
		}
	}

	private static bool AttributeStringMatches( object attr, string target )
	{
		var t = attr.GetType();
		foreach ( var pi in t.GetProperties() )
		{
			if ( pi.PropertyType != typeof( string ) ) continue;
			try { if ( (pi.GetValue( attr ) as string) == target ) return true; } catch { }
		}
		foreach ( var fi in t.GetFields() )
		{
			if ( fi.FieldType != typeof( string ) ) continue;
			try { if ( (fi.GetValue( attr ) as string) == target ) return true; } catch { }
		}
		return false;
	}

	public static List<string> ListButtons( Component comp )
	{
		var labels = new List<string>();
		var type = comp.GetType();
		foreach ( var method in type.GetMethods( BindingFlags.Public | BindingFlags.Instance ) )
		{
			if ( method.GetParameters().Length > 0 ) continue;
			foreach ( var attr in method.GetCustomAttributes( true ) )
			{
				if ( !attr.GetType().Name.Contains( "Button" ) ) continue;
				var label = ExtractAttributeString( attr ) ?? method.Name;
				labels.Add( label );
			}
		}
		return labels;
	}

	private static string ExtractAttributeString( object attr )
	{
		var t = attr.GetType();
		foreach ( var pi in t.GetProperties() )
		{
			if ( pi.PropertyType != typeof( string ) ) continue;
			try { var v = pi.GetValue( attr ) as string; if ( !string.IsNullOrEmpty( v ) ) return v; } catch { }
		}
		foreach ( var fi in t.GetFields() )
		{
			if ( fi.FieldType != typeof( string ) ) continue;
			try { var v = fi.GetValue( attr ) as string; if ( !string.IsNullOrEmpty( v ) ) return v; } catch { }
		}
		return null;
	}

	public static void SetMember( object obj, string memberName, object value )
	{
		var t = obj.GetType();
		var prop = t.GetProperty( memberName );
		if ( prop != null && prop.CanWrite ) { prop.SetValue( obj, ConvertValue( value, prop.PropertyType ) ); return; }
		var field = t.GetField( memberName );
		if ( field != null ) { field.SetValue( obj, ConvertValue( value, field.FieldType ) ); }
	}

	private static object ConvertValue( object value, Type target )
	{
		if ( value == null ) return null;
		if ( target.IsAssignableFrom( value.GetType() ) ) return value;
		try { return Convert.ChangeType( value, target ); } catch { return value; }
	}
}

// ───────── invoke_button ─────────────────────────────────────────────────
public class InvokeButtonHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var typeName = p.TryGetProperty( "component", out var c ) ? c.GetString() : null;
		var label = p.TryGetProperty( "button", out var b ) ? b.GetString() : null;
		if ( string.IsNullOrEmpty( typeName ) || string.IsNullOrEmpty( label ) )
			return Task.FromResult<object>( new { error = "component and button are required" } );

		var comp = WorldGenHelpers.ResolveComponent( p, typeName, out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		object[] args = null;
		if ( p.TryGetProperty( "args", out var argsEl ) && argsEl.ValueKind == JsonValueKind.Array )
		{
			var list = new List<object>();
			foreach ( var el in argsEl.EnumerateArray() )
			{
				switch ( el.ValueKind )
				{
					case JsonValueKind.String: list.Add( el.GetString() ); break;
					case JsonValueKind.Number: list.Add( el.GetDouble() ); break;
					case JsonValueKind.True:   list.Add( true );  break;
					case JsonValueKind.False:  list.Add( false ); break;
					case JsonValueKind.Null:   list.Add( null );  break;
					default:                   list.Add( el.GetRawText() ); break;
				}
			}
			args = list.ToArray();
		}

		try
		{
			var ok = WorldGenHelpers.InvokeButton( comp, label, args );
			if ( !ok ) return Task.FromResult<object>( new { error = $"Button '{label}' not found on {typeName} (with {( args?.Length ?? 0 )} arg(s))" } );
			return Task.FromResult<object>( new { invoked = true, component = typeName, button = label, argCount = args?.Length ?? 0 } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Invoke failed: {ex.Message}" } );
		}
	}
}

// ───────── list_component_buttons ────────────────────────────────────────
public class ListComponentButtonsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var typeName = p.TryGetProperty( "component", out var c ) ? c.GetString() : null;
		if ( string.IsNullOrEmpty( typeName ) )
			return Task.FromResult<object>( new { error = "component is required" } );

		var comp = WorldGenHelpers.ResolveComponent( p, typeName, out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var buttons = WorldGenHelpers.ListButtons( comp );
		return Task.FromResult<object>( new { component = typeName, buttons } );
	}
}

// ───────── raycast_terrain ───────────────────────────────────────────────
public class RaycastTerrainHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;

		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		try
		{
			var sample = comp.GetType().GetMethod( "SampleHeight" );
			if ( sample == null ) return Task.FromResult<object>( new { error = "SampleHeight not available on MapBuilder" } );
			var height = (float)sample.Invoke( comp, new object[] { x, y } );
			return Task.FromResult<object>( new { x, y, z = height } );
		}
		catch ( Exception ex )
		{
			var emsg = ex.Message ?? string.Empty;
			// "Default Surface not found" (and kin) = the surface registry isn't loaded into
			// this editor session yet (a known transient after certain state changes). A trace
			// can't rebuild it in-place, so surface a clear, actionable recovery hint instead of
			// a cryptic failure. (v1.10.0 auto-handled: detected + a restart recommendation.)
			if ( emsg.IndexOf( "Default Surface", StringComparison.OrdinalIgnoreCase ) >= 0
			  || ( emsg.IndexOf( "surface", StringComparison.OrdinalIgnoreCase ) >= 0
			    && emsg.IndexOf( "not found", StringComparison.OrdinalIgnoreCase ) >= 0 ) )
			{
				return Task.FromResult<object>( new {
					error       = "Scene trace failed: the surface registry isn't loaded (\"Default Surface not found\") — a known transient editor state. Call restart_editor to rebuild it, then retry the trace.",
					recoverable = true,
					recovery    = "restart_editor"
				} );
			}
			return Task.FromResult<object>( new { error = $"Raycast failed: {ex.Message}" } );
		}
	}
}

// ───────── add_terrain_hill ──────────────────────────────────────────────
public class AddTerrainHillHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 500f;
		var height = p.TryGetProperty( "height", out var hp ) ? hp.GetSingle() : 100f;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Hills", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var hill = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( hill, "Position", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( hill, "Radius", radius );
		WorldGenHelpers.SetMember( hill, "Height", height );
		list.Add( hill );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );

		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── add_terrain_clearing ──────────────────────────────────────────
public class AddTerrainClearingHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 300f;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Clearings", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Position", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( item, "Radius", radius );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── add_terrain_trail ─────────────────────────────────────────────
public class AddTerrainTrailHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var fromEl = p.TryGetProperty( "from", out var fp ) ? fp : default;
		var toEl = p.TryGetProperty( "to", out var tp ) ? tp : default;
		if ( fromEl.ValueKind != JsonValueKind.Object || toEl.ValueKind != JsonValueKind.Object )
			return Task.FromResult<object>( new { error = "from and to are required objects with x/y" } );

		var from = new Vector2(
			fromEl.TryGetProperty( "x", out var fx ) ? fx.GetSingle() : 0f,
			fromEl.TryGetProperty( "y", out var fy ) ? fy.GetSingle() : 0f );
		var to = new Vector2(
			toEl.TryGetProperty( "x", out var tx ) ? tx.GetSingle() : 0f,
			toEl.TryGetProperty( "y", out var ty ) ? ty.GetSingle() : 0f );
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Trails", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "From", from );
		WorldGenHelpers.SetMember( item, "To", to );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── clear_terrain_features ────────────────────────────────────────
public class ClearTerrainFeaturesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var which = p.TryGetProperty( "what", out var w ) ? w.GetString() : "all";
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var report = new Dictionary<string, int>();
		string[] targets = which == "all"
			? new[] { "Hills", "Clearings", "Trails", "CavePath" }
			: new[] { which };

		foreach ( var prop in targets )
		{
			var list = WorldGenHelpers.GetListProperty( comp, prop, out var lerr, out _ );
			if ( list == null ) continue;
			report[prop] = list.Count;
			list.Clear();
		}

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );
		return Task.FromResult<object>( new { cleared = report, rebuilt = rebuild } );
	}
}

// ───────── add_cave_waypoint ─────────────────────────────────────────────
public class AddCaveWaypointHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "CaveBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var z = p.TryGetProperty( "z", out var zp ) ? zp.GetSingle() : 0f;
		var index = p.TryGetProperty( "index", out var ip ) ? ip.GetInt32() : -1;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Path", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Position", new Vector3( x, y, z ) );
		if ( index >= 0 && index <= list.Count ) list.Insert( index, item );
		else list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Cave" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── clear_cave_path ───────────────────────────────────────────────
public class ClearCavePathHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "CaveBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var list = WorldGenHelpers.GetListProperty( comp, "Path", out var lerr, out _ );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );
		var was = list.Count;
		list.Clear();

		WorldGenHelpers.InvokeButton( comp, "Clear Cave" );
		return Task.FromResult<object>( new { cleared = was } );
	}
}

// ───────── add_forest_poi ────────────────────────────────────────────────
public class AddForestPOIHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var name = p.TryGetProperty( "name", out var nm ) ? nm.GetString() : "POI";
		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 300f;
		var density = p.TryGetProperty( "density_multiplier", out var dp ) ? dp.GetSingle() : 1f;
		var rebuild = p.TryGetProperty( "rebuild", out var rb ) && rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "POIs", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Name", name );
		WorldGenHelpers.SetMember( item, "Position", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( item, "Radius", radius );
		WorldGenHelpers.SetMember( item, "DensityMultiplier", density );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { added = true, index = list.Count - 1, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── add_forest_trail ──────────────────────────────────────────────
public class AddForestTrailHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var fromIdx = p.TryGetProperty( "from_index", out var f ) ? f.GetInt32() : 0;
		var toIdx = p.TryGetProperty( "to_index", out var t ) ? t.GetInt32() : 0;
		var rebuild = p.TryGetProperty( "rebuild", out var rb ) && rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Trails", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "FromIndex", fromIdx );
		WorldGenHelpers.SetMember( item, "ToIndex", toIdx );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── set_forest_seed ───────────────────────────────────────────────
public class SetForestSeedHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var seed = p.TryGetProperty( "seed", out var sp ) ? sp.GetInt32() : 77;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var prop = comp.GetType().GetProperty( "Seed" );
		if ( prop == null ) return Task.FromResult<object>( new { error = "Seed property missing" } );
		prop.SetValue( comp, seed );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { set = true, seed, rebuilt = rebuild } );
	}
}

// ───────── clear_forest_pois ─────────────────────────────────────────────
public class ClearForestPOIsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var list = WorldGenHelpers.GetListProperty( comp, "POIs", out var lerr, out _ );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );
		var was = list.Count;
		list.Clear();

		var trailList = WorldGenHelpers.GetListProperty( comp, "Trails", out _, out _ );
		trailList?.Clear();

		WorldGenHelpers.InvokeButton( comp, "Clear Forest" );
		return Task.FromResult<object>( new { cleared = was } );
	}
}

// ───────── sculpt_terrain ────────────────────────────────────────────────
public class SculptTerrainHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 400f;
		var strength = p.TryGetProperty( "strength", out var sp ) ? sp.GetSingle() : 50f;
		var mode = p.TryGetProperty( "mode", out var mp ) ? mp.GetString() : "raise";

		var sculpt = comp.GetType().GetMethod( "Sculpt" );
		if ( sculpt == null ) return Task.FromResult<object>( new { error = "Sculpt method missing on MapBuilder" } );

		try
		{
			var affected = (int)sculpt.Invoke( comp, new object[] { x, y, radius, strength, mode } );
			return Task.FromResult<object>( new { sculpted = true, mode, affected_vertices = affected } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Sculpt failed: {ex.Message}" } );
		}
	}
}

// ───────── paint_forest_density ──────────────────────────────────────────
public class PaintForestDensityHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 800f;
		var density = p.TryGetProperty( "density", out var dp ) ? dp.GetSingle() : 1f;
		var rebuild = p.TryGetProperty( "rebuild", out var rb ) && rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "DensityRegions", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Center", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( item, "Radius", radius );
		WorldGenHelpers.SetMember( item, "Density", density );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { painted = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── place_along_path ──────────────────────────────────────────────
public class PlaceAlongPathHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		var modelPath = p.TryGetProperty( "model", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrEmpty( modelPath ) )
			return Task.FromResult<object>( new { error = "model path is required (e.g. 'models/dev/box.vmdl')" } );

		var spacing = p.TryGetProperty( "spacing", out var sp ) ? sp.GetSingle() : 200f;
		var jitter = p.TryGetProperty( "jitter", out var jp ) ? jp.GetSingle() : 0f;
		var minScale = p.TryGetProperty( "min_scale", out var mnp ) ? mnp.GetSingle() : 1f;
		var maxScale = p.TryGetProperty( "max_scale", out var mxp ) ? mxp.GetSingle() : 1f;
		var seed = p.TryGetProperty( "seed", out var sdp ) ? sdp.GetInt32() : 42;
		var name = p.TryGetProperty( "name", out var np ) ? np.GetString() : "PathItem";

		// Yaw control (Fix 4): previously yaw was ALWAYS randomized, so a fence/lamppost line
		// came out crooked even when the caller wanted a clean run.
		//   align == true        → orient each piece to face along the path direction.
		//   randomizeYaw == true → jitter yaw randomly (only honored when NOT aligning).
		//   both false (default) → deterministic identity yaw (no randomization).
		var align       = p.TryGetProperty( "align", out var alEl ) && alEl.ValueKind == JsonValueKind.True;
		var randomizeYaw = p.TryGetProperty( "randomizeYaw", out var ryEl ) && ryEl.ValueKind == JsonValueKind.True;

		if ( !p.TryGetProperty( "points", out var pointsEl ) || pointsEl.ValueKind != JsonValueKind.Array )
			return Task.FromResult<object>( new { error = "points must be an array of {x,y,z}" } );

		var points = new List<Vector3>();
		foreach ( var pt in pointsEl.EnumerateArray() )
		{
			points.Add( new Vector3(
				pt.TryGetProperty( "x", out var px ) ? px.GetSingle() : 0f,
				pt.TryGetProperty( "y", out var py ) ? py.GetSingle() : 0f,
				pt.TryGetProperty( "z", out var pz ) ? pz.GetSingle() : 0f ) );
		}
		if ( points.Count < 2 ) return Task.FromResult<object>( new { error = "need at least 2 points" } );

		Model model;
		try { model = Model.Load( modelPath ); }
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"Could not load model '{modelPath}': {ex.Message}" } ); }

		var rng = new Random( seed );
		var folder = scene.CreateObject( true );
		folder.Name = $"== {name}s ==";

		int placed = 0;
		for ( int i = 0; i < points.Count - 1; i++ )
		{
			var from = points[i];
			var to = points[i + 1];
			var seg = to - from;
			var len = seg.Length;
			if ( len < 0.01f ) continue;
			var dir = seg / len;
			var steps = Math.Max( 1, (int)(len / spacing) );
			for ( int s = 0; s <= steps; s++ )
			{
				var t = (float)s / steps;
				var basePos = from + seg * t;
				var jx = (float)(rng.NextDouble() * 2 - 1) * jitter;
				var jy = (float)(rng.NextDouble() * 2 - 1) * jitter;
				var pos = basePos + new Vector3( jx, jy, 0 );

				var go = scene.CreateObject( true );
				go.Name = $"{name} {++placed}";
				go.SetParent( folder );
				go.WorldPosition = pos;
				// Deterministic by default; only spin the yaw when explicitly asked. (Fix 4)
				if ( align )
					go.WorldRotation = Rotation.LookAt( dir, Vector3.Up );
				else if ( randomizeYaw )
					go.WorldRotation = Rotation.FromYaw( (float)(rng.NextDouble() * 360.0) );
				else
					go.WorldRotation = Rotation.Identity;
				var scale = MathX.Lerp( minScale, maxScale, (float)rng.NextDouble() );
				go.WorldScale = new Vector3( scale );

				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = model;
			}
		}

		return Task.FromResult<object>( new { placed, folder = folder.Id.ToString() } );
	}
}

// ════════════════════════════════════════════════════════════════════════
// Coding / type discovery handlers (Batch 16)
// ════════════════════════════════════════════════════════════════════════

// ───────── describe_type ─────────────────────────────────────────────────
public class DescribeTypeHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var name = p.TryGetProperty( "name", out var n ) ? n.GetString() : null;
		if ( string.IsNullOrEmpty( name ) ) return Task.FromResult<object>( new { error = "name is required" } );

		var typeDesc = Game.TypeLibrary.GetType( name );
		Type targetType = typeDesc?.TargetType;

		if ( targetType == null )
		{
			foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
			{
				try
				{
					foreach ( var t in asm.GetTypes() )
					{
						if ( t.Name == name || t.FullName == name ) { targetType = t; break; }
					}
				}
				catch { }
				if ( targetType != null ) break;
			}
		}

		if ( targetType == null ) return Task.FromResult<object>( new { error = $"Type '{name}' not found" } );

		var properties = new List<object>();
		foreach ( var pi in targetType.GetProperties( BindingFlags.Public | BindingFlags.Instance ) )
		{
			properties.Add( new
			{
				name = pi.Name,
				type = pi.PropertyType.Name,
				canRead = pi.CanRead,
				canWrite = pi.CanWrite
			} );
		}

		var methods = new List<object>();
		foreach ( var m in targetType.GetMethods( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static ).Take( 80 ) )
		{
			if ( m.IsSpecialName ) continue;
			var pars = string.Join( ", ", m.GetParameters().Select( pp => $"{pp.ParameterType.Name} {pp.Name}" ) );
			methods.Add( new
			{
				name = m.Name,
				returns = m.ReturnType.Name,
				signature = $"{m.ReturnType.Name} {m.Name}({pars})",
				isStatic = m.IsStatic
			} );
		}

		var events = targetType.GetEvents( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static )
			.Select( e => new { name = e.Name, type = e.EventHandlerType?.Name } ).ToList();

		var attrs = targetType.GetCustomAttributes( false ).Select( a => a.GetType().Name ).ToList();

		return Task.FromResult<object>( new
		{
			name = targetType.Name,
			fullName = targetType.FullName,
			baseType = targetType.BaseType?.Name,
			isAbstract = targetType.IsAbstract,
			isComponent = typeof( Component ).IsAssignableFrom( targetType ),
			properties,
			methods,
			events,
			attributes = attrs
		} );
	}
}

// ───────── search_types ──────────────────────────────────────────────────
public class SearchTypesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var pattern = p.TryGetProperty( "pattern", out var pat ) ? pat.GetString() : "";
		var ns = p.TryGetProperty( "namespace", out var nsp ) ? nsp.GetString() : null;
		var componentsOnly = p.TryGetProperty( "components_only", out var co ) && co.GetBoolean();
		var limit = p.TryGetProperty( "limit", out var lp ) ? lp.GetInt32() : 50;

		var matches = new List<object>();
		var compType = typeof( Component );

		foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
		{
			try
			{
				foreach ( var t in asm.GetTypes() )
				{
					if ( !t.IsPublic ) continue;
					if ( componentsOnly && !compType.IsAssignableFrom( t ) ) continue;
					if ( !string.IsNullOrEmpty( ns ) && (t.Namespace == null || !t.Namespace.Contains( ns, StringComparison.OrdinalIgnoreCase )) ) continue;
					if ( !string.IsNullOrEmpty( pattern ) && !t.Name.Contains( pattern, StringComparison.OrdinalIgnoreCase ) ) continue;

					matches.Add( new
					{
						name = t.Name,
						fullName = t.FullName,
						isComponent = compType.IsAssignableFrom( t ),
						isAbstract = t.IsAbstract
					} );
					if ( matches.Count >= limit ) break;
				}
			}
			catch { }
			if ( matches.Count >= limit ) break;
		}

		return Task.FromResult<object>( new { count = matches.Count, matches } );
	}
}

// ───────── get_method_signature ──────────────────────────────────────────
public class GetMethodSignatureHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var typeName = p.TryGetProperty( "type", out var tp ) ? tp.GetString() : null;
		var methodName = p.TryGetProperty( "method", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrEmpty( typeName ) || string.IsNullOrEmpty( methodName ) )
			return Task.FromResult<object>( new { error = "type and method are required" } );

		Type targetType = Game.TypeLibrary.GetType( typeName )?.TargetType;
		if ( targetType == null )
		{
			foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
			{
				try { foreach ( var t in asm.GetTypes() ) if ( t.Name == typeName ) { targetType = t; break; } } catch { }
				if ( targetType != null ) break;
			}
		}
		if ( targetType == null ) return Task.FromResult<object>( new { error = $"Type '{typeName}' not found" } );

		var overloads = new List<object>();
		foreach ( var m in targetType.GetMethods( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static ) )
		{
			if ( m.Name != methodName ) continue;
			var pars = m.GetParameters().Select( par => new { name = par.Name, type = par.ParameterType.Name, hasDefault = par.HasDefaultValue, defaultValue = par.HasDefaultValue ? par.DefaultValue?.ToString() : null } ).ToArray();
			overloads.Add( new
			{
				returns = m.ReturnType.Name,
				signature = $"{m.ReturnType.Name} {m.Name}({string.Join( ", ", pars.Select( x => $"{x.type} {x.name}" ) )})",
				parameters = pars,
				isStatic = m.IsStatic
			} );
		}

		if ( overloads.Count == 0 ) return Task.FromResult<object>( new { error = $"Method '{methodName}' not found on '{typeName}'" } );
		return Task.FromResult<object>( new { type = typeName, method = methodName, overloads } );
	}
}

// ───────── find_in_project ───────────────────────────────────────────────
public class FindInProjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var symbol = p.TryGetProperty( "symbol", out var sp ) ? sp.GetString() : null;
		if ( string.IsNullOrEmpty( symbol ) ) return Task.FromResult<object>( new { error = "symbol is required" } );

		var ext = p.TryGetProperty( "extension", out var ep ) ? ep.GetString() : ".cs";
		var maxResults = p.TryGetProperty( "max_results", out var mp ) ? mp.GetInt32() : 25;

		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( root ) || !Directory.Exists( root ) )
			return Task.FromResult<object>( new { error = "Project root not found" } );

		var hits = new List<object>();
		try
		{
			foreach ( var file in Directory.EnumerateFiles( root, "*" + ext, SearchOption.AllDirectories ) )
			{
				if ( hits.Count >= maxResults ) break;
				if ( file.Contains( $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}" ) ) continue;
				if ( file.Contains( $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}" ) ) continue;
				if ( file.Contains( $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" ) ) continue;

				try
				{
					var lines = File.ReadAllLines( file );
					for ( int i = 0; i < lines.Length; i++ )
					{
						if ( lines[i].Contains( symbol, StringComparison.Ordinal ) )
						{
							hits.Add( new
							{
								file = file.Substring( root.Length ).TrimStart( Path.DirectorySeparatorChar ),
								line = i + 1,
								text = lines[i].Trim()
							} );
							if ( hits.Count >= maxResults ) break;
						}
					}
				}
				catch { }
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Search failed: {ex.Message}" } );
		}

		return Task.FromResult<object>( new { symbol, count = hits.Count, results = hits } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 17 — Visual & Atmosphere (lighting, post-fx, fog, sky, presets)
// ═══════════════════════════════════════════════════════════════════

// ───────── add_light ─────────────────────────────────────────────────────
public class AddLightHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var type = (p.TryGetProperty( "type", out var t ) ? t.GetString() : "point")?.ToLowerInvariant();
		var name = p.TryGetProperty( "name", out var n ) ? n.GetString() : null;
		var brightness = p.TryGetProperty( "brightness", out var br ) ? br.GetSingle() : 1f;
		var shadows = !p.TryGetProperty( "shadows", out var sh ) || sh.GetBoolean();

		// s&box lights have NO Brightness field — intensity is the LightColor magnitude (HDR),
		// so we scale the colour's RGB by `brightness` (alpha left intact).
		var c = ParseColor( p, "color", Color.White );
		var lit = new Color( c.r * brightness, c.g * brightness, c.b * brightness, c.a );

		var go = scene.CreateObject( true );

		switch ( type )
		{
			case "directional":
				go.Name = name ?? "Directional Light";
				var dl = go.AddComponent<DirectionalLight>();
				dl.LightColor = lit;
				dl.Shadows = shadows;
				if ( p.TryGetProperty( "skyColor", out _ ) )
					dl.SkyColor = ParseColor( p, "skyColor", dl.SkyColor );
				break;

			case "point":
				go.Name = name ?? "Point Light";
				var pl = go.AddComponent<PointLight>();
				pl.LightColor = lit;
				pl.Shadows = shadows;
				if ( p.TryGetProperty( "range", out var pr ) ) pl.Radius = pr.GetSingle();
				break;

			case "spot":
				go.Name = name ?? "Spot Light";
				var sl = go.AddComponent<SpotLight>();
				sl.LightColor = lit;
				sl.Shadows = shadows;
				if ( p.TryGetProperty( "range", out var sr ) ) sl.Radius = sr.GetSingle();
				if ( p.TryGetProperty( "coneInner", out var ci ) ) sl.ConeInner = ci.GetSingle();
				if ( p.TryGetProperty( "coneOuter", out var co ) ) sl.ConeOuter = co.GetSingle();
				break;

			case "ambient":
				go.Name = name ?? "Ambient Light";
				var al = go.AddComponent<AmbientLight>();
				al.Color = lit;
				break;

			default:
				go.Destroy();
				return Task.FromResult<object>( new { error = $"Unknown light type '{type}'. Use directional|point|spot|ambient." } );
		}

		if ( p.TryGetProperty( "position", out var pos ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( pos );
		if ( p.TryGetProperty( "rotation", out var rot ) )
			go.WorldRotation = ClaudeBridge.ParseRotation( rot );
		if ( p.TryGetProperty( "parentId", out var pid ) && Guid.TryParse( pid.GetString(), out var parentGuid ) )
		{
			var parent = scene.Directory.FindByGuid( parentGuid );
			if ( parent != null ) go.SetParent( parent, keepWorldPosition: true );
		}

		return Task.FromResult<object>( new { created = true, type, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}

	/// <summary>Parse a {r,g,b,a?} colour (0-1 floats) from a named property, or return the fallback.</summary>
	internal static Color ParseColor( JsonElement p, string key, Color fallback )
	{
		if ( !p.TryGetProperty( key, out var col ) || col.ValueKind != JsonValueKind.Object )
			return fallback;
		float r = col.TryGetProperty( "r", out var rv ) ? rv.GetSingle() : fallback.r;
		float g = col.TryGetProperty( "g", out var gv ) ? gv.GetSingle() : fallback.g;
		float b = col.TryGetProperty( "b", out var bv ) ? bv.GetSingle() : fallback.b;
		float a = col.TryGetProperty( "a", out var av ) ? av.GetSingle() : 1f;
		return new Color( r, g, b, a );
	}
}

// ───────── set_fog ───────────────────────────────────────────────────────
public class SetFogHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var type = (p.TryGetProperty( "type", out var t ) ? t.GetString() : "gradient")?.ToLowerInvariant();
		if ( type != "gradient" && type != "cubemap" && type != "volumetric" )
			return Task.FromResult<object>( new { error = $"Unknown fog type '{type}'. Use gradient|cubemap|volumetric." } );

		// Re-use an existing GameObject if targetId is given, else create a dedicated Fog object.
		GameObject go = null;
		if ( p.TryGetProperty( "targetId", out var tid ) && Guid.TryParse( tid.GetString(), out var tg ) )
			go = scene.Directory.FindByGuid( tg );
		if ( go == null )
		{
			go = scene.CreateObject( true );
			go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : (type + " fog");
		}

		float F( string key, float fallback ) => p.TryGetProperty( key, out var v ) ? v.GetSingle() : fallback;

		if ( type == "cubemap" )
		{
			var cf = go.GetOrAddComponent<CubemapFog>();
			cf.Tint            = AddLightHandler.ParseColor( p, "color", cf.Tint );
			cf.StartDistance   = F( "startDistance",  cf.StartDistance );
			cf.EndDistance     = F( "endDistance",    cf.EndDistance );
			cf.FalloffExponent = F( "falloff",        cf.FalloffExponent );
			cf.Blur            = F( "blur",           cf.Blur );
			cf.HeightStart     = F( "heightStart",    cf.HeightStart );
			cf.HeightWidth     = F( "heightWidth",    cf.HeightWidth );
			cf.HeightExponent  = F( "heightExponent", cf.HeightExponent );
			return Task.FromResult<object>( new { created = true, type = "cubemap", gameObject = ClaudeBridge.SerializeGo( go ) } );
		}

		if ( type == "volumetric" )
		{
			var vf = go.GetOrAddComponent<VolumetricFogVolume>();
			vf.Color           = AddLightHandler.ParseColor( p, "color", vf.Color );
			vf.Strength        = F( "strength", vf.Strength );
			vf.FalloffExponent = F( "falloff",  vf.FalloffExponent );
			if ( p.TryGetProperty( "size", out var sz ) )
			{
				var s = ClaudeBridge.ParseVector3( sz );
				vf.Bounds = new BBox( -s * 0.5f, s * 0.5f );
			}
			return Task.FromResult<object>( new { created = true, type = "volumetric", gameObject = ClaudeBridge.SerializeGo( go ) } );
		}

		// gradient (default)
		var fog = go.GetOrAddComponent<GradientFog>();
		fog.Color           = AddLightHandler.ParseColor( p, "color", fog.Color );
		fog.StartDistance   = F( "startDistance", fog.StartDistance );
		fog.EndDistance     = F( "endDistance",   fog.EndDistance );
		fog.Height          = F( "height",        fog.Height );
		fog.FalloffExponent = F( "falloff",       fog.FalloffExponent );

		return Task.FromResult<object>( new { created = true, type = "gradient", gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── Batch 17 shared helpers ────────────────────────────────────────
public static class VisualHelpers
{
	/// <summary>
	/// Parse a colour per the cross-language contract: accept EITHER an object
	/// {"r":..,"g":..,"b":..,"a"?:..} OR a comma string "r,g,b,a" OR an array [r,g,b,a]
	/// (0-1 floats). Falls back to <paramref name="fallback"/> for an unparseable/empty value.
	/// C# is the source of truth for parsing; the TS schema accepts the union and passes
	/// the value through unchanged. (Fix 7)
	/// </summary>
	public static Color ParseColorElement( JsonElement c, Color fallback )
	{
		switch ( c.ValueKind )
		{
			case JsonValueKind.Object:
			{
				float r = c.TryGetProperty( "r", out var rv ) ? rv.GetSingle() : fallback.r;
				float g = c.TryGetProperty( "g", out var gv ) ? gv.GetSingle() : fallback.g;
				float b = c.TryGetProperty( "b", out var bv ) ? bv.GetSingle() : fallback.b;
				float a = c.TryGetProperty( "a", out var av ) ? av.GetSingle() : 1f;
				return new Color( r, g, b, a );
			}

			case JsonValueKind.String:
			case JsonValueKind.Array:
			{
				var s = c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText();
				var f = ClaudeBridge.ExtractFloats( s );
				if ( f.Length == 0 ) return fallback;
				return new Color(
					f.Length > 0 ? f[0] : fallback.r,
					f.Length > 1 ? f[1] : fallback.g,
					f.Length > 2 ? f[2] : fallback.b,
					f.Length > 3 ? f[3] : 1f );
			}

			default:
				return fallback;
		}
	}

	/// <summary>Wrap a plain float as a constant ParticleFloat (the s&box particle curve type).</summary>
	public static ParticleFloat PF( float v ) => new ParticleFloat { ConstantValue = v };

	/// <summary>Wrap a Color as a constant ParticleGradient.</summary>
	public static ParticleGradient PG( Color c ) => new ParticleGradient { ConstantValue = c };

	/// <summary>Find the scene's main camera (prefers IsMainCamera), else the first camera, else null.</summary>
	public static CameraComponent FindMainCamera( Scene scene )
	{
		CameraComponent first = null;
		foreach ( var go in scene.GetAllObjects( true ) )
		{
			var cam = go.GetComponent<CameraComponent>();
			if ( cam == null ) continue;
			if ( cam.IsMainCamera ) return cam;
			first ??= cam;
		}
		return first;
	}

	/// <summary>Get an existing GameObject by exact name, or create one.</summary>
	public static GameObject GetOrCreateNamed( Scene scene, string name )
	{
		foreach ( var g in scene.GetAllObjects( true ) )
			if ( g.Name == name ) return g;
		var go = scene.CreateObject( true );
		go.Name = name;
		return go;
	}

	/// <summary>Set a property on any object via reflection, coercing the JSON value to the property's type.</summary>
	public static void SetProp( object comp, string name, JsonElement val )
	{
		var pi = comp.GetType().GetProperty( name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase );
		if ( pi == null || !pi.CanWrite ) return;
		var t = pi.PropertyType;
		try
		{
			object v;
			if ( t == typeof( float ) ) v = val.GetSingle();
			else if ( t == typeof( double ) ) v = val.GetDouble();
			else if ( t == typeof( int ) ) v = val.GetInt32();
			else if ( t == typeof( bool ) ) v = val.GetBoolean();
			else if ( t == typeof( string ) ) v = val.GetString();
			else if ( t == typeof( Color ) ) v = ParseColorElement( val, Color.White );
			else if ( t == typeof( Vector3 ) ) v = ClaudeBridge.ParseVector3( val );
			else if ( t == typeof( Vector2 ) ) v = new Vector2( val.TryGetProperty( "x", out var vx ) ? vx.GetSingle() : 0f, val.TryGetProperty( "y", out var vy ) ? vy.GetSingle() : 0f );
			else if ( t.IsEnum ) v = Enum.Parse( t, val.GetString(), true );
			else return;
			pi.SetValue( comp, v );
		}
		catch { /* best-effort */ }
	}
}

// ───────── add_post_process ───────────────────────────────────────────────
public class AddPostProcessHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var effect = p.TryGetProperty( "effect", out var e ) ? e.GetString() : null;
		if ( string.IsNullOrEmpty( effect ) )
			return Task.FromResult<object>( new { error = "effect is required (e.g. Bloom, Tonemapping, ColorAdjustments, Vignette, FilmGrain, DepthOfField, ChromaticAberration, MotionBlur, Sharpen, AmbientOcclusion)" } );

		CameraComponent cam = null;
		if ( p.TryGetProperty( "cameraId", out var cid ) && Guid.TryParse( cid.GetString(), out var cg ) )
			cam = scene.Directory.FindByGuid( cg )?.GetComponent<CameraComponent>();
		cam ??= VisualHelpers.FindMainCamera( scene );
		if ( cam == null )
			return Task.FromResult<object>( new { error = "No CameraComponent found in the scene to attach post-processing to. Add a camera first." } );

		cam.EnablePostProcessing = true;

		var td = Game.TypeLibrary.GetType( effect );
		if ( td == null )
			return Task.FromResult<object>( new { error = $"Post-process effect type not found: {effect}" } );

		var camGo = cam.GameObject;
		Component comp = camGo.Components.GetAll().FirstOrDefault( c => c.GetType() == td.TargetType );
		comp ??= camGo.Components.Create( td );
		if ( comp == null )
			return Task.FromResult<object>( new { error = $"Failed to create post-process effect: {effect}" } );

		if ( p.TryGetProperty( "properties", out var props ) && props.ValueKind == JsonValueKind.Object )
			foreach ( var prop in props.EnumerateObject() )
				VisualHelpers.SetProp( comp, prop.Name, prop.Value );

		return Task.FromResult<object>( new { added = true, effect, camera = camGo.Name, gameObject = ClaudeBridge.SerializeGo( camGo ) } );
	}
}

// ───────── set_skybox ─────────────────────────────────────────────────────
public class SetSkyboxHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		SkyBox2D sky = null;
		foreach ( var g in scene.GetAllObjects( true ) ) { sky = g.GetComponent<SkyBox2D>(); if ( sky != null ) break; }

		GameObject go;
		if ( sky != null ) { go = sky.GameObject; }
		else
		{
			go = scene.CreateObject( true );
			go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Sky";
			sky = go.AddComponent<SkyBox2D>();
		}

		if ( p.TryGetProperty( "tint", out var tn ) ) sky.Tint = VisualHelpers.ParseColorElement( tn, sky.Tint );
		if ( p.TryGetProperty( "indirectLighting", out var il ) ) sky.SkyIndirectLighting = il.GetBoolean();
		if ( p.TryGetProperty( "material", out var mp ) )
		{
			try { var mat = Material.Load( mp.GetString() ); if ( mat != null ) sky.SkyMaterial = mat; } catch { }
		}

		return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── apply_atmosphere (preset: lighting + fog + post-fx in one call) ─
public class ApplyAtmosphereHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var mood = (p.TryGetProperty( "mood", out var m ) ? m.GetString() : "horror-night")?.ToLowerInvariant();

		Color ambient, dirCol, fogCol;
		float ambientB, dirB, fogStart, fogEnd, fogHeight, saturation, brightness, contrast, vignette;
		switch ( mood )
		{
			case "horror-night":
				ambient = new Color( 0.24f, 0.28f, 0.42f ); ambientB = 2.0f;   // visible blue base
				dirCol  = new Color( 0.50f, 0.58f, 0.80f ); dirB = 1.6f;        // brighter moonlight
				fogCol  = new Color( 0.10f, 0.12f, 0.18f ); fogStart = 500f; fogEnd = 6000f; fogHeight = 400f; // distance-only haze
				saturation = 0.65f; brightness = 0.95f; contrast = 1.10f; vignette = 0.7f;
				break;
			case "foggy-dawn":
				ambient = new Color( 0.50f, 0.52f, 0.55f ); ambientB = 1.2f;
				dirCol  = new Color( 1.00f, 0.85f, 0.70f ); dirB = 1.2f;
				fogCol  = new Color( 0.70f, 0.72f, 0.75f ); fogStart = 50f;  fogEnd = 900f;  fogHeight = 250f;
				saturation = 0.85f; brightness = 1.00f; contrast = 1.00f; vignette = 0.4f;
				break;
			case "overcast":
				ambient = new Color( 0.55f, 0.57f, 0.60f ); ambientB = 1.3f;
				dirCol  = new Color( 0.80f, 0.82f, 0.85f ); dirB = 1.0f;
				fogCol  = new Color( 0.60f, 0.62f, 0.66f ); fogStart = 200f; fogEnd = 3000f; fogHeight = 400f;
				saturation = 0.80f; brightness = 0.95f; contrast = 1.00f; vignette = 0.3f;
				break;
			case "warm-interior":
				ambient = new Color( 0.35f, 0.28f, 0.20f ); ambientB = 1.2f;
				dirCol  = new Color( 1.00f, 0.75f, 0.45f ); dirB = 1.5f;
				fogCol  = new Color( 0.20f, 0.16f, 0.12f ); fogStart = 150f; fogEnd = 2500f; fogHeight = 300f;
				saturation = 1.00f; brightness = 1.00f; contrast = 1.05f; vignette = 0.5f;
				break;
			default:
				return Task.FromResult<object>( new { error = $"Unknown mood '{mood}'. Use horror-night | foggy-dawn | overcast | warm-interior." } );
		}

		var created = new List<string>();

		var ambGo = VisualHelpers.GetOrCreateNamed( scene, "Atmosphere Ambient" );
		ambGo.GetOrAddComponent<AmbientLight>().Color = new Color( ambient.r * ambientB, ambient.g * ambientB, ambient.b * ambientB, 1f );
		created.Add( "AmbientLight" );

		var sunGo = VisualHelpers.GetOrCreateNamed( scene, "Atmosphere Sun" );
		var dl = sunGo.GetOrAddComponent<DirectionalLight>();
		dl.LightColor = new Color( dirCol.r * dirB, dirCol.g * dirB, dirCol.b * dirB, 1f );
		dl.Shadows = true;
		sunGo.WorldRotation = Rotation.From( 55f, 35f, 0f );
		created.Add( "DirectionalLight" );

		var fogGo = VisualHelpers.GetOrCreateNamed( scene, "Atmosphere Fog" );
		var fog = fogGo.GetOrAddComponent<GradientFog>();
		fog.Color = fogCol; fog.StartDistance = fogStart; fog.EndDistance = fogEnd; fog.Height = fogHeight; fog.FalloffExponent = 1.4f;
		created.Add( "GradientFog" );

		var cam = VisualHelpers.FindMainCamera( scene );
		if ( cam != null )
		{
			cam.EnablePostProcessing = true;
			var go = cam.GameObject;
			go.GetOrAddComponent<Tonemapping>();
			var ca = go.GetOrAddComponent<ColorAdjustments>();
			ca.Saturation = saturation; ca.Brightness = brightness; ca.Contrast = contrast;
			var vg = go.GetOrAddComponent<Vignette>();
			vg.Intensity = vignette; vg.Color = new Color( 0f, 0f, 0f, 1f );
			created.Add( "Tonemapping" ); created.Add( "ColorAdjustments" ); created.Add( "Vignette" );
		}

		return Task.FromResult<object>( new { applied = true, mood, components = created, postFxCamera = cam?.GameObject.Name } );
	}
}

// ───────── apply_post_fx_look (preset: just the camera post-fx stack) ──────
public class ApplyPostFxLookHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var look = (p.TryGetProperty( "look", out var l ) ? l.GetString() : "cinematic")?.ToLowerInvariant();
		var cam = VisualHelpers.FindMainCamera( scene );
		if ( cam == null )
			return Task.FromResult<object>( new { error = "No CameraComponent found in the scene." } );

		cam.EnablePostProcessing = true;
		var go = cam.GameObject;
		var applied = new List<string>();

		switch ( look )
		{
			case "cinematic":
				go.GetOrAddComponent<Tonemapping>();
				go.GetOrAddComponent<Bloom>().Strength = 0.5f;
				var cv = go.GetOrAddComponent<Vignette>(); cv.Intensity = 0.5f; cv.Color = new Color( 0f, 0f, 0f, 1f );
				applied.Add( "Tonemapping" ); applied.Add( "Bloom" ); applied.Add( "Vignette" );
				break;
			case "filmic-horror":
				go.GetOrAddComponent<Tonemapping>();
				var ca = go.GetOrAddComponent<ColorAdjustments>(); ca.Saturation = 0.5f; ca.Brightness = 0.75f; ca.Contrast = 1.25f;
				var vg = go.GetOrAddComponent<Vignette>(); vg.Intensity = 1.2f; vg.Color = new Color( 0f, 0f, 0f, 1f );
				go.GetOrAddComponent<FilmGrain>();
				applied.Add( "Tonemapping" ); applied.Add( "ColorAdjustments" ); applied.Add( "Vignette" ); applied.Add( "FilmGrain" );
				break;
			case "clean":
				go.GetOrAddComponent<Tonemapping>();
				applied.Add( "Tonemapping" );
				break;
			default:
				return Task.FromResult<object>( new { error = $"Unknown look '{look}'. Use cinematic | filmic-horror | clean." } );
		}

		return Task.FromResult<object>( new { applied = true, look, components = applied, camera = go.Name } );
	}
}

// ───────── add_envmap_probe ───────────────────────────────────────────────
public class AddEnvmapProbeHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var go = scene.CreateObject( true );
		go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Envmap Probe";
		if ( p.TryGetProperty( "position", out var pos ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( pos );

		var probe = go.AddComponent<EnvmapProbe>();
		float h = (p.TryGetProperty( "size", out var sz ) ? sz.GetSingle() : 1024f) * 0.5f;
		probe.Bounds = new BBox( new Vector3( -h, -h, -h ), new Vector3( h, h, h ) );
		if ( p.TryGetProperty( "tint", out var tn ) )
			probe.TintColor = VisualHelpers.ParseColorElement( tn, probe.TintColor );
		if ( p.TryGetProperty( "feathering", out var ft ) )
			probe.Feathering = ft.GetSingle();

		return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 18 — VFX / particles
// ═══════════════════════════════════════════════════════════════════

// ───────── spawn_particle (additive, texture-free fire/embers/sparks) ──────
public class SpawnParticleHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var kind = (p.TryGetProperty( "kind", out var k ) ? k.GetString() : "fire")?.ToLowerInvariant();

		Color tint; float rate, life, size, coneAngle, speed, gravity; bool loop;
		switch ( kind )
		{
			case "fire":
				tint = new Color( 1f, 0.55f, 0.15f ); rate = 70f; life = 1.1f; size = 7f;  coneAngle = 16f; speed = 130f; gravity = 0f;   loop = true;  break;
			case "embers":
				tint = new Color( 1f, 0.5f, 0.18f );  rate = 14f; life = 2.6f; size = 2.5f; coneAngle = 32f; speed = 90f;  gravity = 35f;  loop = true;  break;
			case "sparks":
				tint = new Color( 1f, 0.85f, 0.45f ); rate = 0f;  life = 0.7f; size = 2f;   coneAngle = 70f; speed = 320f; gravity = 500f; loop = false; break;
			case "magic":
				tint = new Color( 0.6f, 0.3f, 1f );    rate = 30f; life = 1.8f; size = 3f;   coneAngle = 55f; speed = 45f;  gravity = 0f;   loop = true;  break;
			case "dust":
				tint = new Color( 0.6f, 0.55f, 0.45f ); rate = 8f;  life = 3.5f; size = 4f;   coneAngle = 80f; speed = 30f;  gravity = 8f;   loop = true;  break;
			case "blood":
				tint = new Color( 0.5f, 0.02f, 0.02f ); rate = 0f;  life = 0.9f; size = 3f;   coneAngle = 65f; speed = 220f; gravity = 700f; loop = false; break;
			case "snow":
				tint = new Color( 0.95f, 0.97f, 1f );   rate = 25f; life = 6f;   size = 2.5f; coneAngle = 85f; speed = 25f;  gravity = 30f;  loop = true;  break;
			case "smoke":
				return Task.FromResult<object>( new { error = "smoke needs a soft smoke sprite (additive squares look bad) — not in v1; use fire | embers | sparks." } );
			default:
				return Task.FromResult<object>( new { error = $"Unknown kind '{kind}'. Use fire | embers | sparks." } );
		}

		var go = scene.CreateObject( true );
		go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : $"Particles ({kind})";
		if ( p.TryGetProperty( "position", out var pos ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( pos );
		if ( p.TryGetProperty( "color", out var cc ) )
			tint = VisualHelpers.ParseColorElement( cc, tint );
		// Point the emission cone up (+Z) so fire/embers rise.
		go.WorldRotation = Rotation.From( -90f, 0f, 0f );

		var pe = go.AddComponent<ParticleEffect>();
		pe.MaxParticles = 500;
		pe.Lifetime = VisualHelpers.PF( life );
		pe.ApplyColor = true;
		pe.Tint = tint;
		pe.ApplyShape = true;
		pe.Scale = VisualHelpers.PF( size );
		if ( gravity > 0f )
		{
			pe.Force = true;
			pe.ForceDirection = new Vector3( 0f, 0f, -1f );
			pe.ForceScale = VisualHelpers.PF( gravity );
		}

		var em = go.AddComponent<ParticleConeEmitter>();
		em.ConeAngle = VisualHelpers.PF( coneAngle );
		em.VelocityMultiplier = VisualHelpers.PF( speed );
		em.Loop = loop;
		if ( loop )
			em.Rate = VisualHelpers.PF( rate );
		else
			em.Burst = VisualHelpers.PF( 60f );

		var sr = go.AddComponent<ParticleSpriteRenderer>();
		sr.Sprite = Sprite.FromTexture( Texture.White ); // CRITICAL: the renderer draws its Sprite, not Texture — a null Sprite renders NOTHING
		sr.Texture = Texture.White;   // additive white × tint = a glowing dot (no sprite asset needed)
		sr.Additive = true;
		sr.Lighting = false;
		sr.Scale = 1f;
		sr.ParticleEffect = pe;

		return Task.FromResult<object>( new { created = true, kind, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── add_trail (TrailRenderer — trails a moving object) ──────────────
public class AddTrailHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		// Attach to an existing object (so it trails as that object moves), else a new GO.
		GameObject go = null;
		if ( p.TryGetProperty( "targetId", out var tid ) && Guid.TryParse( tid.GetString(), out var tg ) )
			go = scene.Directory.FindByGuid( tg );
		if ( go == null )
		{
			go = scene.CreateObject( true );
			go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Trail";
			if ( p.TryGetProperty( "position", out var pos ) )
				go.WorldPosition = ClaudeBridge.ParseVector3( pos );
		}

		var tr = go.GetOrAddComponent<TrailRenderer>();
		tr.Emitting = true;
		if ( p.TryGetProperty( "lifetime", out var lt ) ) tr.LifeTime = lt.GetSingle();
		if ( p.TryGetProperty( "maxPoints", out var mp ) ) tr.MaxPoints = mp.GetInt32();
		if ( p.TryGetProperty( "pointDistance", out var pd ) ) tr.PointDistance = pd.GetSingle();
		// Color (Gradient) + Width (Curve) left at defaults — those are separate curve structs.

		return Task.FromResult<object>( new { created = true, note = "Trail is only visible while its GameObject moves.", gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── add_beam (BeamEffect — a beam from this object to a target) ─────
public class AddBeamHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var go = scene.CreateObject( true );
		go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Beam";
		if ( p.TryGetProperty( "position", out var pos ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( pos );

		var be = go.AddComponent<BeamEffect>();
		be.TargetPosition = p.TryGetProperty( "target", out var tgt )
			? ClaudeBridge.ParseVector3( tgt )
			: go.WorldPosition + Vector3.Up * 128f;
		be.Scale = VisualHelpers.PF( p.TryGetProperty( "width", out var w ) ? w.GetSingle() : 4f );
		be.Brightness = VisualHelpers.PF( 1f );
		be.Alpha = VisualHelpers.PF( 1f );
		be.BeamColor = VisualHelpers.PG( p.TryGetProperty( "color", out var cc ) ? VisualHelpers.ParseColorElement( cc, Color.White ) : Color.White );
		be.Additive = true;
		be.Lighting = false;
		be.Texture = Texture.White;
		be.Looped = true;
		be.MaxBeams = 4;
		be.InitialBurst = 1;
		be.BeamsPerSecond = 2f;
		be.BeamLifetime = VisualHelpers.PF( 2f );

		return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── create_particle_effect (generic, raw params) ───────────────────
public class CreateParticleEffectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var go = scene.CreateObject( true );
		go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Particle Effect";
		if ( p.TryGetProperty( "position", out var pos ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( pos );
		go.WorldRotation = Rotation.From( -90f, 0f, 0f ); // cone emits +Z (up) by default

		float rate      = p.TryGetProperty( "rate", out var r )        ? r.GetSingle()  : 30f;
		float life      = p.TryGetProperty( "lifetime", out var l )    ? l.GetSingle()  : 2f;
		float size      = p.TryGetProperty( "size", out var sz )       ? sz.GetSingle() : 4f;
		float speed     = p.TryGetProperty( "speed", out var sp )      ? sp.GetSingle() : 100f;
		float coneAngle = p.TryGetProperty( "coneAngle", out var ca )  ? ca.GetSingle() : 40f;
		float gravity   = p.TryGetProperty( "gravity", out var gr )    ? gr.GetSingle() : 0f;
		int   maxP      = p.TryGetProperty( "maxParticles", out var m )? m.GetInt32()   : 500;
		bool  additive  = !p.TryGetProperty( "additive", out var ad )  || ad.GetBoolean();
		bool  loop      = !p.TryGetProperty( "loop", out var lp )      || lp.GetBoolean();
		float burst     = p.TryGetProperty( "burst", out var bu )      ? bu.GetSingle() : 30f;
		var   tint      = p.TryGetProperty( "color", out var cc )      ? VisualHelpers.ParseColorElement( cc, Color.White ) : Color.White;

		var pe = go.AddComponent<ParticleEffect>();
		pe.MaxParticles = maxP;
		pe.Lifetime = VisualHelpers.PF( life );
		pe.ApplyColor = true;
		pe.Tint = tint;
		pe.ApplyShape = true;
		pe.Scale = VisualHelpers.PF( size );
		if ( gravity > 0f )
		{
			pe.Force = true;
			pe.ForceDirection = new Vector3( 0f, 0f, -1f );
			pe.ForceScale = VisualHelpers.PF( gravity );
		}

		var em = go.AddComponent<ParticleConeEmitter>();
		em.ConeAngle = VisualHelpers.PF( coneAngle );
		em.VelocityMultiplier = VisualHelpers.PF( speed );
		em.Loop = loop;
		if ( loop ) em.Rate = VisualHelpers.PF( rate );
		else        em.Burst = VisualHelpers.PF( burst );

		var sr = go.AddComponent<ParticleSpriteRenderer>();
		sr.Sprite = Sprite.FromTexture( Texture.White ); // CRITICAL: renderer draws Sprite, not Texture — null Sprite renders NOTHING
		sr.Texture = Texture.White;
		sr.Additive = additive;
		sr.Lighting = false;
		sr.Scale = 1f;
		sr.ParticleEffect = pe;

		return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 19 — Characters & Models
//  Static + screenshot-verifiable: spawn models/citizens, dress, pose,
//  bodygroups. (Animation *playback* is runtime; the SETUP/pose renders
//  in the editor view, which is what we verify.)
// ═════════════════════════════════════════════════════════════════════

public static class CharacterHelpers
{
	/// <summary>Parse {pitch,yaw,roll} → Rotation (identity if absent).</summary>
	public static Rotation ParseRotation( JsonElement e )
	{
		float pitch = e.TryGetProperty( "pitch", out var p ) ? p.GetSingle() : 0f;
		float yaw   = e.TryGetProperty( "yaw",   out var y ) ? y.GetSingle() : 0f;
		float roll  = e.TryGetProperty( "roll",  out var r ) ? r.GetSingle() : 0f;
		return Rotation.From( pitch, yaw, roll );
	}

	/// <summary>Apply position/rotation/scale params to a GameObject.</summary>
	public static void ApplyTransform( GameObject go, JsonElement p )
	{
		if ( p.TryGetProperty( "position", out var pos ) ) go.WorldPosition = ClaudeBridge.ParseVector3( pos );
		if ( p.TryGetProperty( "rotation", out var rot ) ) go.WorldRotation = ParseRotation( rot );
		if ( p.TryGetProperty( "scale",    out var sc  ) ) go.WorldScale    = ClaudeBridge.ParseVector3( sc );
	}

	/// <summary>Parent the GO to parentId (keep world pos) when provided + found.</summary>
	public static void ApplyParent( GameObject go, Scene scene, JsonElement p )
	{
		if ( p.TryGetProperty( "parentId", out var pid ) && Guid.TryParse( pid.GetString(), out var pg ) )
		{
			var parent = scene.Directory.FindByGuid( pg );
			if ( parent != null ) go.SetParent( parent, true );
		}
	}
}

// ───────── spawn_model (a model object in the world) ───────────────────
public class SpawnModelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var modelPath = p.TryGetProperty( "model", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrWhiteSpace( modelPath ) )
			return Task.FromResult<object>( new { error = "model is required (e.g. 'models/citizen/citizen.vmdl' or an installed model path)" } );

		Model model = null;
		try { model = Model.Load( modelPath ); } catch { }
		if ( model == null )
			return Task.FromResult<object>( new { error = $"Model not found: {modelPath}. Cloud assets must be installed first (install_asset)." } );

		// Model.Load NEVER returns null for an unmounted/missing path — it returns the engine
		// ERROR placeholder (the giant checkered box), which then renders as a "success". Detect
		// that (Model.IsError) and surface a warning instead of a clean success so the caller
		// knows the path didn't resolve (likely a Cloud asset that needs install_asset). (Fix 6)
		bool isErrorModel = model.IsError;

		var go = scene.CreateObject( true );
		go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Model";
		CharacterHelpers.ApplyTransform( go, p );

		var r = go.AddComponent<ModelRenderer>();
		r.Model = model;
		if ( p.TryGetProperty( "tint", out var t ) )
			r.Tint = VisualHelpers.ParseColorElement( t, Color.White );

		CharacterHelpers.ApplyParent( go, scene, p );

		if ( isErrorModel )
		{
			return Task.FromResult<object>( new
			{
				created = true,
				model = modelPath,
				warning = $"'{modelPath}' resolved to the ERROR placeholder model (path not mounted). " +
					"The object spawned but renders as the giant checkered ERROR box. " +
					"If this is a Cloud asset, install it first with install_asset.",
				gameObject = ClaudeBridge.SerializeGo( go )
			} );
		}

		return Task.FromResult<object>( new { created = true, model = modelPath, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── spawn_citizen (animated Citizen character) ──────────────────
public class SpawnCitizenHandler : IBridgeHandler
{
	const string DefaultCitizen = "models/citizen/citizen.vmdl";

	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var modelPath = p.TryGetProperty( "model", out var mp ) ? mp.GetString() : DefaultCitizen;
		Model model = null;
		try { model = Model.Load( modelPath ); } catch { }
		if ( model == null )
			return Task.FromResult<object>( new { error = $"Model not found: {modelPath}" } );

		var go = scene.CreateObject( true );
		go.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Citizen";
		CharacterHelpers.ApplyTransform( go, p );

		var body = go.AddComponent<SkinnedModelRenderer>();
		body.Model = model;
		if ( p.TryGetProperty( "tint", out var t ) )
			body.Tint = VisualHelpers.ParseColorElement( t, Color.White );

		bool animator = !p.TryGetProperty( "animator", out var an ) || an.GetBoolean();
		if ( animator )
		{
			var helper = go.AddComponent<Sandbox.Citizen.CitizenAnimationHelper>();
			helper.Target = body;
			body.PlayAnimationsInEditorScene = true; // idle pose previews in the editor view
			if ( p.TryGetProperty( "holdType",  out var ht ) ) VisualHelpers.SetProp( helper, "HoldType",  ht );
			if ( p.TryGetProperty( "moveStyle", out var ms ) ) VisualHelpers.SetProp( helper, "MoveStyle", ms );
		}

		CharacterHelpers.ApplyParent( go, scene, p );
		return Task.FromResult<object>( new { created = true, hasAnimator = animator, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── dress_citizen (apply clothing to a SkinnedModelRenderer) ────
public class DressCitizenHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );

		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null )
			return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer (use spawn_citizen first)" } );

		var container = new ClothingContainer();
		var applied = new List<string>();
		var missing = new List<string>();
		if ( p.TryGetProperty( "clothing", out var arr ) && arr.ValueKind == JsonValueKind.Array )
		{
			foreach ( var item in arr.EnumerateArray() )
			{
				var path = item.GetString();
				if ( string.IsNullOrWhiteSpace( path ) ) continue;
				Clothing c = null;
				try { c = ResourceLibrary.Get<Clothing>( path ); } catch { }
				if ( c != null ) { container.Add( c ); applied.Add( path ); }
				else missing.Add( path );
			}
		}

		if ( p.TryGetProperty( "tint", out var tn ) && tn.ValueKind == JsonValueKind.Number )
			container.Tint = tn.GetSingle();

		container.Apply( body );
		return Task.FromResult<object>( new { dressed = true, applied, missing, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── set_bodygroup (show/hide parts of a skinned model) ──────────
public class SetBodygroupHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );

		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null )
			return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer" } );

		var name = p.TryGetProperty( "name", out var nm ) ? nm.GetString() : null;
		if ( string.IsNullOrWhiteSpace( name ) )
			return Task.FromResult<object>( new { error = "name (bodygroup) is required" } );

		if ( p.TryGetProperty( "value", out var v ) && v.ValueKind == JsonValueKind.Number )
			body.SetBodyGroup( name, v.GetInt32() );
		else if ( p.TryGetProperty( "choice", out var ch ) && ch.ValueKind == JsonValueKind.String )
			body.SetBodyGroup( name, ch.GetString() );
		else
			return Task.FromResult<object>( new { error = "provide value (int) or choice (string)" } );

		return Task.FromResult<object>( new { set = true, bodygroup = name } );
	}
}

// ───────── pose_citizen (set CitizenAnimationHelper params) ────────────
public class PoseCitizenHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );

		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body != null ) body.PlayAnimationsInEditorScene = true; // so the pose shows in-editor

		var helper = go.GetComponent<Sandbox.Citizen.CitizenAnimationHelper>();
		if ( helper == null )
			return Task.FromResult<object>( new { error = "Target has no CitizenAnimationHelper (spawn_citizen with animator=true)" } );

		var changed = new List<string>();
		if ( p.TryGetProperty( "holdType",    out var ht ) ) { VisualHelpers.SetProp( helper, "HoldType",    ht ); changed.Add( "HoldType" ); }
		if ( p.TryGetProperty( "moveStyle",   out var ms ) ) { VisualHelpers.SetProp( helper, "MoveStyle",   ms ); changed.Add( "MoveStyle" ); }
		if ( p.TryGetProperty( "specialMove", out var sm ) ) { VisualHelpers.SetProp( helper, "SpecialMove", sm ); changed.Add( "SpecialMove" ); }
		if ( p.TryGetProperty( "sitting",     out var si ) ) { helper.IsSitting = si.GetBoolean(); changed.Add( "IsSitting" ); }
		if ( p.TryGetProperty( "duckLevel",   out var dl ) ) { helper.DuckLevel = dl.GetSingle(); changed.Add( "DuckLevel" ); }

		return Task.FromResult<object>( new { posed = true, changed, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 20 — Character polish: equip / look-at / ragdoll / expression
// ═════════════════════════════════════════════════════════════════════

// ───────── equip_model (attach a prop model to a bone/attachment) ──────
public class EquipModelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (target GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null ) return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer (use spawn_citizen first)" } );

		var modelPath = p.TryGetProperty( "model", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrWhiteSpace( modelPath ) )
			return Task.FromResult<object>( new { error = "model (prop model path) is required" } );
		Model model = null;
		try { model = Model.Load( modelPath ); } catch { }
		if ( model == null ) return Task.FromResult<object>( new { error = $"Model not found: {modelPath}" } );

		var point = p.TryGetProperty( "point", out var pt ) ? pt.GetString() : "hand_R";

		body.CreateBoneObjects = true;
		body.CreateAttachments = true;
		GameObject anchor = null;
		try { anchor = body.GetAttachmentObject( point ); } catch { }
		if ( anchor == null ) { try { anchor = body.GetBoneObject( point ); } catch { } }

		var prop = scene.CreateObject( true );
		prop.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Equipped";
		var pr = prop.AddComponent<ModelRenderer>();
		pr.Model = model;
		if ( p.TryGetProperty( "tint", out var t ) ) pr.Tint = VisualHelpers.ParseColorElement( t, Color.White );

		string how;
		if ( anchor != null )
		{
			prop.SetParent( anchor, false );
			prop.LocalPosition = p.TryGetProperty( "offset", out var off ) ? ClaudeBridge.ParseVector3( off ) : Vector3.Zero;
			if ( p.TryGetProperty( "rotation", out var rot ) ) prop.LocalRotation = CharacterHelpers.ParseRotation( rot );
			how = $"parented to '{point}'";
		}
		else if ( body.TryGetBoneTransform( point, out var tx ) )
		{
			prop.WorldPosition = tx.Position;
			prop.WorldRotation = tx.Rotation;
			prop.SetParent( go, true );
			how = $"placed at bone transform '{point}'";
		}
		else
		{
			prop.Destroy();
			return Task.FromResult<object>( new { error = $"Point '{point}' not found — try an attachment (hand_R, hand_L, eyes, hat) or a bone name." } );
		}

		return Task.FromResult<object>( new { equipped = true, point, how, gameObject = ClaudeBridge.SerializeGo( prop ) } );
	}
}

// ───────── set_look_at (aim the citizen's gaze) ────────────────────────
public class SetLookAtHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (Citizen GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var helper = go.GetComponent<Sandbox.Citizen.CitizenAnimationHelper>();
		if ( helper == null ) return Task.FromResult<object>( new { error = "Target has no CitizenAnimationHelper (spawn_citizen with animator=true)" } );

		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body != null ) body.PlayAnimationsInEditorScene = true;

		if ( p.TryGetProperty( "enabled", out var en ) && !en.GetBoolean() )
		{
			helper.LookAtEnabled = false;
			return Task.FromResult<object>( new { lookAt = false } );
		}
		helper.LookAtEnabled = true;

		GameObject target = null;
		if ( p.TryGetProperty( "targetId", out var tid ) && Guid.TryParse( tid.GetString(), out var tg ) )
			target = scene.Directory.FindByGuid( tg );
		if ( target == null && p.TryGetProperty( "target", out var tgt ) )
		{
			target = scene.CreateObject( true );
			target.Name = "LookTarget";
			target.WorldPosition = ClaudeBridge.ParseVector3( tgt );
		}
		if ( target != null ) helper.LookAt = target;

		if ( p.TryGetProperty( "eyesWeight", out var ew ) ) helper.EyesWeight = ew.GetSingle();
		if ( p.TryGetProperty( "headWeight", out var hw ) ) helper.HeadWeight = hw.GetSingle();
		if ( p.TryGetProperty( "bodyWeight", out var bw ) ) helper.BodyWeight = bw.GetSingle();

		return Task.FromResult<object>( new { lookAt = true, target = target?.Name, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── add_ragdoll (ModelPhysics — simulates in PLAY mode) ─────────
public class AddRagdollHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null ) return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer" } );

		var phys = go.GetOrAddComponent<ModelPhysics>();
		phys.Renderer = body;
		phys.Model = body.Model;
		if ( p.TryGetProperty( "motionEnabled", out var me ) ) phys.MotionEnabled = me.GetBoolean();

		return Task.FromResult<object>( new { ragdoll = true, note = "ModelPhysics added — the ragdoll flops via physics in PLAY mode (runtime; not visible in the static editor pose).", gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

// ───────── set_expression (facial morphs) ──────────────────────────────
public class SetExpressionHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null ) return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer" } );
		body.PlayAnimationsInEditorScene = true;

		var morph = p.TryGetProperty( "morph", out var m ) ? m.GetString() : null;
		if ( string.IsNullOrWhiteSpace( morph ) )
			return Task.FromResult<object>( new { error = "morph (name) is required", availableMorphs = body.Morphs.Names } );

		float weight = p.TryGetProperty( "weight", out var w ) ? w.GetSingle() : 1f;
		body.Morphs.Set( morph, weight );

		return Task.FromResult<object>( new { set = true, morph, weight, availableMorphs = body.Morphs.Names } );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 21 — Scene & level building: snap / align / distribute / grid / measure
// ═════════════════════════════════════════════════════════════════════

public static class SceneToolHelpers
{
	/// <summary>Resolve the "ids" string-array param into a list of live GameObjects.</summary>
	public static List<GameObject> ResolveIds( Scene scene, JsonElement p )
	{
		var list = new List<GameObject>();
		if ( p.TryGetProperty( "ids", out var arr ) && arr.ValueKind == JsonValueKind.Array )
			foreach ( var e in arr.EnumerateArray() )
				if ( Guid.TryParse( e.GetString(), out var g ) )
				{
					var go = scene.Directory.FindByGuid( g );
					if ( go != null ) list.Add( go );
				}
		return list;
	}

	public static float AxisVal( Vector3 v, string axis ) => axis == "y" ? v.y : ( axis == "z" ? v.z : v.x );
	public static Vector3 SetAxis( Vector3 v, string axis, float val ) =>
		axis == "y" ? new Vector3( v.x, val, v.z ) : ( axis == "z" ? new Vector3( v.x, v.y, val ) : new Vector3( val, v.y, v.z ) );

	/// <summary>Resolve targets from a single "id" and/or an "ids" array (deduped).</summary>
	public static List<GameObject> ResolveTargets( Scene scene, JsonElement p )
	{
		var list = ResolveIds( scene, p );
		if ( p.TryGetProperty( "id", out var idEl ) && Guid.TryParse( idEl.GetString(), out var g ) )
		{
			var go = scene.Directory.FindByGuid( g );
			if ( go != null && !list.Contains( go ) ) list.Add( go );
		}
		return list;
	}
}

// ───────── snap_to_ground (drop an object onto the surface below) ──────
public class SnapToGroundHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );

		float up      = p.TryGetProperty( "startHeight", out var sh ) ? sh.GetSingle() : 2000f;
		float maxDrop = p.TryGetProperty( "maxDistance", out var md ) ? md.GetSingle() : 20000f;
		float offset  = p.TryGetProperty( "offset", out var of ) ? of.GetSingle() : 0f;

		var pos = go.WorldPosition;
		try
		{
			var tr = scene.Trace.Ray( pos + Vector3.Up * up, pos + Vector3.Down * maxDrop ).Run();
			if ( !tr.Hit )
				return Task.FromResult<object>( new { snapped = false, reason = "No ground hit below the object (works best on collider-less props; objects with colliders may self-hit)." } );
			go.WorldPosition = new Vector3( pos.x, pos.y, tr.HitPosition.z + offset );
			return Task.FromResult<object>( new { snapped = true, groundZ = tr.HitPosition.z, gameObject = ClaudeBridge.SerializeGo( go ) } );
		}
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"Trace failed: {ex.Message}" } ); }
	}
}

// ───────── align_objects (line up on an axis) ──────────────────────────
public class AlignObjectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		var gos = SceneToolHelpers.ResolveIds( scene, p );
		if ( gos.Count < 2 ) return Task.FromResult<object>( new { error = "ids: provide at least 2 GameObject GUIDs" } );

		string axis = p.TryGetProperty( "axis", out var ax ) ? ax.GetString().ToLowerInvariant() : "x";
		string mode = p.TryGetProperty( "mode", out var mo ) ? mo.GetString().ToLowerInvariant() : "first";

		float target;
		if ( mode == "min" || mode == "max" || mode == "average" )
		{
			float mn = float.MaxValue, mx = float.MinValue, sum = 0f;
			foreach ( var g in gos ) { float v = SceneToolHelpers.AxisVal( g.WorldPosition, axis ); if ( v < mn ) mn = v; if ( v > mx ) mx = v; sum += v; }
			target = mode == "min" ? mn : ( mode == "max" ? mx : sum / gos.Count );
		}
		else target = SceneToolHelpers.AxisVal( gos[0].WorldPosition, axis ); // "first"

		foreach ( var g in gos ) g.WorldPosition = SceneToolHelpers.SetAxis( g.WorldPosition, axis, target );
		return Task.FromResult<object>( new { aligned = gos.Count, axis, mode, target } );
	}
}

// ───────── distribute_objects (even spacing on an axis) ────────────────
public class DistributeObjectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		var gos = SceneToolHelpers.ResolveIds( scene, p );
		if ( gos.Count < 3 ) return Task.FromResult<object>( new { error = "ids: provide at least 3 GameObjects to distribute" } );

		string axis = p.TryGetProperty( "axis", out var ax ) ? ax.GetString().ToLowerInvariant() : "x";
		gos.Sort( ( a, b ) => SceneToolHelpers.AxisVal( a.WorldPosition, axis ).CompareTo( SceneToolHelpers.AxisVal( b.WorldPosition, axis ) ) );

		float lo = SceneToolHelpers.AxisVal( gos[0].WorldPosition, axis );
		float hi = SceneToolHelpers.AxisVal( gos[gos.Count - 1].WorldPosition, axis );
		int n = gos.Count;
		for ( int i = 1; i < n - 1; i++ )
		{
			float val = lo + ( hi - lo ) * ( (float)i / ( n - 1 ) );
			gos[i].WorldPosition = SceneToolHelpers.SetAxis( gos[i].WorldPosition, axis, val );
		}
		return Task.FromResult<object>( new { distributed = n, axis, from = lo, to = hi } );
	}
}

// ───────── grid_duplicate (array copies in an X/Y/Z grid) ──────────────
public class GridDuplicateHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );

		int countX = p.TryGetProperty( "countX", out var cx ) ? cx.GetInt32() : 1;
		int countY = p.TryGetProperty( "countY", out var cy ) ? cy.GetInt32() : 1;
		int countZ = p.TryGetProperty( "countZ", out var cz ) ? cz.GetInt32() : 1;
		if ( countX < 1 ) countX = 1; if ( countX > 50 ) countX = 50;
		if ( countY < 1 ) countY = 1; if ( countY > 50 ) countY = 50;
		if ( countZ < 1 ) countZ = 1; if ( countZ > 50 ) countZ = 50;
		var spacing = p.TryGetProperty( "spacing", out var sp ) ? ClaudeBridge.ParseVector3( sp ) : new Vector3( 100f, 100f, 100f );

		var basePos = go.WorldPosition;
		var created = new List<string>();
		// go.Clone() resolves its destination from the ambient active scene, which is null in
		// edit mode → "No Active Scene". Push the editor scene as the active scope so every
		// clone lands in it, the same way add_component/set_property mutate it. (Fix 3)
		using ( scene.Push() )
		{
			for ( int ix = 0; ix < countX; ix++ )
				for ( int iy = 0; iy < countY; iy++ )
					for ( int iz = 0; iz < countZ; iz++ )
					{
						if ( ix == 0 && iy == 0 && iz == 0 ) continue; // keep the original in place
						if ( created.Count >= 500 ) break;             // safety cap
						var clone = go.Clone();
						clone.WorldPosition = basePos + new Vector3( ix * spacing.x, iy * spacing.y, iz * spacing.z );
						created.Add( clone.Id.ToString() );
					}
		}
		return Task.FromResult<object>( new { duplicated = created.Count, grid = new { countX, countY, countZ }, ids = created } );
	}
}

// ───────── measure_distance (between two points or objects) ────────────
public class MeasureDistanceHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !TryResolvePoint( scene, p, "idA", "a", out var a ) )
			return Task.FromResult<object>( new { error = "provide a {x,y,z} or idA (GameObject GUID)" } );
		if ( !TryResolvePoint( scene, p, "idB", "b", out var b ) )
			return Task.FromResult<object>( new { error = "provide b {x,y,z} or idB (GameObject GUID)" } );

		var delta = b - a;
		float horiz = new Vector3( delta.x, delta.y, 0f ).Length;
		return Task.FromResult<object>( new
		{
			distance = delta.Length,
			horizontal = horiz,
			delta = new { delta.x, delta.y, delta.z }
		} );
	}

	static bool TryResolvePoint( Scene scene, JsonElement p, string idKey, string ptKey, out Vector3 pos )
	{
		pos = Vector3.Zero;
		if ( p.TryGetProperty( idKey, out var idEl ) && Guid.TryParse( idEl.GetString(), out var g ) )
		{
			var go = scene.Directory.FindByGuid( g );
			if ( go == null ) return false;
			pos = go.WorldPosition; return true;
		}
		if ( p.TryGetProperty( ptKey, out var ptEl ) ) { pos = ClaudeBridge.ParseVector3( ptEl ); return true; }
		return false;
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 22 — Environment & props: scatter / randomize / group
// ═════════════════════════════════════════════════════════════════════

/// <summary>Tiny deterministic PRNG — avoids sandbox randomness-API uncertainty; same seed → same layout.</summary>
public class Lcg
{
	private uint _s;
	public Lcg( uint seed ) { _s = seed == 0u ? 1u : seed; }
	public float Float01() { _s = _s * 1664525u + 1013904223u; return ( _s >> 8 ) * ( 1.0f / 16777216.0f ); }
	public float Range( float a, float b ) => a + ( b - a ) * Float01();
}

// ───────── scatter_props (scatter N model copies in a radius) ──────────
public class ScatterPropsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		var modelPath = p.TryGetProperty( "model", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrWhiteSpace( modelPath ) )
			return Task.FromResult<object>( new { error = "model (path) is required" } );
		Model model = null;
		try { model = Model.Load( modelPath ); } catch { }
		if ( model == null ) return Task.FromResult<object>( new { error = $"Model not found: {modelPath}" } );

		var center   = p.TryGetProperty( "center", out var c ) ? ClaudeBridge.ParseVector3( c ) : Vector3.Zero;
		float radius = p.TryGetProperty( "radius", out var r ) ? r.GetSingle() : 256f;
		int count    = p.TryGetProperty( "count", out var cn ) ? cn.GetInt32() : 10;
		if ( count < 1 ) count = 1; if ( count > 300 ) count = 300;
		bool randomYaw = !p.TryGetProperty( "randomYaw", out var ry ) || ry.GetBoolean();
		bool snap      = !p.TryGetProperty( "snapToGround", out var sg ) || sg.GetBoolean();
		float scaleMin = p.TryGetProperty( "scaleMin", out var smn ) ? smn.GetSingle() : 1f;
		float scaleMax = p.TryGetProperty( "scaleMax", out var smx ) ? smx.GetSingle() : 1f;
		uint seed      = p.TryGetProperty( "seed", out var sd ) ? (uint)sd.GetInt32() : 1u;
		var rng = new Lcg( seed );
		string name = p.TryGetProperty( "name", out var nm ) ? nm.GetString() : "Prop";
		bool tinted = p.TryGetProperty( "tint", out var tEl );

		GameObject group = null;
		if ( !p.TryGetProperty( "group", out var gp ) || gp.GetBoolean() )
		{
			group = scene.CreateObject( true );
			group.Name = $"{name} Scatter";
			group.WorldPosition = center;
		}

		var created = new List<string>();
		for ( int i = 0; i < count; i++ )
		{
			var dir = Rotation.FromYaw( rng.Range( 0f, 360f ) ).Forward; // unit direction, no trig
			var pos = center + dir * ( radius * rng.Float01() );
			if ( snap )
			{
				try
				{
					var tr = scene.Trace.Ray( pos + Vector3.Up * 2000f, pos + Vector3.Down * 20000f ).Run();
					if ( tr.Hit ) pos = new Vector3( pos.x, pos.y, tr.HitPosition.z );
				}
				catch { }
			}

			var prop = scene.CreateObject( true );
			prop.Name = $"{name} {i + 1}";
			prop.WorldPosition = pos;
			if ( randomYaw ) prop.WorldRotation = Rotation.FromYaw( rng.Range( 0f, 360f ) );
			if ( scaleMax > scaleMin ) { float s = rng.Range( scaleMin, scaleMax ); prop.WorldScale = new Vector3( s, s, s ); }
			var mr = prop.AddComponent<ModelRenderer>();
			mr.Model = model;
			if ( tinted ) mr.Tint = VisualHelpers.ParseColorElement( tEl, Color.White );
			if ( group != null ) prop.SetParent( group, true );
			created.Add( prop.Id.ToString() );
		}

		return Task.FromResult<object>( new { scattered = created.Count, groupId = group?.Id.ToString(), seed } );
	}
}

// ───────── randomize_transforms (vary yaw/scale for a natural look) ────
public class RandomizeTransformsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		var gos = SceneToolHelpers.ResolveIds( scene, p );
		if ( gos.Count < 1 ) return Task.FromResult<object>( new { error = "ids: provide at least 1 GameObject GUID" } );

		bool doYaw     = !p.TryGetProperty( "randomYaw", out var ry ) || ry.GetBoolean();
		float scaleMin = p.TryGetProperty( "scaleMin", out var smn ) ? smn.GetSingle() : 1f;
		float scaleMax = p.TryGetProperty( "scaleMax", out var smx ) ? smx.GetSingle() : 1f;
		uint seed      = p.TryGetProperty( "seed", out var sd ) ? (uint)sd.GetInt32() : 1u;
		var rng = new Lcg( seed );

		foreach ( var g in gos )
		{
			if ( doYaw ) g.WorldRotation = Rotation.FromYaw( rng.Range( 0f, 360f ) );
			if ( scaleMax > scaleMin ) { float s = rng.Range( scaleMin, scaleMax ); g.WorldScale = new Vector3( s, s, s ); }
		}
		return Task.FromResult<object>( new { randomized = gos.Count, seed } );
	}
}

// ───────── group_objects (reparent a set under a new empty) ────────────
public class GroupObjectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		var gos = SceneToolHelpers.ResolveIds( scene, p );
		if ( gos.Count < 1 ) return Task.FromResult<object>( new { error = "ids: provide at least 1 GameObject GUID" } );

		var group = scene.CreateObject( true );
		group.Name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "Group";

		var sum = Vector3.Zero;
		foreach ( var g in gos ) sum += g.WorldPosition;
		group.WorldPosition = sum / gos.Count; // centroid

		foreach ( var g in gos ) g.SetParent( group, true );
		return Task.FromResult<object>( new { grouped = gos.Count, groupId = group.Id.ToString(), name = group.Name } );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 23 — Object utilities & queries: find / tint / replace / tags
// ═════════════════════════════════════════════════════════════════════

// ───────── find_objects (query the scene by name/component/tag) ────────
public class FindObjectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		string nameQ = p.TryGetProperty( "name", out var n ) ? n.GetString()?.ToLowerInvariant() : null;
		string compQ = p.TryGetProperty( "component", out var c ) ? c.GetString() : null;
		string tagQ  = p.TryGetProperty( "tag", out var t ) ? t.GetString() : null;
		int limit = p.TryGetProperty( "limit", out var l ) ? l.GetInt32() : 50;
		if ( limit < 1 ) limit = 1; if ( limit > 500 ) limit = 500;

		var results = new List<object>();
		foreach ( var go in scene.GetAllObjects( true ) )
		{
			if ( go == null ) continue;
			if ( nameQ != null && ( go.Name == null || !go.Name.ToLowerInvariant().Contains( nameQ ) ) ) continue;
			if ( tagQ != null && !go.Tags.Has( tagQ ) ) continue;
			if ( compQ != null )
			{
				bool has = false;
				foreach ( var comp in go.Components.GetAll() )
					if ( string.Equals( comp.GetType().Name, compQ, StringComparison.OrdinalIgnoreCase ) ) { has = true; break; }
				if ( !has ) continue;
			}
			results.Add( new { id = go.Id.ToString(), name = go.Name } );
			if ( results.Count >= limit ) break;
		}
		return Task.FromResult<object>( new { count = results.Count, objects = results } );
	}
}

// ───────── set_tint (recolour one or many renderers) ───────────────────
public class SetTintHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		var gos = SceneToolHelpers.ResolveTargets( scene, p );
		if ( gos.Count == 0 ) return Task.FromResult<object>( new { error = "provide id or ids" } );
		if ( !p.TryGetProperty( "tint", out var tEl ) ) return Task.FromResult<object>( new { error = "tint {r,g,b,a?} is required" } );
		var color = VisualHelpers.ParseColorElement( tEl, Color.White );

		int n = 0;
		foreach ( var go in gos )
		{
			var mr = go.GetComponent<ModelRenderer>();
			if ( mr != null ) { mr.Tint = color; n++; }
		}
		return Task.FromResult<object>( new { tinted = n } );
	}
}

// ───────── replace_model (swap the model on one or many renderers) ─────
public class ReplaceModelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		var gos = SceneToolHelpers.ResolveTargets( scene, p );
		if ( gos.Count == 0 ) return Task.FromResult<object>( new { error = "provide id or ids" } );
		var modelPath = p.TryGetProperty( "model", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrWhiteSpace( modelPath ) ) return Task.FromResult<object>( new { error = "model (path) is required" } );
		Model model = null;
		try { model = Model.Load( modelPath ); } catch { }
		if ( model == null ) return Task.FromResult<object>( new { error = $"Model not found: {modelPath}" } );

		int n = 0;
		foreach ( var go in gos )
		{
			var mr = go.GetComponent<ModelRenderer>();
			if ( mr != null ) { mr.Model = model; n++; }
		}
		return Task.FromResult<object>( new { replaced = n, model = modelPath } );
	}
}

// ───────── set_tags (add/remove/clear gameplay tags) ───────────────────
public class SetTagsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		var gos = SceneToolHelpers.ResolveTargets( scene, p );
		if ( gos.Count == 0 ) return Task.FromResult<object>( new { error = "provide id or ids" } );

		var add = ReadStringArray( p, "add" );
		var remove = ReadStringArray( p, "remove" );
		bool clear = p.TryGetProperty( "clear", out var cl ) && cl.GetBoolean();
		if ( add.Count == 0 && remove.Count == 0 && !clear )
			return Task.FromResult<object>( new { error = "provide add[], remove[], or clear:true" } );

		foreach ( var go in gos )
		{
			if ( clear ) go.Tags.RemoveAll();
			foreach ( var tg in add ) go.Tags.Add( tg );
			foreach ( var tg in remove ) go.Tags.Remove( tg );
		}
		return Task.FromResult<object>( new { updated = gos.Count, added = add, removed = remove, cleared = clear } );
	}

	static List<string> ReadStringArray( JsonElement p, string key )
	{
		var list = new List<string>();
		if ( p.TryGetProperty( key, out var arr ) && arr.ValueKind == JsonValueKind.Array )
			foreach ( var e in arr.EnumerateArray() ) { var s = e.GetString(); if ( !string.IsNullOrWhiteSpace( s ) ) list.Add( s ); }
		return list;
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 24 — Bridge superpowers (editor side): frame_camera
//  Lets Claude AIM ITS OWN SCREENSHOTS at any object/point — fixes the
//  "can't see the result" blindness. (read_log / get_compile_errors live in
//  the MCP server; they read the log file so they work even when s&box has
//  crashed.)
// ═════════════════════════════════════════════════════════════════════

public class FrameCameraHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var session = SceneEditorSession.Active;
		var scene = session?.Scene;
		if ( session == null || scene == null )
			return Task.FromResult<object>( new { error = "No active scene/session" } );

		BBox box;
		string target;
		if ( p.TryGetProperty( "id", out var idEl ) && Guid.TryParse( idEl.GetString(), out var guid ) )
		{
			var go = scene.Directory.FindByGuid( guid );
			if ( go == null )
				return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
			box = go.GetBounds();
			if ( box.Size.Length < 1f ) // no renderer / zero bounds → pad around the point
				box = new BBox( go.WorldPosition - new Vector3( 64f, 64f, 64f ), go.WorldPosition + new Vector3( 64f, 64f, 64f ) );
			target = go.Name;
		}
		else if ( p.TryGetProperty( "position", out var posEl ) )
		{
			var c = ClaudeBridge.ParseVector3( posEl );
			float r = p.TryGetProperty( "radius", out var rEl ) ? rEl.GetSingle() : 128f;
			box = new BBox( c - new Vector3( r, r, r ), c + new Vector3( r, r, r ) );
			target = $"({c.x:0.#},{c.y:0.#},{c.z:0.#}) r{r:0.#}";
		}
		else
		{
			return Task.FromResult<object>( new { error = "provide id (GameObject GUID) or position {x,y,z} (+ optional radius)" } );
		}

		session.FrameTo( box );
		return Task.FromResult<object>( new
		{
			framed = true,
			target,
			center = new { box.Center.x, box.Center.y, box.Center.z },
			note = "Editor camera moved — take_screenshot now captures this view."
		} );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 25 — screenshot aiming + component/tag gaps
// ═════════════════════════════════════════════════════════════════════

// ───────── screenshot_from (THE fix: aim Claude's screenshots) ─────────
//  take_screenshot renders from the scene's MAIN CAMERA (not the viewport).
//  This saves the main camera's transform, moves it to frame a target,
//  captures, and restores it — so screenshots can finally be aimed.
public class ScreenshotFromHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		var cam = VisualHelpers.FindMainCamera( scene );
		if ( cam == null ) return Task.FromResult<object>( new { error = "No main camera found (take_screenshot renders from the scene's main camera)." } );
		var go = cam.GameObject;
		var savedPos = go.WorldPosition;
		var savedRot = go.WorldRotation;

		Vector3 camPos; Rotation camRot; string framed;
		if ( p.TryGetProperty( "id", out var idEl ) && Guid.TryParse( idEl.GetString(), out var guid ) )
		{
			var t = scene.Directory.FindByGuid( guid );
			if ( t == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
			var box = t.GetBounds();
			var c = box.Center;
			float sz = box.Size.Length; if ( sz < 1f ) sz = 128f;
			float dist = sz * 1.4f; if ( dist < 150f ) dist = 150f;
			camPos = c + new Vector3( -1f, -0.6f, 0.7f ).Normal * dist; // 3/4 elevated view
			camRot = Rotation.LookAt( ( c - camPos ).Normal, Vector3.Up );
			framed = t.Name;
		}
		else if ( p.TryGetProperty( "position", out var posEl ) )
		{
			camPos = ClaudeBridge.ParseVector3( posEl );
			if ( p.TryGetProperty( "lookAt", out var laEl ) )
				camRot = Rotation.LookAt( ( ClaudeBridge.ParseVector3( laEl ) - camPos ).Normal, Vector3.Up );
			else if ( p.TryGetProperty( "rotation", out var rotEl ) )
				camRot = CharacterHelpers.ParseRotation( rotEl );
			else camRot = savedRot;
			framed = $"({camPos.x:0.#},{camPos.y:0.#},{camPos.z:0.#})";
		}
		else return Task.FromResult<object>( new { error = "provide id (object to frame) or position {x,y,z} (+ optional lookAt or rotation)" } );

		int w = p.TryGetProperty( "width", out var wEl ) ? wEl.GetInt32() : 1920;
		int h = p.TryGetProperty( "height", out var hEl ) ? hEl.GetInt32() : 1080;

		go.WorldPosition = camPos;
		go.WorldRotation = camRot;
		EditorScene.TakeHighResScreenshot( w, h );
		go.WorldPosition = savedPos; // restore (assumes synchronous capture; verify on first use)
		go.WorldRotation = savedRot;

		return Task.FromResult<object>( new { captured = true, framed, note = "Main camera framed, captured, and restored — read the newest screenshot." } );
	}
}

// ───────── remove_component (remove a component by type from a GO) ──────
public class RemoveComponentHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var typeName = p.TryGetProperty( "component", out var cEl ) ? cEl.GetString() : null;
		if ( string.IsNullOrWhiteSpace( typeName ) )
			return Task.FromResult<object>( new { error = "component (type name) is required" } );

		bool all = p.TryGetProperty( "all", out var aEl ) && aEl.GetBoolean();
		var matches = new List<Component>();
		foreach ( var comp in go.Components.GetAll() )
			if ( string.Equals( comp.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase ) )
			{
				matches.Add( comp );
				if ( !all ) break;
			}
		if ( matches.Count == 0 )
			return Task.FromResult<object>( new { error = $"No '{typeName}' component on the object" } );
		foreach ( var m in matches ) m.Destroy();
		return Task.FromResult<object>( new { removed = matches.Count, component = typeName } );
	}
}

// ───────── get_tags (read a GameObject's tags) ─────────────────────────
public class GetTagsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var tags = new List<string>();
		try { foreach ( var t in go.Tags.TryGetAll() ) tags.Add( t ); } catch { }
		return Task.FromResult<object>( new { id = idEl.GetString(), tags } );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 26 — console_run (run an s&box console command / ConCmd)
//  Also the invocation backbone for execute_csharp (TS orchestrates:
//  write a temp [ConCmd] .cs in the editor-unsandboxed Editor/ folder →
//  hotload → console_run it → read the result from the log → clean up).
// ═════════════════════════════════════════════════════════════════════
public class ConsoleRunHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var command = p.TryGetProperty( "command", out var cEl ) ? cEl.GetString() : null;
		if ( string.IsNullOrWhiteSpace( command ) )
			return Task.FromResult<object>( new { error = "command is required" } );
		try { Sandbox.ConsoleSystem.Run( command ); }
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"Console command failed: {ex.Message}" } ); }
		return Task.FromResult<object>( new { ran = true, command } );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 27 — navigation (REAL editor ops, not component wrappers):
//  bake the scene navmesh + query a path on it. NavMesh.BakeNavMesh() is
//  the static editor bake (async void → poll IsGenerating); GetSimplePath
//  returns List<Vector3>. Verified against Sandbox.Engine.dll IL.
// ═════════════════════════════════════════════════════════════════════
public class BakeNavMeshHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var nav = scene.NavMesh;
		if ( nav == null )
			return Task.FromResult<object>( new { error = "Active scene has no NavMesh" } );

		try
		{
			// NavMesh must be enabled for a bake to do anything.
			nav.IsEnabled = true;

			// Optional agent configuration (read defensively; applied only if present).
			if ( p.TryGetProperty( "agentRadius", out var ar ) )    nav.AgentRadius   = ar.GetSingle();
			if ( p.TryGetProperty( "agentHeight", out var ah ) )    nav.AgentHeight   = ah.GetSingle();
			if ( p.TryGetProperty( "agentStepSize", out var asz ) ) nav.AgentStepSize = asz.GetSingle();
			if ( p.TryGetProperty( "agentMaxSlope", out var ams ) ) nav.AgentMaxSlope = ams.GetSingle();
			if ( p.TryGetProperty( "includeStaticBodies", out var isb ) ) nav.IncludeStaticBodies = isb.GetBoolean();

			// The static editor bake targets Application.Editor.Scene.NavMesh — the SAME
			// instance as scene.NavMesh we just configured — and shows the native editor
			// progress UI. It is async void (fire-and-forget): returns immediately and
			// generation continues in the background, so we do NOT await it. Poll IsGenerating.
			Sandbox.Navigation.NavMesh.BakeNavMesh();

			return Task.FromResult<object>( new
			{
				baking       = true,
				note         = "navmesh generating async; poll get_navmesh_path or re-bake when IsGenerating is false",
				isEnabled    = nav.IsEnabled,
				isGenerating = nav.IsGenerating,
				settings = new
				{
					agentRadius         = nav.AgentRadius,
					agentHeight         = nav.AgentHeight,
					agentStepSize       = nav.AgentStepSize,
					agentMaxSlope       = nav.AgentMaxSlope,
					includeStaticBodies = nav.IncludeStaticBodies
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Bake failed: {ex.Message}" } );
		}
	}
}

public class GetNavMeshPathHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var nav = scene.NavMesh;
		if ( nav == null )
			return Task.FromResult<object>( new { error = "Active scene has no NavMesh" } );

		if ( !p.TryGetProperty( "from", out var fromEl ) || !p.TryGetProperty( "to", out var toEl ) )
			return Task.FromResult<object>( new { error = "Requires 'from' and 'to' Vector3 params" } );

		var from = ClaudeBridge.ParseVector3( fromEl );
		var to   = ClaudeBridge.ParseVector3( toEl );

		try
		{
			// GetSimplePath returns List<Vector3> (verified). Empty/null => unreachable.
			var path = nav.GetSimplePath( from, to );

			if ( path == null || path.Count == 0 )
			{
				return Task.FromResult<object>( new
				{
					reachable = false,
					count     = 0,
					points    = new object[0],
					from      = new { from.x, from.y, from.z },
					to        = new { to.x, to.y, to.z },
					note      = "No path found — is the navmesh baked (bake_navmesh) and both points on it?"
				} );
			}

			var points = new List<object>( path.Count );
			foreach ( var pt in path )
				points.Add( new { pt.x, pt.y, pt.z } );

			return Task.FromResult<object>( new
			{
				reachable = true,
				count     = points.Count,
				points    = points,
				from      = new { from.x, from.y, from.z },
				to        = new { to.x, to.y, to.z }
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Path query failed: {ex.Message}" } );
		}
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 28 — spatial query + reflection bake (REAL ops, not wrappers):
//  physics_overlap = a SPHERE/BOX volume query (complements raycast's ray)
//  via scene.Trace…RunAll(); bake_reflections = EnvmapProbe.BakeAll() so
//  placed reflection probes actually capture (placement alone does nothing).
//  Both verified against the live editor + Sandbox.Engine.dll.
// ═════════════════════════════════════════════════════════════════════
public class PhysicsOverlapHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "center", out var centerEl ) )
			return Task.FromResult<object>( new { error = "requires center (Vector3)" } );

		var center = ClaudeBridge.ParseVector3( centerEl );

		try
		{
			// Zero-length sweep (from == to == center) = a static overlap query.
			SceneTrace trace;

			if ( p.TryGetProperty( "radius", out var radiusEl ) )
			{
				// SPHERE overlap.
				trace = scene.Trace.Sphere( radiusEl.GetSingle(), center, center );
			}
			else if ( p.TryGetProperty( "size", out var sizeEl ) )
			{
				// BOX overlap. The Box(Vector3,…) overload is EXTENTS (half-size), so use
				// the BBox overload with FromPositionAndSize( center, fullSize ) for clarity.
				var size = ClaudeBridge.ParseVector3( sizeEl );
				trace = scene.Trace.Box( BBox.FromPositionAndSize( center, size ), center, center );
			}
			else
			{
				return Task.FromResult<object>( new { error = "requires radius (sphere) or size (box)" } );
			}

			// A GameObject may have several colliders → several hits; dedupe by GUID.
			var seen = new HashSet<Guid>();
			var hits = new List<object>();
			foreach ( var hit in trace.RunAll() )
			{
				var go = hit.GameObject;
				if ( go == null ) continue;
				if ( !seen.Add( go.Id ) ) continue;
				var pos = go.WorldPosition;
				hits.Add( new { id = go.Id.ToString(), name = go.Name, position = new { pos.x, pos.y, pos.z } } );
			}

			return Task.FromResult<object>( new { count = hits.Count, hits } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Overlap query failed: {ex.Message}" } );
		}
	}
}

public class BakeReflectionsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		try
		{
			var probes = scene.GetAllComponents<EnvmapProbe>().ToList();
			if ( probes.Count == 0 )
				return Task.FromResult<object>( new
				{
					baked = false,
					count = 0,
					note  = "No EnvmapProbe components in the scene — add one with add_envmap_probe first."
				} );

			// Bake is async (returns Task). Mirror BakeNavMeshHandler: fire-and-forget so we
			// don't block the single-request-per-frame bridge loop. BakeAll() = scene-wide.
			_ = EnvmapProbe.BakeAll();

			return Task.FromResult<object>( new
			{
				baking = true,
				count  = probes.Count,
				note   = "Reflection envmap bake started for all probes (async). Re-screenshot after a moment to see captured reflections.",
				probes = probes.Select( pr => new
				{
					id       = pr.GameObject?.Id.ToString(),
					name     = pr.GameObject?.Name,
					mode     = pr.Mode.ToString(),
					hasBaked = pr.BakedTexture != null
				} )
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Bake failed: {ex.Message}" } );
		}
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 29 — spawn_vpcf: play a REAL .vpcf via LegacyParticleSystem.
//  This is the RELIABLE particle path (resource-driven, textures baked in),
//  unlike the runtime ParticleEffect graph (Batch 18) that renders nothing.
//  ParticleSystem.Load(logicalPath) → LegacyParticleSystem.Particles; the
//  component auto-plays on enable. Default asset = particles/impact.generic
//  (the only reachable .vpcf — a sparks/impact burst; tint warm for fire).
// ═════════════════════════════════════════════════════════════════════
public class SpawnVpcfHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var vpcfPath = p.TryGetProperty( "vpcf", out var vEl ) ? vEl.GetString() : null;
		if ( string.IsNullOrWhiteSpace( vpcfPath ) )
			vpcfPath = "particles/impact.generic.vpcf";

		ParticleSystem particles;
		try { particles = ParticleSystem.Load( vpcfPath ); }
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"Failed to load '{vpcfPath}': {ex.Message}" } ); }
		if ( particles == null )
			return Task.FromResult<object>( new { error = $"Particle system not found: {vpcfPath}" } );

		try
		{
			var go = scene.CreateObject( true );
			go.Name = ( p.TryGetProperty( "name", out var nEl ) && !string.IsNullOrWhiteSpace( nEl.GetString() ) )
				? nEl.GetString()
				: "Particle (vpcf)";
			if ( p.TryGetProperty( "position", out var pos ) )
				go.WorldPosition = ClaudeBridge.ParseVector3( pos );

			var lps = go.AddComponent<LegacyParticleSystem>();
			lps.Particles = particles;
			lps.Looped = !p.TryGetProperty( "looped", out var lEl ) || lEl.GetBoolean(); // default true
			if ( p.TryGetProperty( "playbackSpeed", out var psEl ) )
				lps.PlaybackSpeed = psEl.GetSingle();

			// Tint applies to the live SceneObject, which is created on enable — may be
			// null on this same frame; report whether it took.
			bool tinted = false;
			if ( p.TryGetProperty( "tint", out var tintEl ) && lps.SceneObject != null )
			{
				lps.SceneObject.ColorTint = VisualHelpers.ParseColorElement( tintEl, Color.White );
				tinted = true;
			}

			return Task.FromResult<object>( new
			{
				created          = true,
				vpcf             = vpcfPath,
				looped           = lps.Looped,
				tinted,
				sceneObjectReady = lps.SceneObject != null,
				gameObject = new
				{
					id       = go.Id.ToString(),
					name     = go.Name,
					position = new { go.WorldPosition.x, go.WorldPosition.y, go.WorldPosition.z }
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"spawn_vpcf failed: {ex.Message}" } );
		}
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 30 — restart_editor: closes the C#-edit→recompile loop.
//  EditorUtility.RestartEditor() relaunches sbox-dev.exe with the same args
//  + this project (reopens straight back in). We handle unsaved scenes here
//  (save or discard) because the engine's own save popup is non-blocking and
//  would race the relaunch — and a bridge-driven restart must be headless
//  (no dialog for a human to click). Mechanism confirmed from screch.auto_restart.
// ═════════════════════════════════════════════════════════════════════
public class RestartEditorHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		// Default: save unsaved scenes before restarting. Pass save:false to discard them.
		bool save = !p.TryGetProperty( "save", out var s ) || s.GetBoolean();
		int handled = 0;
		try
		{
			var unsaved = SceneEditorSession.All.Where( x => x != null && x.HasUnsavedChanges ).ToList();
			foreach ( var sess in unsaved )
			{
				try
				{
					if ( save ) sess.Save( false );
					else        sess.HasUnsavedChanges = false; // mark clean so the engine's OnClose check passes
				}
				catch ( Exception ex )
				{
					Log.Warning( $"[SboxBridge] restart_editor: scene '{sess.Scene?.Name}' {(save ? "save" : "discard")} failed: {ex.Message}" );
				}
				handled++;
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"restart_editor pre-flight failed: {ex.Message}" } );
		}

		// RestartEditor()'s Close() is non-blocking, so this returns and the bridge writes
		// the response before the old process exits; the MCP server then polls
		// get_bridge_status until the relaunched editor's bridge reconnects.
		try { EditorUtility.RestartEditor(); }
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"RestartEditor failed: {ex.Message}" } ); }

		return Task.FromResult<object>( new
		{
			restarting    = true,
			scenesHandled = handled,
			saved         = save,
			note          = "Editor relaunching into this project; poll get_bridge_status until it reconnects (~30-90s)."
		} );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 31 — list_libraries: what addons/libraries are installed in this
//  project (reads Libraries/ + each .sbproj). Lets Claude DISCOVER what's
//  available to build on (e.g. fish.scc = Shrimple Character Controller,
//  facepunch.playercontroller) and leverage it via add_component_with_properties
//  instead of writing movement/tools from scratch.
// ═════════════════════════════════════════════════════════════════════
public class ListLibrariesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var root = Project.Current.GetRootPath();
		var libsDir = Path.Combine( root, "Libraries" );
		var libs = new List<object>();
		try
		{
			if ( Directory.Exists( libsDir ) )
			{
				foreach ( var dir in Directory.GetDirectories( libsDir ) )
				{
					// A library is enabled if it has a live .sbproj (disabled ones use .sbproj.disabled).
					var sbproj = Directory.GetFiles( dir, "*.sbproj" ).FirstOrDefault();
					bool enabled = sbproj != null;
					if ( sbproj == null )
						sbproj = Directory.GetFiles( dir, "*.sbproj.disabled" ).FirstOrDefault();

					string ident = Path.GetFileName( dir ), org = null, title = null, type = null;
					if ( sbproj != null )
					{
						try
						{
							using var doc = JsonDocument.Parse( File.ReadAllText( sbproj ) );
							var r = doc.RootElement;
							if ( r.TryGetProperty( "Title", out var t ) ) title = t.GetString();
							if ( r.TryGetProperty( "Org",   out var o ) ) org   = o.GetString();
							if ( r.TryGetProperty( "Ident", out var i ) ) ident = i.GetString();
							if ( r.TryGetProperty( "Type",  out var ty ) ) type = ty.GetString();
						}
						catch { /* leave folder-name fallbacks */ }
					}
					libs.Add( new { folder = Path.GetFileName( dir ), ident, org, title, type, enabled } );
				}
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"list_libraries failed: {ex.Message}" } );
		}
		return Task.FromResult<object>( new { count = libs.Count, libraries = libs } );
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 32 — recompile_asset: compile a project asset from the bridge.
//  Editor.AssetSystem.RegisterFile(abs) → Asset, then Asset.Compile(true)
//  produces the compiled form (e.g. a .vpcf → .vpcf_c). This is what lets
//  the bridge AUTHOR + COMPILE an asset (write_file a .vpcf → recompile_asset
//  → spawn_vpcf) instead of needing the editor's asset pipeline by hand.
// ═════════════════════════════════════════════════════════════════════
public class RecompileAssetHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var path = p.TryGetProperty( "path", out var pe ) ? pe.GetString() : null;
		if ( string.IsNullOrWhiteSpace( path ) )
			return Task.FromResult<object>( new { error = "path is required (project-relative, e.g. 'particles/fire.vpcf', or absolute)" } );

		string abs;
		try
		{
			if ( Path.IsPathRooted( path ) )
			{
				abs = path;
			}
			else
			{
				var root = Project.Current.GetRootPath();
				// Project assets usually live under Assets/; accept a bare path too.
				var candidates = new[] { Path.Combine( root, "Assets", path ), Path.Combine( root, path ) };
				abs = candidates.FirstOrDefault( File.Exists ) ?? candidates[0];
			}
		}
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"path resolve failed: {ex.Message}" } ); }

		abs = Path.GetFullPath( abs ); // normalize mixed separators (Combine + a /-input mix)
		if ( !File.Exists( abs ) )
			return Task.FromResult<object>( new { error = $"source file not found: {abs}" } );

		try
		{
			// RegisterFile wants an absolute path — try the Windows and forward-slash forms.
			var asset = Editor.AssetSystem.RegisterFile( abs )
				?? Editor.AssetSystem.RegisterFile( abs.Replace( '\\', '/' ) );

			if ( asset != null )
			{
				bool compiled = asset.Compile( true );
				return Task.FromResult<object>( new
				{
					method          = "RegisterFile",
					registered      = true,
					compiled,
					relativePath    = asset.RelativePath,
					isCompiled      = asset.IsCompiled,
					hasCompiledFile = asset.HasCompiledFile,
					compiledFile    = asset.HasCompiledFile ? asset.GetCompiledFile( false ) : null
				} );
			}

			// Fallback: compile the resource straight from its source text.
			var text = File.ReadAllText( abs );
			var logical = path.Replace( '\\', '/' );
			bool cr = Editor.AssetSystem.CompileResource( logical, text );
			return Task.FromResult<object>( new
			{
				method      = "CompileResource",
				compiled    = cr,
				logicalPath = logical,
				note        = "RegisterFile returned null; used CompileResource(path, text)"
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"recompile_asset failed: {ex.Message}" } );
		}
	}
}

// ═════════════════════════════════════════════════════════════════════
//  Batch 33 — animation + bounds (v1.6.0)
//  • list_animations / play_animation drive SkinnedModelRenderer.Sequence
//  • set_animgraph_param drives Set(name,value) for AnimationGraph-driven
//    characters (e.g. Citizen — move_x / move_y / b_grounded …)
//  • get_bounds returns world bounds/center/size (used standalone + by the
//    TS-side screenshot_orbit verification tool)
// ═════════════════════════════════════════════════════════════════════

public class GetBoundsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );

		var box = go.GetBounds();
		var c = box.Center; var s = box.Size; var e = box.Extents;
		var mins = c - e; var maxs = c + e;
		bool empty = s.Length < 0.01f;
		return Task.FromResult<object>( new
		{
			id = idEl.GetString(),
			name = go.Name,
			center   = new { c.x, c.y, c.z },
			size     = new { s.x, s.y, s.z },
			extents  = new { e.x, e.y, e.z },
			mins     = new { mins.x, mins.y, mins.z },
			maxs     = new { maxs.x, maxs.y, maxs.z },
			radius   = s.Length * 0.5f,
			position = new { go.WorldPosition.x, go.WorldPosition.y, go.WorldPosition.z },
			empty,
			note = empty ? "Zero/near-zero bounds (no renderer) — using world position; orbit will frame a default radius." : null
		} );
	}
}

public class PlayAnimationHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null ) return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer (use spawn_citizen or a model with sequences)" } );
		if ( body.Model == null ) return Task.FromResult<object>( new { error = "The SkinnedModelRenderer has no Model assigned" } );

		var anim = p.TryGetProperty( "animation", out var aEl ) ? aEl.GetString()
			: ( p.TryGetProperty( "name", out var nEl ) ? nEl.GetString() : null );
		if ( string.IsNullOrWhiteSpace( anim ) )
			return Task.FromResult<object>( new { error = "animation (sequence name) is required — call list_animations to see options" } );

		var seq = body.Sequence;
		var names = new List<string>();
		try { foreach ( var n in seq.SequenceNames ) names.Add( n ); } catch { }
		if ( names.Count > 0 && !names.Contains( anim, StringComparer.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = $"Sequence '{anim}' not found on this model.", available = names } );

		seq.Name = anim;
		if ( p.TryGetProperty( "looping", out var lEl ) && ( lEl.ValueKind == JsonValueKind.True || lEl.ValueKind == JsonValueKind.False ) )
			seq.Looping = lEl.GetBoolean();
		if ( p.TryGetProperty( "speed", out var spEl ) && spEl.ValueKind == JsonValueKind.Number )
			seq.PlaybackRate = spEl.GetSingle();
		if ( p.TryGetProperty( "time", out var tEl ) && tEl.ValueKind == JsonValueKind.Number )
			seq.TimeNormalized = tEl.GetSingle();

		return Task.FromResult<object>( new
		{
			playing = true,
			animation = anim,
			looping = seq.Looping,
			playbackRate = seq.PlaybackRate,
			duration = seq.Duration,
			note = "Sequence set. The renderer needs PlayAnimationsInEditorScene = true to animate in the editor; screenshot to verify a pose. For Citizen/animgraph characters, set_animgraph_param usually drives motion."
		} );
	}
}

public class SetAnimgraphParamHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null ) return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer (animgraph params need one — e.g. a Citizen)" } );

		var name = p.TryGetProperty( "param", out var nEl ) ? nEl.GetString()
			: ( p.TryGetProperty( "name", out var n2 ) ? n2.GetString() : null );
		if ( string.IsNullOrWhiteSpace( name ) )
			return Task.FromResult<object>( new { error = "param (animgraph parameter name, e.g. 'move_x') is required" } );
		if ( !p.TryGetProperty( "value", out var vEl ) )
			return Task.FromResult<object>( new { error = "value is required (number, bool, or {x,y,z})" } );

		var hint = p.TryGetProperty( "type", out var tEl ) ? tEl.GetString()?.ToLowerInvariant() : null;
		string kind;
		try
		{
			if ( hint == "bool" || ( hint == null && ( vEl.ValueKind == JsonValueKind.True || vEl.ValueKind == JsonValueKind.False ) ) )
			{
				bool b = vEl.ValueKind == JsonValueKind.String ? string.Equals( vEl.GetString(), "true", StringComparison.OrdinalIgnoreCase ) : vEl.GetBoolean();
				body.Set( name, b ); kind = "bool";
			}
			else if ( hint == "vector" || ( hint == null && vEl.ValueKind == JsonValueKind.Object ) )
			{
				body.Set( name, ClaudeBridge.ParseVector3( vEl ) ); kind = "vector";
			}
			else if ( hint == "int" )
			{
				int iv = vEl.ValueKind == JsonValueKind.String ? int.Parse( vEl.GetString() ) : (int) vEl.GetSingle();
				body.Set( name, iv ); kind = "int";
			}
			else // float (default for numbers)
			{
				float fv = vEl.ValueKind == JsonValueKind.String ? float.Parse( vEl.GetString() ) : vEl.GetSingle();
				body.Set( name, fv ); kind = "float";
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set '{name}': {ex.Message}" } );
		}

		return Task.FromResult<object>( new
		{
			set = true,
			param = name,
			kind,
			note = "Animgraph parameter set. Citizen poses in-editor when PlayAnimationsInEditorScene is on; screenshot to verify."
		} );
	}
}

public class ListAnimationsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		var body = go.GetComponent<SkinnedModelRenderer>();
		if ( body == null ) return Task.FromResult<object>( new { error = "Target has no SkinnedModelRenderer (animations need one — e.g. spawn_citizen, or a model with sequences)" } );
		if ( body.Model == null ) return Task.FromResult<object>( new { error = "The SkinnedModelRenderer has no Model assigned" } );

		var names = new List<string>();
		try { foreach ( var n in body.Sequence.SequenceNames ) if ( !string.IsNullOrEmpty( n ) && !names.Contains( n ) ) names.Add( n ); } catch { }
		try { foreach ( var n in body.Model.AnimationNames ) if ( !string.IsNullOrEmpty( n ) && !names.Contains( n ) ) names.Add( n ); } catch { }
		names.Sort( StringComparer.OrdinalIgnoreCase );

		return Task.FromResult<object>( new
		{
			id = idEl.GetString(),
			name = go.Name,
			model = body.Model.ResourceName,
			useAnimGraph = body.UseAnimGraph,
			hasAnimGraph = body.AnimationGraph != null,
			count = names.Count,
			animations = names,
			note = body.UseAnimGraph
				? "Uses an AnimationGraph (e.g. Citizen) — drive motion with set_animgraph_param (graph params like move_x / move_y / b_grounded / b_ducked). play_animation sets a raw sequence by name."
				: "Drive these with play_animation (sequence name)."
		} );
	}
}
