using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// Gameplay Recording family — record live PLAY-MODE gameplay to a .movie clip.
// Closes engine-watch #2 (docs/TOOL_BACKLOG.md): Sandbox.MovieMaker.MovieRecorder
// SHIPPED in the current editor build (verified live 2026-07-12; absent 2026-07-08).
//
//   record_gameplay_clip       start an async frame-loop recording job (play mode only)
//   stop_gameplay_recording    stop → ToClip() → persist as a project .movie asset
//   gameplay_recording_status  poll the job (frames, elapsed, tracked objects)
//
// Surface verified live via reflection probes (2026-07-12):
//   MovieRecorder ctors: (Scene scene, MovieRecorderOptions options = default)
//                        (TrackBinder binder, MovieRecorderOptions options = default)
//   MovieRecorderOptions: record class (int SampleRate, MovieTime? BufferDuration),
//     fluent WithDefaultComponentCapturers() / WithDefaultCaptureActions() /
//     WithCaptureGameObject(go[, trackName]) / WithCaptureComponent(c) /
//     WithCaptureAll<T>(Func<T,bool>? = null) where T : Component.
//   Start() -> IDisposable (Sandbox.Utility.DisposeAction), Stop(), Advance(MovieTime),
//   Capture(), ToClip() -> Compiled.MovieClip, ToResource() -> IMovieResource.
//   MovieTime.FromSeconds(double) + implicit from double.
//
// BEHAVIOR PROVEN LIVE (probe results, 2026-07-12):
//   • WithDefaultComponentCapturers() ALONE captures NOTHING (0 tracks) — capturers
//     define HOW to record component types, not WHAT to record.
//   • WithCaptureAll<Component>() also captured 0 tracks on the open scene — dead end.
//   • WithCaptureGameObject(go) → 10 tracks for one player hierarchy. WORKS.
//   • WithDefaultCaptureActions() → whole-scene capture (19,969 tracks on basic_example).
//     WORKS but heavy — that's the no-ids default, and why ids are recommended.
//   • In PLAY MODE, Start() hooks the running scene and the recorder AUTO-advances +
//     AUTO-captures with game time (proven: a 5.55s wall recording that ALSO called
//     Advance(delta) per editor frame produced a 10.80s clip — exactly double-counted;
//     edit-mode manual loops produced exact manual-only durations). So the frame job
//     below only MONITORS (elapsed, maxSeconds cap) — it never calls Advance/Capture.
//   • Persistence: new MovieResource { Compiled = recorder.ToClip() } → .Serialize()
//     .ToJsonString() → write <Assets>/<folder>/<name>.movie →
//     Editor.AssetSystem.RegisterFile(abs) → asset.Compile(true). list_movies then
//     reports { loadable:true, hasCompiledClip:true } — same as a dock-authored movie.
//
// Frame-job pattern (statics + job class + [EditorEvent.Frame]) mirrors
// PlaytestHandler.cs — the house pattern for play-mode frame jobs. The recorder
// quantizes samples onto its SampleRate grid internally.
//
// Types are fully-qualified (Sandbox.MovieMaker.*) — `using Editor;` is in scope
// and Editor.MovieMaker exists, so bare names risk the FileSystem ambiguity class.
// Deliberately uses ONLY public bridge-independent APIs (no ClaudeBridge internals)
// so the file compiles in any editor assembly.
//
// Registration (MyEditorMenu.cs RegisterHandlers):
//   Register( "record_gameplay_clip",      () => new RecordGameplayClipHandler() );
//   Register( "stop_gameplay_recording",   () => new StopGameplayRecordingHandler() );
//   Register( "gameplay_recording_status", () => new GameplayRecordingStatusHandler() );
// Scene-mutating: NONE — all three must stay callable during play mode (recording
// only exists there). stop_gameplay_recording writes a DISK asset (persists), and
// its optional MoviePlayer wiring during play is runtime-only (discarded on stop),
// matching the play_movie precedent.
// ═══════════════════════════════════════════════════════════════════════════

internal static class GameplayRecorderRunner
{
	internal class Job
	{
		public string JobId;
		public Sandbox.MovieMaker.MovieRecorder Recorder;
		public IDisposable Session;               // Start()'s scoped session — dispose at stop
		public int SampleRate;
		public float MaxSeconds;
		public int FramesCaptured;                // editor frames where the recorder reported captured data
		public double ClipSeconds;                // recorder.Time at the last tick — the clip's timeline length
		public System.Diagnostics.Stopwatch Watch = new(); // wall clock (fallback + diagnostics)
		public List<string> TrackedNames = new(); // captured GameObject names ("whole scene" when empty ids)
		public string CaptureMode;                // "objects" | "scene"
		public bool Stopped;                      // recorder stopped; clip pending save/discard
		public string StopReason;
	}

	private static Job _job;
	private static readonly object _lock = new();
	private static object _lastSummary;

	internal static void Start( Job job ) { lock ( _lock ) { _job = job; _lastSummary = null; } }
	internal static Job Current() { lock ( _lock ) { return _job; } }
	internal static object LastSummary() { lock ( _lock ) { return _lastSummary; } }

	/// <summary>Detach the current job (recording already stopped) and publish its save/discard summary.</summary>
	internal static void Finish( object summary ) { lock ( _lock ) { _lastSummary = summary; _job = null; } }

	/// <summary>Stop the recorder NOW (idempotent). The job stays current so stop_gameplay_recording can persist the clip.</summary>
	internal static void StopRecorder( Job j, string reason )
	{
		if ( j.Stopped ) return;
		try { j.Recorder.Stop(); } catch { }
		try { j.Session?.Dispose(); } catch { }
		j.Watch.Stop();
		j.Stopped = true;
		j.StopReason = reason;
	}

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		Job j;
		lock ( _lock ) { j = _job; }
		if ( j == null || j.Stopped ) return;

		if ( !Game.IsPlaying )
		{
			// Play mode ended under us. The recorded samples live in the recorder, not the
			// scene — stop cleanly and keep the job so stop_gameplay_recording can still save.
			StopRecorder( j, "play mode ended — clip kept in memory, call stop_gameplay_recording to save it" );
			return;
		}

		try
		{
			// MONITOR ONLY: in play mode Start() hooks the running scene, and the recorder
			// advances + captures itself with game time (calling Advance/Capture here too
			// double-counts the timeline — proven live). We track progress and enforce the cap.
			double t;
			try { t = j.Recorder.Time.TotalSeconds; }
			catch { t = j.Watch.Elapsed.TotalSeconds; }
			j.ClipSeconds = t;

			try { if ( j.Recorder.RecordedThisFrame.Any() ) j.FramesCaptured++; } catch { }

			if ( t >= j.MaxSeconds )
				StopRecorder( j, $"maxSeconds safety cap ({j.MaxSeconds}s of clip time) reached — call stop_gameplay_recording to save" );
		}
		catch ( Exception ex )
		{
			// Never let the ticker throw (it'd spam every editor frame). Stop + record why.
			StopRecorder( j, $"recorder error: {ex.Message}" );
		}
	}
}

/// <summary>
/// record_gameplay_clip — start recording live play-mode gameplay into a MovieMaker
/// clip. Requires play mode. Params:
///   ids        : optional array of GameObject GUIDs to capture (recommended — small,
///                focused clips). Omitted → whole-scene capture (heavy: every object).
///   sampleRate : optional int samples/sec (default 30, clamped 1-120)
///   maxSeconds : optional safety cap in clip-time seconds (default 60, clamped 1-600) —
///                auto-stops the job; the clip stays in memory until saved/discarded
/// Returns a job id immediately; recording runs ASYNC in the editor frame loop.
/// </summary>
public class RecordGameplayClipHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "record_gameplay_clip requires play mode — call start_play first" } );

		var scene = Game.ActiveScene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var existing = GameplayRecorderRunner.Current();
		string discardedNote = null;
		if ( existing != null )
		{
			if ( !existing.Stopped )
				return Task.FromResult<object>( new { error = "a gameplay recording is already running — stop_gameplay_recording saves it (or pass discard), gameplay_recording_status polls it" } );
			// A stopped-but-unsaved job exists; starting fresh discards it.
			discardedNote = $"Discarded a previous UNSAVED recording ({System.Math.Round( existing.ClipSeconds, 2 )}s, reason: {existing.StopReason}).";
		}

		int sampleRate = 30;
		if ( p.TryGetProperty( "sampleRate", out var srEl ) && srEl.TryGetInt32( out var sr ) )
			sampleRate = System.Math.Clamp( sr, 1, 120 );

		float maxSeconds = 60f;
		if ( p.TryGetProperty( "maxSeconds", out var msEl ) && msEl.TryGetSingle( out var msF ) )
			maxSeconds = System.Math.Clamp( msF, 1f, 600f );

		try
		{
			// Capturers say HOW component types get recorded; capture targets say WHAT.
			// (Proven live: WithDefaultComponentCapturers() alone → 0 tracks.)
			var options = new Sandbox.MovieMaker.MovieRecorderOptions( sampleRate, null )
				.WithDefaultComponentCapturers();

			var job = new GameplayRecorderRunner.Job { SampleRate = sampleRate, MaxSeconds = maxSeconds };

			if ( p.TryGetProperty( "ids", out var idsEl ) && idsEl.ValueKind == JsonValueKind.Array && idsEl.GetArrayLength() > 0 )
			{
				foreach ( var idEl in idsEl.EnumerateArray() )
				{
					var idStr = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
					if ( idStr == null || !Guid.TryParse( idStr, out var guid ) )
						return Task.FromResult<object>( new { error = $"ids entries must be GameObject GUIDs (got '{idStr}')" } );
					var go = scene.Directory.FindByGuid( guid );
					if ( go == null )
						return Task.FromResult<object>( new { error = $"GameObject not found in the RUNNING scene: {idStr} (play-mode ids differ from editor ids — use get_scene_hierarchy while playing)" } );
					options = options.WithCaptureGameObject( go );
					job.TrackedNames.Add( go.Name );
				}
				job.CaptureMode = "objects";
			}
			else
			{
				// Whole-scene capture. WithCaptureAll<Component>() proved inert (0 tracks);
				// WithDefaultCaptureActions() is the path that actually records everything.
				options = options.WithDefaultCaptureActions();
				job.CaptureMode = "scene";
			}

			job.JobId = Guid.NewGuid().ToString( "N" ).Substring( 0, 8 );
			job.Recorder = new Sandbox.MovieMaker.MovieRecorder( scene, options );
			// Start() hooks the RUNNING scene: the recorder advances + captures itself with
			// game time from here on (proven live) — the frame job only monitors it.
			job.Session = job.Recorder.Start();
			job.Watch.Start();

			GameplayRecorderRunner.Start( job );

			return Task.FromResult<object>( new
			{
				started = true,
				jobId = job.JobId,
				sampleRate,
				maxSeconds,
				capture = job.CaptureMode == "objects" ? $"{job.TrackedNames.Count} GameObject(s): {string.Join( ", ", job.TrackedNames )}" : "whole scene (heavy — pass ids for focused clips)",
				discarded = discardedNote,
				note = "Recording ASYNC in the editor frame loop. Drive gameplay now (playtest/drive_player make scripted runs), poll gameplay_recording_status, then stop_gameplay_recording to save the .movie asset.",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"record_gameplay_clip failed: {ex.Message}" } );
		}
	}
}

/// <summary>
/// stop_gameplay_recording — stop the active recording job and persist it as a project
/// .movie asset (or discard it). Params:
///   name     : optional asset name (default recording_&lt;UTC timestamp&gt;; [A-Za-z0-9_-] only)
///   folder   : optional Assets subfolder (default "recordings")
///   wireToId : optional GameObject GUID — auto-wire a MoviePlayer pointed at the new clip
///   discard  : optional bool — throw the recording away instead of saving
/// Also saves a job that already auto-stopped (maxSeconds cap / play mode ended).
/// </summary>
public class StopGameplayRecordingHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var job = GameplayRecorderRunner.Current();
		if ( job == null )
			return Task.FromResult<object>( new { error = "No gameplay recording to stop — record_gameplay_clip starts one (gameplay_recording_status shows the last saved summary)" } );

		GameplayRecorderRunner.StopRecorder( job, "stopped via stop_gameplay_recording" );

		if ( p.TryGetProperty( "discard", out var dEl ) && dEl.ValueKind == JsonValueKind.True )
		{
			var discardSummary = new
			{
				finished = true,
				saved = false,
				discarded = true,
				jobId = job.JobId,
				clipSeconds = System.Math.Round( job.ClipSeconds, 2 ),
				reason = job.StopReason,
			};
			GameplayRecorderRunner.Finish( discardSummary );
			return Task.FromResult<object>( discardSummary );
		}

		try
		{
			var clip = job.Recorder.ToClip();

			var assetsPath = Project.Current?.GetAssetsPath();
			if ( string.IsNullOrEmpty( assetsPath ) )
				return Task.FromResult<object>( new { error = "No project assets path — clip is still in memory, retry or pass discard:true" } );

			string name = null;
			if ( p.TryGetProperty( "name", out var nEl ) && nEl.ValueKind == JsonValueKind.String )
				name = Sanitize( nEl.GetString() );
			if ( string.IsNullOrEmpty( name ) )
				name = $"recording_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

			string folder = "recordings";
			if ( p.TryGetProperty( "folder", out var fEl ) && fEl.ValueKind == JsonValueKind.String )
			{
				var f = Sanitize( fEl.GetString() );
				if ( !string.IsNullOrEmpty( f ) ) folder = f;
			}

			var dirFull = Path.Combine( assetsPath, folder );
			Directory.CreateDirectory( dirFull );
			var fullPath = Path.Combine( dirFull, name + ".movie" );
			var relPath = $"{folder}/{name}.movie";

			if ( File.Exists( fullPath ) )
				return Task.FromResult<object>( new { error = $"'{relPath}' already exists — pass a different name (clip is still in memory, so just retry)" } );

			// MovieResource : GameResource — Serialize() gives the on-disk .movie JSON
			// (verified live: identical loadable shape to a Movie Maker dock asset).
			var res = new Sandbox.MovieMaker.MovieResource();
			res.Compiled = clip;
			File.WriteAllText( fullPath, res.Serialize().ToJsonString() );

			var asset = Editor.AssetSystem.RegisterFile( fullPath )
				?? Editor.AssetSystem.RegisterFile( fullPath.Replace( '\\', '/' ) );
			bool compiled = false;
			try { compiled = asset?.Compile( true ) ?? false; } catch { }

			// Optional: wire a MoviePlayer on a target object at the new clip.
			object wired = null;
			if ( p.TryGetProperty( "wireToId", out var wEl ) && wEl.ValueKind == JsonValueKind.String )
			{
				if ( !Guid.TryParse( wEl.GetString(), out var wireGuid ) )
				{
					wired = new { ok = false, error = $"wireToId is not a GUID: '{wEl.GetString()}'" };
				}
				else
				{
					var scene = Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
					var go = scene?.Directory.FindByGuid( wireGuid );
					if ( go == null )
					{
						wired = new { ok = false, error = $"wireToId GameObject not found: {wEl.GetString()} (asset was still saved)" };
					}
					else
					{
						var player = go.GetOrAddComponent<Sandbox.MovieMaker.MoviePlayer>();
						// Prefer the registered asset; fall back to the in-memory resource if the
						// asset system hasn't finished loading the compiled clip yet.
						var loaded = ResourceLibrary.Get<Sandbox.MovieMaker.MovieResource>( relPath );
						bool usedAsset = loaded?.Compiled != null;
						player.Resource = usedAsset ? loaded : res;
						wired = new
						{
							ok = true,
							gameObject = go.Name,
							id = go.Id.ToString(),
							source = usedAsset ? "asset" : "in-memory resource (asset still settling — re-wire later via add_movie_player if needed)",
							runtimeOnly = Game.IsPlaying,
						};
					}
				}
			}

			double durationSeconds = 0;
			try { durationSeconds = clip.Duration.TotalSeconds; } catch { }

			var summary = new
			{
				finished = true,
				saved = true,
				assetPath = relPath,
				jobId = job.JobId,
				durationSeconds,
				trackCount = clip.Tracks.Length,
				sampleRate = job.SampleRate,
				compiled,
				stopReason = job.StopReason,
				wired,
				note = TrackNote( clip.Tracks.Length ) + " Verify with list_movies, replay via add_movie_player + play_movie (play mode).",
			};
			GameplayRecorderRunner.Finish( summary );
			return Task.FromResult<object>( summary );
		}
		catch ( Exception ex )
		{
			// Keep the job (and its in-memory clip) so a corrected retry can still save it.
			return Task.FromResult<object>( new { error = $"stop_gameplay_recording failed: {ex.Message} — the clip is still in memory; retry, or pass discard:true to drop it" } );
		}
	}

	static string TrackNote( int tracks )
		=> tracks == 0
			? "WARNING: the clip has 0 tracks (nothing captured — were the target objects present in the running scene?)."
			: $"Saved {tracks} track(s).";

	static string Sanitize( string raw )
	{
		if ( string.IsNullOrWhiteSpace( raw ) ) return null;
		var cleaned = new string( raw.Trim().Select( c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' ? c : '_' ).ToArray() );
		return cleaned.Trim( '_' ).Length == 0 ? null : cleaned;
	}
}

/// <summary>
/// gameplay_recording_status — poll the recording job: live progress while recording,
/// stopped-pending-save state (maxSeconds cap / play ended), or the last saved summary.
/// No params.
/// </summary>
public class GameplayRecordingStatusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var job = GameplayRecorderRunner.Current();
		if ( job != null )
		{
			if ( !job.Stopped )
			{
				return Task.FromResult<object>( new
				{
					recording = true,
					jobId = job.JobId,
					elapsedSeconds = System.Math.Round( job.ClipSeconds, 2 ),   // clip-timeline seconds
					framesWithData = job.FramesCaptured,
					maxSeconds = job.MaxSeconds,
					sampleRate = job.SampleRate,
					capture = job.CaptureMode == "objects" ? string.Join( ", ", job.TrackedNames ) : "whole scene",
					trackedObjectCount = job.CaptureMode == "objects" ? job.TrackedNames.Count : -1,
					note = "Recording. stop_gameplay_recording saves the clip as a .movie asset.",
				} );
			}

			return Task.FromResult<object>( new
			{
				recording = false,
				stopped = true,
				pendingSave = true,
				jobId = job.JobId,
				reason = job.StopReason,
				elapsedSeconds = System.Math.Round( job.ClipSeconds, 2 ),
				framesWithData = job.FramesCaptured,
				capture = job.CaptureMode == "objects" ? string.Join( ", ", job.TrackedNames ) : "whole scene",
				note = "Recording stopped but NOT saved — stop_gameplay_recording persists it (or discards with discard:true).",
			} );
		}

		var summary = GameplayRecorderRunner.LastSummary();
		if ( summary != null )
			return Task.FromResult<object>( summary );

		return Task.FromResult<object>( new { recording = false, note = "No gameplay recording has run yet — record_gameplay_clip starts one (play mode required)." } );
	}
}
