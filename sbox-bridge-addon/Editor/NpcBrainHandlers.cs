using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
//  NPC Brains — Feature Wave #3 (Phase 1 + simulate_npc_perception)
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs, so it can use the
//  shared helpers there directly: ClaudeBridge.TryResolveProjectPath /
//  SanitizeIdentifier / ParseVector3, SceneToolHelpers.*, and the IBridgeHandler
//  interface. These handlers run in the UNSANDBOXED editor (System.Math/MathF/IO
//  are all fine here).
//
//  The C# *strings these handlers generate* run in the SANDBOX (the game). That
//  generated code is deliberately restricted to APIs already proven to compile in
//  the sandbox by the existing create_npc_controller / create_networked_player
//  generators: Component, [Property], [Sync], GetOrAddComponent<NavMeshAgent>(),
//  NavMeshAgent.MoveTo(Vector3), IsProxy, TimeSince, Vector3.Dot/.Normal/
//  .DistanceBetween, Scene.GetAllComponents<T>(), scene.Trace.Ray(a,b).Run(),
//  MathX.Clamp. MathX preferred in generated code; System.Math/MathF also compile on the current SDK (verified 2026-06-09). Array.Clone() still blocked.
//
//  Tools in this file:
//    create_npc_brain        (code-gen; scene-mutating)
//    place_patrol_route      (scene-mutating)
//    assign_patrol_route     (scene-mutating)
//    create_npc_spawner      (code-gen; scene-mutating)
//    simulate_npc_perception (READ-ONLY; not scene-mutating)
//
//  Register(...) lines + _sceneMutatingCommands additions are wired by the main
//  agent in MyEditorMenu.cs (see this wave's summary) to avoid a merge conflict.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared helpers for the NPC-brain generators. Kept internal to this file so it
/// does not collide with anything in MyEditorMenu.cs.
/// </summary>
internal static class NpcBrainHelpers
{
	/// <summary>
	/// Read an optional float param, falling back to <paramref name="fallback"/>.
	/// Tolerates the value arriving as a JSON number OR a numeric string.
	/// </summary>
	public static float Float( JsonElement p, string key, float fallback )
	{
		if ( !p.TryGetProperty( key, out var e ) ) return fallback;
		if ( e.ValueKind == JsonValueKind.Number && e.TryGetSingle( out var f ) ) return f;
		if ( e.ValueKind == JsonValueKind.String && float.TryParse( e.GetString(), out var fs ) ) return fs;
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
	/// Format a float as an invariant-culture C# literal with an 'f' suffix, e.g.
	/// 130 -> "130f", 0.25 -> "0.25f". Invariant culture matters so a comma-decimal
	/// locale on the editor machine cannot emit "0,25f" and break compilation.
	/// </summary>
	public static string F( float v )
	{
		var s = v.ToString( "0.0###", System.Globalization.CultureInfo.InvariantCulture );
		return s + "f";
	}

	/// <summary>
	/// Escape a user string for safe embedding inside a C# double-quoted verbatim
	/// string ( @"" ), where the only escape needed is doubling the quote char.
	/// TargetTag is also identifier-ish but tags can legitimately contain symbols,
	/// so we keep it a string literal rather than sanitizing it to an identifier.
	/// </summary>
	public static string EscVerbatim( string raw ) => ( raw ?? "" ).Replace( "\"", "\"\"" );

	/// <summary>
	/// cos( fovDegrees / 2 ) computed in the EDITOR (MathF is legal here). Baked as
	/// the default of the generated CosFovThreshold property so the sandbox brain
	/// never needs trig. Clamped to a sane FOV range first.
	/// </summary>
	public static float CosHalfFov( float fovDegrees )
	{
		var fov = Math.Clamp( fovDegrees, 1f, 360f );
		var halfRad = ( fov * 0.5f ) * ( MathF.PI / 180f );
		return MathF.Cos( halfRad );
	}

	/// <summary>
	/// Resolve the component on <paramref name="go"/> that exposes a property named
	/// <paramref name="property"/>, and SET that property to <paramref name="value"/>.
	/// Preferred match is a component literally named "NpcBrain"; otherwise the first
	/// component whose TypeLibrary description has that property. Returns the matched
	/// component (so the caller can report its name), or null if none matched.
	///
	/// We deliberately do the find+set inside one method so this file never has to
	/// name the reflection types (TypeDescription / PropertyDescription) — the rest
	/// of the addon always uses `var` for them, which means their namespace is not
	/// guaranteed to be importable here. Keeping it all behind `var` mirrors the
	/// proven SetPrefabRefHandler pattern exactly.
	/// </summary>
	public static Component SetComponentProperty( GameObject go, string property, object value )
	{
		Component fallbackComp = null;

		// Pass 1: prefer an NpcBrain. Pass 2: any component exposing the property.
		foreach ( var c in go.Components.GetAll() )
		{
			var td = Game.TypeLibrary.GetType( c.GetType().Name );
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == property );
			if ( pd == null ) continue;

			if ( c.GetType().Name.Equals( "NpcBrain", StringComparison.OrdinalIgnoreCase ) )
			{
				pd.SetValue( c, value );
				return c;
			}

			fallbackComp = fallbackComp ?? c;
		}

		if ( fallbackComp != null )
		{
			var td = Game.TypeLibrary.GetType( fallbackComp.GetType().Name );
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == property );
			pd?.SetValue( fallbackComp, value );
		}

		return fallbackComp;
	}

	/// <summary>
	/// Find the "perception brain" component on <paramref name="go"/> — the component
	/// simulate_npc_perception should read SightRange/FovDegrees/EyeHeight/TargetTag from.
	///
	/// Why not just match the type name "NpcBrain": a custom-named brain (e.g. BigfootBrain,
	/// generated via create_npc_brain with name="BigfootBrain") exposes the same perception
	/// [Property] surface but a different type name, so a literal name match silently falls
	/// back to spec defaults. We match by CAPABILITY instead:
	///   1. a component literally named "NpcBrain" (the default), else
	///   2. a component whose TypeLibrary description exposes BOTH SightRange and FovDegrees
	///      (the perception contract), else
	///   3. a component whose type name ends with "Brain".
	/// Returns null if none match (caller then uses defaults / explicit overrides).
	/// </summary>
	public static Component FindPerceptionBrain( GameObject go )
	{
		if ( go == null ) return null;

		Component byProps = null;
		Component byName  = null;

		foreach ( var c in go.Components.GetAll() )
		{
			var typeName = c.GetType().Name;

			// 1. Exact "NpcBrain" wins immediately (the generated default).
			if ( typeName.Equals( "NpcBrain", StringComparison.OrdinalIgnoreCase ) )
				return c;

			// 2. Capability match: exposes the perception property contract.
			if ( byProps == null )
			{
				var td = Game.TypeLibrary.GetType( typeName );
				if ( td != null
					&& td.Properties.Any( pp => pp.Name == "SightRange" )
					&& td.Properties.Any( pp => pp.Name == "FovDegrees" ) )
				{
					byProps = c;
				}
			}

			// 3. Name heuristic: "...Brain".
			if ( byName == null && typeName.EndsWith( "Brain", StringComparison.OrdinalIgnoreCase ) )
				byName = c;
		}

		return byProps ?? byName;
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  1. create_npc_brain  (code-gen; scene-mutating)
//     Generates an NpcBrain Component: a finite-state machine (Idle/Patrol/
//     Wander/Chase/Search/Flee/Ambush) driven by occlusion-aware perception
//     (FOV cone + range + LOS trace + hearing) with last-known-position memory.
// ═══════════════════════════════════════════════════════════════════════════
public class CreateNpcBrainHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var name      = NpcBrainHelpers.Str( p, "name", "NpcBrain" );
			var directory = NpcBrainHelpers.Str( p, "directory", "Code" );

			var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
			if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
				return Task.FromResult<object>( new { error = pathErr } );

			if ( File.Exists( fullPath ) )
				return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

			var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

			// ── Preset → defaults. The generated file is identical shape; the preset
			//    only changes [Property] defaults (StartState, CanFlee).
			var behavior = NpcBrainHelpers.Str( p, "behavior", "hunter" ).ToLowerInvariant();
			string startState;
			bool presetCanFlee;
			switch ( behavior )
			{
				case "patrol":   startState = "Patrol"; presetCanFlee = false; break;
				case "guard":    startState = "Ambush"; presetCanFlee = false; break;
				case "swarm":    startState = "Wander"; presetCanFlee = false; break;
				case "skittish": startState = "Patrol"; presetCanFlee = true;  break;
				case "hunter":
				default:         behavior = "hunter"; startState = "Patrol"; presetCanFlee = false; break;
			}

			// ── Tunables (params override preset/spec defaults). ──
			var moveSpeed     = NpcBrainHelpers.Float( p, "moveSpeed",     130f );
			var chaseSpeed    = NpcBrainHelpers.Float( p, "chaseSpeed",    200f );
			var sightRange    = NpcBrainHelpers.Float( p, "sightRange",    1500f );
			var fovDegrees    = NpcBrainHelpers.Float( p, "fovDegrees",    110f );
			var eyeHeight     = NpcBrainHelpers.Float( p, "eyeHeight",     64f );
			var hearingRadius = NpcBrainHelpers.Float( p, "hearingRadius", 600f );
			var giveUpTime    = NpcBrainHelpers.Float( p, "giveUpTime",    6f );
			var searchRadius  = NpcBrainHelpers.Float( p, "searchRadius",  400f );
			var waypointStop  = NpcBrainHelpers.Float( p, "waypointStopDistance", 80f );
			var canFlee       = NpcBrainHelpers.Bool(  p, "canFlee",       presetCanFlee );
			var fleeHealth    = NpcBrainHelpers.Float( p, "fleeHealthFrac", 0.25f );
			var networked     = NpcBrainHelpers.Bool(  p, "networked",     true );
			var targetTag     = NpcBrainHelpers.Str(   p, "targetTag",     "player" );
			// Citizen locomotion animation: when on (default), the generated brain caches a
			// SkinnedModelRenderer + CitizenAnimationHelper in OnStart and drives walk/run/idle
			// from the NavMeshAgent each frame (so the NPC and every spawner clone animate
			// instead of sliding in bind pose). Proven approach ported from BigfootBrain.cs.
			var animate       = NpcBrainHelpers.Bool(  p, "animate",       true );

			var cosFov = NpcBrainHelpers.CosHalfFov( fovDegrees );

			var code = BuildSource(
				className, startState, networked, animate,
				NpcBrainHelpers.EscVerbatim( targetTag ),
				moveSpeed, chaseSpeed, sightRange, fovDegrees, cosFov, eyeHeight,
				hearingRadius, giveUpTime, searchRadius, waypointStop, canFlee, fleeHealth );

			Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
			File.WriteAllText( fullPath, code );

			var states = new[] { "Idle", "Patrol", "Wander", "Chase", "Search", "Flee", "Ambush" };
			var props = new[]
			{
				"StartState","MoveSpeed","ChaseSpeed","SightRange","FovDegrees","CosFovThreshold",
				"EyeHeight","HearingRadius","TargetTag","GiveUpTime","SearchRadius","WaypointStopDistance",
				"PingPong","CanFlee","FleeHealthFrac","CurrentHealthFrac","Waypoints","CurrentState"
			};

			return Task.FromResult<object>( new
			{
				created    = true,
				path       = $"{directory}/{fileName}",
				className,
				behavior,
				networked,
				animate,
				statesIncluded = states,
				propertyNames  = props,
				note = "NavMeshAgent is added automatically via GetOrAddComponent in OnStart. " +
				       "Requires bake_navmesh + a navmesh-walkable scene for movement. " +
				       "Assign a patrol route with place_patrol_route + assign_patrol_route. " +
				       "Verify perception in EDIT mode with simulate_npc_perception; verify chase/search by entering play mode " +
				       "(get_runtime_property CurrentState + timed screenshot_from). " +
				       ( animate
				         ? "Locomotion animation ON: caches a SkinnedModelRenderer + CitizenAnimationHelper in OnStart and drives walk/run/idle from the NavMeshAgent each frame — attach this brain to a GameObject with a Citizen (or any SkinnedModel) renderer (on it or a child) and it animates while moving instead of sliding. Spawner clones inherit it (each runs its own OnStart). Pass animate:false to disable. "
				         : "Locomotion animation OFF (animate:false): the NPC slides in bind pose; drive a CitizenAnimationHelper yourself if you want walk/run anims. " ) +
				       ( networked
				         ? "Networked: host-authoritative (if(IsProxy)return) + [Sync] CurrentState — needs a host session; a no-session solo playtest makes everything a proxy so the brain won't think (use networked:false to iterate solo)."
				         : "Solo/edit build: no IsProxy guard, so it ticks in a single-machine playtest." )
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_npc_brain failed: {ex.Message}" } );
		}
	}

	/// <summary>
	/// Build the NpcBrain component source. Everything here must be SANDBOX-LEGAL.
	/// Movement uses only the confirmed NavMeshAgent.MoveTo(Vector3); perception
	/// uses only Vector3.Dot/.Normal + scene.Trace.Ray(a,b).Run() + Scene.GetAllComponents.
	/// FOV uses a baked cosine threshold (no trig in the sandbox).
	/// When <paramref name="animate"/> is true the generated brain also caches a
	/// CitizenAnimationHelper (off a SkinnedModelRenderer) and feeds it the NavMeshAgent
	/// velocity each frame — sandbox-legal locomotion ported from BigfootBrain.cs (uses
	/// Sandbox.Citizen + MathX, never System.Math).
	/// </summary>
	private static string BuildSource(
		string className, string startState, bool networked, bool animate, string targetTagLiteral,
		float moveSpeed, float chaseSpeed, float sightRange, float fovDegrees, float cosFov,
		float eyeHeight, float hearingRadius, float giveUpTime, float searchRadius,
		float waypointStop, bool canFlee, float fleeHealth )
	{
		string F( float v ) => NpcBrainHelpers.F( v );

		// Host-authority guard line (networked) vs none (solo). The [Sync] on
		// CurrentState lets proxies read the host's state for client-side animation.
		var proxyGuard   = networked ? "\t\tif ( IsProxy ) return;   // host-authoritative — only the host thinks\n" : "";
		var stateAttr    = networked ? "[Sync] " : "";
		var headerNote   = networked
			? "// Host-authoritative AI brain. Only the host runs the FSM; CurrentState is [Sync]'d\n// so proxy clients can animate the NPC. Needs an active network session (a no-session\n// solo playtest makes everything a proxy — generate with networked:false to iterate solo).\n"
			: "// Solo / edit-scene AI brain (no networking guard). Ticks in a single-machine playtest.\n";

		// ── Citizen locomotion animation (ported verbatim from the proven BigfootBrain.cs).
		// Everything here is sandbox-legal: Sandbox.Citizen + GetOrAddComponent + the
		// NavMeshAgent's own Velocity/WishVelocity, no System.Math. When animate:false these
		// fragments are empty strings, so the generated brain is byte-for-byte the old one.
		var animUsing  = animate ? "using Sandbox.Citizen;\n" : "";
		var animFields = animate
			? "\n\t// Citizen locomotion. Drives the anim helper from the agent's velocity each frame so the\n" +
			  "\t// NPC walks/runs/idles instead of sliding in bind pose. Cached off the SkinnedModelRenderer\n" +
			  "\t// in OnStart (works for the source NPC AND its spawner clones — they each run OnStart).\n" +
			  "\tprivate CitizenAnimationHelper _anim;\n" +
			  "\tprivate SkinnedModelRenderer _renderer;\n"
			: "";
		// OnStart wiring. Wiring _anim.Target avoids a WithWishVelocity NRE (see SBOX_KNOWLEDGE.md).
		var animOnStart = animate
			? "\n\t\t// Locomotion animation. Find the SkinnedModelRenderer (this GO or a child), then\n" +
			  "\t\t// get-or-add a CitizenAnimationHelper and wire its Target — the helper NREs in\n" +
			  "\t\t// WithWishVelocity if Target is null. A Citizen .vmdl already has the locomotion\n" +
			  "\t\t// anim-graph, so once fed velocity it walks/runs/idles on its own.\n" +
			  "\t\t_renderer = GetComponent<SkinnedModelRenderer>() ?? GetComponentInChildren<SkinnedModelRenderer>();\n" +
			  "\t\tif ( _renderer.IsValid() )\n" +
			  "\t\t{\n" +
			  "\t\t\t_anim = GetOrAddComponent<CitizenAnimationHelper>();\n" +
			  "\t\t\t_anim.Target = _renderer;\n" +
			  "\t\t}\n"
			: "";
		// Per-frame drive call (placed at the end of OnUpdate) + the method body.
		var animUpdateCall = animate ? "\t\tDriveAnimation();\n" : "";
		var animMethod = animate
			? "\n\t// ── Locomotion animation ────────────────────────────────────────────────────\n" +
			  "\t/// <summary>Feed the Citizen anim helper from the NavMeshAgent each frame so the NPC\n" +
			  "\t/// plays walk/run/idle instead of sliding in bind pose. WithVelocity drives the\n" +
			  "\t/// locomotion blend; WithWishVelocity drives lean/start-stop; IsGrounded keeps it out\n" +
			  "\t/// of the fall pose. Glance toward the chased target, else toward travel direction.</summary>\n" +
			  "\tprivate void DriveAnimation()\n" +
			  "\t{\n" +
			  "\t\tif ( _anim == null || !_anim.IsValid() ) return;\n" +
			  "\n" +
			  "\t\tvar velocity = _agent.Velocity;\n" +
			  "\t\t_anim.WithVelocity( velocity );\n" +
			  "\t\t_anim.WithWishVelocity( _agent.WishVelocity );\n" +
			  "\t\t_anim.IsGrounded = true;\n" +
			  "\n" +
			  "\t\tVector3 lookDir;\n" +
			  "\t\tif ( CurrentState == BrainState.Chase && _target.IsValid() )\n" +
			  "\t\t\tlookDir = ( _target.WorldPosition - WorldPosition ).WithZ( 0f );\n" +
			  "\t\telse\n" +
			  "\t\t\tlookDir = velocity.WithZ( 0f );\n" +
			  "\n" +
			  "\t\tif ( lookDir.Length > 1f )\n" +
			  "\t\t\t_anim.WithLook( lookDir.Normal, 1f, 0.6f, 0.2f );\n" +
			  "\t}\n"
			: "";

		return
$@"using Sandbox;
{animUsing}using System;
using System.Collections.Generic;
using System.Linq;

{headerNote}public sealed class {className} : Component
{{
	public enum BrainState {{ Idle, Patrol, Wander, Chase, Search, Flee, Ambush }}

	// ── Tunables (all [Property] so the bridge can set_property / tune later) ──
	[Property] public BrainState StartState {{ get; set; }} = BrainState.{startState};
	[Property] public float MoveSpeed  {{ get; set; }} = {F( moveSpeed )};
	[Property] public float ChaseSpeed {{ get; set; }} = {F( chaseSpeed )};

	// Perception
	[Property] public float SightRange    {{ get; set; }} = {F( sightRange )};
	// FovDegrees is the human-readable full cone angle. The actual gate compares a
	// dot product against CosFovThreshold = cos(FovDegrees/2), which is baked here so
	// the sandbox needs no trig. If you change FovDegrees at runtime, also update
	// CosFovThreshold (tune_npc_perception / set_property), or call SetFov(...) below.
	[Property] public float FovDegrees      {{ get; set; }} = {F( fovDegrees )};
	[Property] public float CosFovThreshold {{ get; set; }} = {F( cosFov )};
	[Property] public float EyeHeight     {{ get; set; }} = {F( eyeHeight )};
	[Property] public float HearingRadius {{ get; set; }} = {F( hearingRadius )};
	[Property] public string TargetTag    {{ get; set; }} = @""{targetTagLiteral}"";

	// Memory / timing
	[Property] public float GiveUpTime   {{ get; set; }} = {F( giveUpTime )};
	[Property] public float SearchRadius {{ get; set; }} = {F( searchRadius )};
	[Property] public float WaypointStopDistance {{ get; set; }} = {F( waypointStop )};
	[Property] public bool  PingPong     {{ get; set; }} = false;

	// Flee (health source is generic: the game sets CurrentHealthFrac 0..1, or
	// override ShouldFlee() in a partial/subclass — no hard coupling to any HP comp).
	[Property] public bool  CanFlee           {{ get; set; }} = {( canFlee ? "true" : "false" )};
	[Property] public float FleeHealthFrac    {{ get; set; }} = {F( fleeHealth )};
	[Property] public float CurrentHealthFrac {{ get; set; }} = 1f;

	// Patrol route (placed + wired by assign_patrol_route, or hand-set in editor).
	[Property] public List<GameObject> Waypoints {{ get; set; }} = new();

	// ── Runtime state ──
	{stateAttr}public BrainState CurrentState {{ get; private set; }}
	private GameObject _target;
	private Vector3 _lastKnownPos;
	private TimeSince _timeSinceSeen;
	private Vector3 _wanderTarget;
	private TimeSince _timeSinceWanderPick;
	private int _waypointIndex;
	private int _waypointDir = 1;
	private NavMeshAgent _agent;
{animFields}
	protected override void OnStart()
	{{
		_agent = GetOrAddComponent<NavMeshAgent>();
{animOnStart}		CurrentState = StartState;
		_timeSinceSeen = 999f;
		_lastKnownPos = WorldPosition;
		_wanderTarget = WorldPosition;
	}}

	protected override void OnUpdate()
	{{
{proxyGuard}		if ( _agent == null ) return;

		Perceive();
		Think();
		Act();
{animUpdateCall}	}}
{animMethod}

	/// <summary>Recompute the FOV cosine from a degree value at runtime (no trig in
	/// the sandbox: cos(x) via the half-angle identity from a normalized sweep is
	/// overkill, so we keep it simple — set both together).</summary>
	public void SetFov( float degrees, float cosThreshold )
	{{
		FovDegrees = degrees;
		CosFovThreshold = cosThreshold;
	}}

	// ── Perception ────────────────────────────────────────────────────────────
	private void Perceive()
	{{
		var eye = WorldPosition + Vector3.Up * EyeHeight;
		var best = FindVisibleTarget( eye, out var sawSomething );

		if ( best.IsValid() )
		{{
			_target = best;
			_lastKnownPos = best.WorldPosition;
			_timeSinceSeen = 0f;
			return;
		}}

		// Passive hearing: a candidate within HearingRadius is ""heard"" (sets a
		// last-known position to investigate) but is NOT treated as seen — so the
		// NPC investigates rather than instantly aggroing.
		var heard = FindNearestCandidate( WorldPosition, HearingRadius );
		if ( heard.IsValid() )
			_lastKnownPos = heard.WorldPosition;

		// keep _target ref while it grows stale; _timeSinceSeen advances on its own.
	}}

	/// <summary>Pick the nearest candidate that passes range + FOV cone + LOS.</summary>
	private GameObject FindVisibleTarget( Vector3 eye, out bool any )
	{{
		any = false;
		GameObject bestGo = null;
		float bestDist = float.MaxValue;

		foreach ( var cand in Candidates() )
		{{
			var to = cand.WorldPosition - eye;
			float dist = to.Length;
			if ( dist > SightRange ) continue;
			if ( dist < 0.01f ) continue;

			var dir = to.Normal;
			// FOV cone gate (cheap): dot >= cos(half-fov). No trig needed.
			if ( Vector3.Dot( WorldRotation.Forward, dir ) < CosFovThreshold ) continue;

			// Occlusion trace from the eye to the candidate. IgnoreGameObjectHierarchy
			// excludes the NPC's own colliders so it can't ""see"" itself. Clear when the
			// ray hits the candidate directly, hits nothing, or the first hit is
			// essentially at the candidate (a child collider) — a distance test that
			// needs no extra API. Anything blocking earlier (a tree/wall) fails LOS.
			var tr = Scene.Trace.Ray( eye, cand.WorldPosition ).IgnoreGameObjectHierarchy( GameObject ).Run();
			bool clear = !tr.Hit || tr.GameObject == cand || tr.Distance >= dist - 8f;
			if ( !clear ) continue;

			any = true;
			if ( dist < bestDist ) {{ bestDist = dist; bestGo = cand; }}
		}}

		return bestGo;
	}}

	private GameObject FindNearestCandidate( Vector3 from, float maxDist )
	{{
		GameObject best = null;
		float bestDist = maxDist;
		foreach ( var cand in Candidates() )
		{{
			float d = Vector3.DistanceBetween( from, cand.WorldPosition );
			if ( d <= bestDist ) {{ bestDist = d; best = cand; }}
		}}
		return best;
	}}

	/// <summary>Candidate targets = GameObjects tagged TargetTag, excluding self.
	/// Uses Scene.GetAllComponents to enumerate, then filters by tag.</summary>
	private IEnumerable<GameObject> Candidates()
	{{
		foreach ( var c in Scene.GetAllComponents<Collider>() )
		{{
			var go = c.GameObject;
			if ( go == null || go == GameObject ) continue;
			if ( !go.Tags.Has( TargetTag ) ) continue;
			yield return go;
		}}
	}}

	// ── Transition table ────────────────────────────────────────────────────
	private void Think()
	{{
		bool canSee = _target.IsValid() && _timeSinceSeen < 0.1f;

		if ( CanFlee && ShouldFlee() ) {{ CurrentState = BrainState.Flee; return; }}

		switch ( CurrentState )
		{{
			case BrainState.Idle:
			case BrainState.Patrol:
			case BrainState.Wander:
			case BrainState.Ambush:
				if ( canSee ) CurrentState = BrainState.Chase;
				break;

			case BrainState.Chase:
				if ( !canSee && _timeSinceSeen > 0.25f ) CurrentState = BrainState.Search;
				break;

			case BrainState.Search:
				if ( canSee ) CurrentState = BrainState.Chase;
				else if ( _timeSinceSeen > GiveUpTime ) {{ _target = null; CurrentState = StartState; }}
				break;

			case BrainState.Flee:
				if ( !ShouldFlee() ) CurrentState = StartState;
				break;
		}}
	}}

	// ── Action per state ──────────────────────────────────────────────────────
	private void Act()
	{{
		// Apply the desired locomotion speed (chase is faster). NavMeshAgent.MaxSpeed
		// is the agent's speed cap (verified in the navmesh docs).
		_agent.MaxSpeed = ( CurrentState == BrainState.Chase || CurrentState == BrainState.Flee ) ? ChaseSpeed : MoveSpeed;

		switch ( CurrentState )
		{{
			case BrainState.Idle:
			case BrainState.Ambush:
				// Stand still and watch (perception still runs every tick).
				_agent.Stop();
				break;

			case BrainState.Patrol:
				PatrolStep();
				break;

			case BrainState.Wander:
				WanderStep( WorldPosition, SearchRadius );
				break;

			case BrainState.Chase:
				if ( _target.IsValid() )
					_agent.MoveTo( _target.WorldPosition );
				break;

			case BrainState.Search:
				if ( Vector3.DistanceBetween( WorldPosition, _lastKnownPos ) > WaypointStopDistance )
					_agent.MoveTo( _lastKnownPos );
				else
					WanderStep( _lastKnownPos, SearchRadius );
				break;

			case BrainState.Flee:
				FleeStep();
				break;
		}}
	}}

	private void PatrolStep()
	{{
		if ( Waypoints == null || Waypoints.Count == 0 ) return;
		_waypointIndex = (int)MathX.Clamp( _waypointIndex, 0, Waypoints.Count - 1 );

		var wp = Waypoints[_waypointIndex];
		if ( !wp.IsValid() ) {{ AdvanceWaypoint(); return; }}

		if ( Vector3.DistanceBetween( WorldPosition, wp.WorldPosition ) <= WaypointStopDistance )
			AdvanceWaypoint();
		else
			_agent.MoveTo( wp.WorldPosition );
	}}

	private void AdvanceWaypoint()
	{{
		if ( Waypoints == null || Waypoints.Count <= 1 ) return;

		if ( PingPong )
		{{
			if ( _waypointIndex + _waypointDir >= Waypoints.Count || _waypointIndex + _waypointDir < 0 )
				_waypointDir = -_waypointDir;
			_waypointIndex += _waypointDir;
		}}
		else
		{{
			_waypointIndex = ( _waypointIndex + 1 ) % Waypoints.Count;
		}}
	}}

	private void WanderStep( Vector3 home, float radius )
	{{
		bool reached = Vector3.DistanceBetween( WorldPosition, _wanderTarget ) <= WaypointStopDistance;
		if ( reached || _timeSinceWanderPick > 4f )
		{{
			// Pick a fresh point near home. Uses only confirmed APIs (Random.Shared
			// + Vector3). The agent paths toward the nearest reachable point, so an
			// occasional off-mesh pick is harmless. (For strictly-on-mesh wander,
			// swap to Scene.NavMesh.GetRandomPoint(home, radius) once its return type
			// is confirmed via describe_type.)
			var off = new Vector3(
				Random.Shared.Float( -radius, radius ),
				Random.Shared.Float( -radius, radius ),
				0f );
			_wanderTarget = home + off;
			_timeSinceWanderPick = 0f;
		}}
		_agent.MoveTo( _wanderTarget );
	}}

	private void FleeStep()
	{{
		// Move directly away from the last-known threat position.
		var away = ( WorldPosition - _lastKnownPos ).Normal;
		if ( away.Length < 0.01f ) away = WorldRotation.Forward;
		_agent.MoveTo( WorldPosition + away * MathX.Clamp( SearchRadius, 100f, 2000f ) );
	}}

	/// <summary>Generic flee predicate. Driven by CurrentHealthFrac (the game sets
	/// it 0..1). Override in a subclass/partial for game-specific logic (e.g. a
	/// bomb-timer panic in RUN, or a camper-HP check in Sasquatched).</summary>
	public bool ShouldFlee()
	{{
		return CanFlee && CurrentHealthFrac <= FleeHealthFrac;
	}}

	// ── Noise hook (pure C#; the game calls this where a noise happens) ─────────
	// Example: NpcBrain.ReportNoise(flashlightPos, 800f) when a camper clicks a
	// flashlight, or a gunshot in RUN. NPCs within radius investigate (Search).
	public static void ReportNoise( Scene scene, Vector3 pos, float radius )
	{{
		if ( scene == null ) return;
		foreach ( var brain in scene.GetAllComponents<{className}>() )
			brain.HearNoise( pos, radius );
	}}

	public void HearNoise( Vector3 pos, float radius )
	{{
		if ( Vector3.DistanceBetween( WorldPosition, pos ) > radius ) return;
		_lastKnownPos = pos;
		if ( CurrentState != BrainState.Chase )
			CurrentState = BrainState.Search;
	}}
}}
";
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  2. place_patrol_route  (scene-mutating)
//     Create N waypoint empties (tagged), grouped under a parent route object,
//     optionally snapped to the ground so they sit on the navmesh.
// ═══════════════════════════════════════════════════════════════════════════
public class PlacePatrolRouteHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "points", out var pts ) || pts.ValueKind != JsonValueKind.Array )
			return Task.FromResult<object>( new { error = "points (Vector3[]) is required" } );

		var rawPoints = new List<Vector3>();
		foreach ( var e in pts.EnumerateArray() )
			rawPoints.Add( ClaudeBridge.ParseVector3( e ) );

		if ( rawPoints.Count < 2 )
			return Task.FromResult<object>( new { error = "Provide at least 2 points for a patrol route" } );

		var routeName  = NpcBrainHelpers.Str( p, "name", "PatrolRoute" );
		var tag        = NpcBrainHelpers.Str( p, "tag", "waypoint" );
		var snap       = NpcBrainHelpers.Bool( p, "snapToGround", true );

		try
		{
			// Resolve or create the route parent.
			GameObject route = null;
			if ( p.TryGetProperty( "parentId", out var pid ) && Guid.TryParse( pid.GetString(), out var parentGuid ) )
				route = scene.Directory.FindByGuid( parentGuid );

			if ( route == null )
			{
				route = scene.CreateObject( true );
				route.Name = routeName;
				// Place the parent at the centroid for a tidy hierarchy + easy framing.
				var centroid = Vector3.Zero;
				foreach ( var pt in rawPoints ) centroid += pt;
				route.WorldPosition = centroid / rawPoints.Count;
			}

			var waypointIds = new List<string>( rawPoints.Count );
			int i = 0;
			foreach ( var pt in rawPoints )
			{
				var pos = pt;
				if ( snap )
				{
					try
					{
						var tr = scene.Trace.Ray( pos + Vector3.Up * 2000f, pos + Vector3.Down * 20000f ).Run();
						if ( tr.Hit ) pos = new Vector3( pos.x, pos.y, tr.HitPosition.z );
					}
					catch { /* keep the raw point on trace failure */ }
				}

				var wp = scene.CreateObject( true );
				wp.Name = $"{routeName}_WP{i}";
				wp.WorldPosition = pos;
				wp.Tags.Add( tag );
				wp.SetParent( route, keepWorldPosition: true );
				waypointIds.Add( wp.Id.ToString() );
				i++;
			}

			return Task.FromResult<object>( new
			{
				placed     = true,
				routeId    = route.Id.ToString(),
				routeName  = route.Name,
				waypointIds,
				count      = waypointIds.Count,
				snappedToGround = snap,
				note = "Wire these into an NpcBrain with assign_patrol_route (pass routeId or waypointIds). " +
				       "Validate connectivity with get_navmesh_path between consecutive waypoints (catches a point in a wall)."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"place_patrol_route failed: {ex.Message}" } );
		}
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  3. assign_patrol_route  (scene-mutating)
//     Wire a placed route (or an arbitrary GUID list) into a List<GameObject>
//     property (default "Waypoints") on a target NPC's component. This is the
//     list-of-GameObject-refs case plain set_property can't express.
// ═══════════════════════════════════════════════════════════════════════════
public class AssignPatrolRouteHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "npcId", out var npcEl ) || !Guid.TryParse( npcEl.GetString(), out var npcGuid ) )
			return Task.FromResult<object>( new { error = "npcId (GameObject GUID holding the NpcBrain) is required" } );

		var npc = scene.Directory.FindByGuid( npcGuid );
		if ( npc == null )
			return Task.FromResult<object>( new { error = $"NPC GameObject not found: {npcEl.GetString()}" } );

		var property = NpcBrainHelpers.Str( p, "property", "Waypoints" );

		try
		{
			// ── Gather the ordered waypoint GameObjects: explicit waypointIds win,
			//    else the children (hierarchy order) of routeId.
			var waypoints = new List<GameObject>();

			if ( p.TryGetProperty( "waypointIds", out var wpArr ) && wpArr.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in wpArr.EnumerateArray() )
					if ( Guid.TryParse( e.GetString(), out var g ) )
					{
						var go = scene.Directory.FindByGuid( g );
						if ( go != null ) waypoints.Add( go );
					}
			}
			else if ( p.TryGetProperty( "routeId", out var routeEl ) && Guid.TryParse( routeEl.GetString(), out var routeGuid ) )
			{
				var route = scene.Directory.FindByGuid( routeGuid );
				if ( route == null )
					return Task.FromResult<object>( new { error = $"Route GameObject not found: {routeEl.GetString()}" } );
				foreach ( var child in route.Children )
					waypoints.Add( child );
			}
			else
			{
				return Task.FromResult<object>( new { error = "Provide waypointIds (GUID[]) or routeId (route parent GUID)" } );
			}

			if ( waypoints.Count == 0 )
				return Task.FromResult<object>( new { error = "No valid waypoints resolved from the given ids/route" } );

			// ── Resolve the component + property and set the List<GameObject>.
			//    SetValue accepts a List<GameObject>; we hand it the concrete list
			//    (matches how the editor serializes [Property] lists of refs).
			var comp = NpcBrainHelpers.SetComponentProperty( npc, property, waypoints );
			if ( comp == null )
				return Task.FromResult<object>( new { error = $"No component on the NPC exposes a '{property}' property (expected an NpcBrain with a List<GameObject> {property})" } );

			return Task.FromResult<object>( new
			{
				assigned  = true,
				npcId     = npcEl.GetString(),
				component = comp.GetType().Name,
				property,
				count     = waypoints.Count,
				note = "List<GameObject> refs may read back as handles/GUIDs via get_property — trust this count, or confirm patrol in play mode."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"assign_patrol_route failed: {ex.Message}" } );
		}
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  4. create_npc_spawner  (code-gen; scene-mutating)
//     Generate a spawner Component that clones an NPC prefab over time / in
//     escalating waves at spawn points, capped by maxAlive. Host-authoritative
//     when networked (NetworkSpawn, guarded).
// ═══════════════════════════════════════════════════════════════════════════
public class CreateNpcSpawnerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var name      = NpcBrainHelpers.Str( p, "name", "NpcSpawner" );
			var directory = NpcBrainHelpers.Str( p, "directory", "Code" );

			var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
			if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
				return Task.FromResult<object>( new { error = pathErr } );

			if ( File.Exists( fullPath ) )
				return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

			var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );

			var mode       = NpcBrainHelpers.Str( p, "mode", "waves" ).ToLowerInvariant();
			if ( mode != "continuous" && mode != "waves" && mode != "burst" ) mode = "waves";
			var modeEnum   = mode == "continuous" ? "Continuous" : ( mode == "burst" ? "Burst" : "Waves" );

			var count      = NpcBrainHelpers.Int(   p, "count", 5 );
			var interval   = NpcBrainHelpers.Float( p, "interval", 8f );
			var waveCount  = NpcBrainHelpers.Int(   p, "waveCount", 3 );
			var waveGrowth = NpcBrainHelpers.Float( p, "waveGrowth", 1f );
			var radius     = NpcBrainHelpers.Float( p, "radius", 200f );
			var maxAlive   = NpcBrainHelpers.Int(   p, "maxAlive", 12 );
			var networked  = NpcBrainHelpers.Bool(  p, "networked", true );

			var code = BuildSpawnerSource( className, modeEnum, networked,
				count, interval, waveCount, waveGrowth, radius, maxAlive );

			Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
			File.WriteAllText( fullPath, code );

			var props = new[]
			{
				"NpcPrefab","SpawnPoints","Mode","Count","Interval","WaveCount",
				"WaveGrowth","Radius","MaxAlive","AutoStart"
			};

			return Task.FromResult<object>( new
			{
				created   = true,
				path      = $"{directory}/{fileName}",
				className,
				mode,
				networked,
				propertyNames = props,
				note = "Set NpcPrefab via set_prefab_ref. Add spawn points by reusing place_patrol_route (a route of empties) then " +
				       "assign_patrol_route with property=\"SpawnPoints\", or set SpawnPoints by hand. " +
				       ( networked
				         ? "Networked spawns use NetworkSpawn() and are host-only (guarded) — needs a host session."
				         : "Solo build: plain Clone() (no NetworkSpawn)." ) +
				       " Verify by watching GameObject count over time in play mode (get_scene_hierarchy deltas)."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_npc_spawner failed: {ex.Message}" } );
		}
	}

	private static string BuildSpawnerSource(
		string className, string modeEnum, bool networked,
		int count, float interval, int waveCount, float waveGrowth, float radius, int maxAlive )
	{
		string F( float v ) => NpcBrainHelpers.F( v );

		var proxyGuard = networked ? "\t\tif ( IsProxy ) return;   // host spawns authoritatively\n" : "";
		var headerNote = networked
			? "// Host-authoritative spawner. Only the host spawns (NetworkSpawn so clients see the\n// NPCs). Needs an active network session.\n"
			: "// Solo / edit-scene spawner (plain Clone, no networking).\n";

		// Spawn idiom: clone the prefab, place it, and (networked) NetworkSpawn in a
		// try/catch — the verified solo-safe idiom (NetworkSpawn throws with no session).
		var spawnBody = networked
			?
@"		var go = NpcPrefab.Clone( pos );
		try { go.NetworkSpawn(); } catch { /* no session — fall back to a local object */ }
		_alive.Add( go );"
			:
@"		var go = NpcPrefab.Clone( pos );
		_alive.Add( go );";

		return
$@"using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

{headerNote}public sealed class {className} : Component
{{
	public enum SpawnMode {{ Continuous, Waves, Burst }}

	[Property] public GameObject NpcPrefab {{ get; set; }}
	[Property] public List<GameObject> SpawnPoints {{ get; set; }} = new();

	[Property] public SpawnMode Mode {{ get; set; }} = SpawnMode.{modeEnum};
	[Property] public int   Count      {{ get; set; }} = {count};   // per-wave (Waves) or total (Burst/Continuous batch)
	[Property] public float Interval   {{ get; set; }} = {F( interval )}; // seconds between spawns (Continuous) or waves (Waves)
	[Property] public int   WaveCount  {{ get; set; }} = {waveCount};
	[Property] public float WaveGrowth {{ get; set; }} = {F( waveGrowth )}; // multiply Count each wave (>1 = escalating)
	[Property] public float Radius     {{ get; set; }} = {F( radius )};  // random scatter around a spawn point
	[Property] public int   MaxAlive   {{ get; set; }} = {maxAlive};   // concurrency cap
	[Property] public bool  AutoStart  {{ get; set; }} = true;

	private readonly List<GameObject> _alive = new();
	private TimeSince _timeSinceSpawn;
	private int _wavesDone;
	private float _currentWaveCount;
	private bool _started;

	protected override void OnStart()
	{{
		_currentWaveCount = Count;
		_timeSinceSpawn = Interval; // fire promptly on the first eligible tick
		if ( AutoStart ) _started = true;
	}}

	protected override void OnUpdate()
	{{
{proxyGuard}		if ( !_started || NpcPrefab == null ) return;

		// Drop dead/destroyed NPCs from the live list so MaxAlive is accurate.
		_alive.RemoveAll( g => !g.IsValid() );

		switch ( Mode )
		{{
			case SpawnMode.Burst:
				SpawnBatch( (int)_currentWaveCount );
				_started = false; // one-shot
				break;

			case SpawnMode.Continuous:
				if ( _timeSinceSpawn >= Interval )
				{{
					_timeSinceSpawn = 0f;
					TrySpawnOne();
				}}
				break;

			case SpawnMode.Waves:
				if ( _wavesDone >= WaveCount ) {{ _started = false; break; }}
				if ( _timeSinceSpawn >= Interval )
				{{
					_timeSinceSpawn = 0f;
					SpawnBatch( (int)_currentWaveCount );
					_wavesDone++;
					_currentWaveCount = MathX.Clamp( _currentWaveCount * WaveGrowth, 1f, 9999f );
				}}
				break;
		}}
	}}

	private void SpawnBatch( int n )
	{{
		for ( int i = 0; i < n; i++ )
			if ( !TrySpawnOne() ) break;
	}}

	private bool TrySpawnOne()
	{{
		if ( _alive.Count >= MaxAlive ) return false;

		var pos = PickSpawnPos();
{spawnBody}
		return true;
	}}

	private Vector3 PickSpawnPos()
	{{
		var basePos = WorldPosition;
		if ( SpawnPoints != null && SpawnPoints.Count > 0 )
		{{
			var pick = SpawnPoints[Random.Shared.Next( 0, SpawnPoints.Count )];
			if ( pick.IsValid() ) basePos = pick.WorldPosition;
		}}

		var off = new Vector3(
			Random.Shared.Float( -Radius, Radius ),
			Random.Shared.Float( -Radius, Radius ),
			0f );
		return basePos + off;
	}}
}}
";
	}
}

// ═══════════════════════════════════════════════════════════════════════════
//  5. simulate_npc_perception  (READ-ONLY — NOT scene-mutating)
//     Run the EXACT LOS check an NpcBrain would, in edit mode, without play.
//     FOV cone (dot vs CosFovThreshold) + range + occlusion trace. Reports the
//     result AND why — the keystone edit-mode verifier for the perception layer.
// ═══════════════════════════════════════════════════════════════════════════
public class SimulateNpcPerceptionHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "npcId", out var npcEl ) || !Guid.TryParse( npcEl.GetString(), out var npcGuid ) )
			return Task.FromResult<object>( new { error = "npcId (GameObject GUID with an NpcBrain) is required" } );

		var npc = scene.Directory.FindByGuid( npcGuid );
		if ( npc == null )
			return Task.FromResult<object>( new { error = $"NPC GameObject not found: {npcEl.GetString()}" } );

		try
		{
			// ── Read perception params from the NPC's brain if present, else fall back
			//    to spec defaults / explicit overrides in the call. Matches the brain by
			//    CAPABILITY (exposes SightRange+FovDegrees) or a "...Brain" type name — NOT
			//    just the literal type name "NpcBrain" — so a custom-named brain
			//    (e.g. BigfootBrain) is read instead of silently using defaults.
			var brain = NpcBrainHelpers.FindPerceptionBrain( npc );
			// `var` (never name TypeDescription) — its namespace isn't guaranteed importable here.
			var brainTd = brain != null ? Game.TypeLibrary.GetType( brain.GetType().Name ) : null;

			float ReadBrainFloat( string name, float fallback )
			{
				if ( brain == null || brainTd == null ) return fallback;
				var pd = brainTd.Properties.FirstOrDefault( x => x.Name == name );
				if ( pd == null ) return fallback;
				try
				{
					var v = pd.GetValue( brain );
					if ( v is float f ) return f;
					if ( v != null && float.TryParse( v.ToString(), out var fp ) ) return fp;
				}
				catch { }
				return fallback;
			}
			string ReadBrainString( string name, string fallback )
			{
				if ( brain == null || brainTd == null ) return fallback;
				var pd = brainTd.Properties.FirstOrDefault( x => x.Name == name );
				try { return pd?.GetValue( brain )?.ToString() ?? fallback; } catch { return fallback; }
			}

			// Explicit overrides take precedence over brain-read values.
			float sightRange = NpcBrainHelpers.Float( p, "sightRange", ReadBrainFloat( "SightRange", 1500f ) );
			float fovDegrees = NpcBrainHelpers.Float( p, "fovDegrees", ReadBrainFloat( "FovDegrees", 110f ) );
			float eyeHeight  = NpcBrainHelpers.Float( p, "eyeHeight",  ReadBrainFloat( "EyeHeight", 64f ) );
			string targetTag = NpcBrainHelpers.Str(   p, "targetTag",  ReadBrainString( "TargetTag", "player" ) );

			// Use the brain's baked CosFovThreshold if available (keeps this query in
			// lockstep with the generated component); else compute it here.
			float cosFov = ReadBrainFloat( "CosFovThreshold", float.NaN );
			if ( float.IsNaN( cosFov ) ) cosFov = NpcBrainHelpers.CosHalfFov( fovDegrees );

			// ── Resolve the target point: explicit targetId or a raw point.
			GameObject targetGo = null;
			Vector3 targetPos;
			if ( p.TryGetProperty( "targetId", out var tEl ) && Guid.TryParse( tEl.GetString(), out var tGuid ) )
			{
				targetGo = scene.Directory.FindByGuid( tGuid );
				if ( targetGo == null )
					return Task.FromResult<object>( new { error = $"Target GameObject not found: {tEl.GetString()}" } );
				targetPos = targetGo.WorldPosition;
			}
			else if ( p.TryGetProperty( "point", out var ptEl ) )
			{
				targetPos = ClaudeBridge.ParseVector3( ptEl );
			}
			else
			{
				return Task.FromResult<object>( new { error = "Provide targetId (GameObject GUID) or point (Vector3)" } );
			}

			var eye = npc.WorldPosition + Vector3.Up * eyeHeight;
			var to  = targetPos - eye;
			float distance = to.Length;

			// Degenerate: target is essentially at the eye.
			if ( distance < 0.01f )
			{
				return Task.FromResult<object>( new
				{
					canSee = true, inRange = true, inFov = true, losBlocked = false,
					distance, angleDeg = 0.0,
					eye = new { eye.x, eye.y, eye.z },
					note = "Target coincides with the NPC eye position."
				} );
			}

			var dir = to.Normal;
			float dot = Vector3.Dot( npc.WorldRotation.Forward, dir );

			// angle (degrees) for human-readable output. MathF is fine here (editor).
			float angleDeg = MathF.Acos( Math.Clamp( dot, -1f, 1f ) ) * ( 180f / MathF.PI );

			bool inRange = distance <= sightRange;
			bool inFov   = dot >= cosFov;

			// Occlusion trace from the eye toward the target. IgnoreGameObjectHierarchy
			// drops the NPC's own colliders (confirmed builder), so any hit is an
			// external object. It blocks LOS only if it's clearly before the target
			// (hit on the target itself, or a hit at/after the target distance, is not
			// a blocker). Distance test only — no GameObject.Root needed.
			bool losBlocked = false;
			object blockedBy = null;
			var tr = scene.Trace.Ray( eye, targetPos ).IgnoreGameObjectHierarchy( npc ).Run();
			if ( tr.Hit )
			{
				bool hitIsTarget = ( targetGo != null && tr.GameObject == targetGo )
					|| tr.Distance >= distance - 8f; // a hit at/after the target point isn't a blocker
				if ( !hitIsTarget )
				{
					losBlocked = true;
					blockedBy = new { id = tr.GameObject?.Id.ToString(), name = tr.GameObject?.Name };
				}
			}

			bool tagMatch = targetGo == null || targetGo.Tags.Has( targetTag );
			bool canSee = inRange && inFov && !losBlocked && tagMatch;

			return Task.FromResult<object>( new
			{
				canSee,
				inRange,
				inFov,
				losBlocked,
				blockedBy,
				tagMatch,
				distance,
				angleDeg = (double)angleDeg,
				fovHalfAngleDeg = (double)( fovDegrees * 0.5f ),
				sightRange,
				targetTag,
				eye = new { eye.x, eye.y, eye.z },
				brainComponent = brain?.GetType().Name,
				note = brain == null
					? "No perception brain found on this GameObject — used spec defaults / call overrides for the perception params."
					: $"Read perception params from the '{brain.GetType().Name}' component's own SightRange/FovDegrees/EyeHeight/TargetTag (call params override). canSee mirrors what the generated brain computes."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"simulate_npc_perception failed: {ex.Message}" } );
		}
	}
}
