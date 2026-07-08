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
//  Networking primitives pack (v1.20.0, Track B) -- four multiplayer scaffolds
//  (code-gen; scene-mutating):
//
//    create_host_rpc_action       validated + rate-limited [Rpc.Host] action skeleton
//    add_targeted_rpc             Rpc.FilterInclude single-client (unicast) side-effect
//    create_local_player_resolver proxy-safe "who is MY player" resolver (online + offline)
//    add_host_migration_recovery  proxy->authority transition detector + OnBecameHost hook
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, SerializeGo) and ScaffoldHelpers (PrepareCodeFile /
//  WriteCode / Utf8NoBom). Handler code here is UNSANDBOXED editor code.
//
//  The C# *strings these handlers WRITE TO DISK* are SANDBOXED game code and must
//  obey the s&box sandbox rules:
//    - System.Math/MathF/MathX all compile on the current SDK; Array.Clone() is
//      whitelist-blocked (not used here).
//    - Fully-qualify System.Collections.Generic.Dictionary (dodges a missing using).
//    - TimeSince/TimeUntil for timers; float literals formatted InvariantCulture + 'f'.
//    - Guard Networking access: check Networking.IsActive before Networking.IsHost
//      (IsHost can throw with no session). Rpc.Caller re-resolved host-side, never
//      trusting client args for identity.
//    - VERIFIED live against the installed SDK before codegen (describe_type +
//      networking-authority cookbook): Connection.Local (static), Connection.All,
//      Connection.SteamId (NOTE: Connection has NO IsValid member on this SDK —
//      null-check it; caught live by the v1.20.0 verify-gate), Rpc.Caller (Connection) / Rpc.CallerId (Guid),
//      Rpc.FilterInclude(Connection) -> IDisposable, GameObject.Network (NetworkAccessor)
//      -> Owner (Connection) / OwnerId (Guid) / IsOwner / IsProxy, [Sync(SyncFlags.FromHost)],
//      [Rpc.Host] / [Rpc.Broadcast], (ulong)SteamId cast.
//
//  Register(...) lines + the _sceneMutatingCommands additions live in
//  MyEditorMenu.cs (Batch 45) to keep the files decoupled.
// =============================================================================

// -----------------------------------------------------------------------------
// create_host_rpc_action -- the validated, rate-limited host-action skeleton.
//
// The safe answer to "a client asks the host to DO something": a client-callable
// Request() forwards to an [Rpc.Host] body that re-resolves the caller via
// Rpc.Caller (NEVER trusting client args for identity), enforces a per-SteamId
// cooldown from a Dictionary<ulong, TimeSince>, runs a clearly-marked TODO hook,
// and fires a static OnActionExecuted event. Covers the backlog's
// add_rate_limited_rpc.
// -----------------------------------------------------------------------------
public class CreateHostRpcActionHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "HostRpcAction", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float cooldown = p.TryGetProperty( "cooldownSeconds", out var cv ) && cv.TryGetSingle( out var cf ) ? cf : 1f;
			if ( cooldown < 0f ) cooldown = 0f;   // a negative cooldown would emit nonsense

			var code = BuildCode( className, cooldown, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = NetPrimitivesHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				cooldownSeconds = cooldown,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach it to the object that owns this action (a player, a station, or your game manager): add_component_with_properties (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Fire it from the owning client (input / UI button): GetComponent<{className}>()?.Request(); -- it routes to the host, which re-validates and rate-limits.",
					$"Fill in the TODO host block with your authoritative action (spend currency, NetworkSpawn, grant a reward). Re-clamp any gameplay args there -- forged client args bypass NetFlags.",
					$"React to accepted actions: {className}.OnActionExecuted += conn => Log.Info( $\"action by {{conn.DisplayName}}\" ); (fires on the host). Wrap an [Rpc.Broadcast] if every client should react.",
					"Tune CooldownSeconds with set_property. The per-SteamId cooldown is host-only runtime state (not [Sync])."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_host_rpc_action failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float cooldown, System.Globalization.CultureInfo ci )
	{
		string cd = cooldown.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- a validated, rate-limited host action. The safe skeleton for
/// ""a client asks the host to DO something"" (buy, use, vote, interact).
///
/// Flow:  client calls Request()  -&gt;  [Rpc.Host] SubmitRequest() runs ON THE HOST
///        -&gt;  host re-resolves WHO called it via Rpc.Caller (never trusts client
///        args for identity)  -&gt;  enforces a per-SteamId cooldown  -&gt;  runs your
///        host-authoritative action  -&gt;  fires OnActionExecuted.
///
/// [Rpc.Host] is callable by ANY client with forged args -- NetFlags restrict who
/// may INVOKE, which is not security. That is why identity + cooldown + your
/// validation all live INSIDE the host body. Single-player safe (no session -&gt; the
/// RPC just runs locally; the caller falls back to Connection.Local).
///
/// Usage:
///   GetComponent&lt;{className}&gt;()?.Request();   // from input / a UI button, on the owning client
///   {className}.OnActionExecuted += conn =&gt; Log.Info( $""action by {{conn.DisplayName}}"" );
/// </summary>
public sealed class {className} : Component
{{
	/// <summary>Minimum seconds between accepted requests, per calling player.</summary>
	[Property] public float CooldownSeconds {{ get; set; }} = {cd};

	/// <summary>Fires ON THE HOST after an accepted request. Arg = the validated caller.</summary>
	public static Action<Connection> OnActionExecuted {{ get; set; }}

	// Host-only runtime state: last-accept time keyed by the caller's SteamId.
	// NOT [Sync] -- it is the host's own rate-limit bookkeeping, never replicated.
	private readonly System.Collections.Generic.Dictionary<ulong, TimeSince> _cooldowns = new();

	/// <summary>
	/// Client entry point. Call this on the owning client (input handler / UI button).
	/// It routes to the host; do NOT put authoritative logic here -- a client controls
	/// this machine and could call anything. The real work happens host-side.
	/// </summary>
	public void Request()
	{{
		SubmitRequest();   // [Rpc.Host] -- executes on the host (or locally in solo)
	}}

	/// <summary>
	/// Host-authoritative handler. Public so the RPC source generator is happy; the
	/// re-validation below is what actually protects it. NEVER trust args passed from
	/// the client for identity -- re-resolve the caller here.
	/// </summary>
	[Rpc.Host]
	public void SubmitRequest()
	{{
		// Re-resolve the caller SERVER-SIDE. Read Rpc.Caller only when a session is
		// active (offline it is meaningless); fall back to us in solo.
		var caller = Networking.IsActive ? Rpc.Caller : Connection.Local;
		if ( caller == null ) caller = Connection.Local;
		if ( caller == null ) return;   // no identity at all -- refuse

		// FOOTGUN (some SDK builds): Rpc.Caller can return the HOST's own connection
		// for a proxy-initiated call. If identity is security-critical, resolve the
		// acting player from the OWNING component's Network.Owner instead.

		ulong callerId = (ulong)caller.SteamId;

		// Per-SteamId rate limit -- spamming the RPC cannot bypass the cooldown.
		if ( _cooldowns.TryGetValue( callerId, out var since ) && since < CooldownSeconds )
			return;   // still cooling down for this caller
		_cooldowns[callerId] = 0f;   // reset this caller's timer

		// --- TODO: your host-authoritative action goes here ---------------------
		// Runs ONLY on the host. Re-validate + re-clamp any gameplay values, then
		// mutate [Sync(SyncFlags.FromHost)] state / NetworkSpawn() / grant rewards.
		// Example: GetComponent<Wallet>()?.AddMoney( 10 );
		// ------------------------------------------------------------------------

		OnActionExecuted?.Invoke( caller );
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// add_targeted_rpc -- the Rpc.FilterInclude single-client (unicast) pattern.
//
// A host-side SendTo(Connection, string) wraps an [Rpc.Broadcast] call in
// using ( Rpc.FilterInclude( target ) ) so ONLY that one connection executes the
// body, which raises a static OnReceived event.
// -----------------------------------------------------------------------------
public class AddTargetedRpcHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "TargetedRpc", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var code = BuildCode( className );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = NetPrimitivesHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach it to a networked manager object: add_component_with_properties (component=\"{className}\") after the hotload, or re-run with targetId. The object must be NetworkSpawn'd for the RPC to route.",
					$"Send to ONE player from the host: GetComponent<{className}>()?.SendTo( player.Network.Owner, \"You're up next!\" ); -- only that client runs the body.",
					$"Receive on the target: {className}.OnReceived += msg => ShowToast( msg ); -- fires only on the filtered client (and locally in solo).",
					"Use this instead of [Rpc.Broadcast] + a client-side 'is this for me?' check -- FilterInclude scopes it server-side, so no data leaks and no wasted bandwidth."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_targeted_rpc failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className )
	{
		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- send a message to exactly ONE client using Rpc.FilterInclude.
///
/// A normal [Rpc.Broadcast] runs on EVERY machine. Wrapping the call in
/// using ( Rpc.FilterInclude( target ) ) scopes it server-side so ONLY the target
/// connection executes the RPC body -- the right way to unicast (a private prompt,
/// a personal reward toast, a per-player cutscene) instead of broadcasting to all
/// and filtering on the client (which leaks data + wastes bandwidth).
///
/// Call SendTo on the host. Single-player safe (with no session it just runs locally).
///
/// Usage (host-side):
///   GetComponent&lt;{className}&gt;()?.SendTo( somePlayer.Network.Owner, ""You're up next!"" );
///   {className}.OnReceived += msg =&gt; Log.Info( $""(only me) {{msg}}"" );
/// </summary>
public sealed class {className} : Component
{{
	/// <summary>Fires on the TARGET client only (and locally in solo) when a message arrives.</summary>
	public static Action<string> OnReceived {{ get; set; }}

	/// <summary>
	/// Host-side: deliver <paramref name=""message""/> to exactly one connection.
	/// FilterInclude scopes the broadcast so only <paramref name=""target""/> runs it.
	/// </summary>
	public void SendTo( Connection target, string message )
	{{
		if ( target == null ) return;

		// Only the host should originate a targeted message in a host-authoritative
		// game. Guarded behind IsActive because Networking.IsHost can throw with no
		// session; in solo this falls through and just runs locally.
		if ( Networking.IsActive && !Networking.IsHost ) return;

		using ( Rpc.FilterInclude( target ) )
			Receive( message );
	}}

	/// <summary>
	/// The unicast body. Public so the RPC source generator is happy. Runs ONLY on the
	/// filtered target connection (FilterInclude decided that server-side).
	/// </summary>
	[Rpc.Broadcast]
	public void Receive( string message )
	{{
		OnReceived?.Invoke( message );
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_local_player_resolver -- proxy-safe "who is MY player".
//
// Static Local property that lazily finds the player GameObject owned by the local
// connection ( Network.Owner == Connection.Local, or Network.IsOwner ) when
// networking is active, and falls back to the first/only tagged player when it is
// NOT (offline/solo). Cached with an IsValid() revalidation. The corpus footgun
// killer -- running "my player" logic against a proxy of someone else's player.
// -----------------------------------------------------------------------------
public class CreateLocalPlayerResolverHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "LocalPlayerResolver", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var tag = p.TryGetProperty( "playerTag", out var tv ) && !string.IsNullOrWhiteSpace( tv.GetString() )
				? tv.GetString().Trim() : "player";
			var tagLiteral = NetPrimitivesHelpers.EscapeStringLiteral( tag );

			var code = BuildCode( className, tagLiteral );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = NetPrimitivesHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				playerTag = tag,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach ONE to a persistent object (your game manager): add_component_with_properties (component=\"{className}\") after the hotload, or re-run with targetId. Placing it lets you set PlayerTag in the inspector.",
					$"Tag each player GameObject with \"{tag}\" (set_tags) so the resolver can find them.",
					$"Read your player from anywhere: var me = {className}.Local; -- online it is the object you OWN, offline it is the only player. Cached + revalidated automatically.",
					$"Filter events to your own player: if ( {className}.IsLocal( someGameObject ) ) {{ ... }} -- kills the 'ran my UI/logic against a proxy' footgun."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_local_player_resolver failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string tagLiteral )
	{
		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- ""who is MY player?"", the proxy-safe way. Resolves the player
/// GameObject that belongs to THIS machine, both online and offline.
///
/// Online: your player is the tagged object whose Network.Owner is the local
/// connection ( Network.Owner == Connection.Local, or Network.IsOwner ). Offline /
/// solo (no session), there is exactly one player, so it returns the first tagged
/// object. The result is cached and revalidated with IsValid() so a destroyed /
/// respawned player is re-resolved automatically.
///
/// Attach ONE of these to a persistent object (your game manager) so PlayerTag is
/// configurable; the resolver itself is static and callable from anywhere:
///   var me = {className}.Local;                 // my player GameObject (or null)
///   if ( {className}.IsLocal( someGo ) ) ...    // filter events to my own player
///
/// This kills the #1 multiplayer footgun -- running ""my player"" logic against a
/// proxy of someone else's player.
/// </summary>
public sealed class {className} : Component
{{
	/// <summary>Tag that marks a player GameObject. Players must carry this tag.</summary>
	[Property] public string PlayerTag {{ get; set; }} = ""{tagLiteral}"";

	private static {className} _instance;
	private static string _tag = ""{tagLiteral}"";
	private static GameObject _cached;

	protected override void OnEnabled()
	{{
		_instance = this;
		_tag = PlayerTag;
	}}

	protected override void OnDisabled()
	{{
		if ( _instance == this ) _instance = null;
	}}

	/// <summary>The local machine's player GameObject, or null if not found yet.</summary>
	public static GameObject Local
	{{
		get
		{{
			if ( IsLocal( _cached ) ) return _cached;   // cache hit, still valid + still ours
			_cached = Resolve();
			return _cached;
		}}
	}}

	/// <summary>True if <paramref name=""go""/> is the local machine's player.</summary>
	public static bool IsLocal( GameObject go )
	{{
		if ( !go.IsValid() ) return false;
		if ( !Networking.IsActive ) return true;   // solo: the only player is mine
		return go.Network.Owner == Connection.Local || go.Network.IsOwner;
	}}

	private static GameObject Resolve()
	{{
		var scene = Game.ActiveScene;
		if ( !scene.IsValid() ) return null;

		if ( !Networking.IsActive )
		{{
			// Offline / solo: the first tagged player is ours.
			foreach ( var go in scene.GetAllObjects( true ) )
				if ( go.Tags.Has( _tag ) ) return go;
			return null;
		}}

		// Online: our player is the tagged object owned by the local connection.
		foreach ( var go in scene.GetAllObjects( true ) )
		{{
			if ( !go.Tags.Has( _tag ) ) continue;
			if ( go.Network.Owner == Connection.Local || go.Network.IsOwner )
				return go;
		}}
		return null;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// add_host_migration_recovery -- proxy->authority transition detector.
//
// Tracks previous IsProxy each frame; when it flips from true to false (we became
// the authority for this object, i.e. host migration promoted us), it fires a
// static OnBecameHost event and runs a virtual-style TODO rebuild hook, then -- a
// short settle delay later -- a deferred validation hook. Inert offline (IsProxy
// is always false with no session).
// -----------------------------------------------------------------------------
public class AddHostMigrationRecoveryHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "HostMigrationRecovery", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			// Settle delay is fixed at the cookbook-recommended ~1s but exposed as a
			// [Property] so it is tunable; no param for it (keeps the schema to name/directory).
			float settle = 1f;

			var code = BuildCode( className, settle, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = NetPrimitivesHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach it to your host-authoritative manager object: add_component_with_properties (component=\"{className}\") after the hotload, or re-run with targetId. The object should be NetworkSpawn'd.",
					$"React to becoming host: {className}.OnBecameHost += go => Log.Info( \"I am the host now -- rebuilding\" );",
					"Fill in the RebuildAfterMigration() TODO region: re-arm host-only loops/timers against your clock, TakeOwnership of orphans, rebuild handle maps by world position, reconcile your [Sync] registry against the real scene.",
					"Fill in the deferred ValidateAfterMigration() TODO: sanity-check expected-vs-actual and hard-reset the round if it looks corrupt (SettleSeconds delay lets in-flight packets land first).",
					"Requires a real host migration to fire (a second client that becomes host when the first leaves) -- it is inert in solo/offline play."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_host_migration_recovery failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float settle, System.Globalization.CultureInfo ci )
	{
		string st = settle.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- detects when THIS machine takes authority over this object
/// (proxy -&gt; owner), which is what happens to a host-authoritative manager during
/// host migration, and gives you a clean hook to rebuild host-only state.
///
/// It tracks IsProxy each frame; when it flips from true (someone else was the
/// authority) to false (now it is us), it fires OnBecameHost and runs the rebuild
/// hook, then -- after a short settle delay so in-flight packets can land -- runs a
/// deferred validation hook. Inert offline (IsProxy is always false with no session).
///
/// Attach to your host-authoritative manager object. Fill in the two TODO regions.
///
/// Usage:
///   {className}.OnBecameHost += go =&gt; Log.Info( ""I am the host now -- rebuilding"" );
/// </summary>
public sealed class {className} : Component
{{
	/// <summary>Seconds to wait after becoming host before the deferred validation runs.</summary>
	[Property] public float SettleSeconds {{ get; set; }} = {st};

	/// <summary>Fires on the machine that just gained authority. Arg = this GameObject.</summary>
	public static Action<GameObject> OnBecameHost {{ get; set; }}

	private bool _wasProxy;
	private bool _initialized;
	private bool _pendingValidate;
	private TimeSince _sinceBecameHost;

	protected override void OnEnabled()
	{{
		_wasProxy = IsProxy;   // baseline so we only fire on a real transition
		_initialized = true;
	}}

	protected override void OnUpdate()
	{{
		bool proxyNow = IsProxy;
		if ( _initialized && _wasProxy && !proxyNow )
			BecameHost();
		_wasProxy = proxyNow;

		if ( _pendingValidate && _sinceBecameHost > SettleSeconds )
		{{
			_pendingValidate = false;
			ValidateAfterMigration();
		}}
	}}

	private void BecameHost()
	{{
		_sinceBecameHost = 0f;
		_pendingValidate = true;
		RebuildAfterMigration();
		OnBecameHost?.Invoke( GameObject );
	}}

	// virtual-style rebuild hook -- edit this body (the component is sealed, so there
	// is nothing to override; this region IS your override point).
	private void RebuildAfterMigration()
	{{
		// TODO: rebuild host-only state now that YOU are the authority. The previous
		// host is gone; anything it owned or was mid-computing is now your job. Typical
		// moves (networking-authority cookbook, pattern 17):
		//   - Re-arm host-only loops / spawners. A [Sync] TimeUntil stores the DEAD
		//     host's clock epoch -- read its .Relative remaining and re-arm it here.
		//   - Network.TakeOwnership() any orphaned objects you must now manage/destroy.
		//   - Rebuild handle->handle maps by world-position matching (object Ids do not
		//     survive migration).
		//   - Reconcile your [Sync] registry against the REAL scene (drop dead entries,
		//     add visible objects the list is missing).
	}}

	// deferred sanity check -- runs SettleSeconds after becoming host so in-flight
	// packets that have not applied yet do not make a healthy scene look broken.
	private void ValidateAfterMigration()
	{{
		// TODO: compare expected-vs-actual (child counts, roster tags) and hard-reset
		// the round rather than limping along if it looks corrupt. (cookbook pattern 17)
	}}
}}
";
	}
}

/// <summary>
/// Shared helpers for the networking-primitives handlers -- mirrors the standard
/// scaffold placement (GameFeelHelpers / create_event_director) plus a tiny
/// string-literal escaper for baked-in tag defaults.
/// </summary>
internal static class NetPrimitivesHelpers
{
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
			note = $"Generated {className}.cs but it is not in the TypeLibrary yet -- trigger_hotload, then add it with add_component_with_properties.";
			return null;
		}
		try { go.Components.Create( typeDesc ); return ClaudeBridge.SerializeGo( go ); }
		catch ( Exception ex ) { note = $"Placement failed ({ex.Message})."; return null; }
	}

	/// <summary>Escape a user string so it can be baked as a C# double-quoted literal.</summary>
	public static string EscapeStringLiteral( string s )
	{
		if ( string.IsNullOrEmpty( s ) ) return "player";
		return s.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
	}
}
