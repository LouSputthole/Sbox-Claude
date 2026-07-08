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
//  Loot / Economy variants pack (v1.20.0, Track D) -- three Tier-2 scaffolds
//  (code-gen; scene-mutating):
//
//    create_gacha_drop_table  per-rarity roll + pity counter + duplicate detection,
//                             host-authoritative ([Rpc.Host] roll -> [Rpc.Broadcast])
//    create_currency_pickup   networked coin: optional magnet, host-validated grant into
//                             create_economy_wallet's AddMoney, replicated despawn
//    create_offline_progress  DateTime delta on enable + clamp + deterministic tick replay
//                             (the idle-game staple)
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, SerializeGo) and ScaffoldHelpers (PrepareCodeFile /
//  WriteCode / Utf8NoBom). Handler code here is UNSANDBOXED editor code (System.* fine).
//
//  The C# *strings these handlers WRITE TO DISK* are SANDBOXED game code and must
//  obey the s&box sandbox rules:
//    - MathX preferred; System.Math/MathF also compile on the current SDK. Array.Clone()
//      is still whitelist-blocked (not used here); .ToArray() is the replacement.
//    - only sandbox-proven APIs:
//        * Component, [Property], List<T>/HashSet<T> (compile-verified in
//          create_weighted_loot_table / create_inventory), Game.Random.Float (loot table).
//        * [Sync(SyncFlags.FromHost)] + [Rpc.Host] + [Rpc.Broadcast] + Rpc.Caller
//          (compile-verified by create_economy_wallet / add_interaction_station).
//        * Component.ITriggerListener OnTriggerEnter/OnTriggerExit (compile-verified by
//          create_pickup), Scene.GetAllComponents<Collider>() (create_npc_brain),
//          GameObject.Tags.Has, Vector3.DistanceSquared/.Normal, OnFixedUpdate.
//        * FileSystem.Data.ReadJsonOrDefault<T>/WriteJson + DateTime (compile-verified by
//          create_save_system).
//    - GameObject.NetworkDestroy does NOT exist on this SDK -- the host calls
//      GameObject.Destroy() and the destroy replicates network-wide (same as the
//      compile-verified create_event_director self-destruct companion).
//
//  Register(...) lines + the _sceneMutatingCommands additions live in MyEditorMenu.cs
//  (Batch 45) to keep the files decoupled -- see this tool family's handoff summary.
// =============================================================================

// -----------------------------------------------------------------------------
// create_gacha_drop_table -- a sealed gacha roller. Two parallel [Property] lists
// pick a RARITY by cumulative weight; a flat "Rarity:Item" [Property] list picks an
// ITEM uniformly within the chosen rarity. A pity counter guarantees the rarest tier
// after PityAfter rolls without it (and resets on a rarest hit). Owned-item set drives
// duplicate detection with an OnDuplicate host hook. Host-authoritative: the roll runs
// inside an [Rpc.Host] (Rpc.Caller re-validated) and the result is announced via an
// [Rpc.Broadcast] so every machine fires the static OnRolled event.
//
// Builds on the create_weighted_loot_table shape (cumulative-weight Roll + pity) but
// adds the two-level rarity->item pick, dupe detection, and full host authority.
// -----------------------------------------------------------------------------
public class CreateGachaDropTableHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "GachaDropTable", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			int pityAfter = p.TryGetProperty( "pityAfter", out var pv ) && pv.TryGetInt32( out var pi ) ? pi : 50;
			if ( pityAfter < 0 ) pityAfter = 0;   // 0 disables pity

			var code = BuildCode( className, pityAfter, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = LootEconomyHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				pityAfter,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it on a per-player or manager GameObject: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Fill RarityNames / RarityWeights (parallel, cumulative-weight) and Items (\"Rarity:Item\" entries, e.g. \"Legendary:Dragon Fang\") in the inspector or with set_property. The LAST rarity in the list is treated as the rarest tier (the pity target) -- order them Common -> Legendary.",
					$"Roll from any game code: GetComponent<{className}>()?.Roll(); -- Roll() routes to the host via [Rpc.Host] RequestRoll (re-validate the caller inside CanRollFor for a competitive game), and the result fans out via [Rpc.Broadcast] so every machine fires the static event.",
					$"Subscribe to results: {className}.OnRolled += (rarity, item, isDuplicate) => {{ /* play the reveal, update the pull HUD */ }};",
					$"Convert duplicates to shards/currency in the host-side OnDuplicate hook (marked TODO in the file): GetComponent<{className}>().OnDuplicate = item => wallet?.AddMoney( shardValue );",
					pityAfter > 0
						? $"PityAfter is {pityAfter}: after that many rolls with no rarest-tier hit, the next roll is forced to the rarest tier and the counter resets. Tune with set_property."
						: "PityAfter is 0 (pity disabled). Set it with set_property to guarantee the rarest tier after N dry rolls."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_gacha_drop_table failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, int pityAfter, System.Globalization.CultureInfo ci )
	{
		string pity = pityAfter.ToString( ci );

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- a host-authoritative gacha / loot-box roller.
///
/// TWO-LEVEL PICK: RarityNames + RarityWeights are parallel, inspector-editable lists --
/// a cumulative-weight roll selects a RARITY (like create_weighted_loot_table). Items is a
/// flat ""Rarity:Item"" list (e.g. ""Legendary:Dragon Fang""); once a rarity is chosen an
/// item is picked UNIFORMLY from the entries with that prefix. Keep it simple + designer-tunable.
///
/// PITY: PityAfter counts rolls since the last rarest-tier hit. When it reaches PityAfter the
/// next roll is forced to the rarest tier (the LAST entry in RarityNames) and the counter resets.
/// PityAfter = 0 disables pity.
///
/// DUPLICATES: an owned-items set drives duplicate detection. On a dupe the host-side
/// OnDuplicate hook fires (convert it to shards / pity currency -- see the TODO); on a new
/// item it's added to the set.
///
/// HOST-AUTHORITATIVE: Roll() routes to the host via [Rpc.Host] RequestRoll (re-validate the
/// caller -- NetFlags is not security), the host computes the result, and [Rpc.Broadcast]
/// AnnounceRoll fires the static OnRolled event on every machine. Single-player safe
/// (IsProxy is false / Networking inactive -> the RPCs run locally).
///
/// Usage:
///   GetComponent&lt;{className}&gt;()?.Roll();
///   {className}.OnRolled += ( rarity, item, isDup ) => Log.Info( $""{{rarity}} :: {{item}} (dup={{isDup}})"" );
///   GetComponent&lt;{className}&gt;().OnDuplicate = item => {{ /* grant shards host-side */ }};
/// </summary>
public sealed class {className} : Component
{{
	/// Rarities, rarest LAST (the pity target). Parallel to RarityWeights.
	[Property] public List<string> RarityNames {{ get; set; }} = new List<string> {{ ""Common"", ""Rare"", ""Epic"", ""Legendary"" }};

	/// Cumulative-weight rarity chances, parallel to RarityNames. Bigger = more common.
	[Property] public List<float> RarityWeights {{ get; set; }} = new List<float> {{ 79f, 15f, 5f, 1f }};

	/// The item pool as flat ""Rarity:Item"" entries -- an item is picked uniformly among
	/// the entries whose prefix matches the rolled rarity.
	[Property] public List<string> Items {{ get; set; }} = new List<string>
	{{
		""Common:Wooden Sword"", ""Common:Cloth Cap"",
		""Rare:Steel Blade"", ""Rare:Iron Shield"",
		""Epic:Frost Bow"", ""Legendary:Dragon Fang""
	}};

	/// Rolls without a rarest-tier hit before the next roll is guaranteed rarest. 0 = off.
	[Property] public int PityAfter {{ get; set; }} = {pity};

	/// Fires on EVERY machine after a roll (via [Rpc.Broadcast]) -- drive the reveal / HUD here.
	public static Action<string, string, bool> OnRolled {{ get; set; }}

	/// Host-side hook fired when a rolled item is already owned. Convert dupes to shards/currency.
	public Action<string> OnDuplicate {{ get; set; }}

	// Host-authoritative runtime state (only ever written on the host inside DoRoll).
	private int _sinceRarest;
	private readonly HashSet<string> _owned = new HashSet<string>();

	/// <summary>Public entry: request a roll. Routes to the host; the result is broadcast.</summary>
	public void Roll() => RequestRoll();

	/// <summary>
	/// Host-authoritative roll. [Rpc.Host] is invokable by any client with forged args, so
	/// NetFlags is NOT security -- CanRollFor re-validates the caller before we roll.
	/// </summary>
	[Rpc.Host]
	public void RequestRoll()
	{{
		if ( Networking.IsActive )
		{{
			var caller = Rpc.Caller;
			if ( caller == null ) return;
			if ( !CanRollFor( caller ) ) return;
		}}
		DoRoll();
	}}

	/// <summary>
	/// Tighten this to your game: confirm the caller may roll (owns this roller / can afford
	/// the pull / isn't rate-limited). Default allows any valid caller (and single-player).
	/// </summary>
	private bool CanRollFor( Connection caller ) => true;

	// The actual roll -- runs ONLY on the host (or locally in single-player).
	private void DoRoll()
	{{
		if ( RarityNames == null || RarityNames.Count == 0 ) return;
		int rarestIdx = RarityNames.Count - 1;

		int rarityIdx;
		if ( PityAfter > 0 && _sinceRarest >= PityAfter )
			rarityIdx = rarestIdx;                 // pity: force the rarest tier
		else
			rarityIdx = PickRarityIndex();

		if ( rarityIdx < 0 ) rarityIdx = 0;
		if ( rarityIdx > rarestIdx ) rarityIdx = rarestIdx;

		// Pity tracking: reset on a rarest hit, otherwise advance.
		if ( rarityIdx == rarestIdx ) _sinceRarest = 0;
		else _sinceRarest++;

		string rarity = RarityNames[rarityIdx];
		string item = PickItem( rarity );

		bool isDuplicate = _owned.Contains( item );
		if ( isDuplicate )
		{{
			// ── DUPLICATE ────────────────────────────────────────────────
			// TODO: convert the dupe into shards / pity currency here (host-side), e.g.
			//   GetComponent<Wallet>()?.AddMoney( shardValue );
			OnDuplicate?.Invoke( item );
		}}
		else
		{{
			_owned.Add( item );
		}}

		AnnounceRoll( rarity, item, isDuplicate );
	}}

	// Cumulative-weight rarity pick over the parallel lists; -1 if unusable.
	private int PickRarityIndex()
	{{
		int count = Math.Min( RarityNames.Count, RarityWeights?.Count ?? 0 );
		if ( count == 0 ) return 0;                // no weights -> first rarity

		float total = 0f;
		for ( int i = 0; i < count; i++ ) total += RarityWeights[i];
		if ( total <= 0f ) return 0;

		float roll = Game.Random.Float( 0f, total );
		float cumulative = 0f;
		for ( int i = 0; i < count; i++ )
		{{
			cumulative += RarityWeights[i];
			if ( roll < cumulative ) return i;
		}}
		return count - 1;
	}}

	// Uniform item pick within a rarity, reading the ""Rarity:Item"" prefix. Falls back to the
	// rarity name itself if that tier has no items.
	private string PickItem( string rarity )
	{{
		string prefix = rarity + "":"";
		var pool = new List<string>();
		if ( Items != null )
		{{
			foreach ( var entry in Items )
			{{
				if ( entry != null && entry.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
					pool.Add( entry.Substring( prefix.Length ) );
			}}
		}}
		if ( pool.Count == 0 ) return rarity;

		// Float-based index dodges any inclusive/exclusive ambiguity in the int overload.
		int idx = (int) Game.Random.Float( 0f, pool.Count );
		if ( idx >= pool.Count ) idx = pool.Count - 1;
		return pool[idx];
	}}

	/// <summary>
	/// Fires OnRolled on EVERY machine (host + all proxies). Broadcast from the host after a
	/// roll so all clients see the same drop. Public because [Rpc] wrappers require it.
	/// </summary>
	[Rpc.Broadcast]
	public void AnnounceRoll( string rarity, string item, bool isDuplicate )
	{{
		OnRolled?.Invoke( rarity, item, isDuplicate );
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_currency_pickup -- a sealed networked coin/pickup. Host-spawned; on a
// player entering its trigger the HOST validates and grants Value into a wallet
// component on the player, then destroys the pickup network-wide. Optional magnet:
// within MagnetRadius it accelerates toward the nearest player each FixedUpdate.
//
// Deposit is reflection-free: a static Grant seam (wired once to the direct typed
// AddMoney call) keeps the pickup compiling with NO hard dependency on a specific
// wallet class -- mirrors create_pickup's self-contained convention. Pairs with
// create_economy_wallet (AddMoney/TrySpend/CanAfford API shape).
// -----------------------------------------------------------------------------
public class CreateCurrencyPickupHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "CurrencyPickup", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			int   value        = p.TryGetProperty( "value",        out var vv ) && vv.TryGetInt32( out var vi )  ? vi : 1;
			float magnetRadius = p.TryGetProperty( "magnetRadius", out var mv ) && mv.TryGetSingle( out var mf ) ? mf : 0f;
			string walletName  = p.TryGetProperty( "walletComponentName", out var wv ) && !string.IsNullOrWhiteSpace( wv.GetString() )
				? wv.GetString() : "EconomyWallet";

			// Defensive clamps.
			if ( value < 0 )        value = 0;
			if ( magnetRadius < 0f ) magnetRadius = 0f;
			walletName = ClaudeBridge.SanitizeIdentifier( walletName );   // it's used as a type name in a comment

			var code = BuildCode( className, value, magnetRadius, walletName, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = LootEconomyHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				value,
				magnetRadius,
				walletComponentName = walletName,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject -- make sure that object has a trigger Collider (SphereCollider, IsTrigger=true) so OnTriggerEnter fires."
						: $"Place it on a coin prop that has a trigger Collider (SphereCollider, IsTrigger=true) + a ModelRenderer: add_component_to_new_object / create_pickup-style, or re-run with targetId.",
					$"Wire the deposit ONCE at startup (this is the reflection-free direct call -- rename {walletName} if your wallet class differs): {className}.Grant = ( player, amount ) => player.Components.Get<{walletName}>()?.AddMoney( amount );",
					"MULTIPLAYER: NetworkSpawn the coin on the host (network_spawn) so the grant is host-authoritative -- the trigger grant + Destroy run only on the host and the despawn replicates. Single-player works with no networking.",
					magnetRadius > 0f
						? $"MagnetRadius is {magnetRadius.ToString( ci )}: the coin accelerates toward the nearest GameObject tagged '{"player"}' within that range each FixedUpdate (host-side). Tune MagnetRadius / MagnetAccel / MaxMagnetSpeed / PlayerTag with set_property."
						: $"MagnetRadius is 0 (magnet off). Set it > 0 (and confirm players carry the PlayerTag '{"player"}') to make coins fly to the player.",
					$"Value is {value}. OnCollected fires host-side after a grant: {className}.OnCollected += ( player, amount ) => {{ /* SFX / +N popup */ }};"
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_currency_pickup failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, int value, float magnetRadius, string walletName, System.Globalization.CultureInfo ci )
	{
		string val = value.ToString( ci );
		string mag = magnetRadius.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// {className} -- a networked coin / currency pickup. HOST-spawned; when a player enters its
/// trigger the HOST validates and grants Value into the player's wallet, then destroys the
/// pickup network-wide (the host Destroy() replicates -- there is no NetworkDestroy on this SDK).
///
/// MAGNET (optional): while MagnetRadius &gt; 0 the coin accelerates toward the nearest player
/// (a GameObject carrying PlayerTag) each FixedUpdate, host-side, up to MaxMagnetSpeed.
///
/// DEPOSIT is reflection-free and dependency-free: wire the static Grant seam ONCE to your
/// wallet's AddMoney (the direct typed call is in GrantTo below) so {className} compiles with no
/// hard reference to a specific wallet class. Pairs with create_economy_wallet.
///
/// Put it on a GameObject with a trigger Collider (SphereCollider, IsTrigger=true). In
/// multiplayer NetworkSpawn it on the host so IsProxy makes the grant host-authoritative.
/// Single-player works with no networking (IsProxy is false).
///
/// Usage (wire once at startup):
///   {className}.Grant = ( player, amount ) =&gt; player.Components.Get&lt;{walletName}&gt;()?.AddMoney( amount );
/// </summary>
public sealed class {className} : Component, Component.ITriggerListener
{{
	/// How much currency this pickup is worth.
	[Property] public int Value {{ get; set; }} = {val};

	/// Magnet range in world units. 0 = magnet off.
	[Property] public float MagnetRadius {{ get; set; }} = {mag};

	/// Acceleration (units/sec^2) toward the player while magnetised.
	[Property] public float MagnetAccel {{ get; set; }} = 512f;

	/// Cap on magnet speed (units/sec).
	[Property] public float MaxMagnetSpeed {{ get; set; }} = 900f;

	/// Tag a GameObject must carry to be treated as a player (for the magnet + trigger filter).
	[Property] public string PlayerTag {{ get; set; }} = ""player"";

	/// The wallet component's type name -- used to locate it (and to name the fix if Grant is unwired).
	[Property] public string WalletComponentName {{ get; set; }} = ""{walletName}"";

	/// <summary>
	/// How the pickup deposits Value. Wire ONCE at startup to your wallet's AddMoney -- this is
	/// the reflection-free direct call (rename {walletName} to YOUR wallet class if it differs):
	///   {className}.Grant = ( player, amount ) =&gt; player.Components.Get&lt;{walletName}&gt;()?.AddMoney( amount );
	/// Left as a static seam so {className} has no hard dependency on a specific wallet type.
	/// </summary>
	public static Action<GameObject, int> Grant {{ get; set; }}

	/// <summary>Fires host-side after a successful grant (SFX / ""+N"" popup). (player, amount).</summary>
	public static Action<GameObject, int> OnCollected {{ get; set; }}

	private float _magnetSpeed;
	private bool _collected;

	protected override void OnFixedUpdate()
	{{
		if ( IsProxy ) return;                 // host owns the transform; it replicates to clients
		if ( MagnetRadius <= 0f ) return;      // magnet off

		var player = FindNearestPlayer();
		if ( player == null ) {{ _magnetSpeed = 0f; return; }}

		_magnetSpeed = MathX.Clamp( _magnetSpeed + MagnetAccel * Time.Delta, 0f, MaxMagnetSpeed );
		var dir = (player.WorldPosition - WorldPosition).Normal;
		WorldPosition += dir * (_magnetSpeed * Time.Delta);
	}}

	// Nearest GameObject tagged PlayerTag within MagnetRadius, or null. Tag-based so it works
	// with any player controller (no dependency on a specific Player type).
	private GameObject FindNearestPlayer()
	{{
		GameObject best = null;
		float bestDist = MagnetRadius * MagnetRadius;
		foreach ( var collider in Scene.GetAllComponents<Collider>() )
		{{
			var go = collider?.GameObject;
			if ( go == null || !go.Tags.Has( PlayerTag ) ) continue;
			float d = go.WorldPosition.DistanceSquared( WorldPosition );
			if ( d <= bestDist ) {{ bestDist = d; best = go; }}
		}}
		return best;
	}}

	public void OnTriggerEnter( Collider other )
	{{
		if ( _collected ) return;
		var player = other?.GameObject;
		if ( player == null || !player.Tags.Has( PlayerTag ) ) return;

		// HOST-authoritative: on a proxy (a client copy of a host-spawned coin) do nothing.
		// The host grants and the Destroy replicates to everyone.
		if ( IsProxy ) return;

		_collected = true;
		GrantTo( player );
		OnCollected?.Invoke( player, Value );
		GameObject.Destroy();                  // replicates network-wide from the host
	}}

	public void OnTriggerExit( Collider other ) {{ }}

	// Deposit Value into the player's wallet. Reflection-free: prefer the wired Grant seam;
	// otherwise locate the wallet by WalletComponentName just to point at the fix (never silent).
	private void GrantTo( GameObject player )
	{{
		if ( Grant != null ) {{ Grant( player, Value ); return; }}

		var wallet = player.Components.GetAll().FirstOrDefault( c => c.GetType().Name == WalletComponentName );
		if ( wallet != null )
			Log.Warning( $""[{className}] '{{WalletComponentName}}' is on {{player.Name}} but {className}.Grant is unwired -- set {className}.Grant = (p,amt) => p.Components.Get<{{WalletComponentName}}>()?.AddMoney(amt); to deposit {{Value}}."" );
		else
			Log.Warning( $""[{className}] No wallet '{{WalletComponentName}}' on {{player.Name}} and {className}.Grant is unwired -- {{Value}} not granted."" );
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_offline_progress -- a sealed idle-game component. Persists LastSeenUtc
// (DateTime) via FileSystem.Data JSON on a dirty-flag autosave + OnDisabled. On
// enable it computes elapsed = now - LastSeenUtc, clamps to MaxOfflineHours, then
// replays it through SimulateOffline in fixed TickSeconds chunks so idle math stays
// deterministic. Guards clock rollback (negative elapsed -> 0). Fires the static
// OnOfflineProgressApplied(seconds). Copies create_save_system's persistence patterns.
// -----------------------------------------------------------------------------
public class CreateOfflineProgressHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "OfflineProgress", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float maxHours = p.TryGetProperty( "maxOfflineHours", out var mv ) && mv.TryGetSingle( out var mf ) ? mf : 8f;
			float tick     = p.TryGetProperty( "tickSeconds",     out var tv ) && tv.TryGetSingle( out var tf ) ? tf : 1f;

			// Defensive clamps so the deterministic replay can't blow up.
			if ( maxHours < 0f )  maxHours = 0f;
			if ( tick     < 0.1f ) tick    = 0.1f;   // floor keeps the chunk loop bounded

			var code = BuildCode( className, maxHours, tick, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = LootEconomyHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				maxOfflineHours = maxHours,
				tickSeconds = tick,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it on your idle/save manager GameObject: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Fill in the idle math: edit the SimulateOffline(double seconds) TODO in {className}.cs -- it's called in TickSeconds ({tick.ToString( ci )}s) chunks so accumulation is frame-rate independent, e.g. GetComponent<Wallet>()?.AddMoney( (int)(rate * seconds) ).",
					$"React to the catch-up total: {className}.OnOfflineProgressApplied += seconds => ShowWelcomeBack( seconds );",
					$"MaxOfflineHours is {maxHours.ToString( ci )} -- offline time is clamped to this window (and negative elapsed from a clock rollback is treated as 0). Tune MaxOfflineHours / TickSeconds / AutosaveSeconds with set_property.",
					"LastSeenUtc persists to FileSystem.Data (FileName, default offlineprogress.json) on a dirty-flag autosave heartbeat and on OnDisabled, so a crash loses at most AutosaveSeconds of wall-clock."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_offline_progress failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float maxHours, float tick, System.Globalization.CultureInfo ci )
	{
		string mh = maxHours.ToString( ci ) + "f";
		string tk = tick.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- offline / idle progress. Persists LastSeenUtc (DateTime) to FileSystem.Data
/// JSON on a dirty-flag autosave heartbeat and on OnDisabled (copies create_save_system's
/// persistence patterns). On enable it computes elapsed = now - LastSeenUtc, guards a clock
/// rollback (negative -> 0), clamps to MaxOfflineHours, then replays that time through the
/// SimulateOffline(double seconds) hook in fixed TickSeconds chunks so idle accumulation is
/// deterministic (frame-rate independent). Fires the static OnOfflineProgressApplied(seconds).
///
/// Add your idle math to SimulateOffline. Owner/host-only (IsProxy guarded) so a client can't
/// author their own offline earnings.
///
/// Usage:
///   {className}.OnOfflineProgressApplied += seconds => ShowWelcomeBack( seconds );
///   // inside SimulateOffline: GetComponent&lt;Wallet&gt;()?.AddMoney( (int)(rate * seconds) );
/// </summary>
public sealed class {className} : Component
{{
	/// FileSystem.Data path the last-seen timestamp is written to.
	[Property] public string FileName {{ get; set; }} = ""offlineprogress.json"";

	/// Offline time is clamped to this many hours (stops a week-away from paying out a week).
	[Property] public float MaxOfflineHours {{ get; set; }} = {mh};

	/// SimulateOffline chunk size in seconds -- smaller = finer-grained deterministic replay.
	[Property] public float TickSeconds {{ get; set; }} = {tk};

	/// Autosave heartbeat cadence (seconds) that keeps LastSeenUtc fresh. 0 disables the heartbeat.
	[Property] public float AutosaveSeconds {{ get; set; }} = 30f;

	/// The persisted payload. Add your own idle fields here if you want them saved alongside.
	public class OfflineData
	{{
		public DateTime LastSeenUtc {{ get; set; }} = DateTime.UtcNow;
	}}

	public OfflineData Data {{ get; private set; }} = new OfflineData();
	public bool IsDirty {{ get; private set; }}

	/// Fires after offline progress is applied on enable, with the (clamped) elapsed seconds.
	public static Action<double> OnOfflineProgressApplied {{ get; set; }}

	private TimeUntil _nextAutosave;

	protected override void OnEnabled()
	{{
		if ( IsProxy ) return;                 // only the owning machine tracks offline time
		Load();
		ApplyOfflineProgress();
		_nextAutosave = AutosaveSeconds;
	}}

	protected override void OnUpdate()
	{{
		if ( IsProxy || AutosaveSeconds <= 0f ) return;
		if ( _nextAutosave )
		{{
			_nextAutosave = AutosaveSeconds;
			MarkDirty();                       // heartbeat: keep LastSeenUtc current for next session
			if ( IsDirty ) Save();
		}}
	}}

	protected override void OnDisabled()
	{{
		if ( !IsProxy && IsDirty ) Save();
	}}

	/// Mark the timestamp changed so the next autosave tick (or OnDisabled) writes it.
	public void MarkDirty() => IsDirty = true;

	public void Load()
	{{
		var loaded = FileSystem.Data.ReadJsonOrDefault<OfflineData>( FileName, null );
		Data = loaded ?? new OfflineData {{ LastSeenUtc = DateTime.UtcNow }};
	}}

	public void Save()
	{{
		Data.LastSeenUtc = DateTime.UtcNow;
		FileSystem.Data.WriteJson( FileName, Data );
		IsDirty = false;
	}}

	/// <summary>
	/// Compute elapsed offline time, clamp it, and replay it deterministically in TickSeconds
	/// chunks. Called on enable; also safe to call manually.
	/// </summary>
	public void ApplyOfflineProgress()
	{{
		double elapsed = (DateTime.UtcNow - Data.LastSeenUtc).TotalSeconds;
		if ( elapsed < 0.0 ) elapsed = 0.0;                    // clock-rollback guard
		double cap = MaxOfflineHours * 3600.0;
		if ( cap > 0.0 && elapsed > cap ) elapsed = cap;       // clamp to the offline window

		double tick = TickSeconds > 0f ? TickSeconds : 1.0;
		double remaining = elapsed;
		while ( remaining > 0.0 )
		{{
			double chunk = Math.Min( tick, remaining );
			SimulateOffline( chunk );
			remaining -= chunk;
		}}

		OnOfflineProgressApplied?.Invoke( elapsed );

		Data.LastSeenUtc = DateTime.UtcNow;                    // don't re-apply on the next enable
		MarkDirty();
		Save();
	}}

	/// <summary>
	/// TODO: apply idle earnings for `seconds` of offline time. Called in fixed TickSeconds
	/// chunks so the total is deterministic regardless of frame rate, e.g.:
	///   GetComponent&lt;Wallet&gt;()?.AddMoney( (int)(EarnRatePerSecond * seconds) );
	/// </summary>
	private void SimulateOffline( double seconds )
	{{
		// Idle math goes here.
	}}
}}
";
	}
}

/// <summary>
/// Shared placement helper for the loot/economy handlers -- mirrors the standard scaffold
/// placement (create_weighted_loot_table / create_economy_wallet / create_event_director).
/// </summary>
internal static class LootEconomyHelpers
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
}
