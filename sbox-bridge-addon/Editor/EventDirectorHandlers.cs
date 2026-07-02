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
//  create_event_director  (code-gen; scene-mutating)
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, SerializeGo) and ScaffoldHelpers (WriteCode / Utf8NoBom).
//  Handler code here is UNSANDBOXED editor code (System.* is fine).
//
//  The C# *strings this handler WRITES TO DISK* are SANDBOXED game code and must
//  obey the s&box sandbox rules:
//    - MathX preferred; System.Math/MathF also compile on the current SDK. Array.Clone()
//      is still whitelist-blocked -- use .ToArray() (not used here).
//    - guard networking with IsProxy (Networking.IsHost can throw with no session).
//    - only sandbox-proven APIs: Component, [Property], List<T>, TimeUntil, IsProxy,
//      GameObject.Clone(Vector3) + NetworkSpawn() + GetOrAddComponent<T>() (all used by
//      the compile-verified create_npc_spawner), Game.Random.Float (compile-verified in
//      create_weighted_loot_table). No trig, no System.Net, no filesystem.
//
//  DESIGN: the backlog line mentions ISceneMetadata prefab-discovery. That API is
//  NOT present in this SDK / cookbook / addon (grep-confirmed), so this scaffold uses
//  the proven parallel-list convention instead -- [Property] List<GameObject>
//  EventPrefabs + a parallel [Property] List<float> Weights -- exactly like
//  create_weighted_loot_table / create_inventory. A designer fills the lists in the
//  inspector (or the bridge wires them via assign_patrol_route property="EventPrefabs").
//
//  Generates TWO classes into ONE file:
//    {name}            -- the director (interval roll -> weighted pick -> dedupe ->
//                         clone + NetworkSpawn + attach the self-destruct companion).
//    {name}TimedEvent  -- the per-event self-destruct companion the director attaches
//                         to each spawned instance (host destroys after Lifetime).
//
//  Register(...) line + the _sceneMutatingCommands addition are wired by the main
//  agent in MyEditorMenu.cs (see this tool's summary) to keep the files decoupled.
// =============================================================================
public class CreateEventDirectorHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			// ── name + directory. Honor the spec's `path`, fall back to `directory`, then "Code". ──
			var name = p.TryGetProperty( "name", out var nv ) && !string.IsNullOrWhiteSpace( nv.GetString() )
				? nv.GetString() : "EventDirector";

			string directory = "Code";
			if ( p.TryGetProperty( "path", out var pv ) && !string.IsNullOrWhiteSpace( pv.GetString() ) )
				directory = pv.GetString();
			else if ( p.TryGetProperty( "directory", out var dv ) && !string.IsNullOrWhiteSpace( dv.GetString() ) )
				directory = dv.GetString();

			var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
			if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
				return Task.FromResult<object>( new { error = pathErr } );

			if ( File.Exists( fullPath ) )
				return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}. Choose a different name." } );

			var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );
			var timedName = className + "TimedEvent";

			// ── Tunables (params override defaults). ──
			float interval  = p.TryGetProperty( "intervalSeconds", out var iv ) && iv.TryGetSingle( out var iff ) ? iff : 30f;
			int   maxActive = p.TryGetProperty( "maxActive",       out var mv ) && mv.TryGetInt32( out var mi )  ? mi  : 3;
			float lifetime  = p.TryGetProperty( "eventLifetime",   out var lv ) && lv.TryGetSingle( out var lf ) ? lf  : 60f;

			// Defensive clamps so a silly value can't emit a pathological director.
			if ( interval  < 0.1f ) interval  = 0.1f;
			if ( maxActive < 1 )    maxActive = 1;
			if ( lifetime  < 0.1f ) lifetime  = 0.1f;

			var code = BuildCode( className, timedName, interval, maxActive, lifetime, ci );

			Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
			ScaffoldHelpers.WriteCode( fullPath, code );

			// Optional placement on an existing GameObject (only if the type is already in
			// the TypeLibrary, i.e. after a hotload -- same contract as the sibling scaffolds).
			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = PlaceOnTarget( tid.GetString(), className, out note );

			var relPath = $"{directory}/{fileName}";

			return Task.FromResult<object>( new
			{
				created         = true,
				path            = relPath,
				className,
				timedEventClass = timedName,
				intervalSeconds = interval,
				maxActive,
				eventLifetime   = lifetime,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} + {timedName} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Fill EventPrefabs (List<GameObject>) with the event prefabs/templates to spawn and a parallel Weights (List<float>) -- wire the list via assign_patrol_route (property=\"EventPrefabs\") or by hand in the inspector. A missing weight defaults to 1; a zero/negative weight or an invalid prefab is skipped.",
					"Tune IntervalSeconds / MaxActive / EventLifetime / DedupeActive with set_property if the defaults don't fit.",
					"Enter play mode and watch events spawn on the interval and self-destruct after EventLifetime (get_scene_hierarchy deltas). Networked spawns need a host session; solo falls back to a local Clone.",
					"For an ADAPTIVE interval (player-count / inactivity / time-pressure factors), edit RollInterval() to multiply IntervalSeconds by factor methods around 1.0 and clamp the result -- see the ai-director cookbook recipe."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_event_director failed: {ex.Message}" } );
		}
	}

	// Standard scaffold placement helper (mirrors create_weighted_loot_table / create_economy_wallet).
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

	static string BuildCode( string className, string timedName, float interval, int maxActive, float lifetime, System.Globalization.CultureInfo ci )
	{
		string iv = interval.ToString( ci ) + "f";
		string ma = maxActive.ToString( ci );
		string lf = lifetime.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- a generalized L4D-style pacing / AI director.
///
/// On a configurable interval the host rolls a weighted pick over EventPrefabs (with a
/// parallel Weights list), skips any event already active (dedupe) and anything past the
/// MaxActive concurrency cap, clones the chosen prefab at the director's position,
/// NetworkSpawns it, and attaches a {timedName} so the event self-destructs after
/// EventLifetime seconds. Use it for ambient events, waves, and world events.
///
/// Host-authoritative: only the host runs the loop (IsProxy guard); spawned events
/// replicate to clients. Single-player safe (IsProxy is false with no session, and
/// NetworkSpawn falls back to a plain local clone).
///
/// Prefab discovery is the proven parallel-list convention (EventPrefabs + Weights,
/// inspector-editable) rather than a metadata scan -- fill the two lists in the editor
/// or via the bridge. A missing weight defaults to 1.
///
/// Usage:
///   {className}.OnEventSpawned += ( go, i ) => Log.Info( $""event {{i}} -> {{go.Name}}"" );
///   GetComponent&lt;{className}&gt;()?.TryRollAndSpawn();   // host-only manual trigger
/// </summary>
public sealed class {className} : Component
{{
	// The event prefabs the director clones, with a PARALLEL Weights list (both
	// inspector-editable so designers tune rates without touching code). A missing or
	// short Weights entry defaults to 1.
	[Property] public List<GameObject> EventPrefabs {{ get; set; }} = new();
	[Property] public List<float> Weights {{ get; set; }} = new();

	[Property] public float IntervalSeconds {{ get; set; }} = {iv}; // base seconds between rolls
	[Property] public int   MaxActive       {{ get; set; }} = {ma}; // concurrent-event cap
	[Property] public float EventLifetime   {{ get; set; }} = {lf}; // each event self-destructs after this
	[Property] public bool  DedupeActive    {{ get; set; }} = true;       // never spawn an event already active
	[Property] public bool  AutoStart       {{ get; set; }} = true;

	/// <summary>Fires on the host each time an event spawns: (instance, EventPrefabs index).</summary>
	public static Action<GameObject, int> OnEventSpawned {{ get; set; }}

	// Active events tracked as parallel lists (serialization-free runtime state, same
	// idiom as the loot-table / inventory scaffolds). _activeIndices[i] is the EventPrefabs
	// index that produced _activeObjects[i], so dedupe can exclude events already live.
	private readonly List<GameObject> _activeObjects = new();
	private readonly List<int> _activeIndices = new();

	private TimeUntil _nextRoll;
	private bool _started;

	protected override void OnStart()
	{{
		_started = AutoStart;
		_nextRoll = IntervalSeconds;
	}}

	protected override void OnUpdate()
	{{
		if ( IsProxy ) return;      // host owns the director loop
		if ( !_started ) return;

		Prune();                    // drop finished/destroyed events first so the caps stay accurate

		if ( !_nextRoll ) return;   // TimeUntil bool is true ONCE ELAPSED; !elapsed -> wait
		_nextRoll = RollInterval(); // RE-ARM every roll, or the director fires exactly once
		TryRollAndSpawn();
	}}

	/// <summary>Seconds until the next roll. Fixed by default; edit this to make pacing
	/// adaptive -- multiply IntervalSeconds by factor methods around 1.0 (player-count,
	/// inactivity, time-pressure...) and clamp, per the ai-director cookbook recipe.</summary>
	private float RollInterval() => IntervalSeconds;

	// Remove dead/destroyed events so MaxActive and dedupe stay accurate.
	private void Prune()
	{{
		for ( int i = _activeObjects.Count - 1; i >= 0; i-- )
		{{
			if ( !_activeObjects[i].IsValid() )
			{{
				_activeObjects.RemoveAt( i );
				_activeIndices.RemoveAt( i );
			}}
		}}
	}}

	/// <summary>Host-only: weighted-pick an eligible event (deduped against the active
	/// set), clone it, NetworkSpawn it, and attach the timed self-destruct. Safe to call
	/// manually (e.g. from a trigger) -- it still honors MaxActive + dedupe.</summary>
	public void TryRollAndSpawn()
	{{
		if ( IsProxy ) return;
		if ( EventPrefabs == null || EventPrefabs.Count == 0 ) return;
		if ( _activeObjects.Count >= MaxActive ) return;

		int index = PickWeightedIndex();
		if ( index < 0 ) return;

		var prefab = EventPrefabs[index];
		if ( !prefab.IsValid() ) return;

		var go = prefab.Clone( WorldPosition );

		// Attach the self-destruct companion so the event cleans itself up after its lifetime.
		var timed = go.GetOrAddComponent<{timedName}>();
		timed.Lifetime = EventLifetime;

		try {{ go.NetworkSpawn(); }} catch {{ /* no active session -- stays a valid local clone */ }}

		_activeObjects.Add( go );
		_activeIndices.Add( index );
		OnEventSpawned?.Invoke( go, index );
	}}

	/// <summary>Cumulative-weight pick over the ELIGIBLE events; -1 when none qualify.</summary>
	private int PickWeightedIndex()
	{{
		int count = EventPrefabs.Count;

		float total = 0f;
		for ( int i = 0; i < count; i++ )
			if ( IsEligible( i ) ) total += WeightOf( i );

		if ( total <= 0f ) return -1;

		float roll = Game.Random.Float( 0f, total );
		float cumulative = 0f;
		for ( int i = 0; i < count; i++ )
		{{
			if ( !IsEligible( i ) ) continue;
			cumulative += WeightOf( i );
			if ( roll < cumulative ) return i;
		}}
		return -1;
	}}

	// An event is eligible if its prefab is valid, its weight is positive, and (when
	// deduping) it is not already active.
	private bool IsEligible( int i )
	{{
		if ( i < 0 || i >= EventPrefabs.Count ) return false;
		if ( !EventPrefabs[i].IsValid() ) return false;
		if ( WeightOf( i ) <= 0f ) return false;
		if ( DedupeActive && _activeIndices.Contains( i ) ) return false;
		return true;
	}}

	// Weight for an event index; defaults to 1 when Weights is shorter than EventPrefabs.
	private float WeightOf( int i ) => ( Weights != null && i < Weights.Count ) ? Weights[i] : 1f;

	/// <summary>Host-only: start / stop the director loop at runtime.</summary>
	public void StartDirector() {{ _started = true; _nextRoll = IntervalSeconds; }}
	public void StopDirector()  {{ _started = false; }}

	/// <summary>How many events are currently live (as of the last Prune()).</summary>
	public int ActiveCount => _activeObjects.Count;
}}

/// <summary>
/// {timedName} -- the self-destruct companion {className} attaches to every event it
/// spawns. The host destroys the GameObject after Lifetime seconds; the destroy
/// replicates to clients. Proxies never drive it (IsProxy guard), so the host stays the
/// single authority over each event's lifetime.
/// </summary>
public sealed class {timedName} : Component
{{
	[Property] public float Lifetime {{ get; set; }} = {lf};

	private TimeUntil _expire;

	protected override void OnStart()
	{{
		_expire = Lifetime;
	}}

	protected override void OnUpdate()
	{{
		if ( IsProxy ) return;              // host owns lifetime; the destroy replicates
		if ( _expire ) GameObject.Destroy();
	}}
}}
";
	}
}
