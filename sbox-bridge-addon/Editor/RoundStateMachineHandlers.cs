using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// =============================================================================
// create_round_state_machine -- the COMPLEX multi-state round machine.
//
// Sibling of create_round_phase_machine (ScaffoldHandlers.cs). Where the phase
// machine cycles ONE [Sync] enum on a shared timer, THIS scaffolds the
// "state-as-component" shape mined from despawn.murder: a RoundManager singleton
// that drives an abstract RoundState base (Begin/Tick/OnTimeUp/Finish lifecycle
// + a per-state [Sync(SyncFlags.FromHost)] TimeUntil timer) through N named
// RoundState subclasses, with index-wrap advancing, a CanEnter() skip, and
// host-event-plus-mirror-RPC state-change notification (the vault108.suspectra
// "paired local + RPC apply" pattern, reconciled by the durable [Sync] index).
//
// This file lives in the SAME assembly as MyEditorMenu.cs + ScaffoldHandlers.cs,
// so it reuses ClaudeBridge.* (TryResolveProjectPath, SanitizeIdentifier,
// SerializeGo) and ScaffoldHelpers.* (PrepareCodeFile, WriteCode). Handler code
// here is UNSANDBOXED editor code; the C# string it WRITES TO DISK is SANDBOXED
// game code and obeys the sandbox rules (no Array.Clone, IsProxy guards, etc.).
//
// Registration line + the _sceneMutatingCommands entry live in MyEditorMenu.cs
// (kept decoupled from this file, same as the other scaffolds).
// =============================================================================
public class CreateRoundStateMachineHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "RoundManager", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			// --- state names (sanitized identifiers, de-duped), + original names kept parallel ---
			var stateNames = new List<string>();
			var origNames  = new List<string>();
			if ( p.TryGetProperty( "states", out var st ) && st.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in st.EnumerateArray() )
				{
					var s = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
					if ( string.IsNullOrWhiteSpace( s ) ) continue;
					var id = ClaudeBridge.SanitizeIdentifier( s );
					if ( string.IsNullOrEmpty( id ) || stateNames.Contains( id ) ) continue;
					stateNames.Add( id );
					origNames.Add( s.Trim() );
				}
			}
			if ( stateNames.Count == 0 )
			{
				stateNames.AddRange( new[] { "Waiting", "Active", "PostRound" } );
				origNames.AddRange( new[] { "Waiting", "Active", "PostRound" } );
			}

			// --- durations: scalar `duration` default for every state, optional per-state override
			//     via `durations` (array aligned to states OR object keyed by the ORIGINAL name). ---
			float defaultDur = p.TryGetProperty( "duration", out var dv ) && dv.TryGetSingle( out var df ) ? df : 30f;
			var perState = new float[stateNames.Count];
			for ( int i = 0; i < perState.Length; i++ ) perState[i] = defaultDur;

			if ( p.TryGetProperty( "durations", out var du ) )
			{
				if ( du.ValueKind == JsonValueKind.Array )
				{
					int i = 0;
					foreach ( var e in du.EnumerateArray() )
					{
						if ( i >= perState.Length ) break;
						if ( TryFloat( e, out var f ) ) perState[i] = f;
						i++;
					}
				}
				else if ( du.ValueKind == JsonValueKind.Object )
				{
					for ( int i = 0; i < origNames.Count; i++ )
						if ( du.TryGetProperty( origNames[i], out var ev ) && TryFloat( ev, out var f ) ) perState[i] = f;
				}
			}

			bool loop = !( p.TryGetProperty( "loop", out var lv ) && lv.ValueKind == JsonValueKind.False );

			// --- derive collision-safe class names ---
			// baseName is derived from the manager so two managers in one project don't
			// re-declare the same abstract base (RoundManager -> RoundState).
			string baseName;
			if ( className.EndsWith( "Manager", StringComparison.Ordinal ) && className.Length > "Manager".Length )
				baseName = className.Substring( 0, className.Length - "Manager".Length ) + "State";
			else
				baseName = className + "State";
			if ( baseName == className ) baseName = className + "RoundState";

			var used = new HashSet<string> { className, baseName };
			var stateClasses = new List<string>();
			for ( int i = 0; i < stateNames.Count; i++ )
			{
				var stem = stateNames[i].EndsWith( "State", StringComparison.Ordinal ) ? stateNames[i] : stateNames[i] + "State";
				var candidate = stem;
				int suffix = 2;
				while ( used.Contains( candidate ) ) candidate = stem + "_" + ( suffix++ );
				used.Add( candidate );
				stateClasses.Add( candidate );
			}

			var code = BuildCode( className, baseName, stateClasses, stateNames, perState, defaultDur, loop );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = PlaceOnTarget( tid.GetString(), className, out note );

			var nextSteps = new[]
			{
				"1. trigger_hotload to compile the new types.",
				$"2. Add the manager to a GameObject: add_component_to_new_object component:\"{className}\" -- the {stateClasses.Count} state components auto-attach on OnStart, so a non-coder only places the manager.",
				$"3. React to transitions from any system: {className}.OnStateChanged += id => Log.Info( $\"round -> {{id}}\" );",
				$"4. Drive it host-side: GetComponent<{className}>()?.Advance() to step (skips states whose CanEnter() is false), or .EnterIndex( i ) to jump. Fill in each {baseName} stub's Begin/Tick/OnTimeUp/Finish."
			};

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				baseClass = baseName,
				stateClasses = stateClasses.ToArray(),
				states = stateNames.ToArray(),
				loop,
				placedOn,
				note,
				nextSteps
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_round_state_machine failed: {ex.Message}" } );
		}
	}

	static bool TryFloat( JsonElement e, out float f )
	{
		if ( e.ValueKind == JsonValueKind.Number && e.TryGetSingle( out f ) ) return true;
		if ( e.ValueKind == JsonValueKind.String
		     && float.TryParse( e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f ) ) return true;
		f = 0f;
		return false;
	}

	// Mirrors PlaceOnTarget in the other scaffold handlers (ScaffoldHandlers.cs):
	// attach the freshly generated manager to a live scene GameObject by GUID, but
	// only if the type is already in the TypeLibrary (i.e. after a trigger_hotload).
	static object PlaceOnTarget( string targetId, string className, out string note )
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
			note = $"Generated {className}.cs but it is not in the TypeLibrary yet -- trigger_hotload, then add it with add_component_with_properties.";
			return null;
		}
		try { go.Components.Create( typeDesc ); return ClaudeBridge.SerializeGo( go ); }
		catch ( Exception ex ) { note = $"Placement failed ({ex.Message})."; return null; }
	}

	// -------------------------------------------------------------------------
	// The generated (SANDBOXED) game code. One .cs file: the manager (sealed),
	// the abstract RoundState base (NOT sealed -- it owns the virtual lifecycle),
	// and one sealed stub per named state.
	// -------------------------------------------------------------------------
	static string BuildCode(
		string manager, string baseName, List<string> stateClasses, List<string> stateNames,
		float[] perState, float defaultDur, bool loop )
	{
		var ci = CultureInfo.InvariantCulture;
		string defDur = defaultDur.ToString( ci ) + "f";
		string loopLit = loop ? "true" : "false";

		// host branch of EnsureStates -- GetOrAddComponent for each state, in order.
		var hostAdds = string.Join( "\n", stateClasses.Select( c => $"\t\t\t\tGameObject.GetOrAddComponent<{c}>()," ) );

		// proxy branch -- grab the replicated components; only build the array once all exist.
		var proxyDecls = string.Join( "\n", stateClasses.Select( ( c, i ) => $"\t\t\tvar s{i} = GameObject.GetComponent<{c}>();" ) );
		var proxyNulls = string.Join( " && ", stateClasses.Select( ( c, i ) => $"s{i} != null" ) );
		var proxyArray = string.Join( ", ", stateClasses.Select( ( c, i ) => $"s{i}" ) );

		// one sealed stub per state.
		var stubs = new System.Text.StringBuilder();
		for ( int i = 0; i < stateClasses.Count; i++ )
		{
			string cls = stateClasses[i];
			string dur = perState[i].ToString( ci ) + "f";
			stubs.Append( $@"
/// <summary>
/// {cls} -- a {baseName} stub (default {dur} seconds). Fill in the lifecycle hooks.
/// The manager arms TimeLeft on Begin() and Advance()s when it hits 0 (Duration &gt; 0).
/// </summary>
public sealed class {cls} : {baseName}
{{
	// Per-state default; tune in the inspector. (Set in the ctor so an inspector
	// value still wins on deserialize; if unset, the {baseName} default applies.)
	public {cls}() {{ Duration = {dur}; }}

	public override void Begin()
	{{
		base.Begin();   // arms TimeLeft = Duration
		// TODO: entry side-effects (spawn hazards, reset scores, show a banner). Host-authoritative.
	}}

	public override void Tick()
	{{
		// TODO: per-frame logic while this state is active (host-only; the manager gates this).
	}}

	public override void OnTimeUp()
	{{
		// TODO: react to the timer elapsing. The manager Advance()s immediately after.
	}}

	public override void Finish()
	{{
		// TODO: exit cleanup. Copy any per-phase data you need OUT before the next state begins.
	}}

	// public override bool CanEnter() => true;   // return false to have the manager SKIP this state when advancing
}}
" );
		}

		return $@"using Sandbox;
using System;

/// <summary>
/// {manager} -- a host-authoritative multi-state round machine (singleton).
///
/// Mined from the ""state-as-component"" round pattern: each phase is its own
/// {baseName} subclass with a Begin/Tick/OnTimeUp/Finish lifecycle and a
/// per-state [Sync(SyncFlags.FromHost)] TimeUntil timer. The manager holds the
/// authoritative [Sync] StateIndex, ticks ONLY the active state on the host,
/// Advance()s on timeout (index-wraps; skips any state whose CanEnter() is false),
/// and announces transitions via a static event + an [Rpc.Broadcast] mirror so
/// the host's own client fires immediately and every proxy converges without
/// waiting a snapshot. Single-player safe (IsProxy is false, so it just runs).
///
/// The state components auto-attach in OnStart, so you only place {manager}.
///
/// Usage:
///   {manager}.OnStateChanged += id => Log.Info( $""round -> {{id}}"" );
///   GetComponent<{manager}>()?.Advance();        // host-only: step to the next enterable state
///   GetComponent<{manager}>()?.EnterIndex( 0 );  // host-only: jump to a specific state
/// </summary>
public sealed class {manager} : Component
{{
	public static {manager} Instance {{ get; private set; }}

	// Authoritative current state index -- clients cannot write it, only replicate it.
	[Sync( SyncFlags.FromHost )] public int StateIndex {{ get; set; }}

	// Loop back to the first state after the last (true) or hold on the last (false).
	[Property] public bool Loop {{ get; set; }} = {loopLit};

	// Fires on EVERY machine when the state changes (host writes it, all detect it).
	// Arg = the active state's Identifier (its class name). Hook game systems / HUD here.
	public static Action<string> OnStateChanged {{ get; set; }}

	private {baseName}[] _states;
	private int _announcedIndex = -1;

	/// <summary>The currently active state component, or null before init.</summary>
	public {baseName} Current => ( _states != null && StateIndex >= 0 && StateIndex < _states.Length ) ? _states[StateIndex] : null;

	protected override void OnStart()
	{{
		Instance = this;
		EnsureStates();
		// NOTE: if this manager is editor-placed in a NETWORKED game, make sure the
		// object is NetworkSpawn()'d (e.g. by a NetworkHelper) or [Sync] won't replicate.
		if ( !IsProxy ) EnterIndex( 0 );
	}}

	protected override void OnDestroy()
	{{
		if ( Instance == this ) Instance = null;
	}}

	protected override void OnUpdate()
	{{
		EnsureStates();

		if ( IsProxy )
		{{
			// Late-joiner / snapshot reconcile: fire for the synced state if the RPC
			// didn't already (Announce is idempotent per index, so this never double-fires).
			Announce( StateIndex );
			return;
		}}

		// Host-only past here.
		var cur = Current;
		if ( cur == null ) return;
		cur.Tick();
		if ( cur.Duration > 0f && cur.TimeLeft <= 0f )
		{{
			cur.OnTimeUp();
			Advance();
		}}
	}}

	/// <summary>Host-only: make the state at <paramref name=""index""/> (index-wraps) active and arm its timer.</summary>
	public void EnterIndex( int index )
	{{
		if ( IsProxy || _states == null || _states.Length == 0 ) return;
		index = Wrap( index );

		Current?.Finish();                 // exit the old state (copy data out in Finish())
		StateIndex = index;                // [Sync] -> the durable reconcile path for late joiners
		_states[index]?.Begin();           // arms TimeLeft = Duration on the new state

		Announce( index );                 // host's own client fires immediately
		BroadcastAnnounce( index );        // mirror to proxies (low-latency; deduped so it never double-fires)
	}}

	/// <summary>Host-only: advance to the next state whose CanEnter() is true, wrapping if Loop.</summary>
	public void Advance()
	{{
		if ( IsProxy || _states == null || _states.Length == 0 ) return;
		int n = _states.Length;
		for ( int step = 1; step <= n; step++ )
		{{
			int raw = StateIndex + step;
			if ( raw >= n && !Loop ) return;                              // not looping: hold on the last state
			int idx = Wrap( raw );
			var candidate = _states[idx];
			if ( candidate == null || !candidate.CanEnter() ) continue;   // CanEnter() skip
			EnterIndex( idx );
			return;
		}}
		// No enterable state found -- stay put.
	}}

	private int Wrap( int i )
	{{
		int n = ( _states != null && _states.Length > 0 ) ? _states.Length : 1;
		return ( ( i % n ) + n ) % n;
	}}

	private void Announce( int index )
	{{
		if ( _announcedIndex == index ) return;   // idempotent per index -> never double-fires
		_announcedIndex = index;
		OnStateChanged?.Invoke( IdentifierAt( index ) );
	}}

	// Public to match the codebase's proven [Rpc.Broadcast] shape (the source
	// generator is happiest with public RPCs); it's an implementation detail --
	// the !IsProxy guard + Announce's per-index dedupe make it safe to call anywhere.
	[Rpc.Broadcast]
	public void BroadcastAnnounce( int index )
	{{
		if ( !IsProxy ) return;   // the host already fired the event locally in EnterIndex
		Announce( index );
	}}

	private string IdentifierAt( int index )
	{{
		if ( _states != null && index >= 0 && index < _states.Length && _states[index] != null )
			return _states[index].Identifier;
		return index.ToString();
	}}

	// Auto-provision the state components so a non-coder only has to add {manager}.
	// The host adds them (they replicate to proxies as part of the networked object);
	// proxies just grab the replicated set once it arrives.
	private void EnsureStates()
	{{
		if ( _states != null ) return;
		if ( !IsProxy )
		{{
			_states = new {baseName}[]
			{{
{hostAdds}
			}};
		}}
		else
		{{
{proxyDecls}
			if ( {proxyNulls} ) _states = new {baseName}[] {{ {proxyArray} }};
		}}
	}}
}}

/// <summary>
/// {baseName} -- abstract base for one phase of the round, driven by {manager}.
/// NOT sealed: it owns the virtual Begin/Tick/OnTimeUp/Finish lifecycle that the
/// named state stubs override. Override CanEnter() to be skippable; override
/// Begin() and call base.Begin() to (re-)arm the timer.
/// </summary>
public abstract class {baseName} : Component
{{
	// Seconds this state lasts. 0 (or less) = no auto-advance; call the manager's
	// Advance()/EnterIndex() yourself (e.g. a lobby that waits on a ready-vote).
	[Property] public float Duration {{ get; set; }} = {defDur};

	// Host-authoritative countdown for THIS state, re-armed in Begin().
	[Sync( SyncFlags.FromHost )] public TimeUntil TimeLeft {{ get; set; }}

	/// <summary>Stable name for cross-network identification / HUD (never trust proxy object refs).</summary>
	public string Identifier => GetType().Name;

	/// <summary>Return false to have the manager SKIP this state while advancing.</summary>
	public virtual bool CanEnter() => true;

	/// <summary>Called by the manager when this state becomes active. Base arms the timer.</summary>
	public virtual void Begin() {{ TimeLeft = Duration; }}

	/// <summary>Called every host frame while this state is active.</summary>
	public virtual void Tick() {{ }}

	/// <summary>Called by the manager when TimeLeft reaches 0 (only if Duration &gt; 0).</summary>
	public virtual void OnTimeUp() {{ }}

	/// <summary>Called by the manager as this state is replaced by the next.</summary>
	public virtual void Finish() {{ }}
}}
{stubs}";
	}
}
