using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// =============================================================================
//  Cinematics & Dialogue pack (v1.20.0) -- two ENGINE-PROOF, no-asset scaffolds:
//
//    create_cutscene_director  hand-authored camera-shot cutscene player
//                              (NO .movie asset -- works on today's engine)
//    create_dialogue_system    typewriter NPC/story dialogue (Component + Razor HUD)
//
//  These are the HAND-AUTHORED cinematic path. The keyframed/timeline path is the
//  MovieMaker family in MovieMakerHandlers.cs (list_movies / add_movie_player /
//  play_movie / stop_movie) which wires a Sandbox.MovieMaker.MoviePlayer to a
//  .movie asset authored in the editor's Movie Maker dock. Use the cutscene
//  director when you want a cutscene with zero assets and full C# control; use
//  the MovieMaker family when you have (or want to author) a .movie clip.
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  reusing ClaudeBridge (TryResolveProjectPath, SanitizeIdentifier) and
//  ScaffoldHelpers (PrepareCodeFile / WriteCode). Handler code here is UNSANDBOXED
//  editor code; the C# *strings written to disk* are SANDBOXED game code.
//
//  LIVE-VERIFIED API surface used by the generated code (describe_type / search_types,
//  2026-07-08 on the shipping build):
//    - Scene.Camera : CameraComponent (read/WRITE) -- the main camera; set its
//      WorldPosition/WorldRotation to take it over (same GameObject transform the
//      CameraShake scaffold drives). OnPreRender is a general per-frame component
//      method (component-methods doc) called right before rendering, AFTER
//      controllers/animation position things -- the right hook to win the camera.
//    - Rotation.Slerp(a,b,amount,clamp) + Rotation.From(Angles) are static.
//    - Sandbox.Input.ClearActions() zeroes ALL action state for the frame (the
//      clean input-lock idiom); Input.Pressed(action) reads skip/advance;
//      Input.ReleaseActions() on finish. (describe_type "Input" resolves the XR
//      struct -- the real static class is Sandbox.Input; game code's bare `Input`
//      binds to it via `using Sandbox;`.)
//    - MathX has NO Ease/Smoothstep member on this SDK, so the generated code
//      hand-rolls smoothstep (t*t*(3-2t)); System.MathF/Math also compile.
//    - Rotation is quaternion-shaped and awful to hand-edit in the inspector, so
//      the shot list uses List<Angles> (pitch/yaw/roll) and converts at runtime.
//
//  Registration (MyEditorMenu.cs RegisterHandlers, Batch 45+):
//    Register( "create_cutscene_director", () => new CreateCutsceneDirectorHandler() );
//    Register( "create_dialogue_system",   () => new CreateDialogueSystemHandler() );
//  Both are scene-mutating (they write .cs/.razor to disk) -- add both names to
//  _sceneMutatingCommands.
// =============================================================================

// -----------------------------------------------------------------------------
// create_cutscene_director -- a sealed Component that plays a hand-authored
// camera cutscene from parallel shot lists. Takes over Scene.Camera in
// OnPreRender ONLY while playing, eased blend (smoothstep) + hold per shot,
// optional per-shot look-at, input lock (Input.ClearActions), skip action, a
// static Play()/Play(name) entry, a static OnCutsceneFinished event, and an
// optional razor_lint-safe letterbox overlay pair. LOCAL/visual-only.
// -----------------------------------------------------------------------------
public class CreateCutsceneDirectorHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "CutsceneDirector", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			// Directory is re-derived the same way PrepareCodeFile does, for the
			// optional letterbox sibling files.
			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code";

			bool lockInput = !p.TryGetProperty( "lockInput", out var li ) || li.ValueKind != JsonValueKind.False; // default true
			bool letterbox = p.TryGetProperty( "letterbox", out var lb ) && lb.ValueKind == JsonValueKind.True;   // default false
			string skipAction = CinematicsHelpers.SanitizeAction(
				p.TryGetProperty( "skipAction", out var sa ) ? sa.GetString() : null, "jump" );

			var code = BuildCutsceneCode( className, skipAction, lockInput );
			ScaffoldHelpers.WriteCode( fullPath, code );

			// Optional letterbox overlay (Razor PanelComponent + SCSS), razor_lint-safe.
			object letterboxFiles = null;
			if ( letterbox )
			{
				var lbClass = className + "Letterbox";
				var razorRel = $"{directory}/{lbClass}.razor";
				var scssRel  = $"{directory}/{lbClass}.razor.scss";

				if ( ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, lbClass + ".razor" ), out var razorPath, out var rErr )
				  && ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, lbClass + ".razor.scss" ), out var scssPath, out var sErr ) )
				{
					if ( !File.Exists( razorPath ) )
					{
						ScaffoldHelpers.WriteCode( razorPath, BuildLetterboxRazor( className, lbClass ) );
						ScaffoldHelpers.WriteCode( scssPath,  BuildLetterboxScss() );
						letterboxFiles = new { razor = razorRel, scss = scssRel, className = lbClass };
					}
					else
					{
						letterboxFiles = new { skipped = $"{razorRel} already exists -- letterbox not overwritten." };
					}
				}
			}

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				skipAction,
				lockInput,
				letterbox = letterboxFiles,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					$"Attach {className} to any GameObject (add_component_with_properties component=\"{className}\"). It drives Scene.Camera itself in OnPreRender -- it does NOT need to be on the camera.",
					"Author shots in the inspector: fill the parallel lists ShotPositions (Vector3), ShotAngles (pitch/yaw/roll), ShotHoldSeconds, ShotBlendSeconds. Optionally ShotLookAt (per-shot GameObject) overrides ShotAngles by aiming at that object.",
					$"Play from any game code: {className}.Play() (first director) or {className}.Play(\"name\") (matches CutsceneName). Subscribe {className}.OnCutsceneFinished, or gate logic on {className}.IsCutscenePlaying.",
					lockInput
						? "LockInput=true zeroes all input each frame via Input.ClearActions(); the SkipAction press is read first so it still ends the cutscene early."
						: "LockInput=false -- the player keeps control during the cutscene; press SkipAction to end early.",
					"Camera timing: OnPreRender runs after controllers/animation position the camera, so the takeover usually wins. If a player controller re-writes the camera in ITS OwnPreRender, disable that controller for the duration (re-enable on OnCutsceneFinished).",
					"LOCAL-only (each client runs its own view): trigger via an [Rpc.Broadcast] method so every client plays the cutscene.",
					letterbox
						? "Letterbox overlay generated: host the *Letterbox panel under a ScreenPanel (add_screen_panel) -- its bars show while IsCutscenePlaying."
						: "No letterbox (pass letterbox:true to also generate a black-bars overlay panel).",
					"Alternative: for keyframed/timeline cutscenes from a .movie asset, use the MovieMaker family (list_movies / add_movie_player / play_movie) instead."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_cutscene_director failed: {ex.Message}" } );
		}
	}

	static string BuildCutsceneCode( string className, string skipAction, bool lockInput )
	{
		string lockStr = lockInput ? "true" : "false";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- a hand-authored cutscene player. Needs NO .movie asset: you
/// author the shots directly in the inspector as parallel lists and this
/// component takes over the main camera to fly between them.
///
/// (For a keyframed/timeline cutscene authored in the editor's Movie Maker dock,
/// wire a Sandbox.MovieMaker.MoviePlayer instead -- that is the other path.)
///
/// Each shot i is: blend from the previous pose to (ShotPositions[i], ShotAngles[i])
/// over ShotBlendSeconds[i] (smoothstep-eased), then hold for ShotHoldSeconds[i].
/// If ShotLookAt[i] is set, the camera aims at that GameObject instead of using
/// ShotAngles[i]. Playback takes over Scene.Camera in OnPreRender while playing
/// and restores the camera exactly when it finishes.
///
/// Play from anywhere:  {className}.Play();  or  {className}.Play(""intro"");
/// LOCAL-only -- trigger inside an [Rpc.Broadcast] for all clients.
/// </summary>
public sealed class {className} : Component
{{
	// --- Shot list (parallel; one entry per shot, indexed by ShotPositions.Count) ---

	/// Camera world position for each shot.
	[Property] public List<Vector3> ShotPositions {{ get; set; }} = new List<Vector3>();

	/// Camera angles (pitch/yaw/roll) for each shot. Ignored for a shot whose
	/// ShotLookAt entry is set. Angles (not Rotation) because quaternions are
	/// painful to hand-edit in the inspector.
	[Property] public List<Angles> ShotAngles {{ get; set; }} = new List<Angles>();

	/// Seconds to hold on each shot after the blend completes.
	[Property] public List<float> ShotHoldSeconds {{ get; set; }} = new List<float>();

	/// Seconds to blend INTO each shot from the previous pose (0 = hard cut).
	[Property] public List<float> ShotBlendSeconds {{ get; set; }} = new List<float>();

	/// Optional per-shot look-at target -- when set, overrides ShotAngles for that shot.
	[Property] public List<GameObject> ShotLookAt {{ get; set; }} = new List<GameObject>();

	// --- Tunables ---

	/// Name used by the static {className}.Play(""name"") entry point.
	[Property] public string CutsceneName {{ get; set; }} = """";

	/// Freeze player input for the duration (via Input.ClearActions each frame).
	[Property] public bool LockInput {{ get; set; }} = {lockStr};

	/// Input action that ends the cutscene early. Empty = not skippable.
	[Property] public string SkipAction {{ get; set; }} = ""{skipAction}"";

	/// Fallback hold seconds when a shot has no ShotHoldSeconds entry.
	[Property] public float DefaultHoldSeconds {{ get; set; }} = 2f;

	/// Fallback blend seconds when a shot has no ShotBlendSeconds entry.
	[Property] public float DefaultBlendSeconds {{ get; set; }} = 1f;

	/// <summary>Fires (on every client that ran it) when a cutscene ends -- skipped or completed.</summary>
	public static event Action OnCutsceneFinished;

	/// <summary>True while ANY {className} is playing -- game code can gate on this (e.g. a HUD/letterbox).</summary>
	public static bool IsCutscenePlaying {{ get; private set; }}

	private static readonly List<{className}> _all = new List<{className}>();

	private bool _playing;
	private int _shotIndex;
	private TimeSince _sinceShot;
	private Vector3 _fromPos;
	private Rotation _fromRot = Rotation.Identity;

	// Pre-cutscene camera transform, restored exactly on finish.
	private Vector3 _restorePos;
	private Rotation _restoreRot = Rotation.Identity;
	private bool _hasRestore;

	protected override void OnEnabled()
	{{
		_all.Add( this );
	}}

	protected override void OnDisabled()
	{{
		_all.Remove( this );
		if ( _playing ) Finish();
	}}

	/// <summary>Play the first {className} in the scene.</summary>
	public static void Play()
	{{
		if ( _all.Count > 0 ) _all[0].StartCutscene();
		else Log.Warning( ""{className}.Play: no cutscene director in the scene."" );
	}}

	/// <summary>Play the {className} whose CutsceneName matches (case-insensitive).</summary>
	public static void Play( string name )
	{{
		foreach ( var d in _all )
		{{
			if ( string.Equals( d.CutsceneName, name, StringComparison.OrdinalIgnoreCase ) )
			{{
				d.StartCutscene();
				return;
			}}
		}}
		Log.Warning( ""{className}.Play: no cutscene director with that name."" );
	}}

	/// <summary>Begin playback on THIS instance from the first shot.</summary>
	public void StartCutscene()
	{{
		if ( ShotPositions == null || ShotPositions.Count == 0 )
		{{
			Log.Warning( ""{className}: no shots to play (fill ShotPositions)."" );
			return;
		}}

		var cam = Scene?.Camera;
		if ( cam == null )
		{{
			Log.Warning( ""{className}: no main camera (Scene.Camera) to take over."" );
			return;
		}}

		// Capture the pre-cutscene camera transform so we can restore it exactly.
		_restorePos = cam.WorldPosition;
		_restoreRot = cam.WorldRotation;
		_hasRestore = true;

		_fromPos = _restorePos;
		_fromRot = _restoreRot;
		_shotIndex = 0;
		_sinceShot = 0f;
		_playing = true;
		IsCutscenePlaying = true;
	}}

	protected override void OnUpdate()
	{{
		if ( !_playing ) return;

		// Read the skip BEFORE clearing input, so a locked cutscene is still skippable.
		bool skip = !string.IsNullOrEmpty( SkipAction ) && Input.Pressed( SkipAction );

		if ( LockInput ) Input.ClearActions();

		if ( skip )
		{{
			Finish();
			return;
		}}

		// Advance the timeline (blend, then hold). The guarded while-loop rolls
		// straight through any zero-length shots so a list of zeros can't wedge.
		int guard = 0;
		while ( _playing && (float)_sinceShot >= ShotDuration( _shotIndex ) && guard++ < 512 )
		{{
			_fromPos = ShotPos( _shotIndex );
			_fromRot = ShotRot( _shotIndex, _fromPos );
			_shotIndex++;
			_sinceShot = 0f;
			if ( _shotIndex >= ShotPositions.Count )
			{{
				Finish();
				return;
			}}
		}}
	}}

	protected override void OnPreRender()
	{{
		if ( !_playing ) return;

		var cam = Scene?.Camera;
		if ( cam == null ) return;

		ComputePose( out var pos, out var rot );
		cam.WorldPosition = pos;
		cam.WorldRotation = rot;
	}}

	private void Finish()
	{{
		_playing = false;
		IsCutscenePlaying = false;

		// Restore the exact pre-cutscene camera transform (the controller resumes
		// next frame; a static camera is left precisely as it was).
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

		if ( LockInput ) Input.ReleaseActions();
		OnCutsceneFinished?.Invoke();
	}}

	// --- shot helpers (all parallel-list-length safe) ---

	private Vector3 ShotPos( int i )
		=> ( ShotPositions != null && i >= 0 && i < ShotPositions.Count ) ? ShotPositions[i] : _fromPos;

	private Rotation ShotRot( int i, Vector3 atPos )
	{{
		if ( ShotLookAt != null && i >= 0 && i < ShotLookAt.Count && ShotLookAt[i] != null )
		{{
			var dir = ShotLookAt[i].WorldPosition - atPos;
			if ( dir.Length > 0.01f ) return Rotation.LookAt( dir.Normal );
		}}
		if ( ShotAngles != null && i >= 0 && i < ShotAngles.Count )
			return Rotation.From( ShotAngles[i] );
		return _fromRot;
	}}

	private float ShotHold( int i )
		=> ( ShotHoldSeconds != null && i >= 0 && i < ShotHoldSeconds.Count ) ? MathF.Max( 0f, ShotHoldSeconds[i] ) : MathF.Max( 0f, DefaultHoldSeconds );

	private float ShotBlend( int i )
		=> ( ShotBlendSeconds != null && i >= 0 && i < ShotBlendSeconds.Count ) ? MathF.Max( 0f, ShotBlendSeconds[i] ) : MathF.Max( 0f, DefaultBlendSeconds );

	private float ShotDuration( int i ) => ShotBlend( i ) + ShotHold( i );

	private void ComputePose( out Vector3 pos, out Rotation rot )
	{{
		int i = _shotIndex;
		var targetPos = ShotPos( i );
		float blend = ShotBlend( i );
		float raw = blend > 0.0001f ? MathX.Clamp( (float)_sinceShot / blend, 0f, 1f ) : 1f;
		float e = Ease( raw );
		pos = Vector3.Lerp( _fromPos, targetPos, e, true );
		var targetRot = ShotRot( i, pos );
		rot = Rotation.Slerp( _fromRot, targetRot, e, true );
	}}

	// Smoothstep -- MathX has no Ease/Smoothstep member on this SDK.
	private static float Ease( float t )
	{{
		t = MathX.Clamp( t, 0f, 1f );
		return t * t * ( 3f - 2f * t );
	}}
}}
";
	}

	static string BuildLetterboxRazor( string directorClass, string lbClass )
	{
		return $@"@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent;

@* {lbClass} -- cinematic black bars shown while {directorClass}.IsCutscenePlaying.
   Host this panel under a ScreenPanel (add_screen_panel). The bars slide in via a
   CSS transition on the .active class. *@

<div class=""cutscene-letterbox @ActiveClass"">
	<div class=""bar top""></div>
	<div class=""bar bottom""></div>
</div>

@code {{
	private string ActiveClass => {directorClass}.IsCutscenePlaying ? ""active"" : """";

	protected override int BuildHash() => System.HashCode.Combine( {directorClass}.IsCutscenePlaying );
}}
";
	}

	static string BuildLetterboxScss()
	{
		return @".cutscene-letterbox {
	position: absolute;
	top: 0;
	left: 0;
	width: 100%;
	height: 100%;
	pointer-events: none;
}

.cutscene-letterbox .bar {
	position: absolute;
	left: 0;
	width: 100%;
	height: 0;
	background-color: black;
	transition: height 0.4s ease;
}

.cutscene-letterbox .bar.top {
	top: 0;
}

.cutscene-letterbox .bar.bottom {
	bottom: 0;
}

.cutscene-letterbox.active .bar {
	height: 12%;
}
";
	}
}

// -----------------------------------------------------------------------------
// create_dialogue_system -- a sealed Component holding the dialogue state +
// data, paired with a Razor PanelComponent HUD that renders the current line
// with a typewriter reveal. Advance action completes the reveal, then advances;
// static StartDialogue(string[]) / instance Begin() entries; static
// OnDialogueFinished + OnLineShown(index, speaker) events. LOCAL/visual-only.
// -----------------------------------------------------------------------------
public class CreateDialogueSystemHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "DialogueSystem", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code";

			float charsPerSecond = p.TryGetProperty( "charsPerSecond", out var cps ) && cps.TryGetSingle( out var cpsf ) ? cpsf : 40f;
			if ( charsPerSecond < 1f ) charsPerSecond = 1f;
			string advanceAction = CinematicsHelpers.SanitizeAction(
				p.TryGetProperty( "advanceAction", out var aa ) ? aa.GetString() : null, "use" );

			var code = BuildDialogueCode( className, charsPerSecond, advanceAction, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			// The paired HUD panel (razor + scss), razor_lint-safe by construction.
			var panelClass = className + "Panel";
			var razorRel = $"{directory}/{panelClass}.razor";
			var scssRel  = $"{directory}/{panelClass}.razor.scss";

			object panelFiles = null;
			if ( ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, panelClass + ".razor" ), out var razorPath, out var rErr )
			  && ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, panelClass + ".razor.scss" ), out var scssPath, out var sErr ) )
			{
				if ( File.Exists( razorPath ) )
				{
					panelFiles = new { skipped = $"{razorRel} already exists -- panel not overwritten." };
				}
				else
				{
					ScaffoldHelpers.WriteCode( razorPath, BuildDialogueRazor( className, panelClass ) );
					ScaffoldHelpers.WriteCode( scssPath,  BuildDialogueScss() );
					panelFiles = new { razor = razorRel, scss = scssRel, className = panelClass };
				}
			}

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				charsPerSecond,
				advanceAction,
				panel = panelFiles,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} and {panelClass} into the game assembly.",
					$"Attach {className} to a GameObject (add_component_with_properties component=\"{className}\"). Fill its Lines list with \"Speaker: text\" strings in the inspector.",
					$"Attach {panelClass} under a ScreenPanel so the HUD renders: add_screen_panel, then add_component_with_properties component=\"{panelClass}\". It binds to {className}.Current automatically -- no wiring.",
					$"Start dialogue from any game code: {className}.StartDialogue(new[]{{\"Guide: Welcome.\", \"Guide: Press {advanceAction} to continue.\"}}) or set Lines then call instance Begin().",
					$"Press the AdvanceAction ('{advanceAction}') to complete the typewriter reveal, then again to advance; the panel folds VisibleText into BuildHash so it re-renders as characters appear.",
					$"Hooks: subscribe {className}.OnLineShown(index, speaker) for lipsync/audio per line (pair with add_lipsync), and {className}.OnDialogueFinished for the end. Pairs with create_interactable to trigger dialogue on use.",
					"LOCAL-only (per-client HUD): call StartDialogue inside an [Rpc.Broadcast] if every client should see the conversation."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_dialogue_system failed: {ex.Message}" } );
		}
	}

	static string BuildDialogueCode( string className, float charsPerSecond, string advanceAction, System.Globalization.CultureInfo ci )
	{
		string cps = charsPerSecond.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- NPC/story dialogue state + data. Pairs with the generated
/// {className}Panel Razor HUD, which reads {className}.Current and renders the
/// current line with a typewriter reveal.
///
/// Lines use the ""Speaker: text"" convention (the part before the first colon is
/// the speaker; the rest is the line). Press the AdvanceAction once to finish
/// the reveal instantly, again to move to the next line; the last line ends the
/// conversation and fires OnDialogueFinished.
///
/// Start from anywhere:  {className}.StartDialogue( new[] {{ ""Bob: Hi."", ""Bob: Bye."" }} );
/// LOCAL-only -- call inside an [Rpc.Broadcast] for all clients.
/// </summary>
public sealed class {className} : Component
{{
	/// Dialogue lines, ""Speaker: text"" each. Editable in the inspector.
	[Property] public List<string> Lines {{ get; set; }} = new List<string>();

	/// Typewriter reveal speed (characters per second).
	[Property] public float CharsPerSecond {{ get; set; }} = {cps};

	/// Input action that advances the dialogue (completes the reveal, then next line).
	[Property] public string AdvanceAction {{ get; set; }} = ""{advanceAction}"";

	/// <summary>Fires when the last line is dismissed.</summary>
	public static event Action OnDialogueFinished;

	/// <summary>Fires as each line begins -- (lineIndex, speaker). Hook for lipsync/audio.</summary>
	public static event Action<int, string> OnLineShown;

	/// <summary>The most-recently-enabled instance -- what the HUD and StartDialogue use.</summary>
	public static {className} Current {{ get; private set; }}

	public bool IsActive {{ get; private set; }}
	public int CurrentIndex {{ get; private set; }}
	public string CurrentSpeaker {{ get; private set; }} = """";

	private string _currentText = """";
	private TimeSince _sinceLine;
	private bool _forceComplete;

	protected override void OnEnabled()
	{{
		Current = this;
	}}

	protected override void OnDisabled()
	{{
		if ( Current == this ) Current = null;
	}}

	/// <summary>Set Lines and begin, using the active instance.</summary>
	public static void StartDialogue( string[] lines )
	{{
		if ( Current == null )
		{{
			Log.Warning( ""{className}.StartDialogue: no active {className} in the scene."" );
			return;
		}}
		Current.Lines = new List<string>( lines ?? new string[0] );
		Current.Begin();
	}}

	/// <summary>Begin this instance's dialogue from the first line.</summary>
	public void Begin()
	{{
		if ( Lines == null || Lines.Count == 0 )
		{{
			Log.Warning( ""{className}: no lines to show."" );
			return;
		}}
		IsActive = true;
		ShowLine( 0 );
	}}

	private void ShowLine( int index )
	{{
		CurrentIndex = index;
		ParseLine( Lines[index], out var speaker, out var text );
		CurrentSpeaker = speaker;
		_currentText = text;
		_sinceLine = 0f;
		_forceComplete = false;
		OnLineShown?.Invoke( index, speaker );
	}}

	protected override void OnUpdate()
	{{
		if ( !IsActive ) return;
		if ( string.IsNullOrEmpty( AdvanceAction ) ) return;
		if ( !Input.Pressed( AdvanceAction ) ) return;

		if ( !IsLineComplete )
		{{
			// Still typing -- first press snaps the whole line into view.
			_forceComplete = true;
			return;
		}}

		int next = CurrentIndex + 1;
		if ( next >= Lines.Count )
		{{
			End();
			return;
		}}
		ShowLine( next );
	}}

	private void End()
	{{
		IsActive = false;
		OnDialogueFinished?.Invoke();
	}}

	/// <summary>Characters revealed so far for the current line (the typewriter output).</summary>
	public string VisibleText
	{{
		get
		{{
			if ( !IsActive || string.IsNullOrEmpty( _currentText ) ) return """";
			if ( _forceComplete ) return _currentText;
			int n = (int)( (float)_sinceLine * CharsPerSecond );
			if ( n < 0 ) n = 0;
			if ( n >= _currentText.Length ) return _currentText;
			return _currentText.Substring( 0, n );
		}}
	}}

	/// <summary>True once the whole current line is visible.</summary>
	public bool IsLineComplete
		=> _forceComplete || string.IsNullOrEmpty( _currentText ) || ( (float)_sinceLine * CharsPerSecond ) >= _currentText.Length;

	private static void ParseLine( string raw, out string speaker, out string text )
	{{
		raw = raw ?? """";
		int idx = raw.IndexOf( ':' );
		if ( idx > 0 )
		{{
			speaker = raw.Substring( 0, idx ).Trim();
			text = raw.Substring( idx + 1 ).Trim();
		}}
		else
		{{
			speaker = """";
			text = raw.Trim();
		}}
	}}
}}
";
	}

	static string BuildDialogueRazor( string dialogueClass, string panelClass )
	{
		return $@"@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent;

@* {panelClass} -- typewriter dialogue HUD bound to {dialogueClass}.Current.
   Host under a ScreenPanel (add_screen_panel). Renders nothing when no dialogue
   is active. VisibleText is folded into BuildHash so the panel re-renders as the
   typewriter advances. *@

@if ( Dlg != null && Dlg.IsActive )
{{
	<div class=""dialogue-box"">
		<div class=""speaker"">@Dlg.CurrentSpeaker</div>
		<div class=""line"">@Dlg.VisibleText</div>
		<div class=""advance-hint"">@HintText</div>
	</div>
}}

@code {{
	private {dialogueClass} Dlg => {dialogueClass}.Current;

	private string HintText => ( Dlg != null && Dlg.IsLineComplete ) ? ""Continue"" : """";

	protected override int BuildHash()
	{{
		if ( Dlg == null ) return 0;
		return System.HashCode.Combine( Dlg.IsActive, Dlg.CurrentSpeaker, Dlg.VisibleText );
	}}
}}
";
	}

	static string BuildDialogueScss()
	{
		return @".dialogue-box {
	position: absolute;
	bottom: 60px;
	left: 50%;
	transform: translateX(-50%);
	display: flex;
	flex-direction: column;
	width: 640px;
	max-width: 80%;
	padding: 16px 20px;
	background-color: rgba(0,0,0,0.8);
	border-radius: 6px;
	border: 1px solid rgba(255,255,255,0.15);
}

.dialogue-box .speaker {
	font-size: 18px;
	font-weight: bold;
	color: #ffd873;
	margin-bottom: 6px;
}

.dialogue-box .line {
	font-size: 20px;
	color: white;
	white-space: pre-wrap;
}

.dialogue-box .advance-hint {
	align-self: flex-end;
	margin-top: 8px;
	font-size: 14px;
	color: rgba(255,255,255,0.5);
}
";
	}
}

/// <summary>
/// Shared helpers for the cinematics handlers.
/// </summary>
internal static class CinematicsHelpers
{
	/// <summary>
	/// Keep an input-action name to a safe token (letters/digits/underscore) so it
	/// can't break the generated string literal; fall back to a default if empty.
	/// </summary>
	public static string SanitizeAction( string s, string fallback )
	{
		if ( string.IsNullOrWhiteSpace( s ) ) return fallback;
		var sb = new StringBuilder( s.Length );
		foreach ( var ch in s )
		{
			if ( char.IsLetterOrDigit( ch ) || ch == '_' ) sb.Append( ch );
		}
		var cleaned = sb.ToString();
		return string.IsNullOrEmpty( cleaned ) ? fallback : cleaned;
	}
}
