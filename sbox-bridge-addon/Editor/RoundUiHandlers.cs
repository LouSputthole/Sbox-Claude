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

// =============================================================================
//  Round-flow & UI pack (v2.0.x) -- three tools:
//
//    create_round_timer_hud   Razor screen-panel HUD showing the active round /
//                             phase time remaining; binds AT RUNTIME (TypeLibrary
//                             property reflection) to whichever shipped round
//                             machine exists in the scene -- no code coupling.
//    scaffold_map_vote_flow   end-of-round map vote: host-collected [Rpc.Host]
//                             votes, [Sync(FromHost)] NetList tallies, countdown,
//                             deterministic tie-break, winner -> Scene.LoadFromFile,
//                             plus a Razor vote panel.
//    add_panel_buildhash      NOT a codegen scaffold: a file-EDIT tool that patches
//                             an existing .razor to add a BuildHash() override
//                             folding the state declared in its @code block.
//                             razor_lint flags missing BuildHash; this fixes it.
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs /
//  UiFeedbackHandlers.cs, so it reuses the shared statics on ClaudeBridge
//  (TryResolveProjectPath, SanitizeIdentifier), ScaffoldHelpers (WriteCode /
//  Utf8NoBom) and UiFeedbackHelpers (Resolve). Handler code here is UNSANDBOXED
//  editor code (System.* fine).
//
//  The strings these handlers WRITE TO DISK are SANDBOXED game code (.cs) and
//  Razor (.razor / .razor.scss) and obey the sandbox + razor_lint rules:
//    - sealed Components; [Sync(SyncFlags.FromHost)] host-auth; TimeUntil syncable;
//      System.Math/MathF/MathX fine; Array.Clone() blocked (not used);
//      fully-qualified System.Collections.Generic.List/Dictionary;
//      Connection null-check only (Connection has NO IsValid on this SDK).
//    - Razor: PanelComponent overrides BuildHash, NO switch-expressions in @code,
//      ASCII only in @code, SCSS roots are class selectors.
//    - InvariantCulture float formatting with an 'f' suffix.
//
//  Live-verified API surface before codegen (describe_type / search_types):
//    - Sandbox.Scene: LoadFromFile(String) -> Boolean (instance) and
//      Load(SceneLoadOptions) -> Boolean. SceneLoadOptions: ShowLoadingScreen,
//      IsAdditive, DeleteEverything, SetScene(String).
//    - Sandbox.NetList`1 / Sandbox.NetDictionary`2 exist ([Sync]-able collections).
//    - Sandbox.TimeUntil: Relative/Passed/Fraction (Single, read-only).
//    - Sandbox.PropertyDescription: GetValue(Object) -> Object, PropertyType, Name
//      (the reflection surface the generated HUD uses through TypeLibrary).
//    - [Rpc.Host] / Rpc.Caller / Networking.IsActive / Connection.Local /
//      (ulong)Connection.SteamId -- same shape the v1.20.0 NetPrimitives pack
//      compile-verified live.
//
//  Shipped-generator member names the HUD binds against (read from the generators,
//  not guessed): create_round_phase_machine emits [Sync] TimeUntil PhaseTimer +
//  Phase CurrentPhase; create_round_state_machine emits a manager with int
//  StateIndex + Current whose active state carries [Sync] TimeUntil TimeLeft +
//  string Identifier.
//
//  Register(...) lines + the _sceneMutatingCommands additions live in
//  MyEditorMenu.cs (orchestrator integrates) to keep the files decoupled.
// =============================================================================

// -----------------------------------------------------------------------------
// create_round_timer_hud -- a Razor screen-panel HUD (.razor + .razor.scss) that
// shows the active round/phase time remaining in mm:ss. It binds at RUNTIME via
// TypeLibrary property reflection to whichever shipped round machine exists
// (same-GameObject components first, then the whole scene), so it works with
// create_round_phase_machine AND create_round_state_machine output without
// referencing either class. BuildHash folds the WHOLE second remaining ->
// re-renders at 1 Hz, not every frame.
// -----------------------------------------------------------------------------
public class CreateRoundTimerHudHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			var name      = p.TryGetProperty( "name",      out var n ) && !string.IsNullOrWhiteSpace( n.GetString() ) ? n.GetString() : "RoundTimerHud";
			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code/UI";

			float lowTime = p.TryGetProperty( "lowTimeSeconds", out var lv ) && lv.TryGetSingle( out var lf ) ? lf : 10f;
			if ( lowTime < 0f ) lowTime = 0f;

			var className = ClaudeBridge.SanitizeIdentifier( name.EndsWith( ".razor" ) ? Path.GetFileNameWithoutExtension( name ) : name );
			var razorFile = className + ".razor";
			var scssFile  = className + ".razor.scss";

			if ( !UiFeedbackHelpers.Resolve( directory, razorFile, out var razorPath, out var relRazor, out var rerr ) )
				return Task.FromResult<object>( rerr );
			if ( !UiFeedbackHelpers.Resolve( directory, scssFile, out var scssPath, out var relScss, out var serr ) )
				return Task.FromResult<object>( serr );

			ScaffoldHelpers.WriteCode( razorPath, BuildRazor( className, lowTime.ToString( ci ) + "f" ) );
			ScaffoldHelpers.WriteCode( scssPath, BuildScss() );

			return Task.FromResult<object>( new
			{
				created = true,
				razorPath = relRazor,
				scssPath = relScss,
				className,
				lowTimeSeconds = lowTime,
				note = "Binds by TypeLibrary reflection to the FIRST component it finds shaped like a shipped round machine "
				     + "(PhaseTimer:TimeUntil from create_round_phase_machine, or StateIndex+Current from create_round_state_machine). "
				     + "Shows --:-- until one exists. Renders nothing without a ScreenPanel host.",
				nextSteps = new[]
				{
					"trigger_hotload to compile the panel into the game assembly.",
					"If you have no round machine yet: create_round_phase_machine or create_round_state_machine, then hotload again.",
					"add_screen_panel to a HUD GameObject, then add this panel to the SAME object: add_component_with_properties (component=\"" + className + "\").",
					"Tune LowTimeSeconds with set_property -- at/below it the time gets the 'low' class (red by default; style it in the scss).",
					"Verify in play mode with capture_view (renderUI=true): the label shows the phase/state name, the clock counts down once per second."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_round_timer_hud failed: {ex.Message}" } );
		}
	}

	static string BuildRazor( string className, string lowTime )
	{
		// NOTE: all {{ }} inside the $@"" are C# string-escape doubled braces.
		// The generated .razor uses single { } for Razor/C# expressions.
		return $@"@using Sandbox;
@using Sandbox.UI;
@using System.Linq;
@inherits PanelComponent;

@* {className} -- screen-space round/phase countdown (mm:ss).
   Binds AT RUNTIME to whichever shipped round machine exists -- no code coupling:
     - create_round_phase_machine output: a component with a [Sync] TimeUntil
       PhaseTimer (+ CurrentPhase enum for the label), or
     - create_round_state_machine output: a manager with StateIndex + Current,
       whose active state carries a [Sync] TimeUntil TimeLeft (+ Identifier).
   Discovery uses TypeLibrary property reflection: same-GameObject components
   first, then the whole scene; re-scans every 2 seconds while unbound, so a
   machine added later is picked up automatically.
   BuildHash folds the WHOLE second remaining, so the panel re-renders at 1 Hz,
   not every frame. Host under a ScreenPanel (add_screen_panel). *@

<div class=""round-timer @BoundClass"">
	<div class=""plate"">
		<div class=""label"">@_label</div>
		<div class=""time @LowClass"">@_timeText</div>
	</div>
</div>

@code {{
	/// Remaining-seconds threshold at/below which the ""low"" warning class is
	/// applied to the clock (0 disables the warning entirely).
	[Property] public float LowTimeSeconds {{ get; set; }} = {lowTime};

	private Component _machine;
	private PropertyDescription _timerProp;     // phase machine: PhaseTimer (TimeUntil)
	private PropertyDescription _labelSrcProp;  // phase machine: CurrentPhase
	private PropertyDescription _currentProp;   // state machine: Current (active state component)
	private System.Type _stateType;
	private PropertyDescription _stateTimerProp;
	private PropertyDescription _stateIdentProp;

	private bool _bound;
	private int _wholeSeconds = -1;
	private string _label = """";
	private string _timeText = ""--:--"";
	private bool _low;
	private RealTimeSince _sinceScan = 999f;

	private string BoundClass => _bound ? """" : ""unbound"";
	private string LowClass => _low ? ""low"" : """";

	protected override void OnUpdate()
	{{
		if ( _machine == null || !_machine.IsValid() )
		{{
			_bound = false;
			if ( _sinceScan > 2f )
			{{
				_sinceScan = 0f;
				TryBind();
			}}
		}}
		Sample();
	}}

	private void TryBind()
	{{
		_machine = null; _timerProp = null; _labelSrcProp = null; _currentProp = null;
		_stateType = null; _stateTimerProp = null; _stateIdentProp = null;
		if ( Scene == null ) return;

		// Same-GameObject components first, then the whole scene. First match wins.
		var candidates = Components.GetAll<Component>().Concat( Scene.GetAllComponents<Component>() );
		foreach ( var c in candidates )
		{{
			if ( c == null || c == this ) continue;
			var td = TypeLibrary.GetType( c.GetType() );
			if ( td == null ) continue;

			// Shape 1: create_round_phase_machine -- [Sync] TimeUntil PhaseTimer (+ CurrentPhase).
			var timer = td.Properties.FirstOrDefault( x => x.Name == ""PhaseTimer"" && x.PropertyType == typeof( TimeUntil ) );
			if ( timer != null )
			{{
				_machine = c;
				_timerProp = timer;
				_labelSrcProp = td.Properties.FirstOrDefault( x => x.Name == ""CurrentPhase"" );
				_bound = true;
				return;
			}}

			// Shape 2: create_round_state_machine -- a manager with StateIndex + Current.
			var current = td.Properties.FirstOrDefault( x => x.Name == ""Current"" );
			var stateIndex = td.Properties.FirstOrDefault( x => x.Name == ""StateIndex"" );
			if ( current != null && stateIndex != null )
			{{
				_machine = c;
				_currentProp = current;
				_bound = true;
				return;
			}}
		}}
	}}

	private void Sample()
	{{
		float seconds = -1f;
		string label = """";

		if ( _bound && _machine != null && _machine.IsValid() )
		{{
			if ( _timerProp != null )
			{{
				var v = _timerProp.GetValue( _machine );
				if ( v is TimeUntil tu ) seconds = tu.Relative;
				if ( _labelSrcProp != null ) label = _labelSrcProp.GetValue( _machine )?.ToString() ?? """";
			}}
			else if ( _currentProp != null )
			{{
				var state = _currentProp.GetValue( _machine ) as Component;
				if ( state != null && state.IsValid() )
				{{
					// Cache the state type's reflection lookups -- states swap per phase.
					var t = state.GetType();
					if ( _stateType != t )
					{{
						_stateType = t;
						var std = TypeLibrary.GetType( t );
						_stateTimerProp = std != null ? std.Properties.FirstOrDefault( x => x.Name == ""TimeLeft"" && x.PropertyType == typeof( TimeUntil ) ) : null;
						_stateIdentProp = std != null ? std.Properties.FirstOrDefault( x => x.Name == ""Identifier"" ) : null;
					}}
					if ( _stateTimerProp != null )
					{{
						var v = _stateTimerProp.GetValue( state );
						if ( v is TimeUntil tu ) seconds = tu.Relative;
					}}
					if ( _stateIdentProp != null ) label = _stateIdentProp.GetValue( state )?.ToString() ?? """";
				}}
			}}
		}}

		if ( seconds < 0f )
		{{
			_wholeSeconds = -1;
			_timeText = ""--:--"";
			_low = false;
		}}
		else
		{{
			int total = (int) System.MathF.Ceiling( seconds );
			if ( total < 0 ) total = 0;
			_wholeSeconds = total;
			_timeText = ( total / 60 ).ToString() + "":"" + ( total % 60 ).ToString( ""00"" );
			_low = LowTimeSeconds > 0f && seconds <= LowTimeSeconds;
		}}
		_label = label;
	}}

	// Fold the WHOLE second (not the raw float) -- the panel re-renders at 1 Hz.
	protected override int BuildHash() => System.HashCode.Combine( _bound, _wholeSeconds, _label, _low );
}}
";
	}

	static string BuildScss()
	{
		return @".round-timer {
	position: absolute;
	top: 24px;
	left: 0;
	right: 0;
	flex-direction: column;
	align-items: center;
	pointer-events: none;
	transition: opacity 0.25s;
}

.round-timer.unbound {
	opacity: 0;
}

.plate {
	display: flex;
	flex-direction: column;
	align-items: center;
	background-color: rgba(0,0,0,0.6);
	border-radius: 10px;
	padding: 8px 24px;
}

.label {
	font-size: 16px;
	color: rgba(255,255,255,0.7);
	text-transform: uppercase;
	letter-spacing: 2px;
}

.time {
	font-size: 42px;
	font-weight: bold;
	color: white;
}

.time.low {
	color: #ff5040;
}
";
	}
}

// -----------------------------------------------------------------------------
// scaffold_map_vote_flow -- end-of-round map vote. Three files:
//   {name}.cs            sealed host-authoritative vote controller
//   {name}Panel.razor    vote UI (one button per map, live tallies, countdown)
//   {name}Panel.razor.scss
// Votes travel client -> host via [Rpc.Host] with the caller re-resolved host-
// side (Rpc.Caller null-check -- Connection has NO IsValid); tallies replicate
// via [Sync(FromHost)] NetList<int>; ties break deterministically via one LCG
// scramble of a time seed (no System.Random); the winner loads via
// Scene.LoadFromFile (verified live on this SDK).
// -----------------------------------------------------------------------------
public class ScaffoldMapVoteFlowHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			var name      = p.TryGetProperty( "name",      out var n ) && !string.IsNullOrWhiteSpace( n.GetString() ) ? n.GetString() : "MapVote";
			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code";

			var maps = new List<string>();
			if ( p.TryGetProperty( "maps", out var mv ) && mv.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in mv.EnumerateArray() )
				{
					var s = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
					if ( string.IsNullOrWhiteSpace( s ) ) continue;
					// Escape any embedded quotes/backslashes so they can't break the generated literal.
					var clean = s.Trim().Replace( "\\", "/" ).Replace( "\"", "" );
					if ( !maps.Contains( clean ) ) maps.Add( clean );
				}
			}

			float voteDur = p.TryGetProperty( "voteDurationSeconds", out var vv ) && vv.TryGetSingle( out var vf ) ? vf : 20f;
			if ( voteDur < 3f ) voteDur = 3f;
			float linger = p.TryGetProperty( "resultLingerSeconds", out var rv ) && rv.TryGetSingle( out var rf ) ? rf : 4f;
			if ( linger < 0f ) linger = 0f;
			bool autoStart = p.TryGetProperty( "autoStart", out var av ) && av.ValueKind == JsonValueKind.True;

			var className = ClaudeBridge.SanitizeIdentifier( name.EndsWith( ".cs" ) ? Path.GetFileNameWithoutExtension( name ) : name );
			var panelClass = className + "Panel";

			var csFile    = className + ".cs";
			var razorFile = panelClass + ".razor";
			var scssFile  = panelClass + ".razor.scss";

			if ( !UiFeedbackHelpers.Resolve( directory, csFile, out var csPath, out var relCs, out var cerr ) )
				return Task.FromResult<object>( cerr );
			if ( !UiFeedbackHelpers.Resolve( directory, razorFile, out var razorPath, out var relRazor, out var rerr ) )
				return Task.FromResult<object>( rerr );
			if ( !UiFeedbackHelpers.Resolve( directory, scssFile, out var scssPath, out var relScss, out var serr ) )
				return Task.FromResult<object>( serr );

			string mapsInit = maps.Count == 0
				? "new System.Collections.Generic.List<string>()"
				: "new System.Collections.Generic.List<string>() { " + string.Join( ", ", maps.Select( m => "\"" + m + "\"" ) ) + " }";

			ScaffoldHelpers.WriteCode( csPath, BuildController( className, mapsInit, voteDur.ToString( ci ) + "f", linger.ToString( ci ) + "f", autoStart ? "true" : "false" ) );
			ScaffoldHelpers.WriteCode( razorPath, BuildPanelRazor( panelClass, className ) );
			ScaffoldHelpers.WriteCode( scssPath, BuildPanelScss() );

			return Task.FromResult<object>( new
			{
				created = true,
				componentPath = relCs,
				razorPath = relRazor,
				scssPath = relScss,
				className,
				panelClassName = panelClass,
				maps = maps.ToArray(),
				voteDurationSeconds = voteDur,
				resultLingerSeconds = linger,
				autoStart,
				note = "The controller must live on a NETWORK-SPAWNED object in multiplayer or the [Sync] tallies/countdown never replicate. "
				     + "The winning scene load runs on the HOST via Scene.LoadFromFile (API verified live); clients follow through the scene "
				     + "networking layer -- verify the actual client hand-off in a real multi-client session.",
				nextSteps = new[]
				{
					"trigger_hotload to compile " + className + " + " + panelClass + " into the game assembly.",
					"Attach " + className + " to a persistent, network-spawned GameObject (your game-manager object): add_component_with_properties (component=\"" + className + "\").",
					( maps.Count == 0 ? "Fill the MapScenes list in the inspector (e.g. \"scenes/arena.scene\") -- it was generated EMPTY and StartVote() refuses with a warning until it has entries." : "MapScenes was pre-filled with " + maps.Count + " entries -- confirm the .scene paths exist (list_scenes)." ),
					"Show the UI: add_screen_panel, then add " + panelClass + " to the same object. It finds the controller automatically.",
					"Open the vote from your round flow: on the host call GetComponent<" + className + ">()?.StartVote() -- e.g. from a post-round state's Begin() or " + className + ".OnVoteFinished to chain systems.",
					"Verify: start_play, StartVote() via invoke_method, click a map button, watch the tally rise and the winner load after the linger."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"scaffold_map_vote_flow failed: {ex.Message}" } );
		}
	}

	static string BuildController( string className, string mapsInit, string voteDur, string linger, string autoStart )
	{
		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- a host-authoritative end-of-round map vote.
///
/// Flow: the host calls StartVote() (usually from a post-round phase/state) ->
/// every client's {className}Panel shows one button per MapScenes entry ->
/// clicks route client -> host via [Rpc.Host] SubmitVote (the caller is
/// re-resolved HOST-SIDE from Rpc.Caller; client args are re-validated; re-votes
/// overwrite) -> live tallies replicate to everyone via [Sync(FromHost)]
/// NetList<int> -> when the [Sync] countdown expires the host picks the winner
/// (most votes; ties break deterministically via one LCG scramble of a time
/// seed -- no System.Random) -> after ResultLingerSeconds the HOST loads the
/// winning scene with Scene.LoadFromFile.
///
/// MULTIPLAYER REQUIREMENT: this component must live on a NETWORK-SPAWNED object
/// (e.g. via a NetworkHelper) or the [Sync] state never replicates. Single-player
/// safe: IsProxy is false, the RPC runs locally, Connection.Local is the voter.
///
/// Usage:
///   GetComponent<{className}>()?.StartVote();                  // host-only
///   {className}.OnVoteFinished += scene => Log.Info( scene );  // fires everywhere
/// </summary>
public sealed class {className} : Component
{{
	/// Scene files players vote between, e.g. ""scenes/arena.scene"". Edit in the inspector.
	[Property] public System.Collections.Generic.List<string> MapScenes {{ get; set; }} = {mapsInit};

	/// Seconds the vote stays open once StartVote() is called.
	[Property] public float VoteDurationSeconds {{ get; set; }} = {voteDur};

	/// Seconds the winner banner lingers before the host loads the scene.
	[Property] public float ResultLingerSeconds {{ get; set; }} = {linger};

	/// Start the vote automatically on spawn (host only). Usually left off --
	/// call StartVote() from your round machine's post-round state instead.
	[Property] public bool AutoStart {{ get; set; }} = {autoStart};

	[Sync( SyncFlags.FromHost )] public bool VoteActive {{ get; set; }}
	[Sync( SyncFlags.FromHost )] public TimeUntil VoteEndsIn {{ get; set; }}
	[Sync( SyncFlags.FromHost )] public int WinnerIndex {{ get; set; }} = -1;

	/// Live vote counts, index-aligned with MapScenes. Host writes, everyone reads.
	[Sync( SyncFlags.FromHost )] public NetList<int> Tallies {{ get; set; }} = new NetList<int>();

	/// Fires on EVERY machine (host + proxies) when the winner lands. Arg = the
	/// winning scene file. Hook goodbye banners / stat saves here.
	public static Action<string> OnVoteFinished {{ get; set; }}

	// Host-only ballots (SteamId -> map index); re-votes overwrite. Never synced.
	private System.Collections.Generic.Dictionary<ulong, int> _ballots = new System.Collections.Generic.Dictionary<ulong, int>();
	private int _lastAnnouncedWinner = -1;
	private bool _loadQueued;
	private TimeUntil _loadAt;

	protected override void OnStart()
	{{
		if ( !IsProxy && AutoStart ) StartVote();
	}}

	/// <summary>Host-only: open the vote. Resets ballots + tallies and arms the countdown.</summary>
	public void StartVote()
	{{
		if ( IsProxy || VoteActive ) return;
		if ( MapScenes == null || MapScenes.Count == 0 )
		{{
			Log.Warning( ""{className}: no MapScenes configured -- nothing to vote on."" );
			return;
		}}
		_ballots.Clear();
		Tallies.Clear();
		for ( int i = 0; i < MapScenes.Count; i++ ) Tallies.Add( 0 );
		WinnerIndex = -1;
		_lastAnnouncedWinner = -1;
		_loadQueued = false;
		VoteEndsIn = VoteDurationSeconds;
		VoteActive = true;
	}}

	/// <summary>Client entry point -- call from UI. Routes to the host; re-votes overwrite.</summary>
	public void CastVote( int mapIndex )
	{{
		SubmitVote( mapIndex );
	}}

	// Public so the RPC source generator is happy. The re-validation INSIDE the
	// body is what protects it: any client can invoke an [Rpc.Host] with forged args.
	[Rpc.Host]
	public void SubmitVote( int mapIndex )
	{{
		// Re-resolve the caller HOST-SIDE -- never trust client args for identity.
		// Connection has NO IsValid on this SDK: null-check only.
		var caller = Networking.IsActive ? Rpc.Caller : Connection.Local;
		if ( caller == null ) caller = Connection.Local;
		if ( caller == null ) return;

		if ( !VoteActive ) return;
		if ( MapScenes == null || mapIndex < 0 || mapIndex >= MapScenes.Count ) return;   // re-validate the client-sent index

		_ballots[(ulong) caller.SteamId] = mapIndex;
		RebuildTallies();
	}}

	protected override void OnUpdate()
	{{
		// Winner announce -- change-detected so it fires exactly once on every
		// machine (host + proxies) when the [Sync] WinnerIndex lands.
		if ( WinnerIndex >= 0 && WinnerIndex != _lastAnnouncedWinner )
		{{
			_lastAnnouncedWinner = WinnerIndex;
			var sceneFile = ( MapScenes != null && WinnerIndex < MapScenes.Count ) ? MapScenes[WinnerIndex] : """";
			OnVoteFinished?.Invoke( sceneFile );
		}}

		if ( IsProxy ) return;

		if ( VoteActive && VoteEndsIn <= 0f ) FinishVote();

		if ( _loadQueued && _loadAt <= 0f )
		{{
			_loadQueued = false;
			if ( MapScenes != null && WinnerIndex >= 0 && WinnerIndex < MapScenes.Count )
			{{
				var file = MapScenes[WinnerIndex];
				if ( !string.IsNullOrEmpty( file ) )
				{{
					// Verified live on this SDK: Scene.LoadFromFile( string ) -> bool.
					// The HOST performs the load; clients follow via the scene
					// networking layer. For a loading screen or additive load use
					// SceneLoadOptions (ShowLoadingScreen / SetScene) with
					// Scene.Load( options ) instead.
					Scene.LoadFromFile( file );
				}}
			}}
		}}
	}}

	private void FinishVote()
	{{
		VoteActive = false;
		WinnerIndex = PickWinner();
		_loadQueued = true;
		_loadAt = ResultLingerSeconds;
	}}

	private void RebuildTallies()
	{{
		if ( MapScenes == null ) return;
		while ( Tallies.Count < MapScenes.Count ) Tallies.Add( 0 );
		for ( int i = 0; i < Tallies.Count; i++ ) Tallies[i] = 0;
		foreach ( var kv in _ballots )
		{{
			if ( kv.Value >= 0 && kv.Value < Tallies.Count ) Tallies[kv.Value] = Tallies[kv.Value] + 1;
		}}
	}}

	private int PickWinner()
	{{
		int best = 0; int bestVotes = -1; int tieCount = 0;
		for ( int i = 0; i < Tallies.Count; i++ )
		{{
			if ( Tallies[i] > bestVotes ) {{ bestVotes = Tallies[i]; best = i; tieCount = 1; }}
			else if ( Tallies[i] == bestVotes ) tieCount++;
		}}
		if ( tieCount <= 1 ) return best;

		// Deterministic tie-break WITHOUT System.Random (whitelist caution): one
		// LCG scramble of a time seed picks among the tied indices. Host-only.
		var tied = new System.Collections.Generic.List<int>();
		for ( int i = 0; i < Tallies.Count; i++ )
		{{
			if ( Tallies[i] == bestVotes ) tied.Add( i );
		}}
		int seed = (int) ( Time.Now * 1000f );
		unchecked {{ seed = seed * 1103515245 + 12345; }}
		int pick = (int) ( (uint) seed % (uint) tied.Count );
		return tied[pick];
	}}
}}
";
	}

	static string BuildPanelRazor( string panelClass, string controllerClass )
	{
		// NOTE: all {{ }} inside the $@"" are C# string-escape doubled braces.
		return $@"@using Sandbox;
@using Sandbox.UI;
@using System.Linq;
@inherits PanelComponent;

@* {panelClass} -- vote UI for {controllerClass}.
   Finds the controller in the scene automatically (re-scans every second until
   found). One button per MapScenes entry with a live tally; your own pick gets
   the ""mine"" class. Renders the winner banner during the result linger, and
   nothing at all while no vote is running. Host under a ScreenPanel
   (add_screen_panel); buttons need the mouse released to the UI in play mode.
   BuildHash folds whole seconds + tallies, so it re-renders on real changes. *@

@if ( Ctrl == null )
{{
	<div class=""map-vote"">
		<div class=""panel-box"">
			<div class=""title"">Map vote</div>
			<div class=""hint"">Add a {controllerClass} component to the scene.</div>
		</div>
	</div>
}}
else if ( Ctrl.VoteActive )
{{
	<div class=""map-vote"">
		<div class=""panel-box"">
			<div class=""title"">Vote for the next map</div>
			<div class=""timer"">@TimeText</div>
			<div class=""options"">
				@for ( int i = 0; i < Ctrl.MapScenes.Count; i++ )
				{{
					int idx = i;
					<button class=""option @( _myVote == idx ? ""mine"" : """" )"" onclick=@( () => Cast( idx ) )>
						<span class=""mapname"">@DisplayName( idx )</span>
						<span class=""tally"">@TallyOf( idx )</span>
					</button>
				}}
			</div>
		</div>
	</div>
}}
else if ( Ctrl.WinnerIndex >= 0 )
{{
	<div class=""map-vote"">
		<div class=""panel-box"">
			<div class=""title"">Next map</div>
			<div class=""winner"">@DisplayName( Ctrl.WinnerIndex )</div>
		</div>
	</div>
}}

@code {{
	private {controllerClass} _controller;
	private int _myVote = -1;
	private RealTimeSince _sinceScan = 999f;

	private {controllerClass} Ctrl
	{{
		get
		{{
			if ( ( _controller == null || !_controller.IsValid() ) && _sinceScan > 1f )
			{{
				_sinceScan = 0f;
				_controller = Scene != null ? Scene.GetAllComponents<{controllerClass}>().FirstOrDefault() : null;
			}}
			return _controller;
		}}
	}}

	private string TimeText
	{{
		get
		{{
			var c = Ctrl;
			if ( c == null || !c.VoteActive ) return """";
			int total = (int) System.MathF.Ceiling( c.VoteEndsIn.Relative );
			if ( total < 0 ) total = 0;
			return ( total / 60 ).ToString() + "":"" + ( total % 60 ).ToString( ""00"" );
		}}
	}}

	private void Cast( int idx )
	{{
		var c = Ctrl;
		if ( c == null || !c.VoteActive ) return;
		_myVote = idx;
		c.CastVote( idx );   // [Rpc.Host] under the hood -- routes to the host
	}}

	private string DisplayName( int idx )
	{{
		var c = Ctrl;
		if ( c == null || c.MapScenes == null || idx < 0 || idx >= c.MapScenes.Count ) return ""?"";
		var s = c.MapScenes[idx] ?? """";
		int slash = s.LastIndexOf( '/' );
		if ( slash >= 0 ) s = s.Substring( slash + 1 );
		if ( s.EndsWith( "".scene"" ) ) s = s.Substring( 0, s.Length - 6 );
		return s;
	}}

	private int TallyOf( int idx )
	{{
		var c = Ctrl;
		if ( c == null || c.Tallies == null || idx < 0 || idx >= c.Tallies.Count ) return 0;
		return c.Tallies[idx];
	}}

	// Whole seconds only (1 Hz), plus every tally + the vote lifecycle flags.
	protected override int BuildHash()
	{{
		var c = Ctrl;
		int h = System.HashCode.Combine( c != null, _myVote );
		if ( c != null )
		{{
			int secs = c.VoteActive ? (int) c.VoteEndsIn.Relative : -1;
			h = System.HashCode.Combine( h, c.VoteActive, c.WinnerIndex, secs );
			if ( c.Tallies != null )
			{{
				foreach ( var t in c.Tallies ) h = System.HashCode.Combine( h, t );
			}}
		}}
		return h;
	}}
}}
";
	}

	static string BuildPanelScss()
	{
		return @".map-vote {
	position: absolute;
	top: 0;
	left: 0;
	right: 0;
	bottom: 0;
	justify-content: center;
	align-items: center;
	flex-direction: column;
}

.panel-box {
	display: flex;
	flex-direction: column;
	align-items: center;
	background-color: rgba(10,10,16,0.88);
	border: 2px solid rgba(255,255,255,0.12);
	border-radius: 12px;
	padding: 28px 36px;
	min-width: 420px;
}

.title {
	font-size: 26px;
	font-weight: bold;
	color: white;
	text-transform: uppercase;
	letter-spacing: 2px;
	margin-bottom: 6px;
}

.timer {
	font-size: 38px;
	font-weight: bold;
	color: #ffe080;
	margin-bottom: 14px;
}

.options {
	display: flex;
	flex-direction: column;
	width: 100%;
}

.option {
	display: flex;
	flex-direction: row;
	justify-content: space-between;
	background-color: rgba(255,255,255,0.08);
	color: white;
	font-size: 20px;
	padding: 10px 16px;
	margin: 4px 0;
	border-radius: 8px;
}

.option:hover {
	background-color: rgba(120,180,255,0.35);
}

.option.mine {
	background-color: rgba(80,140,255,0.65);
	border: 1px solid rgba(160,200,255,0.9);
}

.mapname {
	font-weight: bold;
}

.tally {
	color: #ffe080;
	min-width: 40px;
	text-align: right;
}

.winner {
	font-size: 34px;
	font-weight: bold;
	color: #80ff9f;
	margin-top: 6px;
}

.hint {
	font-size: 16px;
	color: rgba(255,255,255,0.5);
}
";
	}
}

// -----------------------------------------------------------------------------
// add_panel_buildhash -- razor_lint's companion FIXER. Not a codegen scaffold:
// patches an EXISTING .razor file, inserting a BuildHash() override at the end
// of its @code block that folds the fields/properties declared there via
// System.HashCode.Combine. Heuristic string/regex parsing -- review the diff.
// Timer-typed members (TimeSince/TimeUntil/RealTimeSince/RealTimeUntil) are
// folded as whole seconds so they re-render at 1 Hz instead of every frame.
// -----------------------------------------------------------------------------
public class AddPanelBuildhashHandler : IBridgeHandler
{
	static readonly System.Text.RegularExpressions.Regex RxAttr =
		new System.Text.RegularExpressions.Regex( @"^\s*\[[^\]]*\]\s*" );

	// Auto-property on one line: modifiers, type, name, then "{ ... get".
	static readonly System.Text.RegularExpressions.Regex RxProp =
		new System.Text.RegularExpressions.Regex(
			@"^(?:public|private|protected|internal)\s+(?:(?:new|virtual|override|required)\s+)*(?<type>[A-Za-z_][\w\.<>\[\], \?]*?)\s+(?<name>[A-Za-z_]\w*)\s*\{[^}]*\bget\b" );

	// Field: modifiers, type, name, then '=' (but not '=>') or ';'.
	static readonly System.Text.RegularExpressions.Regex RxField =
		new System.Text.RegularExpressions.Regex(
			@"^(?:public|private|protected|internal)\s+(?:readonly\s+)?(?<type>[A-Za-z_][\w\.<>\[\], \?]*?)\s+(?<name>[A-Za-z_]\w*)\s*(?:=(?!>)|;)" );

	// Lines that can never be hashable instance state.
	static readonly System.Text.RegularExpressions.Regex RxSkip =
		new System.Text.RegularExpressions.Regex(
			@"\b(static|const|event|delegate|class|struct|enum|interface|record|using|namespace)\b" );

	static readonly HashSet<string> TimerTypes = new()
	{
		"TimeSince", "TimeUntil", "RealTimeSince", "RealTimeUntil",
		"Sandbox.TimeSince", "Sandbox.TimeUntil", "Sandbox.RealTimeSince", "Sandbox.RealTimeUntil"
	};

	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var path = p.TryGetProperty( "path", out var pv ) ? pv.GetString() : null;
			if ( string.IsNullOrWhiteSpace( path ) )
				return Task.FromResult<object>( new { error = "path is required (project-relative .razor file, e.g. Code/UI/MyHud.razor)" } );

			var relPath = path.Replace( '\\', '/' );
			if ( !relPath.EndsWith( ".razor", StringComparison.OrdinalIgnoreCase ) )
				return Task.FromResult<object>( new { error = $"Not a .razor file: {relPath}. This tool patches Razor panels only (the .razor.scss needs no BuildHash)." } );

			if ( !ClaudeBridge.TryResolveProjectPath( relPath, out var fullPath, out var pathErr ) )
				return Task.FromResult<object>( new { error = pathErr } );
			if ( !File.Exists( fullPath ) )
				return Task.FromResult<object>( new { error = $"File not found: {relPath}" } );

			var text = File.ReadAllText( fullPath );

			// Same containment check razor_lint uses -- only PanelComponent panels
			// have the BuildHash virtual to override.
			if ( !text.Contains( "inherits PanelComponent" ) )
				return Task.FromResult<object>( new { error = $"{relPath} does not '@inherits PanelComponent' -- BuildHash is a PanelComponent override, so there is nothing to patch." } );

			if ( text.Contains( "BuildHash" ) )
			{
				return Task.FromResult<object>( new
				{
					alreadyPresent = true,
					path = relPath,
					note = "A BuildHash already exists -- file left unchanged. Extend it by hand if it misses render-driving state."
				} );
			}

			// Find the FIRST @code { ... } block by brace-matching the raw string
			// (string-index based so the rest of the file is preserved byte-for-byte).
			int atCode = text.IndexOf( "@code", StringComparison.Ordinal );
			if ( atCode < 0 )
				return Task.FromResult<object>( new { error = $"No @code block found in {relPath}. Add one (even empty) and re-run, or the panel simply has no state to hash." } );

			int open = text.IndexOf( '{', atCode );
			if ( open < 0 )
				return Task.FromResult<object>( new { error = $"Found @code but no opening brace in {relPath}." } );

			int depth = 0, close = -1;
			for ( int i = open; i < text.Length; i++ )
			{
				if ( text[i] == '{' ) depth++;
				else if ( text[i] == '}' )
				{
					depth--;
					if ( depth == 0 ) { close = i; break; }
				}
			}
			if ( close < 0 )
				return Task.FromResult<object>( new { error = $"Unbalanced braces in the @code block of {relPath} -- fix the file first (a brace inside a string literal can also confuse this heuristic parser)." } );

			// Collect hashable members declared in the block (heuristic, line-based).
			var body = text.Substring( open + 1, close - open - 1 );
			var hashedMembers = new List<string>();
			var exprs = new List<string>();
			var seen = new HashSet<string>();

			foreach ( var raw in body.Split( '\n' ) )
			{
				var line = raw.TrimEnd( '\r' ).Trim();
				if ( line.Length == 0 || line.StartsWith( "//" ) || line.StartsWith( "///" ) ) continue;

				// Strip leading [Attribute] prefixes (possibly several).
				while ( true )
				{
					var am = RxAttr.Match( line );
					if ( !am.Success || am.Length == 0 ) break;
					line = line.Substring( am.Length );
				}

				if ( RxSkip.IsMatch( line ) ) continue;

				string type = null, memberName = null;
				var pm = RxProp.Match( line );
				if ( pm.Success ) { type = pm.Groups["type"].Value.Trim(); memberName = pm.Groups["name"].Value; }
				else
				{
					var fm = RxField.Match( line );
					if ( fm.Success ) { type = fm.Groups["type"].Value.Trim(); memberName = fm.Groups["name"].Value; }
				}

				if ( memberName == null || type == null ) continue;
				if ( type == "void" || memberName == "BuildHash" ) continue;
				// Delegates change render state only indirectly -- skip them.
				if ( type.StartsWith( "Action" ) || type.StartsWith( "Func" )
					|| type.StartsWith( "System.Action" ) || type.StartsWith( "System.Func" ) ) continue;
				if ( !seen.Add( memberName ) ) continue;

				if ( TimerTypes.Contains( type ) )
				{
					// Whole-second fold so a running timer re-renders at 1 Hz, not every frame.
					exprs.Add( $"(int) (float) {memberName}" );
					hashedMembers.Add( $"{memberName} (timer -- folded at whole-second granularity)" );
				}
				else
				{
					exprs.Add( memberName );
					hashedMembers.Add( memberName );
				}
			}

			string combineExpr = BuildCombine( exprs );
			string nl = text.Contains( "\r\n" ) ? "\r\n" : "\n";
			string hashLine = exprs.Count == 0
				? "protected override int BuildHash() => 0;   // add_panel_buildhash: no state members found in @code -- fold your render-driving state here"
				: $"protected override int BuildHash() => {combineExpr};";
			string insertion = $"{nl}\t// Added by add_panel_buildhash (heuristic -- review): folds the @code state so the panel re-renders when it changes.{nl}\t{hashLine}{nl}";

			var patched = text.Insert( close, insertion );
			ScaffoldHelpers.WriteCode( fullPath, patched );

			return Task.FromResult<object>( new
			{
				patched = true,
				path = relPath,
				hashedMembers = hashedMembers.ToArray(),
				buildHash = hashLine,
				note = exprs.Count == 0
					? "No fields/properties were detected in @code, so a stub returning 0 was inserted (silences razor_lint but the panel will NOT re-render on state changes) -- replace 0 with your real state."
					: "Heuristic parse: reference-typed members fold by REFERENCE (a mutated List re-renders only when Count-style state is also folded), computed (=>) properties are skipped, timers fold at 1 Hz. Review the diff, then trigger_hotload."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_panel_buildhash failed: {ex.Message}" } );
		}
	}

	// System.HashCode.Combine tops out at 8 args -- nest chunks when there are more.
	static string BuildCombine( List<string> parts )
	{
		if ( parts.Count == 0 ) return "0";
		if ( parts.Count <= 8 )
			return "System.HashCode.Combine( " + string.Join( ", ", parts ) + " )";
		var chunks = new List<string>();
		for ( int i = 0; i < parts.Count; i += 8 )
			chunks.Add( "System.HashCode.Combine( " + string.Join( ", ", parts.Skip( i ).Take( 8 ) ) + " )" );
		return BuildCombine( chunks );
	}
}
