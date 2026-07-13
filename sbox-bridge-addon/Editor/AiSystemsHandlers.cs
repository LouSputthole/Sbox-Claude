using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
//  AI & Systems — Feature Wave (create_needs_system / create_utility_ai /
//  create_npc_schedule_brain / create_event_bus / add_tts_voice)
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs, so it uses the
//  shared helpers directly: ClaudeBridge.TryResolveProjectPath / SanitizeIdentifier /
//  ParseVector3 / SerializeGo, ScaffoldHelpers.PrepareCodeFile / WriteCode, and the
//  IBridgeHandler dispatch contract. Handler code here is UNSANDBOXED editor code.
//
//  The C# *strings these handlers generate* run in the SANDBOX (the game). Every
//  template below was live-compile-verified on 2026-07-12 (written into the live
//  project with default params, hotloaded, compile clean, TypeLibrary-load confirmed
//  for every class, then deleted): sealed Components + [Sync(SyncFlags.FromHost)],
//  nested data classes in [Property] List<T>, an abstract Component base with virtual
//  members, a static (non-Component) class, a C# record, TypeLibrary.GetType(Type) +
//  PropertyDescription.GetValue in game code, Rotation.LookAt(Vector3),
//  Sandbox.Speech.Synthesizer (fluent TrySetVoice/WithText/WithRate/Play), and
//  SoundHandle (Stop(fade)/IsPlaying/IsValid/SetParent/ListenLocal/LipSync.Enabled).
//
//  Registration lines + the _sceneMutatingCommands additions are wired by the main
//  agent in MyEditorMenu.cs (see this wave's summary) to avoid a merge conflict.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared helpers for the AI &amp; Systems generators. Kept internal to this file so
/// it does not collide with anything in MyEditorMenu.cs or sibling handler files.
/// </summary>
internal static class AiSystemsHelpers
{
	/// <summary>Read an optional float param — tolerates a JSON number OR a numeric string.</summary>
	public static float Float( JsonElement p, string key, float fallback )
	{
		if ( !p.TryGetProperty( key, out var e ) ) return fallback;
		if ( e.ValueKind == JsonValueKind.Number && e.TryGetSingle( out var f ) ) return f;
		if ( e.ValueKind == JsonValueKind.String
		     && float.TryParse( e.GetString(), System.Globalization.NumberStyles.Float,
		                        System.Globalization.CultureInfo.InvariantCulture, out var fs ) ) return fs;
		return fallback;
	}

	public static int Int( JsonElement p, string key, int fallback )
	{
		if ( !p.TryGetProperty( key, out var e ) ) return fallback;
		if ( e.ValueKind == JsonValueKind.Number && e.TryGetInt32( out var i ) ) return i;
		if ( e.ValueKind == JsonValueKind.String && int.TryParse( e.GetString(), out var iss ) ) return iss;
		return fallback;
	}

	public static bool Bool( JsonElement p, string key, bool fallback )
	{
		if ( !p.TryGetProperty( key, out var e ) ) return fallback;
		if ( e.ValueKind == JsonValueKind.True ) return true;
		if ( e.ValueKind == JsonValueKind.False ) return false;
		if ( e.ValueKind == JsonValueKind.String && bool.TryParse( e.GetString(), out var b ) ) return b;
		return fallback;
	}

	public static string Str( JsonElement p, string key, string fallback )
	{
		if ( p.TryGetProperty( key, out var e ) && e.ValueKind == JsonValueKind.String )
		{
			var s = e.GetString();
			if ( !string.IsNullOrWhiteSpace( s ) ) return s;
		}
		return fallback;
	}

	/// <summary>
	/// Format a float as an invariant-culture C# literal with an 'f' suffix (130 -> "130f").
	/// Invariant culture matters: a comma-decimal locale must not emit "0,25f".
	/// </summary>
	public static string F( float v )
	{
		var s = v.ToString( "0.0###", System.Globalization.CultureInfo.InvariantCulture );
		return s + "f";
	}

	/// <summary>
	/// Escape a user string for embedding inside a REGULAR C# string literal ("...") in
	/// generated code: backslash-escape \ and ", strip/escape control chars. (EscVerbatim-style
	/// quote-doubling is only valid inside @"" literals — the generated property defaults and
	/// list initializers are regular literals, caught live by the quote-in-need-name test.)
	/// </summary>
	public static string EscString( string raw )
	{
		return ( raw ?? "" )
			.Replace( "\\", "\\\\" )
			.Replace( "\"", "\\\"" )
			.Replace( "\r", "\\r" )
			.Replace( "\n", "\\n" )
			.Replace( "\t", "\\t" );
	}

	/// <summary>
	/// Attach the generated component to a scene GameObject by GUID — only possible if
	/// the type is ALREADY in the TypeLibrary (i.e. after a hotload). Mirrors the proven
	/// PlaceOnTarget in ScaffoldHandlers/EconomySaveHandlers.
	/// </summary>
	public static object PlaceOnTarget( string targetId, string className, out string note )
	{
		note = null;
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) { note = "No active scene to place into."; return null; }
		if ( !Guid.TryParse( targetId, out var guid ) ) { note = "Invalid targetId GUID."; return null; }
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) { note = $"Target GameObject not found: {targetId}"; return null; }
		var typeDesc = Game.TypeLibrary.GetType( className );
		if ( typeDesc == null )
		{
			note = $"Generated {className}.cs but it is not in the TypeLibrary yet — trigger_hotload, then add it with add_component_with_properties.";
			return null;
		}
		try { go.Components.Create( typeDesc ); return ClaudeBridge.SerializeGo( go ); }
		catch ( Exception ex ) { note = $"Placement failed ({ex.Message})."; return null; }
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  1. create_needs_system  (code-gen; scene-mutating)
//     Sim/tycoon needs engine: [Property] list of need definitions, per-need
//     0..100 values decaying over Time.Delta, Satisfy(name, amount), weighted-
//     mean Happiness, static OnNeedCritical / OnHappinessChanged events.
// ═══════════════════════════════════════════════════════════════════════════
public class CreateNeedsSystemHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "NeedsSystem", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var networked = AiSystemsHelpers.Bool( p, "networked", true );

			// ── Need definitions: explicit `needs` array wins, else the classic sim trio.
			var needLines = new StringBuilder();
			var needNames = new List<string>();
			if ( p.TryGetProperty( "needs", out var arr ) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 )
			{
				foreach ( var e in arr.EnumerateArray() )
				{
					var nName  = AiSystemsHelpers.Str(   e, "name", "Need" );
					var decay  = AiSystemsHelpers.Float( e, "decayPerSecond", 0.5f );
					var crit   = AiSystemsHelpers.Float( e, "criticalThreshold", 20f );
					var weight = AiSystemsHelpers.Float( e, "weight", 1f );
					needNames.Add( nName );
					needLines.Append( "\t\tnew NeedDefinition { Name = \"" + AiSystemsHelpers.EscString( nName )
						+ "\", DecayPerSecond = " + AiSystemsHelpers.F( decay )
						+ ", CriticalThreshold = " + AiSystemsHelpers.F( crit )
						+ ", Weight = " + AiSystemsHelpers.F( weight ) + " },\n" );
				}
			}
			else
			{
				needNames.AddRange( new[] { "Hunger", "Energy", "Fun" } );
				needLines.Append( "\t\tnew NeedDefinition { Name = \"Hunger\", DecayPerSecond = 0.8f, CriticalThreshold = 20f, Weight = 1f },\n" );
				needLines.Append( "\t\tnew NeedDefinition { Name = \"Energy\", DecayPerSecond = 0.5f, CriticalThreshold = 15f, Weight = 1f },\n" );
				needLines.Append( "\t\tnew NeedDefinition { Name = \"Fun\", DecayPerSecond = 0.3f, CriticalThreshold = 10f, Weight = 0.5f },\n" );
			}

			var code = BuildSource( className, networked, needLines.ToString() );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string placeNote = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = AiSystemsHelpers.PlaceOnTarget( tid.GetString(), className, out placeNote );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				networked,
				needs = needNames,
				propertyNames = new[] { "Needs", "Happiness" },
				placedOn,
				placementNote = placeNote,
				note = "Per-need values live on the simulating machine only (read with GetNeed(name), restore with Satisfy(name, amount)); " +
				       "the aggregate Happiness (weighted mean 0..100) " +
				       ( networked
				         ? "is [Sync(FromHost)] so clients can read it. Host-authoritative: decay + Satisfy only run on the host — route client actions through an [Rpc.Host] method that calls Satisfy. A no-session solo playtest makes everything a proxy (use networked:false to iterate solo). "
				         : "updates locally (networked:false build — no [Sync], no proxy guard; ticks in a single-machine playtest). " ) +
				       "OnNeedCritical is edge-triggered (fires once crossing below threshold, re-arms above it); OnHappinessChanged fires on >0.25-point moves. " +
				       "Both static events fire on the simulating machine only. Needs list is inspector-editable per instance."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_needs_system failed: {ex.Message}" } );
		}
	}

	private static string BuildSource( string className, bool networked, string needLines )
	{
		var syncAttr    = networked ? "[Sync( SyncFlags.FromHost )] " : "";
		var updateGuard = networked ? "\t\tif ( IsProxy ) return;   // host-authoritative — only the host decays\n\n" : "";
		var satisfyGuard= networked ? "\t\tif ( IsProxy ) return;\n" : "";
		var headerNote  = networked
			? "// Host-authoritative needs engine. Only the host decays/mutates needs; the aggregate\n// Happiness is [Sync]'d so clients can read it. Per-need values live host-side only.\n"
			: "// Local needs engine (networked:false — no [Sync], no proxy guard). Ticks in a\n// single-machine playtest; every machine runs its own copy if used networked.\n";

		return
$@"using Sandbox;
using System;
using System.Collections.Generic;

{headerNote}public sealed class {className} : Component
{{
	/// <summary>One tunable need: value starts at 100 and decays toward 0 at DecayPerSecond.</summary>
	public sealed class NeedDefinition
	{{
		public string Name {{ get; set; }} = ""Need"";
		public float DecayPerSecond {{ get; set; }} = 0.5f;    // points lost per second (0..100 scale)
		public float CriticalThreshold {{ get; set; }} = 20f;  // OnNeedCritical fires when value falls below this
		public float Weight {{ get; set; }} = 1f;              // contribution to the Happiness weighted mean
	}}

	[Property] public List<NeedDefinition> Needs {{ get; set; }} = new()
	{{
{needLines}	}};

	/// <summary>Weighted mean of all need values, 0..100.</summary>
	{syncAttr}public float Happiness {{ get; private set; }} = 100f;

	/// <summary>Fires on the simulating machine when a need first crosses below its critical threshold. Re-arms when satisfied back above it.</summary>
	public static Action<{className}, string> OnNeedCritical {{ get; set; }}
	/// <summary>Fires when Happiness moves by more than 0.25 points. Arg = new happiness.</summary>
	public static Action<{className}, float> OnHappinessChanged {{ get; set; }}

	private readonly Dictionary<string, float> _values = new();
	private readonly HashSet<string> _critical = new();
	private float _lastHappiness = -1f;

	protected override void OnStart()
	{{
		foreach ( var need in Needs )
			if ( need != null && !string.IsNullOrEmpty( need.Name ) && !_values.ContainsKey( need.Name ) )
				_values[need.Name] = 100f;
	}}

	protected override void OnUpdate()
	{{
{updateGuard}		foreach ( var need in Needs )
		{{
			if ( need == null || string.IsNullOrEmpty( need.Name ) ) continue;
			if ( !_values.TryGetValue( need.Name, out var v ) ) {{ v = 100f; }}

			var nv = MathX.Clamp( v - need.DecayPerSecond * Time.Delta, 0f, 100f );
			_values[need.Name] = nv;

			// Edge-triggered: fires once on crossing below threshold, re-arms above it.
			if ( nv < need.CriticalThreshold )
			{{
				if ( _critical.Add( need.Name ) ) OnNeedCritical?.Invoke( this, need.Name );
			}}
			else
			{{
				_critical.Remove( need.Name );
			}}
		}}

		RecomputeHappiness();
	}}

	/// <summary>Current value (0..100) of a need by name, or -1 if unknown.</summary>
	public float GetNeed( string name )
		=> name != null && _values.TryGetValue( name, out var v ) ? v : -1f;

	/// <summary>Restore a need by amount (clamped 0..100).</summary>
	public void Satisfy( string name, float amount )
	{{
{satisfyGuard}		if ( name == null || !_values.ContainsKey( name ) ) return;
		_values[name] = MathX.Clamp( _values[name] + amount, 0f, 100f );
		RecomputeHappiness();
	}}

	private void RecomputeHappiness()
	{{
		float total = 0f, weight = 0f;
		foreach ( var need in Needs )
		{{
			if ( need == null || string.IsNullOrEmpty( need.Name ) ) continue;
			if ( !_values.TryGetValue( need.Name, out var v ) ) continue;
			total += v * need.Weight;
			weight += need.Weight;
		}}
		var h = weight > 0f ? total / weight : 100f;
		if ( System.MathF.Abs( h - _lastHappiness ) > 0.25f )
		{{
			_lastHappiness = h;
			Happiness = h;
			OnHappinessChanged?.Invoke( this, h );
		}}
	}}
}}
";
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  2. create_utility_ai  (code-gen; scene-mutating)
//     Scored-action brain: abstract {Prefix}Action base (Score 0..1 +
//     Begin/Tick/End) + sealed {Prefix}Brain that picks the highest-scoring
//     sibling action every EvaluateInterval (hysteresis bonus prevents
//     flip-flopping) + two example actions (Idle, Wander).
// ═══════════════════════════════════════════════════════════════════════════
public class CreateUtilityAiHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var rawName   = AiSystemsHelpers.Str( p, "name", "Utility" );
			if ( rawName.EndsWith( ".cs", StringComparison.OrdinalIgnoreCase ) )
				rawName = rawName.Substring( 0, rawName.Length - 3 );
			var directory = AiSystemsHelpers.Str( p, "directory", "Code" );

			var prefix   = ClaudeBridge.SanitizeIdentifier( rawName, "Utility" );
			var fileName = $"{prefix}Ai.cs";
			if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
				return Task.FromResult<object>( new { error = pathErr } );
			if ( File.Exists( fullPath ) )
				return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}. Choose a different name." } );

			var evaluateInterval = AiSystemsHelpers.Float( p, "evaluateInterval", 0.25f );
			var hysteresisBonus  = AiSystemsHelpers.Float( p, "hysteresisBonus",  0.15f );
			var moveSpeed        = AiSystemsHelpers.Float( p, "moveSpeed",        80f );
			var wanderRadius     = AiSystemsHelpers.Float( p, "wanderRadius",     300f );
			var networked        = AiSystemsHelpers.Bool(  p, "networked",        true );

			var brainName  = $"{prefix}Brain";
			var actionBase = $"{prefix}Action";
			var idleName   = $"{prefix}IdleAction";
			var wanderName = $"{prefix}WanderAction";

			var code = BuildSource( brainName, actionBase, idleName, wanderName, networked,
				evaluateInterval, hysteresisBonus, moveSpeed, wanderRadius );

			Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string placeNote = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = AiSystemsHelpers.PlaceOnTarget( tid.GetString(), brainName, out placeNote );

			return Task.FromResult<object>( new
			{
				created = true,
				path = $"{directory}/{fileName}",
				classNames = new[] { actionBase, brainName, idleName, wanderName },
				networked,
				propertyNames = new[] { "EvaluateInterval", "HysteresisBonus", "ScoreWeight", "BaseScore", "MoveSpeed", "WanderRadius", "SecondsToFullDesire" },
				placedOn,
				placementNote = placeNote,
				note = $"Utility AI vs create_npc_brain: the FSM brain has FIXED transitions (Idle→Chase→Search…); this brain has NO transition table — " +
				       $"every {actionBase} sibling self-scores 0..1 each EvaluateInterval and the highest (score × ScoreWeight, current action +HysteresisBonus) wins, " +
				       "so behavior emerges from the scores. Add behaviors by subclassing the abstract base ON THE SAME GameObject as the brain " +
				       "(targetId placement attaches ONLY the brain — add the example actions with add_component_with_properties after a hotload). " +
				       "The two examples alternate emergently: Wander desire builds while idle, collapses on arrival. Wander moves by direct transform walk (no navmesh, walks through walls). " +
				       ( networked
				         ? "Networked: host-authoritative (IsProxy guard) + [Sync] CurrentActionName — needs a host session; use networked:false to iterate solo."
				         : "Solo/local build: no proxy guard, ticks in a single-machine playtest." )
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_utility_ai failed: {ex.Message}" } );
		}
	}

	private static string BuildSource(
		string brainName, string actionBase, string idleName, string wanderName, bool networked,
		float evaluateInterval, float hysteresisBonus, float moveSpeed, float wanderRadius )
	{
		string F( float v ) => AiSystemsHelpers.F( v );

		var syncAttr   = networked ? "[Sync( SyncFlags.FromHost )] " : "";
		var proxyGuard = networked ? "\t\tif ( IsProxy ) return;   // host-authoritative — only the host thinks\n\n" : "";
		var headerNote = networked
			? "// Host-authoritative: only the host evaluates + ticks actions; CurrentActionName is\n// [Sync]'d for client UI. A no-session solo playtest makes everything a proxy —\n// generate with networked:false to iterate solo.\n"
			: "// Solo / local brain (networked:false — no proxy guard). Ticks in a single-machine playtest.\n";

		return
$@"using Sandbox;
using System;

// Utility AI — scored-action brain. Unlike an FSM (fixed transition table), actions
// self-score 0..1 every EvaluateInterval and the highest score wins (emergent switching).
// Add more actions by subclassing {actionBase} on the same GameObject.
{headerNote}
/// <summary>Base class for utility actions. Put subclasses on the SAME GameObject as the brain.</summary>
public abstract class {actionBase} : Component
{{
	/// <summary>Multiplier applied to Score() — raise to bias this action.</summary>
	[Property] public float ScoreWeight {{ get; set; }} = 1f;

	/// <summary>Desirability this instant, 0..1. Highest-scoring sibling action wins.</summary>
	public abstract float Score();

	/// <summary>Called once when this action becomes the active one.</summary>
	public virtual void Begin() {{ }}
	/// <summary>Called every frame while this action is active.</summary>
	public virtual void Tick() {{ }}
	/// <summary>Called once when a better-scoring action takes over.</summary>
	public virtual void End() {{ }}
}}

/// <summary>Picks and runs the highest-scoring sibling {actionBase}.</summary>
public sealed class {brainName} : Component
{{
	/// <summary>Seconds between score evaluations (the active action Ticks every frame regardless).</summary>
	[Property] public float EvaluateInterval {{ get; set; }} = {F( evaluateInterval )};
	/// <summary>Score bonus the CURRENT action gets during evaluation — hysteresis so near-ties don't flip-flop.</summary>
	[Property] public float HysteresisBonus {{ get; set; }} = {F( hysteresisBonus )};

	{syncAttr}public string CurrentActionName {{ get; private set; }} = """";

	public {actionBase} Current {{ get; private set; }}

	/// <summary>Fires on the simulating machine when the active action changes. Args = brain, new action type name.</summary>
	public static Action<{brainName}, string> OnActionChanged {{ get; set; }}

	private TimeSince _sinceEval;

	protected override void OnStart()
	{{
		_sinceEval = 999f;   // evaluate on the first eligible frame
	}}

	protected override void OnUpdate()
	{{
{proxyGuard}		if ( _sinceEval >= EvaluateInterval )
		{{
			_sinceEval = 0f;
			Evaluate();
		}}

		if ( Current != null && Current.IsValid() && Current.Active )
			Current.Tick();
	}}

	private void Evaluate()
	{{
		{actionBase} best = null;
		float bestScore = float.MinValue;

		foreach ( var action in Components.GetAll<{actionBase}>() )
		{{
			if ( action == null || !action.IsValid() || !action.Active ) continue;
			float score = MathX.Clamp( action.Score(), 0f, 1f ) * action.ScoreWeight;
			if ( action == Current ) score += HysteresisBonus;
			if ( score > bestScore ) {{ bestScore = score; best = action; }}
		}}

		if ( best == Current ) return;

		if ( Current != null && Current.IsValid() ) Current.End();
		Current = best;
		CurrentActionName = best != null ? best.GetType().Name : """";
		if ( best != null ) best.Begin();
		OnActionChanged?.Invoke( this, CurrentActionName );
	}}
}}

/// <summary>Example action: constant low score — the fallback when nothing else wants to run.</summary>
public sealed class {idleName} : {actionBase}
{{
	[Property] public float BaseScore {{ get; set; }} = 0.1f;

	public override float Score() => BaseScore;
}}

/// <summary>Example action: desire builds while not wandering; walks to random points near home, then resets.</summary>
public sealed class {wanderName} : {actionBase}
{{
	[Property] public float MoveSpeed {{ get; set; }} = {F( moveSpeed )};
	[Property] public float WanderRadius {{ get; set; }} = {F( wanderRadius )};
	/// <summary>Seconds of not-wandering until desire reaches 1.0.</summary>
	[Property] public float SecondsToFullDesire {{ get; set; }} = 6f;

	private Vector3 _home;
	private Vector3 _target;
	private TimeSince _sinceSatisfied;

	protected override void OnStart()
	{{
		_home = WorldPosition;
		_target = WorldPosition;
		_sinceSatisfied = 0f;
	}}

	public override float Score()
		=> MathX.Clamp( _sinceSatisfied / System.MathF.Max( SecondsToFullDesire, 0.1f ), 0f, 1f );

	public override void Begin() => PickTarget();

	public override void Tick()
	{{
		var flat = ( _target - WorldPosition ).WithZ( 0f );
		if ( flat.Length <= 8f )
		{{
			_sinceSatisfied = 0f;   // reached — desire collapses, idle takes over until it rebuilds
			PickTarget();
			return;
		}}

		var step = flat.Normal * MoveSpeed * Time.Delta;
		if ( step.Length > flat.Length ) step = flat;
		WorldPosition += step;
		WorldRotation = Rotation.LookAt( flat.Normal );
	}}

	private void PickTarget()
	{{
		_target = _home + new Vector3(
			Random.Shared.Float( -WanderRadius, WanderRadius ),
			Random.Shared.Float( -WanderRadius, WanderRadius ),
			0f );
	}}
}}
";
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  3. create_npc_schedule_brain  (code-gen; scene-mutating)
//     Daily-routine NPC: schedule entries (startHour/endHour/task/target),
//     reads the hour from any create_day_night_clock component (capability
//     match: float TimeOfDay), falls back to an internal clock, walks to the
//     active task's target, idles outside the schedule. Static OnTaskChanged.
// ═══════════════════════════════════════════════════════════════════════════
public class CreateNpcScheduleBrainHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "NpcScheduleBrain", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var moveSpeed      = AiSystemsHelpers.Float( p, "moveSpeed", 100f );
			var arriveDistance = AiSystemsHelpers.Float( p, "arriveDistance", 32f );
			var fallbackDayLen = AiSystemsHelpers.Float( p, "fallbackDayLengthSeconds", 600f );
			var fallbackStart  = AiSystemsHelpers.Float( p, "fallbackStartHour", 8f );
			var useNavMesh     = AiSystemsHelpers.Bool(  p, "useNavMeshAgent", false );
			var networked      = AiSystemsHelpers.Bool(  p, "networked", true );

			// ── Schedule entries: explicit `schedule` array wins, else a work/relax default.
			var entryLines = new StringBuilder();
			var taskNames  = new List<string>();
			if ( p.TryGetProperty( "schedule", out var arr ) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 )
			{
				foreach ( var e in arr.EnumerateArray() )
				{
					var start  = AiSystemsHelpers.Float( e, "startHour", 8f );
					var end    = AiSystemsHelpers.Float( e, "endHour", 17f );
					var task   = AiSystemsHelpers.Str(   e, "taskName", "Task" );
					var target = AiSystemsHelpers.Str(   e, "targetName", "" );
					taskNames.Add( task );

					var line = "\t\tnew ScheduleEntry { StartHour = " + AiSystemsHelpers.F( start )
						+ ", EndHour = " + AiSystemsHelpers.F( end )
						+ ", TaskName = \"" + AiSystemsHelpers.EscString( task ) + "\"";
					if ( !string.IsNullOrEmpty( target ) )
						line += ", TargetName = \"" + AiSystemsHelpers.EscString( target ) + "\"";
					if ( e.TryGetProperty( "targetPosition", out var posEl ) && posEl.ValueKind != JsonValueKind.Null )
					{
						var v = ClaudeBridge.ParseVector3( posEl );
						line += ", TargetPosition = new Vector3( " + AiSystemsHelpers.F( v.x ) + ", " + AiSystemsHelpers.F( v.y ) + ", " + AiSystemsHelpers.F( v.z ) + " )";
					}
					entryLines.Append( line + " },\n" );
				}
			}
			else
			{
				taskNames.AddRange( new[] { "Work", "Relax" } );
				entryLines.Append( "\t\tnew ScheduleEntry { StartHour = 8f, EndHour = 17f, TaskName = \"Work\", TargetName = \"WorkSpot\" },\n" );
				entryLines.Append( "\t\tnew ScheduleEntry { StartHour = 17f, EndHour = 22f, TaskName = \"Relax\", TargetName = \"HomeSpot\" },\n" );
			}

			var code = BuildSource( className, networked, useNavMesh, entryLines.ToString(),
				moveSpeed, arriveDistance, fallbackDayLen, fallbackStart );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string placeNote = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = AiSystemsHelpers.PlaceOnTarget( tid.GetString(), className, out placeNote );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				networked,
				useNavMeshAgent = useNavMesh,
				tasks = taskNames,
				propertyNames = new[] { "Schedule", "MoveSpeed", "ArriveDistance", "FallbackDayLengthSeconds", "FallbackStartHour" },
				placedOn,
				placementNote = placeNote,
				note = "Time source: binds by CAPABILITY to any component exposing a float TimeOfDay property (the create_day_night_clock contract) — " +
				       "same GameObject first, then scene-wide, re-scanned every 5s while unbound. If NO clock exists it honestly falls back to its own " +
				       "internal clock (FallbackDayLengthSeconds per 24h, starting at FallbackStartHour) — check UsingClockComponent at runtime. " +
				       "A clock with a different shape (e.g. a 0..1 DayProgress) will NOT bind — generate a create_day_night_clock or match the contract. " +
				       "Entries with EndHour < StartHour wrap past midnight. TargetName resolves a scene GameObject by name (case-insensitive, cached per task); " +
				       "missing names mean the NPC idles. Outside every entry the NPC idles in place. " +
				       ( useNavMesh
				         ? "Movement: NavMeshAgent.MoveTo — REQUIRES a baked navmesh (bake_navmesh) or the NPC won't move. "
				         : "Movement: direct transform walk (no navmesh, walks through walls — pass useNavMeshAgent:true for pathfinding). " ) +
				       ( networked
				         ? "Networked: host-authoritative (IsProxy guard) + [Sync] CurrentTask — needs a host session; use networked:false to iterate solo."
				         : "Solo/local build: no proxy guard, ticks in a single-machine playtest." )
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_npc_schedule_brain failed: {ex.Message}" } );
		}
	}

	private static string BuildSource(
		string className, bool networked, bool useNavMesh, string entryLines,
		float moveSpeed, float arriveDistance, float fallbackDayLen, float fallbackStart )
	{
		string F( float v ) => AiSystemsHelpers.F( v );

		var syncAttr   = networked ? "[Sync( SyncFlags.FromHost )] " : "";
		var proxyGuard = networked ? "\t\tif ( IsProxy ) return;   // host-authoritative — only the host routes\n\n" : "";
		var headerNote = networked
			? "// Host-authoritative daily-routine brain. Only the host reads the clock and moves the\n// NPC; CurrentTask is [Sync]'d for client UI. A no-session solo playtest makes everything\n// a proxy — generate with networked:false to iterate solo.\n"
			: "// Solo / local daily-routine brain (networked:false — no proxy guard).\n";

		// NavMeshAgent variant swaps the movement body; MoveTo/Stop/MaxSpeed are the same
		// calls the shipped create_npc_brain generator emits (proven sandbox surface).
		var agentField   = useNavMesh ? "\tprivate NavMeshAgent _agent;\n" : "";
		var agentOnStart = useNavMesh ? "\t\t_agent = GetOrAddComponent<NavMeshAgent>();\n" : "";
		var moveBody = useNavMesh
			?
@"		var flat = ( target - WorldPosition ).WithZ( 0f );
		if ( flat.Length <= ArriveDistance ) { _agent.Stop(); return; }   // arrived — idle at the task spot
		_agent.MaxSpeed = MoveSpeed;
		_agent.MoveTo( target );"
			:
@"		var flat = ( target - WorldPosition ).WithZ( 0f );
		if ( flat.Length <= ArriveDistance ) return;   // arrived — idle at the task spot

		var step = flat.Normal * MoveSpeed * Time.Delta;
		if ( step.Length > flat.Length ) step = flat;
		WorldPosition += step;
		WorldRotation = Rotation.LookAt( flat.Normal );";

		return
$@"using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

// Daily-routine NPC brain. Reads the hour from any create_day_night_clock component
// (capability match: a float TimeOfDay property) found on this GameObject or in the
// scene; falls back to its own internal clock when none exists. Walks the NPC to the
// active schedule entry's target and idles outside the schedule.
{headerNote}public sealed class {className} : Component
{{
	/// <summary>One routine block. EndHour smaller than StartHour wraps past midnight (e.g. 22 -> 6).</summary>
	public sealed class ScheduleEntry
	{{
		public float StartHour {{ get; set; }} = 8f;    // inclusive, 0..24
		public float EndHour {{ get; set; }} = 17f;     // exclusive
		public string TaskName {{ get; set; }} = ""Task"";
		public string TargetName {{ get; set; }} = """";  // named scene GameObject to walk to (wins over TargetPosition)
		public Vector3 TargetPosition {{ get; set; }}   // fixed world position, used when TargetName is empty
	}}

	[Property] public List<ScheduleEntry> Schedule {{ get; set; }} = new()
	{{
{entryLines}	}};

	[Property] public float MoveSpeed {{ get; set; }} = {F( moveSpeed )};
	[Property] public float ArriveDistance {{ get; set; }} = {F( arriveDistance )};

	// Internal fallback clock — used ONLY when no TimeOfDay clock component is found.
	[Property] public float FallbackDayLengthSeconds {{ get; set; }} = {F( fallbackDayLen )};
	[Property] public float FallbackStartHour {{ get; set; }} = {F( fallbackStart )};

	{syncAttr}public string CurrentTask {{ get; private set; }} = """";

	/// <summary>The hour (0..24) currently driving the schedule.</summary>
	public float CurrentHour {{ get; private set; }}
	/// <summary>True when bound to a scene clock component, false when on the internal fallback.</summary>
	public bool UsingClockComponent => _clock != null && _clock.IsValid();

	/// <summary>Fires on the simulating machine when the active task changes. Args = brain, new task name ("""" = idle).</summary>
	public static Action<{className}, string> OnTaskChanged {{ get; set; }}

	private Component _clock;
	private PropertyDescription _hourProp;
	private float _fallbackHour;
	private GameObject _targetGo;
	private string _resolvedTargetName;
	private RealTimeSince _sinceClockScan;
{agentField}
	protected override void OnStart()
	{{
{agentOnStart}		_fallbackHour = MathX.Clamp( FallbackStartHour, 0f, 24f );
		_sinceClockScan = 999f;
	}}

	protected override void OnUpdate()
	{{
{proxyGuard}		// Bind (and occasionally re-bind) to a clock — one may hotload/spawn later.
		if ( ( _clock == null || !_clock.IsValid() ) && _sinceClockScan > 5f )
			TryBindClock();

		CurrentHour = ReadHour();

		var entry = ActiveEntry( CurrentHour );
		var task = entry != null ? ( entry.TaskName ?? """" ) : """";
		if ( task != CurrentTask )
		{{
			CurrentTask = task;
			_targetGo = null;
			_resolvedTargetName = null;
			OnTaskChanged?.Invoke( this, task );
		}}

		if ( entry == null ) return;   // outside every schedule block — idle in place

		var target = ResolveTarget( entry );
		if ( target == null ) return;
		MoveToward( target.Value );
	}}

	private void TryBindClock()
	{{
		_sinceClockScan = 0f;
		_clock = null;
		_hourProp = null;
		if ( Scene == null ) return;

		// Same-GameObject components first, then the whole scene. Capability match:
		// a float TimeOfDay property (the create_day_night_clock contract).
		var candidates = Components.GetAll<Component>().Concat( Scene.GetAllComponents<Component>() );
		foreach ( var c in candidates )
		{{
			if ( c == null || c == this || !c.IsValid() ) continue;
			var td = TypeLibrary.GetType( c.GetType() );
			if ( td == null ) continue;
			var hour = td.Properties.FirstOrDefault( x => x.Name == ""TimeOfDay"" && x.PropertyType == typeof( float ) );
			if ( hour == null ) continue;
			_clock = c;
			_hourProp = hour;
			return;
		}}
	}}

	private float ReadHour()
	{{
		if ( _clock != null && _clock.IsValid() && _hourProp != null )
		{{
			var v = _hourProp.GetValue( _clock );
			if ( v is float f ) return MathX.Clamp( f, 0f, 24f );
		}}

		// Internal fallback: 24 in-game hours elapse per FallbackDayLengthSeconds.
		_fallbackHour += ( 24f / MathX.Clamp( FallbackDayLengthSeconds, 1f, 86400f ) ) * Time.Delta;
		while ( _fallbackHour >= 24f ) _fallbackHour -= 24f;
		return _fallbackHour;
	}}

	private ScheduleEntry ActiveEntry( float hour )
	{{
		if ( Schedule == null ) return null;
		foreach ( var e in Schedule )
		{{
			if ( e == null ) continue;
			bool active = e.StartHour <= e.EndHour
				? hour >= e.StartHour && hour < e.EndHour
				: hour >= e.StartHour || hour < e.EndHour;   // wraps past midnight
			if ( active ) return e;
		}}
		return null;
	}}

	private Vector3? ResolveTarget( ScheduleEntry entry )
	{{
		if ( !string.IsNullOrEmpty( entry.TargetName ) )
		{{
			if ( _targetGo != null && _targetGo.IsValid() && _resolvedTargetName == entry.TargetName )
				return _targetGo.WorldPosition;

			_targetGo = FindByNameRecursive( Scene, entry.TargetName );
			_resolvedTargetName = entry.TargetName;
			if ( _targetGo != null && _targetGo.IsValid() ) return _targetGo.WorldPosition;
			return null;   // named target missing from the scene — idle
		}}
		return entry.TargetPosition;
	}}

	private static GameObject FindByNameRecursive( GameObject root, string name )
	{{
		if ( root == null ) return null;
		foreach ( var child in root.Children )
		{{
			if ( child == null ) continue;
			if ( string.Equals( child.Name, name, StringComparison.OrdinalIgnoreCase ) ) return child;
			var found = FindByNameRecursive( child, name );
			if ( found != null ) return found;
		}}
		return null;
	}}

	private void MoveToward( Vector3 target )
	{{
{moveBody}
	}}
}}
";
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  4. create_event_bus  (code-gen; scene-mutating [writes a file])
//     Typed LOCAL pub/sub: static class with Subscribe<T>(owner, Action<T>),
//     Unsubscribe(owner), Publish<T>(evt). Plain owner-keyed handler lists —
//     no weak refs; owners must Unsubscribe in OnDestroy. Not a Component.
// ═══════════════════════════════════════════════════════════════════════════
public class CreateEventBusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "EventBus", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var code = BuildSource( className );
			ScaffoldHelpers.WriteCode( fullPath, code );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				exampleEvent = $"{className}Ping",
				api = new[] { "Subscribe<T>(object owner, Action<T> handler)", "Unsubscribe(object owner)", "Publish<T>(T evt)", "Count<T>()", "Clear()" },
				note = "Pure STATIC class — nothing to place in the scene (no targetId). LOCAL only: Publish runs handlers synchronously on the " +
				       "publishing machine, exact-type-T subscribers only (no base-type dispatch); NOT networked — pair with [Rpc.Broadcast]/[Rpc.Host] " +
				       "methods that Publish on arrival for networked events. Handler lists hold PLAIN references (no weak refs): every subscriber MUST " +
				       "call Unsubscribe(this) in OnDestroy or the handler AND the owner leak for the scene's life; call Clear() on scene teardown. " +
				       $"A tiny example event record ({className}Ping) is included — define your own events as small records/classes."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_event_bus failed: {ex.Message}" } );
		}
	}

	private static string BuildSource( string className )
	{
		return
$@"using System;
using System.Collections.Generic;

/// <summary>
/// {className} — typed LOCAL pub/sub. Subscribe with an owner object, publish typed
/// events, handlers run synchronously on the publishing machine. NOT networked — pair
/// with [Rpc.Broadcast] / [Rpc.Host] methods that Publish on arrival for networked events.
///
/// Handler lists hold PLAIN references (no weak refs): every subscriber MUST call
/// Unsubscribe(this) in OnDestroy, or the handler AND the owner leak for the scene's life.
/// </summary>
public static class {className}
{{
	private static readonly Dictionary<Type, List<(object Owner, Delegate Handler)>> _subs = new();

	/// <summary>Register a handler for events of type T. owner is your component (used by Unsubscribe).</summary>
	public static void Subscribe<T>( object owner, Action<T> handler )
	{{
		if ( owner == null || handler == null ) return;
		if ( !_subs.TryGetValue( typeof( T ), out var list ) )
		{{
			list = new List<(object, Delegate)>();
			_subs[typeof( T )] = list;
		}}
		list.Add( (owner, handler) );
	}}

	/// <summary>Remove ALL handlers registered by this owner, across every event type. Call in OnDestroy.</summary>
	public static void Unsubscribe( object owner )
	{{
		if ( owner == null ) return;
		foreach ( var list in _subs.Values )
			list.RemoveAll( s => ReferenceEquals( s.Owner, owner ) );
	}}

	/// <summary>Deliver evt to every exact-type-T subscriber, synchronously, in subscribe order.</summary>
	public static void Publish<T>( T evt )
	{{
		if ( !_subs.TryGetValue( typeof( T ), out var list ) || list.Count == 0 ) return;

		// Snapshot so a handler may Subscribe/Unsubscribe mid-publish safely.
		foreach ( var sub in list.ToArray() )
		{{
			if ( sub.Handler is Action<T> a ) a( evt );
		}}
	}}

	/// <summary>Handlers currently registered for T (diagnostics).</summary>
	public static int Count<T>() => _subs.TryGetValue( typeof( T ), out var l ) ? l.Count : 0;

	/// <summary>Drop every subscription — call on scene teardown / game restart.</summary>
	public static void Clear() => _subs.Clear();
}}

/// <summary>Example event — define your own as small records and Publish them.</summary>
public record {className}Ping( string Message );
";
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  5. add_tts_voice  (code-gen; scene-mutating)
//     TTS speaker component over the verified Sandbox.Speech.Synthesizer:
//     Say(text) → TrySetVoice → WithText → WithRate → Play() → SoundHandle,
//     stop-previous-on-say, positional/2D routing, optional viseme-data
//     extraction (Handle.LipSync.Enabled). Audio-only — see note for why
//     Sandbox.LipSync is not auto-wired.
// ═══════════════════════════════════════════════════════════════════════════
public class AddTtsVoiceHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "TtsSpeaker", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var voiceName     = AiSystemsHelpers.Str(   p, "voiceName", "" );
			var voiceGender   = AiSystemsHelpers.Str(   p, "voiceGender", "" );
			var voiceAge      = AiSystemsHelpers.Str(   p, "voiceAge", "" );
			var rate          = AiSystemsHelpers.Int(   p, "rate", 0 );
			var volume        = AiSystemsHelpers.Float( p, "volume", 1f );
			var positional    = AiSystemsHelpers.Bool(  p, "positional", true );
			var stopPrevious  = AiSystemsHelpers.Bool(  p, "stopPreviousOnSay", true );
			var stopFade      = AiSystemsHelpers.Float( p, "stopFadeSeconds", 0.1f );
			var enableVisemes = AiSystemsHelpers.Bool(  p, "enableVisemeData", false );

			var code = BuildSource( className,
				AiSystemsHelpers.EscString( voiceName ),
				AiSystemsHelpers.EscString( voiceGender ),
				AiSystemsHelpers.EscString( voiceAge ),
				rate, volume, positional, stopPrevious, stopFade, enableVisemes );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string placeNote = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = AiSystemsHelpers.PlaceOnTarget( tid.GetString(), className, out placeNote );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				propertyNames = new[] { "VoiceName", "VoiceGender", "VoiceAge", "Rate", "Volume", "Positional", "StopPreviousOnSay", "StopFadeSeconds", "EnableVisemeData" },
				placedOn,
				placementNote = placeNote,
				note = "Call <class>.Say(\"text\") from game code (LOCAL audio — wrap in [Rpc.Broadcast] for everyone to hear). " +
				       "The Synthesizer API surface compiles (verified live) but the editor cannot playtest audio, so RUNTIME behavior " +
				       "(actual speech, voice selection, viseme data) is UNVERIFIED — verify in play mode with your ears. " +
				       "Voice availability is machine/OS-specific: call LogVoices() in play mode to list installed voices; TrySetVoice is " +
				       "best-effort (falls back to the OS default). Gender/age hint strings (e.g. \"Female\"/\"Adult\") are passed through unvalidated. " +
				       "LIPSYNC: audio-only by design — s&box's Sandbox.LipSync component consumes a BaseSoundComponent (verified), not the raw " +
				       "SoundHandle TTS produces, and Synthesizer.OnVisemeReached's delegate arg types can't be confirmed via reflection, so neither is " +
				       "auto-wired. enableVisemeData:true sets Handle.LipSync.Enabled so your own mouth-drive code can read Handle.LipSync.Visemes (runtime-unverified)."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_tts_voice failed: {ex.Message}" } );
		}
	}

	private static string BuildSource(
		string className, string voiceNameLit, string voiceGenderLit, string voiceAgeLit,
		int rate, float volume, bool positional, bool stopPrevious, float stopFade, bool enableVisemes )
	{
		string F( float v ) => AiSystemsHelpers.F( v );
		string B( bool b ) => b ? "true" : "false";

		return
$@"using Sandbox;
using System;

/// <summary>
/// {className} — speaks text through the OS speech synthesizer (Sandbox.Speech.Synthesizer).
/// LOCAL audio only: Say() synthesizes and plays on the calling machine. For networked
/// voice, call Say from inside an [Rpc.Broadcast] handler so every client speaks it.
/// </summary>
public sealed class {className} : Component
{{
	/// <summary>Exact installed OS voice name (see LogVoices). Empty = use VoiceGender/VoiceAge, or the OS default.</summary>
	[Property] public string VoiceName {{ get; set; }} = ""{voiceNameLit}"";
	/// <summary>Voice gender hint, used only when VoiceName is empty (e.g. ""Female"", ""Male""). Needs VoiceAge too.</summary>
	[Property] public string VoiceGender {{ get; set; }} = ""{voiceGenderLit}"";
	/// <summary>Voice age hint paired with VoiceGender (e.g. ""Adult"", ""Child"", ""Senior"").</summary>
	[Property] public string VoiceAge {{ get; set; }} = ""{voiceAgeLit}"";
	/// <summary>Speaking rate offset: negative = slower, positive = faster, 0 = normal.</summary>
	[Property] public int Rate {{ get; set; }} = {rate};
	[Property] public float Volume {{ get; set; }} = {F( volume )};
	/// <summary>True: 3D sound parented to this GameObject (follows the speaker). False: flat 2D voice on the listener.</summary>
	[Property] public bool Positional {{ get; set; }} = {B( positional )};
	/// <summary>Fade out any still-playing previous line when Say is called again.</summary>
	[Property] public bool StopPreviousOnSay {{ get; set; }} = {B( stopPrevious )};
	[Property] public float StopFadeSeconds {{ get; set; }} = {F( stopFade )};
	/// <summary>Enable viseme extraction on the played handle (read Handle.LipSync.Visemes from your own mouth-drive code).</summary>
	[Property] public bool EnableVisemeData {{ get; set; }} = {B( enableVisemes )};

	/// <summary>The most recent line's SoundHandle (null before the first Say).</summary>
	public SoundHandle Handle {{ get; private set; }}
	public bool IsSpeaking => Handle != null && Handle.IsValid && Handle.IsPlaying;

	/// <summary>Synthesize and play a line. Repeated calls interrupt the previous line when StopPreviousOnSay.</summary>
	public void Say( string text )
	{{
		if ( string.IsNullOrWhiteSpace( text ) ) return;

		if ( StopPreviousOnSay && Handle != null && Handle.IsPlaying )
			Handle.Stop( StopFadeSeconds );

		var synth = new Sandbox.Speech.Synthesizer();

		if ( !string.IsNullOrWhiteSpace( VoiceName ) )
			synth.TrySetVoice( VoiceName );
		else if ( !string.IsNullOrWhiteSpace( VoiceGender ) && !string.IsNullOrWhiteSpace( VoiceAge ) )
			synth.TrySetVoice( VoiceGender, VoiceAge );

		var handle = synth.WithText( text ).WithRate( Rate ).Play();
		if ( handle == null ) return;

		handle.Volume = Volume;
		if ( Positional )
		{{
			handle.Position = WorldPosition;
			handle.SetParent( GameObject );   // follows the speaker as it moves
		}}
		else
		{{
			handle.ListenLocal = true;
		}}

		if ( EnableVisemeData )
			handle.LipSync.Enabled = true;

		Handle = handle;
	}}

	/// <summary>Fade out the current line (no-op when nothing is playing).</summary>
	public void StopSpeaking()
	{{
		if ( Handle != null && Handle.IsPlaying ) Handle.Stop( StopFadeSeconds );
	}}

	/// <summary>Log every installed OS voice + the currently selected one (voice availability is machine-specific).</summary>
	public void LogVoices()
	{{
		var synth = new Sandbox.Speech.Synthesizer();
		if ( !string.IsNullOrWhiteSpace( VoiceName ) ) synth.TrySetVoice( VoiceName );
		foreach ( var v in synth.InstalledVoices )
			Log.Info( $""[{className}] voice: {{v}}"" );
		Log.Info( $""[{className}] selected: {{synth.CurrentVoice}}"" );
	}}

	protected override void OnDestroy()
	{{
		if ( Handle != null && Handle.IsPlaying ) Handle.Stop( 0f );
	}}
}}
";
	}
}
