using Editor;
using Sandbox;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// Camera capture workflow shared by the native inline-image tools and the legacy
// file-IPC handlers. Bookmarks and comparison baselines are deliberately scoped
// to this editor session: no project files are written and hotload/restart clears
// the state.

internal sealed class CameraBookmarkDefinition
{
	internal string Name { get; init; }
	internal string Source { get; init; }
	internal string TargetId { get; init; }
	internal string TargetName { get; init; }
	internal Vector3 Position { get; init; }
	internal Rotation Rotation { get; init; }
	internal Vector3? LookAt { get; init; }
	internal float FieldOfView { get; init; }
	internal DateTime SavedUtc { get; init; }
}

internal readonly record struct CameraBaselineKey( string Name, int Width, int Height, bool RenderUI );

internal sealed class CameraDiffMetrics
{
	internal bool BaselineAvailable { get; init; }
	internal bool Comparable { get; init; }
	internal bool? ExactMatch { get; init; }
	internal int Threshold { get; init; }
	internal double? MeanAbsoluteChannelDelta { get; init; }
	internal double? ChangedPixelPercent { get; init; }
	internal int? MaxChannelDelta { get; init; }
	internal string Reason { get; init; }
}

internal sealed class CameraOcclusionReport
{
	internal bool Checked { get; init; }
	internal bool Blocked { get; init; }
	internal bool StartedSolid { get; init; }
	internal string BlockerId { get; init; }
	internal string BlockerName { get; init; }
	internal float? HitDistance { get; init; }
	internal float? CameraToLookAtDistance { get; init; }
	internal string Reason { get; init; }
}

internal sealed class CameraCaptureEntry
{
	internal CameraBookmarkDefinition Bookmark { get; init; }
	internal byte[] Png { get; init; }
	internal CameraDiffMetrics Diff { get; init; }
	internal CameraOcclusionReport Occlusion { get; init; }
}

internal sealed class CameraCaptureSetResult
{
	internal int Width { get; init; }
	internal int Height { get; init; }
	internal bool RenderUI { get; init; }
	internal bool ComparedPrevious { get; init; }
	internal int DiffThreshold { get; init; }
	internal IReadOnlyList<CameraCaptureEntry> Captures { get; init; }
}

internal sealed class CameraTopdownResult
{
	internal byte[] Png { get; init; }
	internal string TargetId { get; init; }
	internal string TargetName { get; init; }
	internal Vector3 Center { get; init; }
	internal Vector3 CameraPosition { get; init; }
	internal Rotation CameraRotation { get; init; }
	internal float CameraHeight { get; init; }
	internal float WorldHeight { get; init; }
	internal float WorldWidth { get; init; }
	internal float GroundPlaneZ { get; init; }
	internal int Width { get; init; }
	internal int Height { get; init; }
	internal bool RenderUI { get; init; }
}

internal static class CameraCaptureService
{
	internal const int MaxBookmarks = 64;
	internal const int MaxCaptureSetViews = 8;
	internal const int MaxCaptureWidth = 1600;
	internal const int MaxCaptureHeight = 1200;
	internal const long MaxCaptureSetPixels = 8_000_000;

	private const int MaxBaselines = 64;
	private const long MaxBaselineBytes = 64L * 1024L * 1024L;
	private const float OcclusionEndTolerance = 8f;
	private static readonly object StateLock = new();
	private static readonly Dictionary<string, CameraBookmarkDefinition> Bookmarks =
		new( StringComparer.OrdinalIgnoreCase );
	private static readonly Dictionary<CameraBaselineKey, byte[]> Baselines = new();
	private static readonly Queue<CameraBaselineKey> BaselineOrder = new();
	private static long BaselineBytes;

	internal static CameraBookmarkDefinition SaveBookmark(
		string name,
		string targetId,
		Vector3? position,
		Vector3? lookAt,
		Rotation? rotation,
		float? fov )
	{
		var scene = RequireScene();
		name = NormalizeName( name );

		if ( !string.IsNullOrWhiteSpace( targetId ) && position.HasValue )
			throw new Exception( "Pass targetId OR position, not both." );
		if ( lookAt.HasValue && rotation.HasValue )
			throw new Exception( "Pass lookAt OR rotation, not both." );
		if ( !position.HasValue && (lookAt.HasValue || rotation.HasValue) )
			throw new Exception( "lookAt/rotation require position." );

		CameraBookmarkDefinition bookmark;
		if ( !string.IsNullOrWhiteSpace( targetId ) )
		{
			var target = FindTarget( scene, targetId );
			float resolvedFov = ClampFov( fov ?? 80f );
			var pose = FrameTarget( target, resolvedFov );
			bookmark = new CameraBookmarkDefinition
			{
				Name = name,
				Source = "target",
				TargetId = target.Id.ToString(),
				TargetName = target.Name,
				Position = pose.Position,
				Rotation = pose.Rotation,
				LookAt = pose.LookAt,
				FieldOfView = resolvedFov,
				SavedUtc = DateTime.UtcNow,
			};
		}
		else if ( position.HasValue )
		{
			var resolvedRotation = lookAt.HasValue
				? LookAtRotation( position.Value, lookAt.Value )
				: rotation ?? Rotation.Identity;
			bookmark = new CameraBookmarkDefinition
			{
				Name = name,
				Source = "explicit",
				Position = position.Value,
				Rotation = resolvedRotation,
				LookAt = lookAt,
				FieldOfView = ClampFov( fov ?? 80f ),
				SavedUtc = DateTime.UtcNow,
			};
		}
		else
		{
			var camera = VisualHelpers.FindMainCamera( scene )
				?? throw new Exception( "No main camera in the scene. Pass targetId or position." );
			bookmark = new CameraBookmarkDefinition
			{
				Name = name,
				Source = "main-camera",
				Position = camera.GameObject.WorldPosition,
				Rotation = camera.GameObject.WorldRotation,
				FieldOfView = ClampFov( fov ?? camera.FieldOfView ),
				SavedUtc = DateTime.UtcNow,
			};
		}

		lock ( StateLock )
		{
			if ( !Bookmarks.ContainsKey( name ) && Bookmarks.Count >= MaxBookmarks )
				throw new Exception( $"Camera bookmark limit reached ({MaxBookmarks}). Delete one before saving another." );
			Bookmarks[name] = bookmark;
			RemoveBaselinesForBookmarkLocked( name );
		}

		return bookmark;
	}

	internal static IReadOnlyList<CameraBookmarkDefinition> ListBookmarks()
	{
		lock ( StateLock )
		{
			return Bookmarks.Values
				.OrderBy( x => x.Name, StringComparer.OrdinalIgnoreCase )
				.ToArray();
		}
	}

	internal static bool DeleteBookmark( string name )
	{
		name = NormalizeName( name );
		lock ( StateLock )
		{
			var deleted = Bookmarks.Remove( name );
			if ( deleted )
				RemoveBaselinesForBookmarkLocked( name );
			return deleted;
		}
	}

	internal static CameraCaptureSetResult CaptureSet(
		IReadOnlyList<string> requestedNames,
		int width,
		int height,
		bool renderUI,
		bool comparePrevious,
		int diffThreshold,
		bool checkOcclusion )
	{
		if ( requestedNames == null || requestedNames.Count == 0 )
			throw new Exception( "names must contain at least one saved camera bookmark." );
		if ( requestedNames.Count > MaxCaptureSetViews )
			throw new Exception( $"capture_camera_set accepts at most {MaxCaptureSetViews} bookmarks." );

		width = Math.Clamp( width, 16, MaxCaptureWidth );
		height = Math.Clamp( height, 16, MaxCaptureHeight );
		diffThreshold = Math.Clamp( diffThreshold, 0, 255 );
		long aggregatePixels = (long)requestedNames.Count * width * height;
		if ( aggregatePixels > MaxCaptureSetPixels )
		{
			throw new Exception(
				$"Capture set is {aggregatePixels:N0} pixels; maximum is {MaxCaptureSetPixels:N0}. " +
				"Reduce width/height or capture fewer bookmarks." );
		}

		var normalizedNames = requestedNames.Select( NormalizeName ).ToArray();
		var duplicate = normalizedNames
			.GroupBy( x => x, StringComparer.OrdinalIgnoreCase )
			.FirstOrDefault( x => x.Count() > 1 );
		if ( duplicate != null )
			throw new Exception( $"Duplicate bookmark in capture set: {duplicate.Key}" );

		CameraBookmarkDefinition[] bookmarks;
		lock ( StateLock )
		{
			bookmarks = normalizedNames.Select( name =>
			{
				if ( !Bookmarks.TryGetValue( name, out var bookmark ) )
					throw new Exception( $"Camera bookmark not found: {name}. Call list_camera_bookmarks." );
				return bookmark;
			} ).ToArray();
		}

		var baselineKeys = bookmarks
			.Select( bookmark => BaselineKey( bookmark.Name, width, height, renderUI ) )
			.ToArray();
		var previousBaselines = new byte[bookmarks.Length][];
		lock ( StateLock )
		{
			for ( int i = 0; i < baselineKeys.Length; i++ )
				Baselines.TryGetValue( baselineKeys[i], out previousBaselines[i] );
		}

		var scene = RequireScene();
		var captures = new List<CameraCaptureEntry>( bookmarks.Length );
		for ( int index = 0; index < bookmarks.Length; index++ )
		{
			var bookmark = bookmarks[index];
			var occlusion = checkOcclusion
				? CheckOcclusion( scene, bookmark )
				: new CameraOcclusionReport { Checked = false, Reason = "checkOcclusion=false" };
			float? farPlane = BookmarkFarPlane( scene, bookmark );
			var png = RenderPerspective(
				scene,
				bookmark.Position,
				bookmark.Rotation,
				bookmark.FieldOfView,
				farPlane,
				width,
				height,
				renderUI );

			var diff = comparePrevious
				? ComparePng( previousBaselines[index], png, diffThreshold )
				: null;

			captures.Add( new CameraCaptureEntry
			{
				Bookmark = bookmark,
				Png = png,
				Diff = diff,
				Occlusion = occlusion,
			} );
		}

		lock ( StateLock )
		{
			for ( int i = 0; i < bookmarks.Length; i++ )
			{
				if ( !Bookmarks.TryGetValue( bookmarks[i].Name, out var current )
					|| !ReferenceEquals( current, bookmarks[i] ) )
					throw new Exception( $"Camera bookmark changed during capture: {bookmarks[i].Name}. Capture again." );
			}
			for ( int i = 0; i < captures.Count; i++ )
				StoreBaselineLocked( baselineKeys[i], captures[i].Png );
		}

		return new CameraCaptureSetResult
		{
			Width = width,
			Height = height,
			RenderUI = renderUI,
			ComparedPrevious = comparePrevious,
			DiffThreshold = diffThreshold,
			Captures = captures,
		};
	}

	internal static CameraTopdownResult CaptureTopdown(
		string targetId,
		Vector3? explicitCenter,
		float? requestedWorldHeight,
		float? requestedCameraHeight,
		int width,
		int height,
		bool renderUI )
	{
		if ( !string.IsNullOrWhiteSpace( targetId ) && explicitCenter.HasValue )
			throw new Exception( "Pass targetId OR center, not both." );
		if ( string.IsNullOrWhiteSpace( targetId ) && !explicitCenter.HasValue )
			throw new Exception( "capture_topdown requires targetId or center." );

		if ( requestedWorldHeight.HasValue && !float.IsFinite( requestedWorldHeight.Value ) )
			throw new Exception( "worldHeight must be finite." );
		if ( requestedCameraHeight.HasValue && !float.IsFinite( requestedCameraHeight.Value ) )
			throw new Exception( "cameraHeight must be finite." );

		width = Math.Clamp( width, 16, MaxCaptureWidth );
		height = Math.Clamp( height, 16, MaxCaptureHeight );
		float aspect = (float)width / height;
		var scene = RequireScene();
		string resolvedTargetId = null;
		string targetName = null;
		Vector3 center;
		float groundPlaneZ;
		float boundsBottomZ;
		float boundsTopZ;
		float defaultWorldHeight;

		if ( !string.IsNullOrWhiteSpace( targetId ) )
		{
			var target = FindTarget( scene, targetId );
			var bounds = target.GetBounds();
			center = bounds.Center;
			groundPlaneZ = bounds.Mins.z;
			boundsBottomZ = bounds.Mins.z;
			boundsTopZ = bounds.Maxs.z;
			defaultWorldHeight = Math.Max(
				Math.Max( bounds.Size.x, bounds.Size.y / aspect ) * 1.25f,
				64f );
			resolvedTargetId = target.Id.ToString();
			targetName = target.Name;
		}
		else
		{
			center = explicitCenter.Value;
			groundPlaneZ = center.z;
			boundsBottomZ = center.z;
			boundsTopZ = center.z;
			defaultWorldHeight = 1024f;
		}

		float worldHeight = Math.Clamp( requestedWorldHeight ?? defaultWorldHeight, 16f, 100_000f );
		float worldWidth = worldHeight * aspect;
		float minimumCameraHeight = Math.Clamp(
			Math.Max( boundsTopZ - center.z + 64f, 64f ),
			64f,
			1_000_000f );
		float defaultCameraHeight = Math.Max( worldHeight * 2f, minimumCameraHeight + 448f );
		float cameraHeight = Math.Clamp(
			Math.Max( requestedCameraHeight ?? defaultCameraHeight, minimumCameraHeight ),
			minimumCameraHeight,
			1_000_000f );

		var cameraPosition = center + Vector3.Up * cameraHeight;
		float farPlane = Math.Max( cameraPosition.z - boundsBottomZ + 64f, 1024f );
		var cameraRotation = Rotation.LookAt( Vector3.Down, Vector3.Forward );
		var png = RenderOrthographic(
			scene,
			cameraPosition,
			cameraRotation,
			worldHeight,
			farPlane,
			width,
			height,
			renderUI );

		return new CameraTopdownResult
		{
			Png = png,
			TargetId = resolvedTargetId,
			TargetName = targetName,
			Center = center,
			CameraPosition = cameraPosition,
			CameraRotation = cameraRotation,
			CameraHeight = cameraHeight,
			WorldHeight = worldHeight,
			WorldWidth = worldWidth,
			GroundPlaneZ = groundPlaneZ,
			Width = width,
			Height = height,
			RenderUI = renderUI,
		};
	}

	internal static Vector3 ParseVector3String( string value, string label )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			throw new Exception( $"{label} must be a comma vector \"x,y,z\"." );
		var parts = value.Split( ',', StringSplitOptions.TrimEntries );
		if ( parts.Length != 3
			|| !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
			|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
			|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z )
			|| !float.IsFinite( x ) || !float.IsFinite( y ) || !float.IsFinite( z ) )
		{
			throw new Exception( $"{label} must be a finite comma vector \"x,y,z\", got: {value}" );
		}
		return new Vector3( x, y, z );
	}

	internal static Vector3 ParseVector3Element( JsonElement element, string label )
	{
		if ( element.ValueKind == JsonValueKind.String )
		{
			// Native MCP gate stringifies structured args - recover {"x":..}/[x,y,z]
			// passed as a JSON string (same contract as OptionalRotation below).
			if ( ClaudeBridge.TryUnwrapStructuredJsonString( element, out var structuredVec ) )
				return ParseVector3Element( structuredVec, label );
			return ParseVector3String( element.GetString(), label );
		}
		if ( element.ValueKind == JsonValueKind.Object )
		{
			return new Vector3(
				RequiredFiniteComponent( element, "x", label ),
				RequiredFiniteComponent( element, "y", label ),
				RequiredFiniteComponent( element, "z", label ) );
		}
		if ( element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 3 )
		{
			var values = element.EnumerateArray().ToArray();
			return new Vector3(
				RequiredFiniteNumber( values[0], $"{label}[0]" ),
				RequiredFiniteNumber( values[1], $"{label}[1]" ),
				RequiredFiniteNumber( values[2], $"{label}[2]" ) );
		}
		throw new Exception( $"{label} must be a finite {{x,y,z}} object, three-number array, or \"x,y,z\" string." );
	}

	private static float RequiredFiniteComponent( JsonElement value, string property, string label )
	{
		if ( !value.TryGetProperty( property, out var element ) )
			throw new Exception( $"{label}.{property} is required." );
		return RequiredFiniteNumber( element, $"{label}.{property}" );
	}

	private static float RequiredFiniteNumber( JsonElement value, string label )
	{
		if ( value.ValueKind != JsonValueKind.Number || !value.TryGetSingle( out var result ) || !float.IsFinite( result ) )
			throw new Exception( $"{label} must be a finite number." );
		return result;
	}

	internal static object BookmarkManifest( CameraBookmarkDefinition bookmark )
	{
		var angles = bookmark.Rotation.Angles();
		return new
		{
			name = bookmark.Name,
			scope = "editor-session",
			source = bookmark.Source,
			targetId = bookmark.TargetId,
			targetName = bookmark.TargetName,
			position = VectorManifest( bookmark.Position ),
			rotation = new
			{
				pitch = (double)angles.pitch,
				yaw = (double)angles.yaw,
				roll = (double)angles.roll,
			},
			lookAt = bookmark.LookAt.HasValue ? VectorManifest( bookmark.LookAt.Value ) : null,
			fov = (double)bookmark.FieldOfView,
			savedUtc = bookmark.SavedUtc.ToString( "O", CultureInfo.InvariantCulture ),
		};
	}

	internal static object CaptureManifest( CameraCaptureSetResult result )
	{
		return new
		{
			captured = true,
			scope = "editor-session",
			count = result.Captures.Count,
			width = result.Width,
			height = result.Height,
			renderUI = result.RenderUI,
			comparePrevious = result.ComparedPrevious,
			diffThreshold = result.DiffThreshold,
			baselineNote = "The latest capture becomes the session baseline for the same bookmark, dimensions, and renderUI setting, subject to a 64-entry/64 MiB session budget.",
			images = result.Captures.Select( (capture, index) => new
			{
				index,
				name = capture.Bookmark.Name,
				bookmark = BookmarkManifest( capture.Bookmark ),
				occlusion = OcclusionManifest( capture.Occlusion ),
				diff = DiffManifest( capture.Diff ),
			} ).ToArray(),
		};
	}

	internal static object TopdownManifest( CameraTopdownResult result )
	{
		float worldUnitsPerPixel = result.WorldHeight / result.Height;
		float halfX = result.WorldHeight * 0.5f;
		float halfY = result.WorldWidth * 0.5f;
		var angles = result.CameraRotation.Angles();
		return new
		{
			captured = true,
			projection = "orthographic-topdown",
			targetId = result.TargetId,
			targetName = result.TargetName,
			width = result.Width,
			height = result.Height,
			renderUI = result.RenderUI,
			center = VectorManifest( result.Center ),
			camera = new
			{
				position = VectorManifest( result.CameraPosition ),
				rotation = new
				{
					pitch = (double)angles.pitch,
					yaw = (double)angles.yaw,
					roll = (double)angles.roll,
				},
				height = (double)result.CameraHeight,
				screenUpWorld = VectorManifest( result.CameraRotation.Up ),
				screenRightWorld = VectorManifest( result.CameraRotation.Right ),
			},
			groundPlane = new
			{
				z = (double)result.GroundPlaneZ,
				worldHeight = (double)result.WorldHeight,
				worldWidth = (double)result.WorldWidth,
				worldUnitsPerPixel = (double)worldUnitsPerPixel,
				min = VectorManifest( new Vector3(
					result.Center.x - halfX,
					result.Center.y - halfY,
					result.GroundPlaneZ ) ),
				max = VectorManifest( new Vector3(
					result.Center.x + halfX,
					result.Center.y + halfY,
					result.GroundPlaneZ ) ),
			},
			determinism = "Fixed orthographic projection; screen-up is world +X; renderUI defaults false.",
		};
	}

	internal static string WriteTemporaryPng( byte[] png, string prefix )
	{
		var safePrefix = string.IsNullOrWhiteSpace( prefix ) ? "camera" : prefix;
		var path = Path.Combine(
			Path.GetTempPath(),
			$"bridge_{safePrefix}_{Guid.NewGuid():N}.png" );
		File.WriteAllBytes( path, png );
		return path;
	}

	private static Scene RequireScene()
	{
		return (Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene)
			?? throw new Exception( "No active scene" );
	}

	private static GameObject FindTarget( Scene scene, string targetId )
	{
		if ( !Guid.TryParse( targetId, out _ ) )
			throw new Exception( $"targetId must be a GameObject GUID, got: {targetId}" );
		return ClaudeBridge.ResolveGameObject( scene, targetId )
			?? throw new Exception( $"GameObject not found: {targetId}" );
	}

	private static (Vector3 Position, Rotation Rotation, Vector3 LookAt) FrameTarget( GameObject target, float fieldOfView )
	{
		var bounds = target.GetBounds();
		var center = bounds.Center;
		float radius = bounds.Size.Length * 0.5f;
		if ( radius < 0.5f ) radius = 64f;
		float halfFovRadians = Math.Clamp( fieldOfView, 1f, 179f ) * (MathF.PI / 360f);
		float distance = Math.Max( radius / MathF.Tan( halfFovRadians ) * 1.2f, 150f );
		var position = center + new Vector3( -1f, -0.6f, 0.7f ).Normal * distance;
		return (position, LookAtRotation( position, center ), center);
	}

	internal static Rotation LookAtRotation( Vector3 position, Vector3 lookAt )
	{
		var direction = lookAt - position;
		if ( direction.Length < 0.001f )
			throw new Exception( "Camera position and lookAt cannot be the same point." );
		var forward = direction.Normal;
		var up = MathF.Abs( forward.z ) > 0.999f ? Vector3.Forward : Vector3.Up;
		return Rotation.LookAt( forward, up );
	}

	internal static float ClampFov( float fov )
	{
		if ( !float.IsFinite( fov ) ) throw new Exception( "fov must be finite." );
		return Math.Clamp( fov, 1f, 179f );
	}

	internal static float FramingFarPlane( float cameraToCenter, float radius )
	{
		if ( !float.IsFinite( cameraToCenter ) || cameraToCenter < 0f
			|| !float.IsFinite( radius ) || radius < 0f )
			throw new Exception( "Camera framing distances must be finite and non-negative." );
		return Math.Clamp(
			Math.Max( cameraToCenter + radius * 2f + 256f, 10_000f ),
			10_000f,
			10_000_000f );
	}

	private static float? BookmarkFarPlane( Scene scene, CameraBookmarkDefinition bookmark )
	{
		if ( !bookmark.LookAt.HasValue ) return null;
		float radius = 0f;
		if ( !string.IsNullOrWhiteSpace( bookmark.TargetId ) )
		{
			var target = ClaudeBridge.ResolveGameObject( scene, bookmark.TargetId );
			if ( target != null )
			{
				var bounds = target.GetBounds();
				if ( BoundsInspection.IsFinite( bounds.Size ) )
					radius = Math.Max( bounds.Size.Length * 0.5f, 0f );
			}
		}
		return FramingFarPlane(
			Vector3.DistanceBetween( bookmark.Position, bookmark.LookAt.Value ),
			radius );
	}

	private static byte[] RenderPerspective(
		Scene scene,
		Vector3 position,
		Rotation rotation,
		float fov,
		float? farPlane,
		int width,
		int height,
		bool renderUI )
	{
		return Render(
			scene,
			position,
			rotation,
			width,
			height,
			renderUI,
			camera =>
			{
				camera.FieldOfView = ClampFov( fov );
				if ( farPlane.HasValue ) camera.ZFar = farPlane.Value;
			} );
	}

	private static byte[] RenderOrthographic(
		Scene scene,
		Vector3 position,
		Rotation rotation,
		float worldHeight,
		float farPlane,
		int width,
		int height,
		bool renderUI )
	{
		return Render(
			scene,
			position,
			rotation,
			width,
			height,
			renderUI,
			camera =>
			{
				camera.Orthographic = true;
				camera.OrthographicHeight = worldHeight;
				camera.ZNear = 1f;
				camera.ZFar = Math.Max( farPlane, 1024f );
			} );
	}

	private static byte[] Render(
		Scene scene,
		Vector3 position,
		Rotation rotation,
		int width,
		int height,
		bool renderUI,
		Action<CameraComponent> configure )
	{
		GameObject tempCamera = null;
		try
		{
			tempCamera = scene.CreateObject( false );
			tempCamera.Name = "__bridge_capture_cam";
			tempCamera.WorldPosition = position;
			tempCamera.WorldRotation = rotation;
			var camera = tempCamera.AddComponent<CameraComponent>();
			camera.IsMainCamera = false;
			camera.Priority = -100;
			configure?.Invoke( camera );
			tempCamera.Enabled = true;

			using var bitmap = new Bitmap( width, height );
			camera.RenderToBitmap( bitmap, renderUI );
			return bitmap.ToPng();
		}
		finally
		{
			tempCamera?.Destroy();
		}
	}

	private static CameraOcclusionReport CheckOcclusion(
		Scene scene,
		CameraBookmarkDefinition bookmark )
	{
		if ( !bookmark.LookAt.HasValue )
		{
			return new CameraOcclusionReport
			{
				Checked = false,
				Reason = "Bookmark uses rotation only; no lookAt line is available.",
			};
		}

		var lookAt = bookmark.LookAt.Value;
		float totalDistance = Vector3.DistanceBetween( bookmark.Position, lookAt );
		GameObject target = null;
		if ( !string.IsNullOrWhiteSpace( bookmark.TargetId )
			&& Guid.TryParse( bookmark.TargetId, out var targetGuid ) )
		{
			target = ClaudeBridge.ResolveGameObject( scene, bookmark.TargetId );
		}

		var trace = target != null
			? scene.Trace.Ray( bookmark.Position, lookAt ).IgnoreGameObjectHierarchy( target ).Run()
			: scene.Trace.Ray( bookmark.Position, lookAt ).Run();
		bool blocked = trace.StartedSolid
			|| (trace.Hit && trace.Distance < totalDistance - OcclusionEndTolerance);

		return new CameraOcclusionReport
		{
			Checked = true,
			Blocked = blocked,
			StartedSolid = trace.StartedSolid,
			BlockerId = blocked ? trace.GameObject?.Id.ToString() : null,
			BlockerName = blocked ? trace.GameObject?.Name : null,
			HitDistance = trace.Hit ? trace.Distance : null,
			CameraToLookAtDistance = totalDistance,
			Reason = blocked
				? "A physics trace found geometry between the camera and lookAt point."
				: "No blocking physics geometry was found before the lookAt point.",
		};
	}

	private static CameraDiffMetrics ComparePng(
		byte[] previousPng,
		byte[] currentPng,
		int threshold )
	{
		if ( previousPng == null || previousPng.Length == 0 )
		{
			return new CameraDiffMetrics
			{
				BaselineAvailable = false,
				Comparable = false,
				Threshold = threshold,
				Reason = "No previous session capture exists for this bookmark and settings.",
			};
		}

		if ( previousPng.AsSpan().SequenceEqual( currentPng ) )
		{
			return new CameraDiffMetrics
			{
				BaselineAvailable = true,
				Comparable = true,
				ExactMatch = true,
				Threshold = threshold,
				MeanAbsoluteChannelDelta = 0d,
				ChangedPixelPercent = 0d,
				MaxChannelDelta = 0,
				Reason = "Encoded PNG bytes match exactly; pixel decode was skipped.",
			};
		}

		try
		{
			using var previous = SKBitmap.Decode( previousPng );
			using var current = SKBitmap.Decode( currentPng );
			if ( previous == null || current == null )
			{
				return new CameraDiffMetrics
				{
					BaselineAvailable = true,
					Comparable = false,
					Threshold = threshold,
					Reason = "SkiaSharp could not decode one of the PNG captures.",
				};
			}
			if ( previous.Width != current.Width || previous.Height != current.Height )
			{
				return new CameraDiffMetrics
				{
					BaselineAvailable = true,
					Comparable = false,
					Threshold = threshold,
					Reason = $"Image dimensions differ ({previous.Width}x{previous.Height} vs {current.Width}x{current.Height}).",
				};
			}

			var previousPixels = previous.Pixels;
			var currentPixels = current.Pixels;
			if ( previousPixels.Length != currentPixels.Length )
				throw new Exception( "Decoded pixel counts differ." );
			long absoluteDelta = 0;
			long changedPixels = 0;
			int maxChannelDelta = 0;
			long pixelCount = currentPixels.LongLength;
			for ( int i = 0; i < currentPixels.Length; i++ )
			{
				var before = previousPixels[i];
				var after = currentPixels[i];
				int dr = Math.Abs( before.Red - after.Red );
				int dg = Math.Abs( before.Green - after.Green );
				int db = Math.Abs( before.Blue - after.Blue );
				int da = Math.Abs( before.Alpha - after.Alpha );
				int pixelMax = Math.Max( Math.Max( dr, dg ), Math.Max( db, da ) );
				absoluteDelta += dr + dg + db + da;
				if ( pixelMax > threshold ) changedPixels++;
				if ( pixelMax > maxChannelDelta ) maxChannelDelta = pixelMax;
			}

			return new CameraDiffMetrics
			{
				BaselineAvailable = true,
				Comparable = true,
				ExactMatch = maxChannelDelta == 0,
				Threshold = threshold,
				MeanAbsoluteChannelDelta = pixelCount == 0
					? 0d
					: absoluteDelta / (pixelCount * 4d),
				ChangedPixelPercent = pixelCount == 0
					? 0d
					: changedPixels * 100d / pixelCount,
				MaxChannelDelta = maxChannelDelta,
				Reason = "RGBA pixels compared with SkiaSharp; changedPixelPercent counts pixels whose largest channel delta exceeds threshold.",
			};
		}
		catch ( Exception ex )
		{
			return new CameraDiffMetrics
			{
				BaselineAvailable = true,
				Comparable = false,
				Threshold = threshold,
				Reason = $"PNG comparison failed: {ex.Message}",
			};
		}
	}

	private static object DiffManifest( CameraDiffMetrics diff )
	{
		if ( diff == null ) return null;
		return new
		{
			baselineAvailable = diff.BaselineAvailable,
			comparable = diff.Comparable,
			exactMatch = diff.ExactMatch,
			threshold = diff.Threshold,
			meanAbsoluteChannelDelta = diff.MeanAbsoluteChannelDelta,
			changedPixelPercent = diff.ChangedPixelPercent,
			maxChannelDelta = diff.MaxChannelDelta,
			reason = diff.Reason,
		};
	}

	private static object OcclusionManifest( CameraOcclusionReport report )
	{
		if ( report == null ) return null;
		return new
		{
			@checked = report.Checked,
			blocked = report.Blocked,
			startedSolid = report.StartedSolid,
			blockerId = report.BlockerId,
			blockerName = report.BlockerName,
			hitDistance = report.HitDistance,
			cameraToLookAtDistance = report.CameraToLookAtDistance,
			reason = report.Reason,
		};
	}

	private static object VectorManifest( Vector3 value ) => new
	{
		x = (double)value.x,
		y = (double)value.y,
		z = (double)value.z,
	};

	private static string NormalizeName( string name )
	{
		name = name?.Trim();
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new Exception( "Camera bookmark name is required." );
		if ( name.Length > 64 )
			throw new Exception( "Camera bookmark name must be 64 characters or fewer." );
		return name;
	}

	private static CameraBaselineKey BaselineKey( string name, int width, int height, bool renderUI )
		=> new( name.ToLowerInvariant(), width, height, renderUI );

	private static void StoreBaselineLocked( CameraBaselineKey key, byte[] png )
	{
		if ( Baselines.TryGetValue( key, out var previous ) )
			BaselineBytes -= previous.LongLength;
		else
			BaselineOrder.Enqueue( key );
		Baselines[key] = png;
		BaselineBytes += png.LongLength;
		while ( (Baselines.Count > MaxBaselines || BaselineBytes > MaxBaselineBytes)
			&& BaselineOrder.Count > 0 )
		{
			var oldest = BaselineOrder.Dequeue();
			if ( Baselines.TryGetValue( oldest, out var removed ) )
			{
				Baselines.Remove( oldest );
				BaselineBytes -= removed.LongLength;
			}
		}
	}

	private static void RemoveBaselinesForBookmarkLocked( string name )
	{
		foreach ( var key in Baselines.Keys
			.Where( key => key.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) )
			.ToArray() )
		{
			if ( Baselines.TryGetValue( key, out var removed ) )
			{
				Baselines.Remove( key );
				BaselineBytes -= removed.LongLength;
			}
		}

		int queued = BaselineOrder.Count;
		for ( int i = 0; i < queued; i++ )
		{
			var key = BaselineOrder.Dequeue();
			if ( Baselines.ContainsKey( key ) ) BaselineOrder.Enqueue( key );
		}
	}
}

public class SaveCameraBookmarkHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			string name = RequiredString( p, "name" );
			string targetId = OptionalString( p, "targetId" );
			Vector3? position = OptionalVector( p, "position" );
			Vector3? lookAt = OptionalVector( p, "lookAt" );
			Rotation? rotation = OptionalRotation( p, "rotation" );
			float? fov = p.TryGetProperty( "fov", out var fovElement )
				&& fovElement.ValueKind == JsonValueKind.Number
					? fovElement.GetSingle()
					: null;
			var bookmark = CameraCaptureService.SaveBookmark(
				name,
				targetId,
				position,
				lookAt,
				rotation,
				fov );
			return Task.FromResult<object>( new
			{
				saved = true,
				scope = "editor-session",
				bookmark = CameraCaptureService.BookmarkManifest( bookmark ),
				note = "Bookmarks and comparison baselines reset on addon hotload or editor restart.",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"save_camera_bookmark failed: {ex.Message}" } );
		}
	}

	internal static string RequiredString( JsonElement p, string key )
	{
		var value = OptionalString( p, key );
		if ( string.IsNullOrWhiteSpace( value ) )
			throw new Exception( $"{key} is required." );
		return value;
	}

	internal static string OptionalString( JsonElement p, string key )
	{
		return p.TryGetProperty( key, out var element ) && element.ValueKind == JsonValueKind.String
			? element.GetString()
			: null;
	}

	internal static Vector3? OptionalVector( JsonElement p, string key )
	{
		return p.TryGetProperty( key, out var element ) && element.ValueKind != JsonValueKind.Null
			? CameraCaptureService.ParseVector3Element( element, key )
			: null;
	}

	internal static Rotation? OptionalRotation( JsonElement p, string key )
	{
		if ( !p.TryGetProperty( key, out var element ) || element.ValueKind == JsonValueKind.Null )
			return null;
		if ( ClaudeBridge.TryUnwrapStructuredJsonString( element, out var structured ) )
			element = structured;

		float pitch;
		float yaw;
		float roll;
		if ( element.ValueKind == JsonValueKind.String )
		{
			var angles = CameraCaptureService.ParseVector3String( element.GetString(), key );
			(pitch, yaw, roll) = (angles.x, angles.y, angles.z);
		}
		else if ( element.ValueKind == JsonValueKind.Object )
		{
			pitch = RequiredNumber( element, "pitch", key );
			yaw = RequiredNumber( element, "yaw", key );
			roll = RequiredNumber( element, "roll", key );
		}
		else if ( element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 3 )
		{
			var values = element.EnumerateArray().ToArray();
			if ( values.Any( value => value.ValueKind != JsonValueKind.Number
				|| !value.TryGetSingle( out var number )
				|| !float.IsFinite( number ) ) )
				throw new Exception( $"{key} array values must be finite numbers." );
			pitch = values[0].GetSingle();
			yaw = values[1].GetSingle();
			roll = values[2].GetSingle();
		}
		else
		{
			throw new Exception( $"{key} must be \"pitch,yaw,roll\", a numeric {{pitch,yaw,roll}} object, or a three-number array." );
		}

		return Rotation.From( pitch, yaw, roll );
	}

	private static float RequiredNumber( JsonElement value, string property, string label )
	{
		if ( !value.TryGetProperty( property, out var element )
			|| element.ValueKind != JsonValueKind.Number
			|| !element.TryGetSingle( out var result )
			|| !float.IsFinite( result ) )
			throw new Exception( $"{label}.{property} is required and must be a finite number." );
		return result;
	}
}

public class ListCameraBookmarksHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var bookmarks = CameraCaptureService.ListBookmarks();
		return Task.FromResult<object>( new
		{
			scope = "editor-session",
			total = bookmarks.Count,
			max = CameraCaptureService.MaxBookmarks,
			bookmarks = bookmarks.Select( CameraCaptureService.BookmarkManifest ).ToArray(),
			note = "Session-only: bookmarks reset on addon hotload or editor restart.",
		} );
	}
}

public class DeleteCameraBookmarkHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			string name = SaveCameraBookmarkHandler.RequiredString( p, "name" );
			bool deleted = CameraCaptureService.DeleteBookmark( name );
			return Task.FromResult<object>( new
			{
				deleted,
				name,
				scope = "editor-session",
				note = deleted
					? "Bookmark and its comparison baselines were removed."
					: "Bookmark was not present; no state changed.",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"delete_camera_bookmark failed: {ex.Message}" } );
		}
	}
}

public class CaptureCameraSetHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !p.TryGetProperty( "names", out var namesElement )
				|| namesElement.ValueKind != JsonValueKind.Array )
			{
				return Task.FromResult<object>( new { error = "names (array of camera bookmark names) is required" } );
			}

			var nameElements = namesElement.EnumerateArray().ToArray();
			if ( nameElements.Any( element => element.ValueKind != JsonValueKind.String ) )
				return Task.FromResult<object>( new { error = "every names entry must be a string" } );
			var names = nameElements.Select( element => element.GetString() ).ToArray();
			int width = OptionalInt( p, "width", 960 );
			int height = OptionalInt( p, "height", 540 );
			bool renderUI = OptionalBool( p, "renderUI", false );
			bool comparePrevious = OptionalBool( p, "comparePrevious", false );
			int diffThreshold = OptionalInt( p, "diffThreshold", 8 );
			bool checkOcclusion = OptionalBool( p, "checkOcclusion", true );
			var result = CameraCaptureService.CaptureSet(
				names,
				width,
				height,
				renderUI,
				comparePrevious,
				diffThreshold,
				checkOcclusion );

			var manifest = CameraCaptureService.CaptureManifest( result );
			var temporaryPaths = new List<string>( result.Captures.Count );
			try
			{
				var captures = new List<object>( result.Captures.Count );
				for ( int index = 0; index < result.Captures.Count; index++ )
				{
					var capture = result.Captures[index];
					var path = CameraCaptureService.WriteTemporaryPng( capture.Png, "camera_set" );
					temporaryPaths.Add( path );
					captures.Add( new { index, name = capture.Bookmark.Name, path } );
				}
				return Task.FromResult<object>( new
				{
					captured = true,
					manifest,
					captures = captures.ToArray(),
					note = "Legacy file-IPC result. Native MCP returns the same manifest followed by ordered inline PNG images.",
				} );
			}
			catch
			{
				foreach ( var path in temporaryPaths )
				{
					try { File.Delete( path ); } catch { }
				}
				throw;
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"capture_camera_set failed: {ex.Message}" } );
		}
	}

	internal static int OptionalInt( JsonElement p, string key, int fallback )
		=> p.TryGetProperty( key, out var element ) && element.TryGetInt32( out var value )
			? value
			: fallback;

	internal static bool OptionalBool( JsonElement p, string key, bool fallback )
		=> p.TryGetProperty( key, out var element )
			&& (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
				? element.GetBoolean()
				: fallback;
}

public class CaptureTopdownHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			string targetId = SaveCameraBookmarkHandler.OptionalString( p, "targetId" );
			Vector3? center = SaveCameraBookmarkHandler.OptionalVector( p, "center" );
			float? worldHeight = OptionalFloat( p, "worldHeight" );
			float? cameraHeight = OptionalFloat( p, "cameraHeight" );
			int width = CaptureCameraSetHandler.OptionalInt( p, "width", 1024 );
			int height = CaptureCameraSetHandler.OptionalInt( p, "height", 1024 );
			bool renderUI = CaptureCameraSetHandler.OptionalBool( p, "renderUI", false );
			var result = CameraCaptureService.CaptureTopdown(
				targetId,
				center,
				worldHeight,
				cameraHeight,
				width,
				height,
				renderUI );
			return Task.FromResult<object>( new
			{
				captured = true,
				manifest = CameraCaptureService.TopdownManifest( result ),
				path = CameraCaptureService.WriteTemporaryPng( result.Png, "topdown" ),
				note = "Legacy file-IPC result. Native MCP returns the manifest plus an inline PNG image.",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"capture_topdown failed: {ex.Message}" } );
		}
	}

	private static float? OptionalFloat( JsonElement p, string key )
		=> p.TryGetProperty( key, out var element ) && element.ValueKind == JsonValueKind.Number
			? element.GetSingle()
			: null;
}
