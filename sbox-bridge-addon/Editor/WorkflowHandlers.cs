using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════
// Batch 53 — bridge_workflow + scene understanding + Tier-2 scaffolds (wave 3)
//   checkpoint_scene / restore_checkpoint / list_checkpoints — scene snapshots
//     to temp storage: the agent-side safety net for the engine's inert undo.
//   describe_scene — one-call scene orientation (histograms, cameras, bounds).
//   create_team_assigner / create_idle_income — corpus Tier-2 scaffolds.
// ═══════════════════════════════════════════════════════════════════

internal static class SceneCheckpoints
{
	internal static string Dir()
	{
		var ident = Project.Current?.Config?.Ident ?? "unknown";
		var dir = Path.Combine( Path.GetTempPath(), "sbox-bridge-checkpoints", ident );
		Directory.CreateDirectory( dir );
		return dir;
	}
}

/// <summary>checkpoint_scene — snapshot every root GameObject to a temp file.</summary>
public class CheckpointSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( Game.IsPlaying )
			return Task.FromResult<object>( new { error = "Checkpoints capture the EDIT scene — stop play first (stop_play), or runtime state would poison the snapshot." } );

		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var label = p.TryGetProperty( "label", out var l ) && !string.IsNullOrWhiteSpace( l.GetString() )
			? string.Concat( l.GetString().Where( c => char.IsLetterOrDigit( c ) || c == '-' || c == '_' ) )
			: "checkpoint";

		try
		{
			var roots = new System.Text.Json.Nodes.JsonArray();
			int objectCount = 0;
			var options = new GameObject.SerializeOptions();
			foreach ( var root in scene.Children.ToArray() )
			{
				if ( root == null ) continue;
				roots.Add( root.Serialize( options ) );
				objectCount++;
			}

			var id = $"cp_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{label}";
			var file = new System.Text.Json.Nodes.JsonObject
			{
				["scene"] = scene.Name,
				["label"] = label,
				["createdUtc"] = DateTime.UtcNow.ToString( "o" ),
				["roots"] = roots
			};
			var path = Path.Combine( SceneCheckpoints.Dir(), id + ".json" );
			File.WriteAllText( path, JsonSerializer.Serialize( file, new JsonSerializerOptions { WriteIndented = false } ) );

			return Task.FromResult<object>( new
			{
				checkpointed = true,
				id,
				label,
				scene = scene.Name,
				rootObjects = objectCount,
				sizeBytes = new FileInfo( path ).Length,
				note = "Snapshot stored OUTSIDE the project (temp dir; survives editor restarts, not OS cleanup). Roll back with restore_checkpoint; browse with list_checkpoints."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"checkpoint_scene failed: {ex.Message}" } );
		}
	}
}

/// <summary>restore_checkpoint — REPLACE the open scene's contents with a snapshot.</summary>
public class RestoreCheckpointHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.TryGetProperty( "id", out var idEl ) ? idEl.GetString() : null;
		if ( string.IsNullOrWhiteSpace( id ) )
			return Task.FromResult<object>( new { error = "id (checkpoint id from checkpoint_scene / list_checkpoints) is required — restore is destructive and never guesses" } );

		var path = Path.Combine( SceneCheckpoints.Dir(), id + ".json" );
		if ( !File.Exists( path ) )
			return Task.FromResult<object>( new { error = $"Checkpoint not found: {id}. See list_checkpoints." } );

		try
		{
			var node = System.Text.Json.Nodes.JsonNode.Parse( File.ReadAllText( path ) );
			var roots = node?["roots"]?.AsArray();
			if ( roots == null )
				return Task.FromResult<object>( new { error = "Checkpoint file has no roots array — corrupt?" } );

			// Full replace: destroy every current root, then rebuild from the snapshot.
			// Original guids are preserved (no collisions after the clear), so component
			// cross-references inside the snapshot stay wired.
			int destroyed = 0;
			foreach ( var child in scene.Children.ToArray() )
			{
				if ( child == null ) continue;
				child.Destroy();
				destroyed++;
			}

			int restored = 0;
			foreach ( var rootNode in roots )
			{
				if ( rootNode is not System.Text.Json.Nodes.JsonObject obj ) continue;
				var go = scene.CreateObject( true );
				go.Deserialize( obj );
				restored++;
			}

			return Task.FromResult<object>( new
			{
				restored = true,
				id,
				destroyedRoots = destroyed,
				restoredRoots = restored,
				note = "Scene contents replaced from the snapshot. The scene FILE on disk is untouched until save_scene."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"restore_checkpoint failed: {ex.Message}" } );
		}
	}
}

/// <summary>list_checkpoints — enumerate stored scene snapshots for this project.</summary>
public class ListCheckpointsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var entries = Directory.GetFiles( SceneCheckpoints.Dir(), "cp_*.json" )
				.Select( f => new FileInfo( f ) )
				.OrderByDescending( f => f.LastWriteTimeUtc )
				.Select( f => new
				{
					id = Path.GetFileNameWithoutExtension( f.Name ),
					createdUtc = f.LastWriteTimeUtc.ToString( "o" ),
					sizeBytes = f.Length
				} )
				.ToArray();

			return Task.FromResult<object>( new
			{
				total = entries.Length,
				checkpoints = entries,
				note = entries.Length == 0
					? "No checkpoints yet — create one with checkpoint_scene before risky batch edits."
					: "Pass an id to restore_checkpoint to roll the scene back."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"list_checkpoints failed: {ex.Message}" } );
		}
	}
}

/// <summary>describe_scene — one-call scene orientation: composition, cameras, lights, bounds, tags.</summary>
public class DescribeSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var playing = Game.IsPlaying;
		var scene = playing ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		try
		{
			var componentCounts = new Dictionary<string, int>();
			var tagCounts = new Dictionary<string, int>();
			var cameras = new List<object>();
			int lightCount = 0, total = 0;
			bool hasBounds = false;
			Vector3 mins = default, maxs = default;

			foreach ( var go in scene.GetAllObjects( true ) )
			{
				if ( go == null ) continue;
				total++;

				foreach ( var tag in go.Tags.TryGetAll() )
					tagCounts[tag] = tagCounts.GetValueOrDefault( tag ) + 1;

				foreach ( var comp in go.Components.GetAll() )
				{
					if ( comp == null ) continue;
					var typeName = comp.GetType().Name;
					componentCounts[typeName] = componentCounts.GetValueOrDefault( typeName ) + 1;

					if ( comp is CameraComponent )
						cameras.Add( new { id = go.Id.ToString(), name = go.Name, position = go.WorldPosition.ToString() } );
					else if ( typeName.Contains( "Light", StringComparison.OrdinalIgnoreCase ) )
						lightCount++;
				}

				// Aggregate world bounds from renderable objects only (empty bounds skew the box).
				var box = go.GetBounds();
				if ( box.Size.Length > 0.01f )
				{
					if ( !hasBounds ) { mins = box.Mins; maxs = box.Maxs; hasBounds = true; }
					else { mins = Vector3.Min( mins, box.Mins ); maxs = Vector3.Max( maxs, box.Maxs ); }
				}
			}

			return Task.FromResult<object>( new
			{
				scene = scene.Name,
				playing,
				totalObjects = total,
				rootObjects = scene.Children.Count,
				componentHistogram = componentCounts.OrderByDescending( kv => kv.Value ).Take( 20 )
					.ToDictionary( kv => kv.Key, kv => kv.Value ),
				cameras,
				lightCount,
				tags = tagCounts.OrderByDescending( kv => kv.Value ).Take( 12 )
					.ToDictionary( kv => kv.Key, kv => kv.Value ),
				contentBounds = hasBounds
					? new { mins = mins.ToString(), maxs = maxs.ToString(), size = ( maxs - mins ).ToString() }
					: null,
				note = "Drill down: find_objects by component/tag, get_scene_hierarchy for structure, find_broken_references for health, screenshot_orbit to look at something."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"describe_scene failed: {ex.Message}" } );
		}
	}
}

/// <summary>create_team_assigner — scaffold a host-authoritative balanced team assigner.</summary>
public class CreateTeamAssignerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "TeamAssigner", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var teams = p.TryGetProperty( "teams", out var t ) && t.ValueKind == JsonValueKind.Array
				? t.EnumerateArray().Select( e => e.GetString() ).Where( s => !string.IsNullOrWhiteSpace( s ) ).ToArray()
				: new[] { "Red", "Blue" };
			if ( teams.Length < 2 ) teams = new[] { "Red", "Blue" };

			var code = BuildCode( className, teams );
			ScaffoldHelpers.WriteCode( fullPath, code );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				teams,
				nextSteps = new[]
				{
					"trigger_hotload, then check compile_status",
					$"Attach {className} to your game manager object",
					$"Call {className}.Instance.AssignSmallest(steamId) from your join hook (e.g. INetworkListener.OnActive)",
					$"Subscribe to {className}.OnTeamAssigned for HUD/spawn logic"
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_team_assigner failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string[] teams )
	{
		var teamList = string.Join( ", ", teams.Select( t => $"\"{t}\"" ) );
		return $@"using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// {className} — host-authoritative balanced team assignment (smallest-bucket draft).
///
/// The HOST owns the roster: call AssignSmallest(steamId) when a player joins
/// (e.g. from INetworkListener.OnActive) and it drops them into the emptiest
/// team, announces via [Rpc.Broadcast] so every client's mirror agrees, and
/// fires the static OnTeamAssigned. Rebalance() redrafts everyone smallest-first.
/// Single-player safe (assigns team 0).
/// </summary>
public sealed class {className} : Component
{{
	public static {className} Instance {{ get; private set; }}

	[Property] public List<string> TeamNames {{ get; set; }} = new() {{ {teamList} }};

	/// <summary>steamId → team index. Host-written, client-mirrored via the broadcast.</summary>
	readonly Dictionary<ulong, int> _roster = new();

	/// <summary>(steamId, teamIndex, teamName) — fires on host and all clients.</summary>
	public static event Action<ulong, int, string> OnTeamAssigned;

	protected override void OnEnabled()
	{{
		Instance = this;
	}}

	protected override void OnDisabled()
	{{
		if ( Instance == this ) Instance = null;
	}}

	/// <summary>Host-only: put a player on the emptiest team. Returns the team index.</summary>
	public int AssignSmallest( ulong steamId )
	{{
		if ( IsProxy ) return GetTeam( steamId );

		var counts = new int[TeamNames.Count];
		foreach ( var kv in _roster )
			if ( kv.Key != steamId && kv.Value >= 0 && kv.Value < counts.Length )
				counts[kv.Value]++;

		int smallest = 0;
		for ( int i = 1; i < counts.Length; i++ )
			if ( counts[i] < counts[smallest] ) smallest = i;

		AnnounceAssignment( steamId, smallest );
		return smallest;
	}}

	/// <summary>Host-only: redraft every known player smallest-first (stable order).</summary>
	public void Rebalance()
	{{
		if ( IsProxy ) return;
		var players = _roster.Keys.ToArray();
		_roster.Clear();
		foreach ( var id in players )
			AssignSmallest( id );
	}}

	/// <summary>Team index for a player, or -1 if unassigned.</summary>
	public int GetTeam( ulong steamId )
		=> _roster.TryGetValue( steamId, out var team ) ? team : -1;

	/// <summary>All players on a team.</summary>
	public IEnumerable<ulong> GetMembers( int teamIndex )
		=> _roster.Where( kv => kv.Value == teamIndex ).Select( kv => kv.Key );

	[Rpc.Broadcast]
	void AnnounceAssignment( ulong steamId, int teamIndex )
	{{
		_roster[steamId] = teamIndex;
		var name = teamIndex >= 0 && teamIndex < TeamNames.Count ? TeamNames[teamIndex] : $""Team {{teamIndex}}"";
		OnTeamAssigned?.Invoke( steamId, teamIndex, name );
	}}
}}
";
	}
}

/// <summary>create_idle_income — scaffold a host-authoritative passive income ticker.</summary>
public class CreateIdleIncomeHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "IdleIncome", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float perTick = p.TryGetProperty( "incomePerTick", out var it ) && it.TryGetSingle( out var itf ) ? itf : 1f;
			float tickSeconds = p.TryGetProperty( "tickSeconds", out var ts ) && ts.TryGetSingle( out var tsf ) ? tsf : 1f;

			var code = BuildCode( className, perTick, tickSeconds );
			ScaffoldHelpers.WriteCode( fullPath, code );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				nextSteps = new[]
				{
					"trigger_hotload, then check compile_status",
					$"Attach {className} next to your wallet component (create_economy_wallet) — it auto-wires any sibling with an AddMoney(int) method",
					"Pair with create_offline_progress for away-time earnings — the complete idle kit"
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_idle_income failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float perTick, float tickSeconds )
	{
		var ci = System.Globalization.CultureInfo.InvariantCulture;
		return $@"using Sandbox;
using System;
using System.Linq;

/// <summary>
/// {className} — host-authoritative passive income: every TickSeconds the host
/// grants IncomePerTick × Multiplier. Auto-wires a sibling wallet: the first
/// component on this GameObject with an AddMoney(int) method (e.g. a
/// create_economy_wallet scaffold) receives the grant; otherwise override
/// Grant(). TotalEarned replicates to clients. Pair with create_offline_progress
/// for away-time earnings. Single-player safe.
/// </summary>
public class {className} : Component
{{
	[Property] public float IncomePerTick {{ get; set; }} = {perTick.ToString( ci )}f;
	[Property] public float TickSeconds {{ get; set; }} = {tickSeconds.ToString( ci )}f;
	[Property] public float Multiplier {{ get; set; }} = 1f;
	[Property] public bool Paused {{ get; set; }}

	[Sync( SyncFlags.FromHost )] public float TotalEarned {{ get; set; }}

	/// <summary>(amount granted this tick, new TotalEarned) — host only.</summary>
	public static event Action<float, float> OnIncomeTick;

	TimeUntil _nextTick;

	protected override void OnStart()
	{{
		_nextTick = TickSeconds;
	}}

	protected override void OnFixedUpdate()
	{{
		if ( IsProxy || Paused ) return;
		if ( !_nextTick ) return;
		_nextTick = TickSeconds;

		var amount = IncomePerTick * Multiplier;
		if ( amount <= 0f ) return;

		TotalEarned += amount;
		Grant( amount );
		OnIncomeTick?.Invoke( amount, TotalEarned );
	}}

	/// <summary>
	/// Deliver the income. Default: invoke AddMoney(int) on the first sibling
	/// component that has one (reflection via TypeLibrary — no compile-time
	/// dependency on the wallet type). Override for custom delivery.
	/// </summary>
	protected virtual void Grant( float amount )
	{{
		foreach ( var comp in Components.GetAll() )
		{{
			if ( comp == this || comp is null ) continue;
			var type = Game.TypeLibrary?.GetType( comp.GetType() );
			var method = type?.Methods?.FirstOrDefault( m => m.Name == ""AddMoney"" );
			if ( method == null ) continue;
			try
			{{
				method.Invoke( comp, new object[] {{ (int)amount }} );
				return;
			}}
			catch {{ /* wrong signature — keep looking */ }}
		}}
		// No wallet sibling found — TotalEarned still accumulates; read it directly
		// or override Grant() to route income where you need it.
	}}
}}
";
	}
}
