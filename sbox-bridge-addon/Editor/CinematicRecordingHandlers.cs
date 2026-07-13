using Editor;
using Sandbox;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// Cinematic Recording pair — scripted-run capture + killcam scaffold.
//
//   record_playtest   one call = scripted playtest + gameplay recording of the
//                     SAME run (composition of the Batch 60 recorder + the
//                     playtest harness; automated regression footage)
//   create_killcam    codegen scaffold: rolling-buffer MovieRecorder + on-death
//                     replay through MoviePlayer + replay-camera takeover
//
// Same assembly as MyEditorMenu.cs — composes the shipped internals directly:
//   PlaytestHandler.Execute / PlaytestRunner (IsActive, Abort, FindControllerOn)
//   RecordGameplayClipHandler / StopGameplayRecordingHandler /
//   GameplayRecorderRunner (Current, StopRecorder) — never duplicates their logic.
//
// LIVE-VERIFIED (probes on the running Gravehold editor, 2026-07-13):
//   • SANDBOXED GAME CODE CAN DRIVE MOVIEMAKER: a generated sealed Component
//     constructed MovieRecorderOptions(30, MovieTime.FromSeconds(3)), built a
//     MovieRecorder(Scene, options), Start()/Stop()/ToClip()'d it, and played the
//     clip via Components.GetOrCreate<MoviePlayer>().Play(clip) — compiled clean
//     through the normal hotload gate and ran in play mode. The Batch 60 handlers
//     run unsandboxed, but none of this is editor-only. So create_killcam ships
//     the REAL MovieMaker path (no transform-history fallback needed).
//   • BufferDuration IS a rolling buffer: ~8.72 s of recorded clip time with
//     BufferDuration=3 s produced clip.Duration == 3.00 s exactly (5 tracks for
//     one object hierarchy), re-based so playback starts at Position 0.
//   • Compiled.MovieClip (asm Sandbox.Engine): Tracks (ImmutableArray), Duration
//     (MovieTime), ToResource(). No TimeRange property — Duration is the contract.
//   • MoviePlayer: Resource rw, Clip rw, IsPlaying rw, IsLooping rw, TimeScale rw,
//     Position (MovieTime) rw, PositionSeconds rw, Play() / Play(MovieResource) /
//     Play(IMovieClip), CreateTargets, UpdateTargets().
//   • Recorder frame law: in play mode Start() AUTO-advances/captures (gotcha #13;
//     official docs: Start() captures every FIXED UPDATE) — record_playtest's
//     monitor job only WATCHES the playtest and never pumps.
//
// OFFICIAL DOC (Facepunch/sbox-docs docs/movie-maker/recording-api.md) — facts
// taken from the doc rather than probes:
//   • Killcams/replays from GAME code are the documented use case (samples run in
//     game code) — matches the live probe above.
//   • MovieRecorderOptions.Default is a STATIC (invisible to describe_type): the
//     idiomatic base config capturing all renderers/cameras/sound points/particles.
//     Record `with` syntax works: MovieRecorderOptions.Default with { SampleRate = 60 }.
//   • Gotcha §13's "WithCaptureAll<T>() is inert" is a misdiagnosis: bare
//     `new MovieRecorderOptions()` deliberately captures NOTHING; WithCaptureAll<T>
//     needs a matching WithComponentCapturer (or Default, which includes every
//     ComponentCapturer<T> with a public parameterless ctor).
//   • Default.WithFilter( x => !x.Tags.Has("effect") ) — predicate excludes objects
//     AND their descendants, checked once on first capture attempt.
//   • [DefaultMovieRecorderOptions] attributed static methods customize the
//     game-wide default (also used by the in-game `movie` concmd).
//
// Registration (MyEditorMenu.cs RegisterHandlers):
//   Register( "record_playtest", () => new RecordPlaytestHandler() );
//   Register( "create_killcam",  () => new CreateKillcamHandler() );
// _sceneMutatingCommands: add "create_killcam" (writes a .cs scaffold to disk —
// same as every create_* scaffold). "record_playtest" must NOT be added: it only
// exists in play mode (Batch 60 precedent — record_gameplay_clip/playtest).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Frame-loop monitor for record_playtest: waits for the playtest job to finish,
/// then stops the paired gameplay recording and persists the clip — composing
/// StopGameplayRecordingHandler so save semantics (asset write, register, compile,
/// summary publishing) stay single-sourced. Mirrors the PlaytestRunner /
/// GameplayRecorderRunner statics+job+[EditorEvent.Frame] house pattern.
/// </summary>
internal static class RecordPlaytestRunner
{
	internal class Job
	{
		public string RecordingJobId;
		public string ClipName;
		public string Folder;
	}

	private static Job _job;
	private static readonly object _lock = new();

	internal static void Start( Job job ) { lock ( _lock ) { _job = job; } }
	internal static bool IsActive() { lock ( _lock ) { return _job != null; } }

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		Job j;
		lock ( _lock ) { j = _job; }
		if ( j == null ) return;

		// The playtest is still running (it was verifiably active when we started).
		// Play-mode death is handled by the runners themselves: PlaytestRunner
		// summarizes + clears, GameplayRecorderRunner auto-stops keeping the clip
		// in memory — both make IsActive() false and land us in the save path.
		if ( PlaytestRunner.IsActive() ) return;

		lock ( _lock ) { _job = null; }   // claim exactly once

		try
		{
			var recJob = GameplayRecorderRunner.Current();
			if ( recJob == null || recJob.JobId != j.RecordingJobId )
			{
				Log.Warning( $"[SboxBridge] record_playtest: recording job '{j.RecordingJobId}' is gone (superseded or already saved) — no clip to save." );
				return;
			}

			// Stop is idempotent — covers both "still recording" and "auto-stopped
			// because play mode ended / maxSeconds cap hit".
			GameplayRecorderRunner.StopRecorder( recJob, "record_playtest: playtest finished" );

			var saveParams = new System.Text.Json.Nodes.JsonObject
			{
				["name"] = j.ClipName,
				["folder"] = j.Folder,
			};
			var result = new StopGameplayRecordingHandler().Execute( CinematicRecordingHelpers.ToElement( saveParams ) ).Result;

			var err = CinematicRecordingHelpers.PropOf( result, "error" );
			if ( err != null )
				Log.Warning( $"[SboxBridge] record_playtest: clip save failed ({err}) — the clip is still in memory; call stop_gameplay_recording manually (new name) or discard:true." );
			else
				Log.Info( $"[SboxBridge] record_playtest: playtest finished — clip saved as {j.Folder}/{j.ClipName}.movie (gameplay_recording_status has the summary)." );
		}
		catch ( Exception ex )
		{
			// Never let the ticker throw (it'd spam every editor frame).
			Log.Warning( $"[SboxBridge] record_playtest: finalize error: {ex.Message} — if a clip is pending, stop_gameplay_recording can still save it." );
		}
	}
}

/// <summary>
/// record_playtest — run a scripted playtest AND record the same run to a .movie
/// clip in one call. Requires play mode. Params:
///   steps      : REQUIRED — the playtest step list (identical schema to playtest:
///                move/look/lookDelta/action/jump/set/wait/capture/assert)
///   id         : optional player/controller GameObject GUID (playtest targeting)
///   component  : optional controller component type name (playtest targeting)
///   ids        : optional GameObject GUIDs to record (default: the playtest's
///                resolved player hierarchy; no player found → whole scene, heavy)
///   sampleRate : optional recording samples/sec (default 30, clamped 1-120)
///   clipName   : optional .movie asset name (default playtest_&lt;UTC timestamp&gt;)
///   folder     : optional Assets subfolder (default "recordings")
/// Starts both jobs and returns immediately; a monitor job auto-stops + saves the
/// clip the moment the playtest finishes. Poll playtest_status for the pass/fail
/// transcript, then gameplay_recording_status for the saved clip summary.
/// </summary>
public class RecordPlaytestHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "record_playtest requires play mode — call start_play first" } );

		var scene = Game.ActiveScene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( PlaytestRunner.IsActive() )
			return Task.FromResult<object>( new { error = "a playtest is already running — poll playtest_status until it finishes (or playtest_abort)" } );
		if ( RecordPlaytestRunner.IsActive() )
			return Task.FromResult<object>( new { error = "a record_playtest is already finalizing — poll playtest_status / gameplay_recording_status" } );

		var existingRec = GameplayRecorderRunner.Current();
		if ( existingRec != null && !existingRec.Stopped )
			return Task.FromResult<object>( new { error = "a gameplay recording is already running — stop_gameplay_recording saves it (or pass discard), then retry" } );

		if ( !p.TryGetProperty( "steps", out var stepsEl ) || stepsEl.ValueKind != JsonValueKind.Array || stepsEl.GetArrayLength() == 0 )
			return Task.FromResult<object>( new { error = "steps (a non-empty array of playtest step objects) is required" } );

		try
		{
			// ── Resolve WHAT to record (before anything starts) ────────────────
			int sampleRate = 30;
			if ( p.TryGetProperty( "sampleRate", out var srEl ) && srEl.TryGetInt32( out var sr ) )
				sampleRate = System.Math.Clamp( sr, 1, 120 );

			string clipName = null;
			if ( p.TryGetProperty( "clipName", out var cnEl ) && cnEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( cnEl.GetString() ) )
				clipName = cnEl.GetString();
			clipName ??= $"playtest_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

			string folder = "recordings";
			if ( p.TryGetProperty( "folder", out var fEl ) && fEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( fEl.GetString() ) )
				folder = fEl.GetString();

			var recordIds = new System.Text.Json.Nodes.JsonArray();
			string captureDescription;
			if ( p.TryGetProperty( "ids", out var idsEl ) && idsEl.ValueKind == JsonValueKind.Array && idsEl.GetArrayLength() > 0 )
			{
				// Explicit capture set — pass through; RecordGameplayClipHandler validates
				// the GUIDs against the RUNNING scene.
				foreach ( var idEl in idsEl.EnumerateArray() )
					recordIds.Add( idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText() );
				captureDescription = $"{recordIds.Count} explicit GameObject(s)";
			}
			else
			{
				// Default: the playtest's player hierarchy — resolved exactly the way
				// PlaytestRunner.ResolveAnchor does (same target-id/component params).
				var player = ResolvePlaytestPlayer( scene, p );
				if ( player != null )
				{
					recordIds.Add( player.Id.ToString() );
					captureDescription = $"player hierarchy: {player.Name}";
				}
				else
				{
					captureDescription = "whole scene (no player controller found — heavy; pass ids for focused clips)";
				}
			}

			// Safety cap for the recorder: generous over-estimate of the playtest's
			// duration. The monitor stops the recording the moment the playtest ends;
			// this cap only matters if that monitor dies.
			float maxSeconds = EstimateCapSeconds( stepsEl );

			// ── 1. Start the playtest (all step parsing/validation reused) ─────
			var playtestResult = new PlaytestHandler().Execute( p ).Result;
			var ptErr = CinematicRecordingHelpers.PropOf( playtestResult, "error" );
			if ( ptErr != null )
				return Task.FromResult<object>( new { error = $"record_playtest: playtest refused: {ptErr}" } );

			// ── 2. Start the recording of the same run ─────────────────────────
			// Same frame, zero playtest steps have ticked yet — the clip covers the
			// whole run. Composition keeps capture semantics single-sourced.
			var recParams = new System.Text.Json.Nodes.JsonObject
			{
				["sampleRate"] = sampleRate,
				["maxSeconds"] = maxSeconds,
			};
			if ( recordIds.Count > 0 ) recParams["ids"] = recordIds;

			var recResult = new RecordGameplayClipHandler().Execute( CinematicRecordingHelpers.ToElement( recParams ) ).Result;
			var recErr = CinematicRecordingHelpers.PropOf( recResult, "error" );
			if ( recErr != null )
			{
				PlaytestRunner.Abort();   // roll back — don't leave a half-started pair
				return Task.FromResult<object>( new { error = $"record_playtest: recorder refused: {recErr} (playtest aborted)" } );
			}

			var recJob = GameplayRecorderRunner.Current();
			var discardedNote = CinematicRecordingHelpers.PropOf( recResult, "discarded" );

			// ── 3. Monitor: auto-stop + save when the playtest ends ────────────
			RecordPlaytestRunner.Start( new RecordPlaytestRunner.Job
			{
				RecordingJobId = recJob?.JobId,
				ClipName = clipName,
				Folder = folder,
			} );

			return Task.FromResult<object>( new
			{
				started = true,
				steps = stepsEl.GetArrayLength(),
				recordingJobId = recJob?.JobId,
				capture = captureDescription,
				sampleRate,
				clipName,
				folder,
				recorderCapSeconds = maxSeconds,
				discardedPreviousRecording = discardedNote,
				note = "Playtest + recording running ASYNC in the editor frame loop. Poll playtest_status until finished:true for the per-step pass/fail transcript; the clip auto-saves the moment the playtest ends — then gameplay_recording_status returns { saved, assetPath, durationSeconds, trackCount }. A FAILING playtest still saves its clip: replay the exact failure with add_movie_player + play_movie.",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"record_playtest failed: {ex.Message}" } );
		}
	}

	/// <summary>
	/// Resolve the player object the playtest will drive, using ITS id/component
	/// params and ITS controller heuristic (PlaytestRunner.FindControllerOn) —
	/// so "record the playtest's player" and "drive the player" agree.
	/// </summary>
	static GameObject ResolvePlaytestPlayer( Scene scene, JsonElement p )
	{
		string componentType = null;
		if ( p.TryGetProperty( "component", out var cEl ) && cEl.ValueKind == JsonValueKind.String )
			componentType = cEl.GetString();

		Component controller = null;
		if ( p.TryGetProperty( "id", out var idEl ) && idEl.ValueKind == JsonValueKind.String )
		{
			var go = ClaudeBridge.ResolveGameObject( scene, idEl.GetString() );
			if ( go != null ) controller = PlaytestRunner.FindControllerOn( go, componentType );
		}
		if ( controller == null )
		{
			foreach ( var obj in scene.GetAllObjects( true ) )
			{
				controller = PlaytestRunner.FindControllerOn( obj, componentType );
				if ( controller != null ) break;
			}
		}
		return controller?.GameObject;
	}

	/// <summary>Over-estimate the playtest's clip-time duration for the recorder's safety cap.</summary>
	static float EstimateCapSeconds( JsonElement stepsEl )
	{
		int totalFrames = 0;
		foreach ( var stepEl in stepsEl.EnumerateArray() )
		{
			if ( stepEl.ValueKind != JsonValueKind.Object ) continue;
			if ( stepEl.TryGetProperty( "frames", out var fEl ) && fEl.TryGetInt32( out var f ) )
				totalFrames += System.Math.Clamp( f, 1, 1800 );
			else if ( stepEl.TryGetProperty( "wait", out var wEl ) && wEl.TryGetInt32( out var w ) )
				totalFrames += System.Math.Clamp( w, 1, 1800 );
			else
				totalFrames += 30;   // per-verb defaults are ≤30 frames — over-estimating is safe
		}
		double est = totalFrames / 60.0;
		return (float) System.Math.Clamp( est * 2 + 30, 30, 600 );
	}
}

/// <summary>
/// create_killcam — generate a sealed killcam Component: rolling-buffer recording
/// of a target + on-death replay through a MoviePlayer with a replay-camera
/// takeover. Built on the LIVE-verified fact that sandboxed game code can drive
/// Sandbox.MovieMaker at runtime, and that MovieRecorderOptions.BufferDuration
/// keeps exactly the last N seconds (re-based to 0). Params:
///   name           : optional class/file name (default "Killcam")
///   directory      : optional project folder (default "Code")
///   bufferSeconds  : optional rolling-buffer length (default 10, clamped 2-120)
///   sampleRate     : optional recorder samples/sec (default 30, clamped 1-120)
///   cameraDistance : optional replay chase-cam distance (default 150)
///   cameraHeight   : optional replay chase-cam height (default 60)
///   wholeScene     : optional bool — generated default for RecordWholeScene
///                    (MovieRecorderOptions.Default: replay rewinds EVERYTHING the
///                    recorder saw — cinematic but heavy in big scenes; default false)
/// </summary>
public class CreateKillcamHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "Killcam", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float bufferSeconds = 10f;
			if ( p.TryGetProperty( "bufferSeconds", out var bsEl ) && bsEl.TryGetSingle( out var bsf ) )
				bufferSeconds = System.Math.Clamp( bsf, 2f, 120f );

			int sampleRate = 30;
			if ( p.TryGetProperty( "sampleRate", out var srEl ) && srEl.TryGetInt32( out var sri ) )
				sampleRate = System.Math.Clamp( sri, 1, 120 );

			float cameraDistance = 150f;
			if ( p.TryGetProperty( "cameraDistance", out var cdEl ) && cdEl.TryGetSingle( out var cdf ) )
				cameraDistance = System.Math.Clamp( cdf, 10f, 2000f );

			float cameraHeight = 60f;
			if ( p.TryGetProperty( "cameraHeight", out var chEl ) && chEl.TryGetSingle( out var chf ) )
				cameraHeight = System.Math.Clamp( chf, 0f, 2000f );

			bool wholeScene = p.TryGetProperty( "wholeScene", out var wsEl ) && wsEl.ValueKind == JsonValueKind.True;

			var code = BuildKillcamCode( className, bufferSeconds, sampleRate, cameraDistance, cameraHeight, wholeScene, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				bufferSeconds,
				sampleRate,
				cameraDistance,
				cameraHeight,
				wholeScene,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					$"Attach {className} to a MANAGER GameObject (not the player itself): add_component_with_properties component=\"{className}\", then set_component_reference its Target to the player.",
					$"Arm it: set WatchOnStart=true (starts buffering when the scene starts), or call StartWatching()/StartWatching(target) from game code — e.g. in your spawn/respawn logic.",
					$"Replay: call TriggerReplay() from your death code (pairs with create_health_system — subscribe its death event). The last ≤{bufferSeconds.ToString( ci )}s replays through a MoviePlayer while the main camera chase-follows the target; the camera is restored exactly afterwards and {className}.OnReplayFinished fires (re-arm with StartWatching there for round-based games).",
					"IMPORTANT: the replay REWINDS THE LIVE TARGET through its recorded past (MoviePlayer binds tracks back to the same objects). That is the classic killcam: the target is dead/inactive when it runs. If its controller is still alive it will fight the replay — disable it for the duration.",
					"RecordWholeScene=true switches the buffer to MovieRecorderOptions.Default (the official docs' idiom: all renderers/cameras/sound points/particles) — the replay then rewinds EVERYTHING, killer included. Cinematic, but heavy in object-dense scenes; the targeted default records only the Target hierarchy.",
					"LOCAL/visual-only (each client replays its own view) — trigger via an [Rpc.Broadcast] method if every client should see it. Rolling buffer verified live: BufferDuration keeps exactly the last N seconds, re-based to start at 0.",
					"Alternative paths: record_playtest / record_gameplay_clip capture scripted runs to .movie assets; create_cutscene_director is the hand-authored camera path.",
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_killcam failed: {ex.Message}" } );
		}
	}

	static string BuildKillcamCode( string className, float bufferSeconds, int sampleRate, float cameraDistance, float cameraHeight, bool wholeScene, System.Globalization.CultureInfo ci )
	{
		string buf = bufferSeconds.ToString( ci ) + "f";
		string dist = cameraDistance.ToString( ci ) + "f";
		string height = cameraHeight.ToString( ci ) + "f";
		string whole = wholeScene ? "true" : "false";

		return $@"using Sandbox;
using Sandbox.MovieMaker;
using System;

/// <summary>
/// {className} — rolling-buffer killcam. While armed it records the Target into a
/// MovieRecorder whose BufferDuration keeps ONLY the last MaxBufferSeconds
/// (verified live: the compiled clip's Duration equals the buffer length, re-based
/// to start at 0). TriggerReplay() stops recording, builds the clip, replays it
/// through a MoviePlayer — rewinding the target through its recorded past — while
/// this component takes over Scene.Camera with a chase view, then restores the
/// camera exactly and fires OnReplayFinished.
///
/// Arm:    StartWatching() / StartWatching(target)   (or WatchOnStart)
/// Replay: TriggerReplay()                           (call from your death code)
/// LOCAL-only — trigger inside an [Rpc.Broadcast] for all clients.
/// </summary>
public sealed class {className} : Component
{{
	/// The GameObject to record (its whole hierarchy becomes tracks). Empty = this GameObject.
	[Property] public GameObject Target {{ get; set; }}

	/// Rolling-buffer length in seconds — the replay shows at most this much history.
	[Property] public float MaxBufferSeconds {{ get; set; }} = {buf};

	/// Recorder samples per second.
	[Property] public int SampleRate {{ get; set; }} = {sampleRate};

	/// Record the WHOLE scene (MovieRecorderOptions.Default: all renderers, cameras,
	/// sound points, particles) instead of just the Target hierarchy. The replay then
	/// rewinds everything the recorder saw — cinematic, but heavy in big scenes.
	[Property] public bool RecordWholeScene {{ get; set; }} = {whole};

	/// Start buffering automatically when this component starts.
	[Property] public bool WatchOnStart {{ get; set; }} = false;

	/// Replay chase-camera distance behind the target.
	[Property] public float CameraDistance {{ get; set; }} = {dist};

	/// Replay chase-camera height above the target.
	[Property] public float CameraHeight {{ get; set; }} = {height};

	/// Replay speed (1 = realtime, 0.5 = slow motion).
	[Property] public float ReplayTimeScale {{ get; set; }} = 1f;

	/// <summary>Fires (locally) when a replay ends — re-arm with StartWatching() here.</summary>
	public static event Action OnReplayFinished;

	/// <summary>True while ANY {className} replay is running — gate HUD/respawn logic on this.</summary>
	public static bool IsReplaying {{ get; private set; }}

	/// <summary>True while the rolling buffer is recording.</summary>
	public bool IsWatching => _recorder != null;

	private MovieRecorder _recorder;
	private IDisposable _session;
	private GameObject _watched;
	private MoviePlayer _player;
	private float _clipSeconds;
	private TimeSince _sinceReplay;
	private bool _replaying;

	// Pre-replay camera transform, restored exactly on finish.
	private Vector3 _restorePos;
	private Rotation _restoreRot = Rotation.Identity;
	private bool _hasRestore;

	protected override void OnStart()
	{{
		if ( WatchOnStart ) StartWatching();
	}}

	/// <summary>Begin (or restart) the rolling-buffer recording of Target (falls back to this GameObject).</summary>
	public void StartWatching() => StartWatching( Target ?? GameObject );

	/// <summary>Begin (or restart) the rolling-buffer recording of a specific target.</summary>
	public void StartWatching( GameObject target )
	{{
		if ( target == null || !target.IsValid() )
		{{
			Log.Warning( ""{className}: no valid target to watch."" );
			return;
		}}
		if ( _replaying )
		{{
			Log.Warning( ""{className}: can't StartWatching during a replay — wait for OnReplayFinished."" );
			return;
		}}

		StopWatching();

		float buffer = MathX.Clamp( MaxBufferSeconds, 1f, 600f );
		int rate = System.Math.Clamp( SampleRate, 1, 120 );

		// BufferDuration = rolling buffer: the recorder itself discards anything older
		// than the last `buffer` seconds. In play mode Start() captures every fixed
		// update on its own — never pump Advance/Capture manually (double-counts).
		MovieRecorderOptions options;
		if ( RecordWholeScene )
		{{
			// Official idiom (movie-maker recording-api docs): Default captures all
			// renderers/cameras/sound points/particles; scope further with
			// options.WithFilter( x => !x.Tags.Has( ""effect"" ) ) if needed.
			options = MovieRecorderOptions.Default with
			{{
				SampleRate = rate,
				BufferDuration = MovieTime.FromSeconds( buffer ),
			}};
		}}
		else
		{{
			options = new MovieRecorderOptions( rate, MovieTime.FromSeconds( buffer ) )
				.WithDefaultComponentCapturers()
				.WithCaptureGameObject( target );
		}}

		_recorder = new MovieRecorder( Scene, options );
		_session = _recorder.Start();
		_watched = target;
	}}

	/// <summary>Stop buffering WITHOUT replaying (discards the buffered history).</summary>
	public void StopWatching()
	{{
		try {{ _session?.Dispose(); }} catch {{ }}
		try {{ _recorder?.Stop(); }} catch {{ }}
		_session = null;
		_recorder = null;
	}}

	/// <summary>
	/// Stop recording, build the buffered clip (the last ≤MaxBufferSeconds) and play
	/// it back: the target is rewound through its recorded past while the main camera
	/// chase-follows it. Fires OnReplayFinished when done (also when there was nothing
	/// to replay, so death→respawn flows never hang on a missing event).
	/// </summary>
	public void TriggerReplay()
	{{
		if ( _replaying ) return;
		if ( _recorder == null )
		{{
			Log.Warning( ""{className}: TriggerReplay called while not watching — call StartWatching first."" );
			OnReplayFinished?.Invoke();
			return;
		}}

		try {{ _session?.Dispose(); }} catch {{ }}
		try {{ _recorder.Stop(); }} catch {{ }}
		var clip = _recorder.ToClip();
		_recorder = null;
		_session = null;

		if ( clip == null || clip.Tracks.Length == 0 )
		{{
			Log.Warning( ""{className}: buffered clip has no tracks (was the target present while watching?) — skipping replay."" );
			OnReplayFinished?.Invoke();
			return;
		}}

		_clipSeconds = (float) clip.Duration.TotalSeconds;

		_player = GameObject.Components.GetOrCreate<MoviePlayer>();
		_player.IsLooping = false;
		_player.TimeScale = MathX.Clamp( ReplayTimeScale, 0.05f, 10f );
		_player.Play( clip );

		var cam = Scene?.Camera;
		if ( cam != null )
		{{
			_restorePos = cam.WorldPosition;
			_restoreRot = cam.WorldRotation;
			_hasRestore = true;
		}}

		_sinceReplay = 0f;
		_replaying = true;
		IsReplaying = true;
	}}

	protected override void OnUpdate()
	{{
		if ( !_replaying ) return;

		float scale = MathX.Clamp( ReplayTimeScale, 0.05f, 10f );
		bool timeUp = (float) _sinceReplay >= ( _clipSeconds / scale ) + 0.25f;
		bool playerDone = _player != null && ( !_player.IsPlaying || _player.PositionSeconds >= _clipSeconds - 0.01f );
		if ( timeUp || playerDone ) FinishReplay();
	}}

	protected override void OnPreRender()
	{{
		// Chase-cam takeover ONLY while replaying. OnPreRender runs after controllers/
		// animation position things, so this write wins the frame.
		if ( !_replaying ) return;

		var cam = Scene?.Camera;
		if ( cam == null ) return;
		if ( _watched == null || !_watched.IsValid() ) return;

		var focus = _watched.WorldPosition + Vector3.Up * 32f;
		var eye = focus - _watched.WorldRotation.Forward * CameraDistance + Vector3.Up * CameraHeight;
		var dir = focus - eye;
		if ( dir.Length < 0.01f ) return;

		cam.WorldPosition = eye;
		cam.WorldRotation = Rotation.LookAt( dir.Normal );
	}}

	protected override void OnDisabled()
	{{
		StopWatching();
		if ( _replaying ) FinishReplay();
	}}

	private void FinishReplay()
	{{
		_replaying = false;
		IsReplaying = false;

		if ( _player != null )
		{{
			try {{ _player.IsPlaying = false; }} catch {{ }}
			_player = null;
		}}

		// Restore the exact pre-replay camera (a live controller resumes next frame).
		if ( _hasRestore )
		{{
			var cam = Scene?.Camera;
			if ( cam != null )
			{{
				cam.WorldPosition = _restorePos;
				cam.WorldRotation = _restoreRot;
			}}
			_hasRestore = false;
		}}

		OnReplayFinished?.Invoke();
	}}
}}
";
	}
}

/// <summary>Shared helpers for the cinematic-recording handlers.</summary>
internal static class CinematicRecordingHelpers
{
	/// <summary>Build a JsonElement from a JsonObject for composing sibling handlers
	/// (plain ToJsonString — custom JsonSerializerOptions throw in editor context).</summary>
	public static JsonElement ToElement( System.Text.Json.Nodes.JsonObject obj )
	{
		using var doc = JsonDocument.Parse( obj.ToJsonString() );
		return doc.RootElement.Clone();
	}

	/// <summary>Read a string property off a composed handler's anonymous result (e.g. "error").</summary>
	public static string PropOf( object result, string name )
	{
		try { return result?.GetType().GetProperty( name )?.GetValue( result ) as string; }
		catch { return null; }
	}
}
