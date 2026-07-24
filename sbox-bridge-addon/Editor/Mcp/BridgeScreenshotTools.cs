using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor;
using Editor.Mcp;
using Sandbox;

/// <summary>
/// Screenshots that arrive as INLINE PNG image blocks (no disk paths to read back).
/// Hand-written — unlike the generated wrappers these convert the bridge's PNG-file
/// results into McpResult.Image so the agent sees the picture directly.
/// </summary>
[McpToolset( "bridge_screenshot", "Capture inline PNG images from the main camera, framed/free viewpoints, object orbits, saved session camera bookmarks, ordered comparison sets, or a deterministic orthographic top-down view. Works in edit and play mode; temporary cameras are removed after every capture." )]
public static class BridgeScreenshotTools
{
	/// <summary>
	/// Screenshot the scene's main camera view (the player's view in play mode) and return it
	/// as an inline PNG image. To frame a specific object or position instead, use capture_view
	/// or screenshot_orbit.
	/// </summary>
	/// <param name="width">Image width in pixels (16-3840). Default 1280.</param>
	/// <param name="height">Image height in pixels (16-2160). Default 720.</param>
	/// <param name="renderUI">Include screen-space UI (HUD) in the capture. Default true.</param>
	[McpTool.ReadOnly( "take_screenshot" )]
	public static Task<object> TakeScreenshot( int width = 1280, int height = 720, bool renderUI = true )
		=> CaptureAsync( McpGate.Args( ("width", width), ("height", height), ("renderUI", renderUI) ) );

	/// <summary>
	/// Screenshot the scene from a chosen viewpoint and return it as an inline PNG image.
	/// Pass id to auto-frame a GameObject (3/4 elevated view sized to its bounds), or position
	/// (+ lookAt or rotation) for a free camera. With neither, captures the main camera view.
	/// Uses a temporary camera that is removed afterwards — the scene is left unchanged.
	/// </summary>
	/// <param name="id">GUID of a GameObject to auto-frame (from get_scene_hierarchy or find_objects).</param>
	/// <param name="position">Camera position as "x,y,z".</param>
	/// <param name="lookAt">Point to aim the camera at, as "x,y,z".</param>
	/// <param name="rotation">Camera rotation as "pitch,yaw,roll" degrees (alternative to lookAt).</param>
	/// <param name="fov">Field of view in degrees. Default is the camera's default.</param>
	/// <param name="width">Image width in pixels (16-3840). Default 1280.</param>
	/// <param name="height">Image height in pixels (16-2160). Default 720.</param>
	/// <param name="renderUI">Include screen-space UI in the capture. Default true.</param>
	[McpTool( "capture_view" )]
	public static Task<object> CaptureView( string id = null, string position = null, string lookAt = null,
		string rotation = null, double? fov = null, int width = 1280, int height = 720, bool renderUI = true )
		=> CaptureAsync( McpGate.Args(
			("id", id), ("position", position), ("lookAt", lookAt), ("rotation", rotation),
			("fov", fov), ("width", width), ("height", height), ("renderUI", renderUI) ) );

	/// <summary>
	/// Frame a GameObject or a free camera position and return the shot as an inline PNG image.
	/// Same as capture_view (kept under its historical name — existing workflows use it).
	/// </summary>
	/// <param name="id">GUID of a GameObject to auto-frame.</param>
	/// <param name="position">Camera position as "x,y,z".</param>
	/// <param name="lookAt">Point to aim the camera at, as "x,y,z".</param>
	/// <param name="rotation">Camera rotation as "pitch,yaw,roll" degrees.</param>
	/// <param name="width">Image width in pixels. Default 1280.</param>
	/// <param name="height">Image height in pixels. Default 720.</param>
	[McpTool( "screenshot_from" )]
	public static Task<object> ScreenshotFrom( string id = null, string position = null, string lookAt = null,
		string rotation = null, int width = 1280, int height = 720 )
		=> CaptureAsync( McpGate.Args(
			("id", id), ("position", position), ("lookAt", lookAt), ("rotation", rotation),
			("width", width), ("height", height) ) );

	/// <summary>
	/// Capture a GameObject from several angles in ONE call — orbits around the object and
	/// returns every angle as an inline PNG image, so 3D work can be verified from multiple
	/// sides instead of guessed from one.
	/// </summary>
	/// <param name="id">GUID of the GameObject to orbit.</param>
	/// <param name="shots">Number of angles around the object (2-8). Default 4.</param>
	/// <param name="elevation">Camera height factor: 0 = level with the object, 1 = high above. Default 0.4.</param>
	/// <param name="distance">Camera distance in units. Default: auto from the object's bounds.</param>
	/// <param name="width">Image width in pixels. Default 1280.</param>
	/// <param name="height">Image height in pixels. Default 720.</param>
	[McpTool( "screenshot_orbit" )]
	public static async Task<object> ScreenshotOrbit( string id, int shots = 4, double elevation = 0.4,
		double? distance = null, int width = 1280, int height = 720 )
	{
		var scene = Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null )
			throw new Exception( "No active scene" );
		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"id must be a GameObject GUID, got: {id}" );
		var go = ClaudeBridge.ResolveGameObject( scene, id )
			?? throw new Exception( $"GameObject not found: {id}" );

		if ( !double.IsFinite( elevation ) )
			throw new Exception( "elevation must be finite" );
		if ( distance.HasValue && (!double.IsFinite( distance.Value )
			|| distance.Value <= 0.0 || distance.Value > float.MaxValue) )
			throw new Exception( "distance must be a finite number greater than zero" );
		shots = Math.Clamp( shots, 2, 8 );
		var box = go.GetBounds();
		var center = box.Center;
		float size = box.Size.Length; if ( size < 1f ) size = 128f;
		float dist = (float)( distance ?? Math.Max( size * 1.6f, 150f ) );
		if ( !float.IsFinite( dist ) || dist <= 0f )
			throw new Exception( "Resolved camera distance must fit in a positive finite float" );
		float elev = (float)Math.Clamp( elevation, 0.0, 1.0 );

		var result = McpResult.Text( $"Orbit of '{go.Name}' — {shots} angles, distance {dist:0}, elevation {elev:0.##}. Angles are evenly spaced yaw steps starting at 0°." );
		for ( int i = 0; i < shots; i++ )
		{
			float yaw = MathF.Tau * i / shots;
			var offset = new Vector3( MathF.Cos( yaw ), MathF.Sin( yaw ), elev ).Normal * dist;
			var pos = center + offset;
			var png = await CapturePngAsync( McpGate.Args(
				("position", $"{pos.x},{pos.y},{pos.z}"),
				("lookAt", $"{center.x},{center.y},{center.z}"),
				("width", width), ("height", height) ) );
			result = result.WithImage( png, "image/png" );
		}
		return result;
	}

	/// <summary>
	/// Save or replace a named camera bookmark in editor-session memory. Pass targetId to save the
	/// bridge's deterministic framed view of a GameObject, position plus lookAt/rotation for an
	/// explicit pose, or omit both to snapshot the current main camera. Returns the resolved pose.
	/// Bookmarks reset on addon hotload or editor restart; list them with list_camera_bookmarks and
	/// capture an ordered group with capture_camera_set.
	/// </summary>
	/// <param name="name">Bookmark name (case-insensitive, 1-64 characters).</param>
	/// <param name="targetId">GameObject GUID to resolve into a deterministic framed pose.</param>
	/// <param name="position">Explicit camera position as "x,y,z".</param>
	/// <param name="lookAt">World point to aim at as "x,y,z"; pair with position.</param>
	/// <param name="rotation">Explicit camera rotation as "pitch,yaw,roll"; alternative to lookAt.</param>
	/// <param name="fov">Field of view in degrees (clamped 1-179). Defaults to 80, or the main camera FOV when snapshotting it.</param>
	[McpTool( "save_camera_bookmark" )]
	public static Task<object> SaveCameraBookmark( string name, string targetId = null,
		string position = null, string lookAt = null, string rotation = null, double? fov = null )
		=> McpGate.Run( "save_camera_bookmark", McpGate.Args(
			("name", name), ("targetId", targetId), ("position", position), ("lookAt", lookAt),
			("rotation", rotation), ("fov", fov) ) );

	/// <summary>
	/// List all session-scoped camera bookmarks in stable name order. Returns each resolved position,
	/// rotation, optional lookAt/target, FOV, and save time. Read-only; bookmarks reset on addon
	/// hotload or editor restart. Feed names into capture_camera_set.
	/// </summary>
	[McpTool.ReadOnly( "list_camera_bookmarks" )]
	public static Task<object> ListCameraBookmarks()
		=> McpGate.Run( "list_camera_bookmarks", McpGate.Args() );

	/// <summary>
	/// Delete one session-scoped camera bookmark and its comparison baselines. Returns deleted=false
	/// when the name was already absent. Use list_camera_bookmarks to inspect what remains.
	/// </summary>
	/// <param name="name">Bookmark name to delete (case-insensitive).</param>
	[McpTool( "delete_camera_bookmark" )]
	public static Task<object> DeleteCameraBookmark( string name )
		=> McpGate.Run( "delete_camera_bookmark", McpGate.Args( ("name", name) ) );

	/// <summary>
	/// Capture 1-8 saved camera bookmarks in the exact requested order and return a labeled JSON
	/// manifest followed by one inline PNG per bookmark. Captures are capped at 1600x1200 and eight
	/// million aggregate pixels. Every run becomes the session baseline for the same bookmark and
	/// settings; comparePrevious uses SkiaSharp RGBA metrics against that prior capture. renderUI
	/// defaults false for stable comparisons. Occlusion checks use physics traces and are diagnostic:
	/// they never move the saved camera.
	/// </summary>
	/// <param name="names">Saved bookmark names in desired output order (1-8, no duplicates).</param>
	/// <param name="comparePrevious">Compare each image with the previous session capture using the same name, dimensions, and renderUI setting.</param>
	/// <param name="diffThreshold">Per-channel threshold (0-255) used for changedPixelPercent. Default 8.</param>
	/// <param name="width">Image width in pixels (16-1600). Default 960.</param>
	/// <param name="height">Image height in pixels (16-1200). Default 540.</param>
	/// <param name="renderUI">Include screen-space UI. Defaults false for deterministic visual comparisons.</param>
	/// <param name="checkOcclusion">Trace from camera to lookAt and report blocking physics geometry. Default true.</param>
	[McpTool( "capture_camera_set" )]
	public static Task<object> CaptureCameraSet( string[] names, bool comparePrevious = false,
		int diffThreshold = 8, int width = 960, int height = 540, bool renderUI = false,
		bool checkOcclusion = true )
	{
		var captures = CameraCaptureService.CaptureSet(
			names,
			width,
			height,
			renderUI,
			comparePrevious,
			diffThreshold,
			checkOcclusion );
		var result = McpResult.Text( JsonSerializer.Serialize(
			CameraCaptureService.CaptureManifest( captures ),
			new JsonSerializerOptions { WriteIndented = true } ) );
		foreach ( var capture in captures.Captures )
			result = result.WithImage( capture.Png, "image/png" );
		return Task.FromResult<object>( result );
	}

	/// <summary>
	/// Capture a deterministic orthographic top-down PNG over a GameObject or explicit world center.
	/// Returns a JSON manifest followed by the inline image. The manifest records the fixed camera
	/// pose, ground-plane bounds, screen axes, and world-units-per-pixel so agents can place or
	/// measure objects from the image. renderUI defaults false; dimensions are capped at 1600x1200.
	/// </summary>
	/// <param name="targetId">GameObject GUID whose bounds determine center, ground Z, and default worldHeight.</param>
	/// <param name="center">Explicit top-down center as "x,y,z"; use instead of targetId.</param>
	/// <param name="worldHeight">Vertical ground-plane span in world units (screen-up/world +X). Defaults to aspect-aware target bounds + 25%, or 1024 for explicit center.</param>
	/// <param name="cameraHeight">Camera height above center. Does not change orthographic scale. Defaults to worldHeight*2 with additional clearance above tall targets.</param>
	/// <param name="width">Image width in pixels (16-1600). Default 1024.</param>
	/// <param name="height">Image height in pixels (16-1200). Default 1024.</param>
	/// <param name="renderUI">Include screen-space UI. Default false.</param>
	[McpTool( "capture_topdown" )]
	public static Task<object> CaptureTopdown( string targetId = null, string center = null,
		double? worldHeight = null, double? cameraHeight = null, int width = 1024,
		int height = 1024, bool renderUI = false )
	{
		Vector3? parsedCenter = string.IsNullOrWhiteSpace( center )
			? null
			: CameraCaptureService.ParseVector3String( center, "center" );
		var capture = CameraCaptureService.CaptureTopdown(
			targetId,
			parsedCenter,
			worldHeight.HasValue ? (float?)worldHeight.Value : null,
			cameraHeight.HasValue ? (float?)cameraHeight.Value : null,
			width,
			height,
			renderUI );
		var result = McpResult.Text( JsonSerializer.Serialize(
			CameraCaptureService.TopdownManifest( capture ),
			new JsonSerializerOptions { WriteIndented = true } ) );
		return Task.FromResult<object>( result.WithImage( capture.Png, "image/png" ) );
	}
	// ── shared capture plumbing ─────────────────────────────────────

	/// <summary>Run the capture_view handler and convert its PNG-file result to an inline image.</summary>
	static async Task<object> CaptureAsync( JsonObject args )
	{
		var png = await CapturePngAsync( args );
		return McpResult.Image( png, "image/png" );
	}

	/// <summary>Run the capture_view handler, read the PNG it wrote, delete the temp file.</summary>
	static async Task<byte[]> CapturePngAsync( JsonObject args )
	{
		var result = await McpGate.Run( "capture_view", args );
		var path = result?.GetType().GetProperty( "path" )?.GetValue( result ) as string;
		if ( string.IsNullOrEmpty( path ) || !System.IO.File.Exists( path ) )
			throw new Exception( "Capture produced no PNG file" );
		try
		{
			return System.IO.File.ReadAllBytes( path );
		}
		finally
		{
			try { System.IO.File.Delete( path ); } catch { /* temp file, best-effort */ }
		}
	}
}
