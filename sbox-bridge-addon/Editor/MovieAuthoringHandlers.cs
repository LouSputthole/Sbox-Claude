using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// Movie Authoring family — author a .movie cutscene clip from a declarative
// shot list, deterministically, in EDIT mode. No Movie Maker dock, no play
// mode, no real-time waiting: the whole clip bakes inside one handler call.
//
//   author_movie_clip   shot list → keyframe timeline → baked .movie asset
//
// The key mechanism (docs/BRIDGE_GOTCHAS.md §13, flipped around): in PLAY mode
// MovieRecorder.Start() auto-advances with game time (manual pumping double-
// counts), but in EDIT mode it does NOT auto-advance — manual Advance(dt) +
// Capture() loops produce EXACT manual durations. So the handler steps a
// camera through an interpolated shot path and hand-pumps the recorder one
// synthetic frame at a time at the clip's sample rate. This is the OFFICIAL
// in-editor recording idiom — sbox-docs movie-maker/recording-api.md:
// "Instead of Start and Stop, call Advance to move the recording along by an
// amount of time, and Capture to record a frame." The doc also confirms
// Advance(double) via the implicit MovieTime conversion, and documents
// MovieRecorderOptions.Default (static, describe_type-blind: captures all
// Renderers/Cameras/SoundPoints/particles) — deliberately NOT used here: on
// a big scene Default bakes thousands of frozen-pose tracks; the targeted
// WithCaptureGameObject + WithCaptureComponent pair keeps authored clips at
// exactly the ~15 camera tracks they need.
//
// PROVEN LIVE (edit-mode probes on Gravehold, 2026-07-13):
//   • Capture() records the CURRENT WorldTransform of a GameObject moved
//     between Advance calls — a 91-frame bake at 30 fps decoded back to
//     exactly x = 100·t at t=1.0s and t=2.5s (via MoviePlayer.Position +
//     UpdateTargets(), which applies clip state in edit mode).
//   • Duration math is exact: 91 × Advance(1/30) → recorder.Time = Duration
//     = 3.0333s. MovieTime.FromSeconds(1/30) = 0.033333; implicit double →
//     MovieTime conversion compiles and works.
//   • WithCaptureGameObject(go) alone records ONLY GameObject-level tracks
//     (Enabled|LocalPosition|LocalRotation|LocalScale) — NO camera properties.
//     Adding WithCaptureComponent(cam) records the CameraComponent too
//     (15 tracks incl. FieldOfView; FOV decoded exactly 70.00 at t=0.5 for a
//     60→80 ramp). fovDegrees is therefore REAL and baked into the clip.
//   • Persistence path identical to GameplayRecorderHandlers: MovieResource
//     { Compiled = clip } → Serialize().ToJsonString() (no custom
//     JsonSerializerOptions — gotcha #14) → Assets/<folder>/<name>.movie →
//     Editor.AssetSystem.RegisterFile → asset.Compile(true). This is the
//     EDITOR-side path that makes a dock-visible, loadable project asset;
//     the official docs' game-side path (FileSystem.Data.WriteJson of
//     clip.ToResource()) lands in the data folder, not the project.
//   • E2E (2026-07-13): a 3-shot bake → 3.5s clip, 105 frames, 15 tracks,
//     bakeMs=6; list_movies { loadable:true, hasCompiledClip:true };
//     add_movie_player createTargets:true RECREATED the destroyed temp
//     camera (with its CameraComponent) under "Auto-Created Targets"; in
//     play mode play_movie ran the playhead to positionSeconds=3.5 (full
//     duration). Worst-case bake (120s × 120fps = 14,400 frames) extrapolates
//     to well under a second — synchronous single-call is safe, no frame job.
//   • HOTLOAD WARNING (observed live): after the bridge LIBRARY assembly
//     hotloads, MovieRecorder.Start() can throw NRE from EVERY assembly until
//     the editor restarts — editor-global MovieMaker state doesn't survive
//     the swap. Same class as gotcha #9; restart_editor clears it.
//
// DELIBERATE OMISSION — no `alsoCapture` param: in edit mode nothing else
// moves during the bake, so capturing extra GameObjects would only bake
// constant (frozen-pose) tracks. Authored clips animate ONLY what the bake
// moves — the camera. Record moving gameplay with record_gameplay_clip.
//
// Types are fully-qualified (Sandbox.MovieMaker.*) — `using Editor;` is in
// scope and Editor.MovieMaker exists, so bare names risk the FileSystem
// ambiguity class.
//
// Registration (MyEditorMenu.cs RegisterHandlers):
//   Register( "author_movie_clip", () => new AuthorMovieClipHandler() );
// Scene-mutating: YES — add "author_movie_clip" to _sceneMutatingCommands.
// It writes+registers a project asset and creates (or borrows and moves) a
// camera GameObject mid-call. The borrowed camera's WorldTransform + FOV are
// restored EXACTLY in finally, but the scene IS mutated during the bake, and
// the handler has no play-mode use case to preserve: it independently refuses
// play mode because MovieRecorder auto-advances there (gotcha #13) and would
// double-count a manual pump. Gate + handler guard = belt and braces.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// author_movie_clip — bake a .movie cutscene clip from a declarative shot
/// list in EDIT mode (refused during play). Params:
///   shots      : REQUIRED array (1-32) of shot objects:
///                  position     : "x,y,z" camera position (required)
///                  lookAt       : optional GameObject GUID (resolved to its
///                                 position at bake time) or "x,y,z" point;
///                                 omitted → keep the previous shot's rotation
///                  fovDegrees   : optional FOV (5-170); omitted → carry the
///                                 previous shot's (first shot: camera's own)
///                  holdSeconds  : optional hold at this pose (default 1, 0-120)
///                  blendSeconds : optional blend from the previous shot
///                                 (default 1, 0-120; ignored on shot 0)
///                  ease         : optional "smoothstep" (default) | "linear"
///   clipName   : optional asset name (default authored_&lt;UTC&gt;; [A-Za-z0-9_-])
///   folder     : optional Assets subfolder (default "movies")
///   sampleRate : optional samples/sec (default 30, clamped 1-120)
///   cameraId   : optional GUID of an existing camera GameObject to bake
///                through (must have a CameraComponent; transform + FOV are
///                restored EXACTLY afterwards). Default: a temp camera is
///                created and destroyed after the bake.
/// Total timeline (holds + blends) capped at 120 seconds.
/// </summary>
public class AuthorMovieClipHandler : IBridgeHandler
{
	private const double MaxTotalSeconds = 120.0;
	private const int MaxShots = 32;

	private class Shot
	{
		public Vector3 Position;
		public Vector3? LookAt;     // resolved world point (targetId resolved at bake time)
		public float? FovRaw;       // explicit fovDegrees, if given
		public float Fov;           // resolved: chained from the previous shot when omitted
		public float Hold;
		public float Blend;         // blend INTO this shot (ignored on shot 0)
		public bool Smooth;         // smoothstep (true) | linear (false)
		public Rotation RotEnd;     // settled rotation once this shot is reached
	}

	public Task<object> Execute( JsonElement p )
	{
		if ( Game.IsPlaying )
			return Task.FromResult<object>( new { error = "author_movie_clip works in EDIT mode only — in play mode MovieRecorder auto-advances with game time and a manual bake double-counts (stop_play first). To record live gameplay use record_gameplay_clip." } );

		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active editor scene" } );

		int sampleRate = 30;
		if ( p.TryGetProperty( "sampleRate", out var srEl ) && srEl.TryGetInt32( out var sr ) )
			sampleRate = System.Math.Clamp( sr, 1, 120 );

		// ── camera: borrow an existing one, or create a temp ────────────────
		GameObject camGo = null;
		CameraComponent cam = null;
		bool tempCamera = false;

		if ( p.TryGetProperty( "cameraId", out var camEl ) && camEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( camEl.GetString() ) )
		{
			camGo = ClaudeBridge.ResolveGameObject( scene, camEl.GetString() );
			if ( camGo == null )
				return Task.FromResult<object>( new { error = $"cameraId GameObject not found: {camEl.GetString()} (get_scene_hierarchy / find_objects list ids)" } );
			cam = camGo.GetComponent<CameraComponent>();
			if ( cam == null )
				return Task.FromResult<object>( new { error = $"'{camGo.Name}' has no CameraComponent — pass a camera GameObject, or omit cameraId to bake through a temp camera" } );
		}

		// ── parse + resolve shots BEFORE touching the scene ─────────────────
		if ( !p.TryGetProperty( "shots", out var shotsEl ) || shotsEl.ValueKind != JsonValueKind.Array || shotsEl.GetArrayLength() == 0 )
			return Task.FromResult<object>( new { error = "shots is required — a non-empty array of { position, lookAt?, fovDegrees?, holdSeconds?, blendSeconds?, ease? }" } );
		if ( shotsEl.GetArrayLength() > MaxShots )
			return Task.FromResult<object>( new { error = $"Too many shots ({shotsEl.GetArrayLength()}) — max {MaxShots}" } );

		var shots = new List<Shot>();

		foreach ( var el in shotsEl.EnumerateArray() )
		{
			if ( el.ValueKind != JsonValueKind.Object )
				return Task.FromResult<object>( new { error = "each shots entry must be an object" } );
			if ( !el.TryGetProperty( "position", out var posEl ) )
				return Task.FromResult<object>( new { error = $"shot {shots.Count}: position is required ('x,y,z')" } );

			var shot = new Shot { Position = ClaudeBridge.ParseVector3( posEl ) };

			if ( el.TryGetProperty( "lookAt", out var lookEl ) )
			{
				if ( lookEl.ValueKind == JsonValueKind.String && Guid.TryParse( lookEl.GetString(), out _ ) )
				{
					var target = ClaudeBridge.ResolveGameObject( scene, lookEl.GetString() );
					if ( target == null )
						return Task.FromResult<object>( new { error = $"shot {shots.Count}: lookAt GameObject not found: {lookEl.GetString()}" } );
					shot.LookAt = target.WorldPosition; // resolved at bake time — static in edit mode
				}
				else if ( lookEl.ValueKind != JsonValueKind.Null )
				{
					shot.LookAt = ClaudeBridge.ParseVector3( lookEl );
				}
			}

			if ( el.TryGetProperty( "fovDegrees", out var fovEl ) && fovEl.TryGetSingle( out var fovF ) )
				shot.FovRaw = System.Math.Clamp( fovF, 5f, 170f );

			shot.Hold = 1f;
			if ( el.TryGetProperty( "holdSeconds", out var hEl ) && hEl.TryGetSingle( out var hF ) )
				shot.Hold = System.Math.Clamp( hF, 0f, (float)MaxTotalSeconds );

			shot.Blend = 1f;
			if ( el.TryGetProperty( "blendSeconds", out var bEl ) && bEl.TryGetSingle( out var bF ) )
				shot.Blend = System.Math.Clamp( bF, 0f, (float)MaxTotalSeconds );

			shot.Smooth = true;
			if ( el.TryGetProperty( "ease", out var eEl ) && eEl.ValueKind == JsonValueKind.String )
			{
				var ease = eEl.GetString()?.Trim().ToLowerInvariant();
				if ( ease == "linear" ) shot.Smooth = false;
				else if ( ease != "smoothstep" && !string.IsNullOrEmpty( ease ) )
					return Task.FromResult<object>( new { error = $"shot {shots.Count}: ease must be \"linear\" or \"smoothstep\" (got '{eEl.GetString()}')" } );
			}

			shots.Add( shot );
		}

		double total = shots[0].Hold;
		for ( int i = 1; i < shots.Count; i++ ) total += shots[i].Blend + shots[i].Hold;
		if ( total <= 0 )
			return Task.FromResult<object>( new { error = "Timeline has zero duration — give at least one shot a holdSeconds or blendSeconds > 0" } );
		if ( total > MaxTotalSeconds )
			return Task.FromResult<object>( new { error = $"Timeline is {total:F1}s — capped at {MaxTotalSeconds}s. Shorten holds/blends or split into multiple clips." } );

		// ── output path (validate before the bake so failures leave no trace) ──
		var assetsPath = Project.Current?.GetAssetsPath();
		if ( string.IsNullOrEmpty( assetsPath ) )
			return Task.FromResult<object>( new { error = "No project assets path" } );

		string name = null;
		if ( p.TryGetProperty( "clipName", out var nEl ) && nEl.ValueKind == JsonValueKind.String )
			name = Sanitize( nEl.GetString() );
		if ( string.IsNullOrEmpty( name ) )
			name = $"authored_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

		string folder = "movies";
		if ( p.TryGetProperty( "folder", out var fEl ) && fEl.ValueKind == JsonValueKind.String )
		{
			var f = Sanitize( fEl.GetString() );
			if ( !string.IsNullOrEmpty( f ) ) folder = f;
		}

		var dirFull = Path.Combine( assetsPath, folder );
		var fullPath = Path.Combine( dirFull, name + ".movie" );
		var relPath = $"{folder}/{name}.movie";
		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"'{relPath}' already exists — pass a different clipName" } );

		// ── bake ─────────────────────────────────────────────────────────────
		Transform savedXf = default;
		float savedFov = 0f;
		IDisposable session = null;

		try
		{
			if ( cam == null )
			{
				camGo = scene.CreateObject( true );
				camGo.Name = "__author_movie_cam";
				cam = camGo.AddComponent<CameraComponent>();
				cam.IsMainCamera = false;   // never hijack the scene's main camera
				cam.Priority = -100;
				tempCamera = true;
			}

			savedXf = camGo.WorldTransform;
			savedFov = cam.FieldOfView;

			// Resolve the FOV chain now that the camera exists: an omitted fovDegrees
			// carries the previous shot's value (first shot: the camera's own FOV).
			float chainFov = cam.FieldOfView;
			foreach ( var s in shots )
			{
				if ( s.FovRaw.HasValue ) chainFov = s.FovRaw.Value;
				s.Fov = chainFov;
			}

			// Settled rotation per shot (lookAt chained; seed = camera's start rotation).
			var startRot = camGo.WorldRotation;
			for ( int i = 0; i < shots.Count; i++ )
			{
				var prev = i == 0 ? startRot : shots[i - 1].RotEnd;
				shots[i].RotEnd = shots[i].LookAt.HasValue
					? LookAtSafe( shots[i].LookAt.Value - shots[i].Position, prev )
					: prev;
			}

			// FOV is captured ONLY when the CameraComponent is explicitly targeted
			// (WithCaptureGameObject alone records just Enabled/Local* — proven live).
			var options = new Sandbox.MovieMaker.MovieRecorderOptions( sampleRate, null )
				.WithDefaultComponentCapturers()
				.WithCaptureGameObject( camGo )
				.WithCaptureComponent( cam );
			var rec = new Sandbox.MovieMaker.MovieRecorder( scene, options );
			session = rec.Start(); // edit mode: does NOT auto-advance (gotcha #13) — we pump manually

			double dt = 1.0 / sampleRate;
			var step = Sandbox.MovieMaker.MovieTime.FromSeconds( dt );
			int frames = System.Math.Max( 2, (int)System.Math.Round( total * sampleRate ) );

			var watch = System.Diagnostics.Stopwatch.StartNew();
			for ( int f = 0; f < frames; f++ )
			{
				PoseAt( shots, f * dt, out var pos, out var rot, out var fov );
				camGo.WorldPosition = pos;
				camGo.WorldRotation = rot;
				cam.FieldOfView = fov;
				rec.Capture();
				rec.Advance( step ); // duration = frames/sampleRate, exactly (proven live)
			}
			watch.Stop();

			rec.Stop();
			session.Dispose();
			session = null;

			var clip = rec.ToClip();

			// ── persist: the exact GameplayRecorderHandlers path ─────────────
			Directory.CreateDirectory( dirFull );
			var res = new Sandbox.MovieMaker.MovieResource();
			res.Compiled = clip;
			File.WriteAllText( fullPath, res.Serialize().ToJsonString() );

			var asset = Editor.AssetSystem.RegisterFile( fullPath )
				?? Editor.AssetSystem.RegisterFile( fullPath.Replace( '\\', '/' ) );
			bool compiled = false;
			try { compiled = asset?.Compile( true ) ?? false; } catch { }

			bool loadable = false;
			try { loadable = ResourceLibrary.Get<Sandbox.MovieMaker.MovieResource>( relPath ) != null; } catch { }

			double durationSeconds = 0;
			try { durationSeconds = clip.Duration.TotalSeconds; } catch { }

			return Task.FromResult<object>( new
			{
				authored = true,
				path = relPath,
				name,
				durationSeconds,
				frames,
				sampleRate,
				shots = shots.Count,
				tracks = clip.Tracks.Length,
				bakeMs = watch.ElapsedMilliseconds,
				compiled,
				loadable,
				camera = tempCamera
					? $"temp camera (created for the bake, destroyed after; clip binds its id {camGo.Id} — play back with add_movie_player createTargets:true)"
					: $"borrowed '{camGo.Name}' ({camGo.Id}) — transform + FOV restored exactly; the clip animates THIS object on playback",
				nextSteps = "list_movies to verify; add_movie_player (moviePath" + ( tempCamera ? ", createTargets:true" : $", id:{camGo.Id}" ) + ") to wire it; play_movie in play mode to watch it. Nothing was saved to the scene.",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"author_movie_clip failed: {ex.GetType().Name}: {ex.Message}" } );
		}
		finally
		{
			try { session?.Dispose(); } catch { }
			if ( tempCamera )
			{
				try { camGo?.Destroy(); } catch { }
			}
			else if ( camGo != null && cam != null )
			{
				// EXACT restore of the borrowed camera — same structs back.
				try { camGo.WorldTransform = savedXf; } catch { }
				try { cam.FieldOfView = savedFov; } catch { }
			}
		}
	}

	/// <summary>Evaluate the camera pose at timeline second t (segments: hold₀, then blendᵢ+holdᵢ per shot).</summary>
	private static void PoseAt( List<Shot> shots, double t, out Vector3 pos, out Rotation rot, out float fov )
	{
		double cursor = shots[0].Hold;
		if ( t < cursor || shots.Count == 1 )
		{
			pos = shots[0].Position; rot = shots[0].RotEnd; fov = shots[0].Fov;
			return;
		}

		for ( int i = 1; i < shots.Count; i++ )
		{
			var a = shots[i - 1];
			var b = shots[i];

			if ( b.Blend > 0 && t < cursor + b.Blend )
			{
				float raw = (float)((t - cursor) / b.Blend);
				float e = b.Smooth ? raw * raw * (3f - 2f * raw) : raw;
				e = System.Math.Clamp( e, 0f, 1f );
				pos = Vector3.Lerp( a.Position, b.Position, e, true );
				// Aim from the CURRENT interpolated position (the cutscene-director idiom).
				var targetRot = b.LookAt.HasValue ? LookAtSafe( b.LookAt.Value - pos, b.RotEnd ) : b.RotEnd;
				rot = Rotation.Slerp( a.RotEnd, targetRot, e, true );
				fov = a.Fov + (b.Fov - a.Fov) * e;
				return;
			}
			cursor += b.Blend;

			if ( t < cursor + b.Hold )
			{
				pos = b.Position; rot = b.RotEnd; fov = b.Fov;
				return;
			}
			cursor += b.Hold;
		}

		var last = shots[shots.Count - 1];
		pos = last.Position; rot = last.RotEnd; fov = last.Fov;
	}

	private static Rotation LookAtSafe( Vector3 dir, Rotation fallback )
		=> dir.Length > 0.01f ? Rotation.LookAt( dir.Normal ) : fallback;

	private static string Sanitize( string raw )
	{
		if ( string.IsNullOrWhiteSpace( raw ) ) return null;
		var cleaned = new string( raw.Trim().Select( c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' ? c : '_' ).ToArray() );
		return cleaned.Trim( '_' ).Length == 0 ? null : cleaned;
	}
}
