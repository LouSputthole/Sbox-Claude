using System;
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
[McpToolset( "bridge_screenshot", "Capture the scene as inline PNG images: the main camera view, a framed shot of any GameObject, a free camera position, or a multi-angle orbit around an object. Works in edit and play mode (play mode captures the running game, HUD included)." )]
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
		var go = scene.Directory.FindByGuid( guid )
			?? throw new Exception( $"GameObject not found: {id}" );

		shots = Math.Clamp( shots, 2, 8 );
		var box = go.GetBounds();
		var center = box.Center;
		float size = box.Size.Length; if ( size < 1f ) size = 128f;
		float dist = (float)( distance ?? Math.Max( size * 1.6f, 150f ) );
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
		var bytes = System.IO.File.ReadAllBytes( path );
		try { System.IO.File.Delete( path ); } catch { /* temp file, best-effort */ }
		return bytes;
	}
}
