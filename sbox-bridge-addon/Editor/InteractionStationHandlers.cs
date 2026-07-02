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
//  add_interaction_station  (code-gen; scene-mutating)
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
//    - guard networking with IsProxy (Networking.IsHost can throw with no session);
//      clients request host state changes through an [Rpc.Host], never write it directly.
//    - only sandbox-proven APIs: Component + Component.IPressable (compile-verified by
//      create_interactable), [Property], [Sync(SyncFlags.FromHost)] on a Guid + TimeUntil
//      (compile-verified by create_economy_wallet / create_round_phase_machine), IsProxy,
//      Scene.Directory.FindByGuid, [Rpc.Host] / [Rpc.Broadcast]. No System.Net, no filesystem.
//
//  DESIGN: a "station" prop (crafting bench / shop till / arcade cabinet) that ONE
//  user occupies at a time. Corpus-grounded choices:
//    - GameObject / Connection are NOT [Sync]-able (networking-authority cookbook,
//      gotcha table) -- so occupancy is stored as a [Sync(SyncFlags.FromHost)] Guid
//      (the occupant's GameObject Id) and resolved via Scene.Directory.FindByGuid.
//    - IPressable.Press() runs on the pressing CLIENT (a proxy of a host-owned station),
//      which cannot write FromHost state -- so the claim is routed to the host via an
//      [Rpc.Host] Occupy(); the host is the single authoritative writer.
//    - A reservation grace window (TimeUntil ReservationExpires) keeps the station held
//      for its last user for GraceSeconds after they leave, so a brief walk-away doesn't
//      let someone jump the queue.
//    - An optional unlock-level gate (RequiredLevel) blocks users below a level; the
//      station can't know your progression system, so it reads a static ResolveUserLevel
//      hook the game wires up (gate is inactive until wired).
//    - Opening fires a static OnStationOpened(GameObject) event (subscribe to open the
//      overlay UI), with an opt-in [Rpc.Broadcast] mirror for machines that need it.
//
//  Register(...) line + the _sceneMutatingCommands addition are ALREADY wired in
//  MyEditorMenu.cs (Batch 43) -- this file only supplies the referenced handler class.
// =============================================================================
public class AddInteractionStationHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			// ── name + directory. Honor the spec's `path`, fall back to `directory`, then "Code". ──
			var name = p.TryGetProperty( "name", out var nv ) && !string.IsNullOrWhiteSpace( nv.GetString() )
				? nv.GetString() : "InteractionStation";

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

			// ── Tunables (params override defaults). ──
			float grace    = p.TryGetProperty( "graceSeconds",  out var gv ) && gv.TryGetSingle( out var gf ) ? gf : 5f;
			int   reqLevel = p.TryGetProperty( "requiredLevel", out var rv ) && rv.TryGetInt32( out var ri )  ? ri : 0;

			// Defensive clamps so a silly value can't emit a pathological station.
			if ( grace    < 0f ) grace    = 0f;
			if ( reqLevel < 0 )  reqLevel = 0;

			var code = BuildCode( className, grace, reqLevel, ci );

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
				created       = true,
				path          = relPath,
				className,
				graceSeconds  = grace,
				requiredLevel = reqLevel,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it on your station prop: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId. Give the prop a Collider so IPressable can be raycast by the player's use key.",
					$"Subscribe to open your overlay UI: {className}.OnStationOpened += user => {{ if ( user == myLocalPlayer ) OpenStationUI(); }}; -- filter to your LOCAL player so only the presser sees the personal overlay.",
					$"Close/leave the station from your UI or a walk-away check: GetComponent<{className}>()?.Leave( myPlayerGameObject ); -- this starts the {grace.ToString( ci )}s reservation grace window for the last user.",
					reqLevel > 0
						? $"RequiredLevel is {reqLevel}: wire the gate to your progression system with {className}.ResolveUserLevel = go => go.GetComponent<YourProgress>()?.Level ?? 0; (the gate is inactive until this is set)."
						: $"RequiredLevel is 0 (no level gate). Set it with set_property and wire {className}.ResolveUserLevel to activate the gate.",
					"Tune GraceSeconds / RequiredLevel / Prompt with set_property. Occupancy is host-authoritative; multiplayer claims route through the [Rpc.Host] Occupy() -- re-validate the caller inside it for a competitive game (NetFlags is not security)."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_interaction_station failed: {ex.Message}" } );
		}
	}

	// Standard scaffold placement helper (mirrors create_event_director / create_interactable).
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

	static string BuildCode( string className, float grace, int reqLevel, System.Globalization.CultureInfo ci )
	{
		string graceStr = grace.ToString( ci ) + "f";
		string levelStr = reqLevel.ToString( ci );

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- an interaction ""station"" prop (crafting bench / shop till / arcade
/// cabinet) driven by Component.IPressable, used by ONE person at a time.
///
/// Any player controller that implements the s&amp;box ""use"" interaction (the built-in
/// PlayerController drives IPressable on the object the player looks at) calls Press()/
/// CanPress()/Hover()/Blur() with no custom player code.
///
/// OCCUPANCY is host-authoritative. GameObject / Connection are NOT [Sync]-able, so the
/// occupant is stored as a [Sync(SyncFlags.FromHost)] Guid (their GameObject Id) and
/// resolved via Scene.Directory.FindByGuid. Press() runs on the pressing CLIENT (which
/// cannot write FromHost state), so the claim is routed to the host via [Rpc.Host]
/// Occupy(). Single-player safe (IsProxy is false with no networking).
///
/// RESERVATION GRACE: when the occupant leaves, the station stays reserved for them for
/// GraceSeconds (a [Sync] TimeUntil) so a brief walk-away doesn't let someone jump the
/// queue. When the window lapses the station is free for anyone.
///
/// LEVEL GATE: users below RequiredLevel can't use the station. The station can't know
/// your progression system, so it reads the static ResolveUserLevel hook -- wire it to
/// activate the gate (it is inactive, and the station usable at any level, while unset).
///
/// OVERLAY HOOK: a successful press fires the static OnStationOpened(GameObject) event on
/// the pressing machine (subscribe to open your overlay UI). Call NotifyOpenedEveryone to
/// fan it out to all machines (e.g. an ""occupied"" indicator).
///
/// Usage:
///   {className}.OnStationOpened += user =&gt; {{ if ( user == myLocalPlayer ) OpenStationUI(); }};
///   {className}.ResolveUserLevel = go =&gt; go.GetComponent&lt;YourProgress&gt;()?.Level ?? 0;   // activate the gate
///   GetComponent&lt;{className}&gt;()?.Leave( myPlayerGameObject );   // when the UI closes / player walks away
/// </summary>
public sealed class {className} : Component, Component.IPressable
{{
	/// <summary>HUD prompt shown when a player looks at the station (your HUD reads this).</summary>
	[Property] public string Prompt {{ get; set; }} = ""Use"";

	/// <summary>Users below this level can't use the station. 0 = no gate.</summary>
	[Property] public int RequiredLevel {{ get; set; }} = {levelStr};

	/// <summary>Seconds the station stays reserved for the last user after they leave.</summary>
	[Property] public float GraceSeconds {{ get; set; }} = {graceStr};

	// Host-authoritative occupancy. Guid.Empty = free. (GameObject isn't [Sync]-able, so
	// we sync the occupant's GameObject Id and resolve it via Scene.Directory.FindByGuid.)
	[Sync( SyncFlags.FromHost )] public Guid OccupantId {{ get; set; }}

	// Reservation grace: after the occupant leaves, the station is held for the last user
	// until ReservationExpires elapses. Synced so every machine agrees on availability.
	[Sync( SyncFlags.FromHost )] public Guid ReservedForId {{ get; set; }}
	[Sync( SyncFlags.FromHost )] public TimeUntil ReservationExpires {{ get; set; }}

	/// <summary>True while someone is actively occupying the station.</summary>
	public bool IsOccupied => OccupantId != Guid.Empty;

	/// <summary>True while the station is held for a last user during the grace window.</summary>
	public bool IsReserved => !IsOccupied && ReservedForId != Guid.Empty && ReservationExpires > 0f;

	/// <summary>
	/// Fires when a user opens this station -- on the pressing machine by default, or on
	/// every machine if you call NotifyOpenedEveryone. Subscribers should filter to their
	/// LOCAL player before opening a personal overlay UI.
	/// </summary>
	public static Action<GameObject> OnStationOpened {{ get; set; }}

	/// <summary>
	/// Optional level gate. Wire this to your progression system to activate RequiredLevel,
	/// e.g. {className}.ResolveUserLevel = go =&gt; go.GetComponent&lt;YourProgress&gt;()?.Level ?? 0;
	/// While null the gate is inactive (the station is usable at any level).
	/// </summary>
	public static Func<GameObject, int> ResolveUserLevel {{ get; set; }}

	// --- IPressable surface (matches the compile-verified create_interactable) ---

	public bool CanPress( Component.IPressable.Event e )
	{{
		var user = e.Source?.GameObject;
		if ( !user.IsValid() ) return false;
		if ( RequiredLevel > 0 && ResolveUserLevel != null && ResolveUserLevel( user ) < RequiredLevel )
			return false;                       // below the unlock level
		return IsAvailableTo( user.Id );
	}}

	public bool Press( Component.IPressable.Event e )
	{{
		var user = e.Source?.GameObject;
		if ( !user.IsValid() || !CanPress( e ) ) return false;

		Occupy( user.Id );                      // route the claim to the host
		OnStationOpened?.Invoke( user );        // open the overlay locally for the presser
		return true;
	}}

	public void Hover( Component.IPressable.Event e ) {{ }}
	public void Blur( Component.IPressable.Event e ) {{ }}

	/// <summary>Is the station available to this user right now?</summary>
	public bool IsAvailableTo( Guid userId )
	{{
		if ( OccupantId == userId ) return true;        // already ours
		if ( OccupantId != Guid.Empty ) return false;   // occupied by someone else
		// Vacant: honor an unexpired reservation held for a DIFFERENT user.
		if ( ReservedForId != Guid.Empty && ReservedForId != userId && ReservationExpires > 0f )
			return false;
		return true;
	}}

	/// <summary>
	/// Host-authoritative claim, routed here from Press() on the pressing client. In a
	/// competitive game re-validate the caller here (NetFlags is not security -- map
	/// Rpc.Caller to their player object and confirm it matches userId before writing).
	/// </summary>
	[Rpc.Host]
	public void Occupy( Guid userId )
	{{
		if ( !IsAvailableTo( userId ) ) return;
		OccupantId = userId;
		ReservedForId = userId;
		ReservationExpires = 0f;                // held outright while occupied
	}}

	/// <summary>Release the station (call when the user closes the overlay or walks away).</summary>
	public void Leave( GameObject user )
	{{
		if ( user.IsValid() ) Vacate( user.Id );
	}}

	/// <summary>
	/// Host-authoritative release. Starts the reservation grace window so the last user can
	/// return within GraceSeconds before anyone else can claim the station.
	/// </summary>
	[Rpc.Host]
	public void Vacate( Guid userId )
	{{
		if ( OccupantId != userId ) return;     // only the current occupant may vacate
		OccupantId = Guid.Empty;
		ReservedForId = userId;
		ReservationExpires = GraceSeconds;      // hold it for them a little longer
	}}

	/// <summary>
	/// Optional: fire OnStationOpened on EVERY machine (host + all proxies), e.g. so other
	/// players can show an ""occupied"" indicator. Overlay subscribers must filter to their
	/// local player so only the presser gets the personal overlay.
	/// </summary>
	[Rpc.Broadcast]
	public void NotifyOpenedEveryone( Guid userId )
	{{
		var user = Scene?.Directory?.FindByGuid( userId );
		if ( user.IsValid() ) OnStationOpened?.Invoke( user );
	}}
}}
";
	}
}
