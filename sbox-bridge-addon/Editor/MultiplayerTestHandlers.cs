using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// Multiplayer test harness — host a lobby on the RUNNING game and boot real
// sbox.exe clients that auto-join it. Implements the engine's official test
// path (networking/testing-multiplayer.md: "Join via new instance" /
// `connect local`) as bridge tools:
//
//   start_multiplayer_test     host (CreateLobby if needed) + spawn N clients (-joinlocal)
//   multiplayer_test_status    read-only: networking + connections + client PIDs + join-wait
//   stop_multiplayer_test      kill tracked clients, retaining survivors for retry (+ optional disconnect / stray sweep)
//
// THE PROVEN RECIPE (live-verified 2026-07-13, bridge-driven e2e):
//   • Play mode does NOT host by default: Networking.IsActive == false until someone
//     calls Sandbox.Networking.CreateLobby(). The configured overload takes
//     Sandbox.Network.LobbyConfig (NOT Sandbox.LobbyConfig — that namespace fails to
//     compile). LobbyConfig verified via reflection probe 2026-07-24: struct {
//     MaxPlayers:int, Name:string, Privacy:LobbyPrivacy,
//     Hidden/DestroyWhenHostLeaves/AutoSwitchToBestHost:bool }. Its defaults are
//     Public + visible, so this harness always supplies a private, hidden config.
//     The SDK defines Private as invite-only; whether -joinlocal bypasses that policy is
//     intentionally retained as a live P7 smoke requirement in TESTING.md.
//   • Second client: launch sbox.exe with -joinlocal → boots a REAL game client in
//     ~80 seconds that auto-joins the local session. (+instanceid N also exists for
//     numbering — untested, not passed.) The exe is NEVER hardcoded: the editor IS
//     sbox-dev.exe in the install folder, so Path.GetDirectoryName(Environment.ProcessPath)
//     finds sbox.exe next to it; fallback = the same Steam libraryfolders.vdf scan the
//     MCP server's log locator uses (sbox-mcp-server/src/tools/diagnostics.ts).
//   • Success signal, host-side: Sandbox.Connection.All.Count goes 1 → N+1
//     (verified 1→2; the guest joined with the Steam guest account name).
//   • Local transport is Sandbox.Network.TcpChannel — killing a client mid-connection
//     leaves benign TcpChannel.SendThread traces in the log (not an error).
//   • MEMORY: one sbox.exe client used ~4.5 GB RAM → default clients=1, hard cap 2.
//   • Networking.MaxPlayers is READ-ONLY (set via LobbyConfig only); Networking.IsHost
//     can throw with no session — always guard behind Networking.IsActive.
//
// LIMITS (honesty): the bridge cannot see or drive the CLIENT instance — there is no
// bridge inside it. All assertions are host-side (Connection.All, [Sync] state via
// inspect_networked_object, get_runtime_property); a human can eyeball the client window.
//
// Frame-job pattern (statics + [EditorEvent.Frame] ticker) mirrors PlaytestHandler.cs —
// the house pattern for play-mode frame jobs. System.Diagnostics / System.IO types are
// fully qualified (GameplayRecorderHandlers.cs precedent — `using Editor;` is in scope,
// bare names risk the FileSystem ambiguity class).
//
// Registration (MyEditorMenu.cs RegisterHandlers):
//   Register( "start_multiplayer_test",  () => new StartMultiplayerTestHandler() );
//   Register( "multiplayer_test_status", () => new MultiplayerTestStatusHandler() );
//   Register( "stop_multiplayer_test",   () => new StopMultiplayerTestHandler() );
// Scene-mutating: NONE — all three must stay callable during play mode (the session
// only exists there; record_playtest / record_gameplay_clip precedent). They touch
// processes and the network session, never the scene file.
// ═══════════════════════════════════════════════════════════════════════════

internal static class MultiplayerTestRunner
{
	internal class ClientProc
	{
		public int Pid;
		public DateTime StartedUtc;
		public System.Diagnostics.Process Process;   // handle kept so stop can Kill(entireProcessTree:true)
	}

	// ── Registry of spawned sbox.exe clients (persists across calls; cleared by stop) ──
	private static readonly List<ClientProc> _clients = new();
	private static readonly object _lock = new();

	// ── Join-wait job (one at a time, ticked by the editor frame loop) ──
	private static string _jobId;
	private static string _joinState = "none";   // none | pending | complete | timeout
	private static string _joinNote;
	private static int _expected;
	private static DateTime _waitStartUtc;
	private static DateTime _deadlineUtc;
	private static double _elapsedAtEnd;

	internal static List<ClientProc> Snapshot() { lock ( _lock ) { return _clients.ToList(); } }

	internal static int AliveCount() { lock ( _lock ) { return _clients.Count( IsAlive ); } }

	internal static void Track( ClientProc c ) { lock ( _lock ) { _clients.Add( c ); } }

	internal static void ResetAfterStop( IEnumerable<ClientProc> remaining )
	{
		lock ( _lock )
		{
			_clients.Clear();
			if ( remaining != null ) _clients.AddRange( remaining );
			_jobId = null;
			_joinState = "none";
			_joinNote = null;
			_expected = 0;
		}
	}

	internal static bool IsAlive( ClientProc c )
	{
		try { if ( c.Process != null ) return !c.Process.HasExited; } catch { }
		try { System.Diagnostics.Process.GetProcessById( c.Pid ); return true; }
		catch { return false; }
	}

	internal static string BeginJoinWait( int expected, int waitSeconds )
	{
		lock ( _lock )
		{
			_jobId = Guid.NewGuid().ToString( "N" ).Substring( 0, 8 );
			_joinState = "pending";
			_joinNote = null;
			_expected = expected;
			_waitStartUtc = DateTime.UtcNow;
			_deadlineUtc = _waitStartUtc.AddSeconds( waitSeconds );
			_elapsedAtEnd = 0;
			return _jobId;
		}
	}

	internal static object JoinWaitStatus( int liveCount )
	{
		lock ( _lock )
		{
			if ( _joinState == "none" )
				return new { state = "none" };
			return new
			{
				state = _joinState,
				jobId = _jobId,
				expectedConnections = _expected,
				currentConnections = liveCount,
				elapsedSeconds = _joinState == "pending"
					? System.Math.Round( ( DateTime.UtcNow - _waitStartUtc ).TotalSeconds, 1 )
					: _elapsedAtEnd,
				note = _joinNote,
			};
		}
	}

	static void EndJoinWait( string state, string note )
	{
		lock ( _lock )
		{
			_joinState = state;
			_joinNote = note;
			_elapsedAtEnd = System.Math.Round( ( DateTime.UtcNow - _waitStartUtc ).TotalSeconds, 1 );
		}
	}

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		string state; int expected; DateTime deadline;
		lock ( _lock ) { state = _joinState; expected = _expected; deadline = _deadlineUtc; }
		if ( state != "pending" ) return;

		try
		{
			if ( !Game.IsPlaying )
			{
				// The host session dies with play mode; spawned clients keep running
				// (they're separate processes) — stop_multiplayer_test cleans them up.
				EndJoinWait( "timeout", "play mode ended before all clients joined — spawned clients are still running, stop_multiplayer_test kills them" );
				return;
			}

			int count = 0;
			try { if ( Networking.IsActive ) count = Connection.All.Count; } catch { }

			if ( count >= expected )
			{
				EndJoinWait( "complete", $"reached {count}/{expected} connections" );
				return;
			}

			if ( DateTime.UtcNow > deadline )
				EndJoinWait( "timeout", $"only {count}/{expected} connections when the wait expired — a client boots in ~80 s; keep polling multiplayer_test_status (the join can still land) or restart with a longer waitForJoinSeconds" );
		}
		catch ( Exception ex )
		{
			// Never let the ticker throw (it'd spam every editor frame).
			EndJoinWait( "timeout", $"join-wait error: {ex.Message}" );
		}
	}

	/// <summary>
	/// Resolve the sbox.exe game client without hardcoding an install path.
	/// 1) Next to the running editor process (the editor IS sbox-dev.exe in the install dir).
	/// 2) Steam library scan: parse steamapps/libraryfolders.vdf in the default Steam roots,
	///    then check steamapps/common/sbox/sbox.exe in each library (same discovery the MCP
	///    server's log locator uses).
	/// </summary>
	internal static string ResolveSboxExe( out string how )
	{
		how = null;
		try
		{
			var dir = System.IO.Path.GetDirectoryName( System.Environment.ProcessPath );
			if ( !string.IsNullOrEmpty( dir ) )
			{
				var exe = System.IO.Path.Combine( dir, "sbox.exe" );
				if ( System.IO.File.Exists( exe ) )
				{
					how = "next to the running editor process";
					return exe;
				}
			}
		}
		catch { }

		try
		{
			var roots = new[]
			{
				@"C:\Program Files (x86)\Steam",
				@"C:\Program Files\Steam",
			};
			var libs = new List<string>();
			foreach ( var root in roots )
			{
				var vdf = System.IO.Path.Combine( root, "steamapps", "libraryfolders.vdf" );
				if ( !System.IO.File.Exists( vdf ) ) continue;
				libs.Add( root );
				var txt = System.IO.File.ReadAllText( vdf );
				foreach ( System.Text.RegularExpressions.Match m in
					System.Text.RegularExpressions.Regex.Matches( txt, "\"path\"\\s+\"([^\"]+)\"" ) )
				{
					libs.Add( m.Groups[1].Value.Replace( "\\\\", "\\" ) );
				}
			}
			foreach ( var lib in libs )
			{
				var exe = System.IO.Path.Combine( lib, "steamapps", "common", "sbox", "sbox.exe" );
				if ( System.IO.File.Exists( exe ) )
				{
					how = "Steam library scan (libraryfolders.vdf)";
					return exe;
				}
			}
		}
		catch { }

		return null;
	}
}

/// <summary>
/// start_multiplayer_test — host a lobby on the RUNNING game (Networking.CreateLobby if
/// no session is active) and spawn N real sbox.exe clients with -joinlocal that boot
/// (~80 s each) and auto-join. Requires play mode. Params:
///   clients            : optional int, how many client instances (default 1, hard cap 2
///                        per run — one client uses ~4.5 GB RAM; overlapping tracked runs
///                        are rejected)
///   maxPlayers         : optional int lobby capacity (default 4; only applied when THIS
///                        call creates the lobby, via LobbyConfig — Networking.MaxPlayers
///                        is read-only; must fit the host plus every requested client)
///   waitForJoinSeconds : optional int (default 120, clamped 0-600). &gt; 0 starts an async
///                        frame-loop job that polls Connection.All.Count until it reaches
///                        expectedConnections or times out — poll multiplayer_test_status.
///                        0 = return right after spawning.
/// Returns { hosting, isHost, lobbyCreated, maxPlayers, spawned:[pids], sboxExe,
///           resolvedVia, baselineConnections, expectedConnections, jobId?, note }
/// immediately.
/// </summary>
public class StartMultiplayerTestHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "start_multiplayer_test requires play mode — call start_play first" } );

		int clients = 1;
		if ( p.TryGetProperty( "clients", out var cEl ) && cEl.TryGetInt32( out var ci ) )
			clients = System.Math.Clamp( ci, 1, 2 );

		int maxPlayers = 4;
		if ( p.TryGetProperty( "maxPlayers", out var mpEl ) && mpEl.TryGetInt32( out var mp ) )
			maxPlayers = System.Math.Clamp( mp, 2, 64 );

		int waitForJoinSeconds = 120;
		if ( p.TryGetProperty( "waitForJoinSeconds", out var wEl ) && wEl.TryGetInt32( out var wf ) )
			waitForJoinSeconds = System.Math.Clamp( wf, 0, 600 );

		int aliveAlready = MultiplayerTestRunner.AliveCount();
		if ( aliveAlready > 0 )
			return Task.FromResult<object>( new { error = $"a multiplayer test is already running with {aliveAlready} tracked client(s). stop_multiplayer_test first; overlapping starts cannot attribute joins reliably." } );

		// 1) Resolve sbox.exe before creating any network state — never hardcoded
		//    (editor dir first, then Steam libraries).
		var exe = MultiplayerTestRunner.ResolveSboxExe( out var how );
		if ( exe == null )
			return Task.FromResult<object>( new { error = "could not find sbox.exe — looked next to the running editor process, then in every Steam library from libraryfolders.vdf. Is this a non-Steam install? No lobby was created." } );

		// 2) Host if nobody is. Play mode does NOT host by default: Networking.IsActive
		//    stays false until someone calls CreateLobby (proven live 2026-07-13).
		bool lobbyCreated = false;
		bool wasActive = false;
		try { wasActive = Networking.IsActive; } catch { }

		if ( !wasActive && maxPlayers < clients + 1 )
			return Task.FromResult<object>( new { error = $"maxPlayers must be at least {clients + 1} for the host plus {clients} requested client(s); no lobby or client process was created." } );

		int baselineConnections = 1;
		if ( wasActive )
		{
			try { baselineConnections = System.Math.Max( 1, Connection.All.Count ); } catch { }

			int activeMaxPlayers = 0;
			try { activeMaxPlayers = Networking.MaxPlayers; } catch { }
			if ( activeMaxPlayers <= 0 )
				return Task.FromResult<object>( new { error = "active lobby capacity is not available yet; wait for the networking session to settle, then retry. No client process was created." } );
			if ( baselineConnections + clients > activeMaxPlayers )
				return Task.FromResult<object>( new
				{
					error = $"active lobby capacity is {activeMaxPlayers}, with {baselineConnections} connection(s) already present; {clients} requested client(s) would exceed it. No client process was created."
				} );
		}

		if ( !wasActive )
		{
			try
			{
				// Explicitly private + hidden: LobbyConfig otherwise defaults to a
				// discoverable public lobby, which is unsafe for a local test harness.
				Networking.CreateLobby( new Sandbox.Network.LobbyConfig
				{
					MaxPlayers = maxPlayers,
					Name = "Claude Bridge multiplayer test",
					Privacy = Sandbox.Network.LobbyPrivacy.Private,
					Hidden = true,
					DestroyWhenHostLeaves = true,
					AutoSwitchToBestHost = false,
				} );
				lobbyCreated = true;
			}
			catch ( Exception ex )
			{
				return Task.FromResult<object>( new { error = $"CreateLobby failed: {ex.Message}" } );
			}

			// A newly created lobby can take a frame to expose its host connection.
			// One is the minimum truthful baseline because this process is the host.
			try { baselineConnections = System.Math.Max( 1, Connection.All.Count ); } catch { }
		}

		// 3) Spawn N REAL game clients that auto-join the local session (editor is
		//    unsandboxed — Process.Start is fine here).
		var spawned = new List<int>();
		var spawnErrors = new List<string>();
		for ( int i = 0; i < clients; i++ )
		{
			try
			{
				var psi = new System.Diagnostics.ProcessStartInfo
				{
					FileName = exe,
					Arguments = "-joinlocal",
					WorkingDirectory = System.IO.Path.GetDirectoryName( exe ),
					UseShellExecute = false,
				};
				var proc = System.Diagnostics.Process.Start( psi );
				if ( proc == null )
				{
					spawnErrors.Add( $"client {i}: Process.Start returned null" );
					continue;
				}
				MultiplayerTestRunner.Track( new MultiplayerTestRunner.ClientProc
				{
					Pid = proc.Id,
					StartedUtc = DateTime.UtcNow,
					Process = proc,
				} );
				spawned.Add( proc.Id );
			}
			catch ( Exception ex )
			{
				spawnErrors.Add( $"client {i}: {ex.Message}" );
			}
		}

		if ( spawned.Count == 0 )
		{
			string rollback = null;
			if ( lobbyCreated )
			{
				try
				{
					Networking.Disconnect();
					rollback = "The lobby created by this call was disconnected.";
				}
				catch ( Exception ex )
				{
					rollback = $"WARNING: rollback of the lobby created by this call failed: {ex.Message}. Call stop_multiplayer_test disconnect:true.";
				}
			}
			return Task.FromResult<object>( new
			{
				error = $"no client spawned: {string.Join( "; ", spawnErrors )}"
					+ ( rollback == null ? "" : $" {rollback}" )
			} );
		}

		// Add only the processes spawned by this call to the pre-spawn connection
		// baseline. This prevents an unrelated existing connection from satisfying
		// the join wait before a newly launched client arrives.
		int expected = baselineConnections + spawned.Count;

		string jobId = null;
		if ( waitForJoinSeconds > 0 )
			jobId = MultiplayerTestRunner.BeginJoinWait( expected, waitForJoinSeconds );

		bool hosting = false, isHost = false;
		try { hosting = Networking.IsActive; isHost = hosting && Networking.IsHost; } catch { }
		int liveMaxPlayers = 0;
		try { liveMaxPlayers = Networking.MaxPlayers; } catch { }

		return Task.FromResult<object>( new
		{
			hosting,
			isHost,
			lobbyCreated,
			maxPlayers = liveMaxPlayers,
			spawned = spawned.ToArray(),
			spawnErrors = spawnErrors.Count > 0 ? spawnErrors.ToArray() : null,
			sboxExe = exe,
			resolvedVia = how,
			baselineConnections,
			expectedConnections = expected,
			jobId,
			waitForJoinSeconds,
			note = "Each client is a REAL sbox.exe instance: ~80 s to boot + auto-join, ~4.5 GB RAM. "
				+ ( jobId != null
					? "Poll multiplayer_test_status until joinWait.state='complete' (connectionCount reaches expectedConnections). "
					: "waitForJoinSeconds=0 — poll multiplayer_test_status yourself until connectionCount reaches expectedConnections. " )
				+ ( lobbyCreated && !hosting ? "Lobby creation may settle asynchronously — trust multiplayer_test_status over this snapshot. " : "" )
				+ ( lobbyCreated ? "The test lobby is private and hidden. " : "" )
				+ "Then drive the HOST player (drive_player/playtest) and assert replication host-side (inspect_networked_object / get_runtime_property) — the bridge cannot see inside the client window. stop_multiplayer_test cleans up.",
		} );
	}
}

/// <summary>
/// multiplayer_test_status — read-only snapshot of the multiplayer test harness:
/// networking state, host-side connections, tracked client processes, and the join-wait
/// job. Works whether or not a test is running (empty registry = honest empty state).
/// Returns { networkingActive, isHost, connectionCount, connections:[{name,id,isHost}],
///           clients:[{pid,alive,uptimeSeconds}], joinWait:{state,...}, playing }.
/// No params.
/// </summary>
public class MultiplayerTestStatusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		bool active = false, isHost = false;
		try { active = Networking.IsActive; } catch { }
		try { isHost = active && Networking.IsHost; } catch { }   // IsHost can throw with no session

		int connectionCount = 0;
		var connections = new List<object>();
		if ( active )
		{
			try
			{
				foreach ( var c in Connection.All )
				{
					if ( c is null ) continue;   // Connection has NO IsValid — null-check only
					string name = null; string id = null; bool h = false;
					try { name = string.IsNullOrEmpty( c.DisplayName ) ? c.Name : c.DisplayName; } catch { }
					try { id = c.Id.ToString(); } catch { }
					try { h = c.IsHost; } catch { }
					connections.Add( new { name, id, isHost = h } );
				}
				connectionCount = connections.Count;
			}
			catch { }
		}

		var clients = MultiplayerTestRunner.Snapshot().Select( c => (object) new
		{
			pid = c.Pid,
			alive = MultiplayerTestRunner.IsAlive( c ),
			uptimeSeconds = System.Math.Round( ( DateTime.UtcNow - c.StartedUtc ).TotalSeconds, 1 ),
		} ).ToList();

		return Task.FromResult<object>( new
		{
			networkingActive = active,
			isHost,
			connectionCount,
			connections,
			clients,
			joinWait = MultiplayerTestRunner.JoinWaitStatus( connectionCount ),
			playing = Game.IsPlaying,
		} );
	}
}

/// <summary>
/// stop_multiplayer_test — kill every tracked sbox.exe test client (process tree),
/// clear exited entries, and retain live failures for a normal retry. Params:
///   disconnect : optional bool (default false) — also Networking.Disconnect() the host
///                session on the running game
///   alsoStrays : optional bool (default false) — safety net: kill ANY process named
///                'sbox' that isn't the editor (sbox-dev) or our own process
/// Returns { stopped, clients:[{pid,killed,note?}], remainingPids, disconnected, strays, note }. Failed live kills remain tracked so a normal stop call can retry.
/// A PID may already be dead (client crashed / closed by hand) — reported per-PID, not
/// an error. Killing a connected client leaves benign TcpChannel.SendThread traces in
/// the editor log (not an error). Safe to call with nothing running.
/// </summary>
public class StopMultiplayerTestHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		bool disconnect = p.TryGetProperty( "disconnect", out var dEl ) && dEl.ValueKind == JsonValueKind.True;
		bool alsoStrays = p.TryGetProperty( "alsoStrays", out var sEl ) && sEl.ValueKind == JsonValueKind.True;

		var results = new List<object>();
		var survivors = new List<MultiplayerTestRunner.ClientProc>();
		foreach ( var c in MultiplayerTestRunner.Snapshot() )
		{
			if ( !MultiplayerTestRunner.IsAlive( c ) )
			{
				results.Add( new { pid = c.Pid, killed = false, note = "already dead" } );
				continue;
			}

			try
			{
				var proc = c.Process;
				if ( proc == null || proc.HasExited )
					proc = System.Diagnostics.Process.GetProcessById( c.Pid );
				proc.Kill( entireProcessTree: true );
				bool exited = proc.WaitForExit( 2000 );
				if ( exited )
				{
					results.Add( new { pid = c.Pid, killed = true } );
				}
				else
				{
					survivors.Add( c );
					results.Add( new { pid = c.Pid, killed = false, note = "kill requested but process is still alive after 2 seconds; it remains tracked for retry" } );
				}
			}
			catch ( Exception ex )
			{
				bool alive = MultiplayerTestRunner.IsAlive( c );
				if ( alive ) survivors.Add( c );
				results.Add( new
				{
					pid = c.Pid,
					killed = false,
					note = alive
						? $"{ex.Message}; process remains alive and tracked for retry"
						: $"{ex.Message}; process no longer appears alive"
				} );
			}
		}

		object disconnected = null;
		if ( disconnect )
		{
			try
			{
				bool active = false;
				try { active = Networking.IsActive; } catch { }
				if ( active )
				{
					Networking.Disconnect();
					disconnected = true;
				}
				else
				{
					disconnected = false;   // nothing to disconnect
				}
			}
			catch ( Exception ex )
			{
				disconnected = $"Disconnect failed: {ex.Message}";
			}
		}

		var strays = new List<object>();
		if ( alsoStrays )
		{
			int ownPid = System.Environment.ProcessId;
			try
			{
				// GetProcessesByName matches the process name EXACTLY: "sbox" = sbox.exe game
				// clients only; the editor is "sbox-dev". Belt-and-braces guards anyway —
				// never kill the editor or ourselves.
				foreach ( var proc in System.Diagnostics.Process.GetProcessesByName( "sbox" ) )
				{
					try
					{
						if ( proc.Id == ownPid ) continue;
						if ( proc.ProcessName.Contains( "dev", StringComparison.OrdinalIgnoreCase ) ) continue;
						proc.Kill( entireProcessTree: true );
						strays.Add( new { pid = proc.Id, killed = true } );
					}
					catch ( Exception ex )
					{
						strays.Add( new { pid = proc.Id, killed = false, note = ex.Message } );
					}
				}
			}
			catch ( Exception ex )
			{
				strays.Add( new { error = $"stray scan failed: {ex.Message}" } );
			}
		}

		var remaining = survivors.Where( MultiplayerTestRunner.IsAlive ).ToList();
		MultiplayerTestRunner.ResetAfterStop( remaining );
		bool stopped = remaining.Count == 0;

		return Task.FromResult<object>( new
		{
			stopped,
			clients = results,
			remainingPids = remaining.Select( c => c.Pid ).ToArray(),
			disconnected,
			strays = alsoStrays ? strays : null,
			note = ( stopped
				? "No tracked client processes remain; the harness registry was cleared."
				: $"{remaining.Count} tracked client process(es) remain alive and registered; retry stop_multiplayer_test or use alsoStrays:true for explicit recovery." )
				+ ( disconnect ? " Host session disconnect attempted (see 'disconnected')." : " Host session left as-is — pass disconnect:true to Networking.Disconnect() it." )
				+ " Killing a connected client leaves benign TcpChannel.SendThread traces in the log.",
		} );
	}
}