using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// Play-mode INPUT DRIVER — drive_player  (EXPERIMENTAL)
//
// This file lives in the SAME assembly as MyEditorMenu.cs, so it reuses the
// IBridgeHandler dispatch contract and the shared ClaudeBridge.ResolveGameObject
// helper. It is UNSANDBOXED editor code, so System.Math is fine here (NOT MathX —
// MathX is only required in the C# *strings* we WRITE to disk; this file writes
// nothing to disk, it drives the live play-mode scene directly).
//
// WHY THIS EXISTS — the gap simulate_input cannot close
// ─────────────────────────────────────────────────────
// simulate_input calls Sandbox.Input.SetAction(action, down) ONCE. The bridge
// runs each handler to completion inside a SINGLE editor frame (see
// ClaudeBridge.ProcessPendingOnMainThread — one Execute() per request, returns
// within that frame). So SetAction flips the action for ~one frame:
//   • Input.Pressed("x") (the rising EDGE) frequently MISSES it — by the time the
//     player controller's OnUpdate samples input, or because the press+release
//     collapse into the same frame, the edge never registers. (Confirmed live:
//     ShovelEquipped stayed false after a press AND a 500 ms hold.)
//   • There is NO analog injection at all — Input.AnalogMove / Input.AnalogLook
//     are engine-driven; Sandbox.Input exposes no setter, so WASD-style movement
//     and mouse-look can't be synthesized through SetAction.
//
// THE DESIGN — a per-frame driver that outlives the handler call
// ──────────────────────────────────────────────────────────────
// A handler cannot itself span multiple frames (it must return inside one frame).
// So drive_player ENQUEUES a "drive job" and returns immediately; a dedicated
// [EditorEvent.Frame] ticker (PlayInputDriver.OnFrame) applies that job across the
// requested number of frames while play mode is live. Each ticked frame we:
//   1. Drive the controller DIRECTLY (the reliable path — bypasses Input entirely):
//        • set EyeAngles (look) — absolute target or per-frame delta,
//        • feed analog movement by writing the controller's wish/move state
//          (WishVelocity / AnalogMove-equivalent, resolved by reflection so it
//          works for the built-in PlayerController AND the bridge-generated
//          CharacterController controllers).
//   2. ALSO hold the named action DOWN via Input.SetAction every frame for the
//        whole duration — holding across many frames is what finally lets
//        Input.Pressed fire its edge (frame N action=false → frame N+1 action=true).
//
// Driving the controller directly is the robust path; the held SetAction is the
// belt-and-suspenders for action-based controls (jump/use/attack) that read
// Input.Pressed/Down. We do BOTH so the tool works regardless of how a given
// project samples input.
//
// EXPERIMENTAL — the controller-state field names vary by SDK / project. The
// reflection resolver tries the known set (EyeAngles, WishVelocity, AnalogMove,
// Move) and REPORTS exactly which members it found and wrote, so a live session
// can confirm/adjust. See the registration + live-test notes in the impl summary.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Per-frame driver state. A single active job at a time (re-issuing replaces it).
/// Ticked by the [EditorEvent.Frame] handler below; self-clears when play mode
/// ends or the duration elapses.
/// </summary>
internal static class PlayInputDriver
{
	internal class DriveJob
	{
		public Guid TargetId;             // controller GameObject (resolved each frame; runtime objects can move in/out of the directory)
		public string ComponentType;      // e.g. "PlayerController" — null = first controller-ish component found
		public int FramesRemaining;       // counts down to 0
		public int FramesTotal;

		// Look
		public bool HasLook;              // absolute EyeAngles target (pitch,yaw,roll)
		public Angles LookAngles;
		public bool HasLookDelta;         // per-frame delta added to EyeAngles each tick
		public Angles LookDeltaPerFrame;

		// Move — analog wish on the controller's local frame. x=forward(+)/back(-), y=left(+)/right(-)
		public bool HasMove;
		public Vector2 Move;              // normalized-ish; multiplied into the controller's own speed
		public float MoveSpeed;           // units/sec used when we have to synthesize a WishVelocity directly

		// Action hold (edge-friendly): held DOWN for the whole duration via Input.SetAction each frame.
		public string HoldAction;

		// Diagnostics captured on the first applied frame (surfaced back to the caller via the last-result cache).
		public List<string> AppliedMembers = new();
		public string ResolveNote;
		public bool EverApplied;
	}

	private static DriveJob _job;
	private static readonly object _lock = new();

	// Last finished/started job summary, so the handler's response can report what actually happened.
	private static object _lastSummary;

	internal static void Start( DriveJob job )
	{
		lock ( _lock ) { _job = job; }
	}

	internal static object ConsumeSummary()
	{
		lock ( _lock ) { var s = _lastSummary; return s; }
	}

	internal static object Snapshot( DriveJob j )
	{
		return new
		{
			targetId        = j.TargetId.ToString(),
			component       = j.ComponentType,
			framesTotal     = j.FramesTotal,
			framesRemaining = j.FramesRemaining,
			hasLook         = j.HasLook,
			hasLookDelta    = j.HasLookDelta,
			hasMove         = j.HasMove,
			holdAction      = j.HoldAction,
		};
	}

	/// <summary>
	/// Ticked EVERY editor frame (separate from the bridge's IPC frame handler so a
	/// slow drive never blocks request processing, and vice-versa). Applies the active
	/// drive job to the live PlayerController while play mode runs.
	/// </summary>
	[EditorEvent.Frame]
	public static void OnFrame()
	{
		DriveJob j;
		lock ( _lock ) { j = _job; }
		if ( j == null ) return;

		// Drop the job the instant we leave play mode — the play scene is gone.
		if ( !Game.IsPlaying )
		{
			lock ( _lock ) { _lastSummary = Finish( j, "play mode ended" ); _job = null; }
			return;
		}

		try { ApplyOnce( j ); }
		catch ( Exception ex )
		{
			lock ( _lock ) { _lastSummary = Finish( j, $"driver error: {ex.Message}" ); _job = null; }
			return;
		}

		j.FramesRemaining--;
		if ( j.FramesRemaining <= 0 )
		{
			// Release the held action on the final frame so it doesn't stick down.
			if ( !string.IsNullOrEmpty( j.HoldAction ) )
			{
				try { Sandbox.Input.SetAction( j.HoldAction, false ); } catch { }
			}
			lock ( _lock ) { _lastSummary = Finish( j, "completed" ); _job = null; }
		}
	}

	static object Finish( DriveJob j, string reason )
	{
		return new
		{
			finished        = true,
			reason,
			everApplied     = j.EverApplied,
			framesApplied   = j.FramesTotal - System.Math.Max( 0, j.FramesRemaining ),
			appliedMembers  = j.AppliedMembers.Distinct().ToList(),
			resolveNote     = j.ResolveNote,
		};
	}

	/// <summary>Apply the job to the controller for ONE frame.</summary>
	static void ApplyOnce( DriveJob j )
	{
		var scene = Game.ActiveScene;
		if ( scene == null ) { j.ResolveNote = "no active play scene"; return; }

		var controller = ResolveController( scene, j );
		if ( controller == null )
		{
			j.ResolveNote = "no controller resolved (pass id of a GameObject with a PlayerController/controller component, or have one in the scene)";
			// Still hold the action even with no controller — pure action injection is valid.
			if ( !string.IsNullOrEmpty( j.HoldAction ) )
				try { Sandbox.Input.SetAction( j.HoldAction, true ); } catch { }
			return;
		}

		var td = Game.TypeLibrary.GetType( controller.GetType() );

		// ── LOOK ──────────────────────────────────────────────────────────────
		// Built-in PlayerController exposes EyeAngles (Angles). Setting it each frame
		// is the reliable, drift-free way to aim — it bypasses Input.AnalogLook.
		if ( j.HasLook || j.HasLookDelta )
		{
			Angles target = j.LookAngles;
			if ( j.HasLookDelta )
			{
				var cur = ReadAngles( controller, td, "EyeAngles" ) ?? new Angles();
				target = j.HasLook ? target : cur;
				// Add component-wise (don't rely on an Angles+Angles operator).
				target.pitch += j.LookDeltaPerFrame.pitch;
				target.yaw   += j.LookDeltaPerFrame.yaw;
				target.roll  += j.LookDeltaPerFrame.roll;
			}
			target.pitch = System.Math.Clamp( target.pitch, -89f, 89f );
			j.LookAngles = target;            // accumulate so a delta keeps integrating
			j.HasLook = true;

			if ( TrySetAngles( controller, td, "EyeAngles", target ) )
				j.AppliedMembers.Add( "EyeAngles" );
		}

		// ── MOVE ──────────────────────────────────────────────────────────────
		// Two strategies, best-effort in order:
		//   A) Write the controller's analog-move field if it mirrors one (AnalogMove/MoveInput).
		//   B) Synthesize a WishVelocity in the controller's facing frame and write it.
		//      The built-in PlayerController consumes WishVelocity in its own Move().
		if ( j.HasMove )
		{
			bool wrote = false;

			// Strategy A — a mirrored analog-move vector (Vector2/Vector3) the controller reads.
			foreach ( var member in new[] { "AnalogMove", "MoveInput", "WishMove" } )
			{
				if ( TrySetMoveVector( controller, td, member, j.Move ) )
				{
					j.AppliedMembers.Add( member );
					wrote = true;
					break;
				}
			}

			// Strategy B — synthesize a WishVelocity from the eye/world yaw.
			var yaw = ( ReadAngles( controller, td, "EyeAngles" ) ?? controller.WorldRotation.Angles() ).yaw;
			var rot = Rotation.From( 0f, yaw, 0f );   // yaw-only facing (verified idiom: Rotation.From(pitch,yaw,roll))
			var wish = ( rot.Forward * j.Move.x + rot.Left * j.Move.y );
			if ( wish.Length > 1f ) wish = wish.Normal;
			wish *= j.MoveSpeed;

			if ( TrySetVector3( controller, td, "WishVelocity", wish ) )
			{
				j.AppliedMembers.Add( "WishVelocity" );
				wrote = true;
			}

			if ( !wrote )
				j.ResolveNote = ( j.ResolveNote == null ? "" : j.ResolveNote + " " ) +
					"no movement member could be written (tried AnalogMove/MoveInput/WishMove/WishVelocity) — drive via hold actions or report controller members via describe_type.";
		}

		// ── ACTION HOLD (edge-friendly) ────────────────────────────────────────
		// Held DOWN every frame for the whole duration. Across N frames this finally
		// gives Input.Pressed an edge to catch (false→true on the first held frame).
		if ( !string.IsNullOrEmpty( j.HoldAction ) )
		{
			try { Sandbox.Input.SetAction( j.HoldAction, true ); } catch { }
		}

		j.EverApplied = true;
	}

	// ── Controller resolution ──────────────────────────────────────────────────
	static Component ResolveController( Scene scene, DriveJob j )
	{
		// Explicit target id wins.
		if ( j.TargetId != Guid.Empty )
		{
			var go = ClaudeBridge.ResolveGameObject( scene, j.TargetId.ToString() );
			if ( go != null )
			{
				var c = FindControllerOn( go, j.ComponentType );
				if ( c != null ) return c;
			}
		}

		// Otherwise scan the play scene for the first controller-ish component.
		foreach ( var obj in scene.GetAllObjects( true ) )
		{
			var c = FindControllerOn( obj, j.ComponentType );
			if ( c != null )
			{
				j.TargetId = obj.Id;        // pin it so later frames resolve fast + stably
				return c;
			}
		}
		return null;
	}

	static Component FindControllerOn( GameObject go, string componentType )
	{
		if ( go == null ) return null;
		var all = go.Components.GetAll().ToList();

		if ( !string.IsNullOrEmpty( componentType ) )
			return all.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );

		// Heuristic: exact built-in first, then anything whose type name ends in "Controller"
		// AND that exposes an EyeAngles or WishVelocity member (the controller shape we drive).
		var exact = all.FirstOrDefault( c => c.GetType().Name == "PlayerController" );
		if ( exact != null ) return exact;

		return all.FirstOrDefault( c =>
		{
			var n = c.GetType().Name;
			if ( !n.EndsWith( "Controller", StringComparison.OrdinalIgnoreCase ) ) return false;
			var td = Game.TypeLibrary.GetType( c.GetType() );
			if ( td == null ) return false;
			return td.Properties.Any( pp => pp.Name == "EyeAngles" || pp.Name == "WishVelocity" );
		} );
	}

	// ── Reflection set/get helpers (mirror SetComponentReferenceHandler's idiom) ──
	static Angles? ReadAngles( Component c, TypeDescription td, string member )
	{
		try
		{
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == member );
			if ( pd == null ) return null;
			var v = pd.GetValue( c );
			if ( v is Angles a ) return a;
			if ( v is Rotation r ) return r.Angles();
		}
		catch { }
		return null;
	}

	static bool TrySetAngles( Component c, TypeDescription td, string member, Angles value )
	{
		try
		{
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == member );
			if ( pd == null ) return false;
			if ( pd.PropertyType == typeof( Angles ) ) { pd.SetValue( c, value ); return true; }
			if ( pd.PropertyType == typeof( Rotation ) ) { pd.SetValue( c, Rotation.From( value ) ); return true; }
		}
		catch { }
		return false;
	}

	static bool TrySetVector3( Component c, TypeDescription td, string member, Vector3 value )
	{
		try
		{
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == member );
			if ( pd == null || pd.PropertyType != typeof( Vector3 ) ) return false;
			pd.SetValue( c, value );
			return true;
		}
		catch { return false; }
	}

	// Move vector may be exposed as Vector2 (analog) or Vector3 (XY plane).
	static bool TrySetMoveVector( Component c, TypeDescription td, string member, Vector2 move )
	{
		try
		{
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == member );
			if ( pd == null ) return false;
			if ( pd.PropertyType == typeof( Vector2 ) ) { pd.SetValue( c, move ); return true; }
			if ( pd.PropertyType == typeof( Vector3 ) ) { pd.SetValue( c, new Vector3( move.x, move.y, 0f ) ); return true; }
		}
		catch { }
		return false;
	}
}

/// <summary>
/// drive_player (EXPERIMENTAL) — synthesize sustained player input during play mode by
/// driving the active PlayerController DIRECTLY across N frames: set EyeAngles (look),
/// feed analog movement (wish velocity), and/or hold a named action DOWN long enough that
/// Input.Pressed fires its edge. Closes the gaps single-frame simulate_input cannot:
/// edge-triggered controls and analog move/look. Requires play mode.
/// </summary>
public class DrivePlayerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "drive_player requires play mode (start_play first)" } );

		try
		{
			var scene = Game.ActiveScene;
			if ( scene == null )
				return Task.FromResult<object>( new { error = "No active play scene" } );

			var job = new PlayInputDriver.DriveJob();

			// Target controller (optional — auto-resolves the first PlayerController if omitted).
			if ( p.TryGetProperty( "id", out var idEl ) && idEl.ValueKind == JsonValueKind.String
				 && Guid.TryParse( idEl.GetString(), out var gid ) )
				job.TargetId = gid;
			if ( p.TryGetProperty( "component", out var compEl ) && compEl.ValueKind == JsonValueKind.String )
				job.ComponentType = compEl.GetString();

			// Duration: frames OR durationMs (≈ converted at 60 fps). Default ~0.5s = 30 frames.
			int frames = 30;
			if ( p.TryGetProperty( "frames", out var fEl ) && fEl.TryGetInt32( out var fi ) ) frames = fi;
			else if ( p.TryGetProperty( "durationMs", out var dmEl ) && dmEl.TryGetInt32( out var dm ) )
				frames = (int) System.Math.Round( dm / 1000.0 * 60.0 );
			frames = System.Math.Clamp( frames, 1, 1800 );   // cap at ~30s so a runaway job can't pin input forever
			job.FramesRemaining = frames;
			job.FramesTotal = frames;

			// ── Look ──
			// look: absolute {pitch,yaw,roll} EyeAngles target (held for the whole duration).
			if ( p.TryGetProperty( "look", out var lookEl ) )
			{
				job.LookAngles = ParseAngles( lookEl );
				job.HasLook = true;
			}
			// lookDelta: per-frame {pitch,yaw,roll} added to EyeAngles each frame (turn/pan over time).
			if ( p.TryGetProperty( "lookDelta", out var ldEl ) )
			{
				job.LookDeltaPerFrame = ParseAngles( ldEl );
				job.HasLookDelta = true;
			}

			// ── Move ──
			// move: {x: forward(+)/back(-), y: left(+)/right(-)} in the controller's facing frame.
			if ( p.TryGetProperty( "move", out var moveEl ) )
			{
				job.Move = ParseMove( moveEl );
				job.HasMove = job.Move.Length > 0.0001f;
			}
			job.MoveSpeed = p.TryGetProperty( "moveSpeed", out var msEl ) && msEl.TryGetSingle( out var msf )
				? msf : 160f;   // a sane default cruising speed in source units/sec

			// ── Action hold (edge-friendly press) ──
			// action: a named input action ("jump","use","attack1",…) held DOWN for the whole
			// duration so Input.Pressed catches the rising edge that single-frame SetAction misses.
			if ( p.TryGetProperty( "action", out var actEl ) && actEl.ValueKind == JsonValueKind.String )
				job.HoldAction = actEl.GetString();

			if ( !job.HasLook && !job.HasLookDelta && !job.HasMove && string.IsNullOrEmpty( job.HoldAction ) )
				return Task.FromResult<object>( new { error = "Nothing to drive — provide at least one of: look, lookDelta, move, action." } );

			PlayInputDriver.Start( job );

			return Task.FromResult<object>( new
			{
				driving = true,
				experimental = true,
				frames,
				note = "Drive job started — it runs ASYNC across editor frames; this returns immediately. " +
					   "Poll the result with drive_player_status (or wait ~frames/60 seconds), then verify the " +
					   "effect with capture_view / get_runtime_property. The controller is driven directly " +
					   "(EyeAngles + WishVelocity); the action (if any) is held DOWN every frame so Input.Pressed fires.",
				job = PlayInputDriver.Snapshot( job ),
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"drive_player failed: {ex.Message}" } );
		}
	}

	// {pitch,yaw,roll} object, [pitch,yaw,roll] array, or "pitch,yaw,roll" string.
	static Angles ParseAngles( JsonElement el )
		=> ClaudeBridge.ParseRotation( el ).Angles();

	// {x,y} object, [x,y] array, or "x,y" string. x=forward(+)/back(-), y=left(+)/right(-).
	static Vector2 ParseMove( JsonElement el )
	{
		float x = 0f, y = 0f;
		if ( el.ValueKind == JsonValueKind.Object )
		{
			if ( el.TryGetProperty( "x", out var xp ) && xp.TryGetSingle( out var xf ) ) x = xf;
			if ( el.TryGetProperty( "y", out var yp ) && yp.TryGetSingle( out var yf ) ) y = yf;
		}
		else if ( el.ValueKind == JsonValueKind.Array )
		{
			var a = el.EnumerateArray().ToList();
			if ( a.Count > 0 && a[0].TryGetSingle( out var a0 ) ) x = a0;
			if ( a.Count > 1 && a[1].TryGetSingle( out var a1 ) ) y = a1;
		}
		else if ( el.ValueKind == JsonValueKind.String )
		{
			var parts = ( el.GetString() ?? "" ).Split( ',' );
			if ( parts.Length > 0 ) float.TryParse( parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x );
			if ( parts.Length > 1 ) float.TryParse( parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y );
		}
		// Clamp the analog magnitude to 1 so callers can pass either normalized or raw values.
		var v = new Vector2( x, y );
		if ( v.Length > 1f ) v = v.Normal;
		return v;
	}
}

/// <summary>
/// drive_player_status (EXPERIMENTAL) — read the result of the most recently FINISHED
/// drive_player job (which members were written, how many frames applied, why it ended).
/// Because drive_player runs across frames and returns immediately, this is how you confirm
/// it actually applied. Returns {active:true} while a job is still running.
/// </summary>
public class DrivePlayerStatusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var summary = PlayInputDriver.ConsumeSummary();
		if ( summary == null )
			return Task.FromResult<object>( new { active = false, lastResult = (object) null, note = "No drive_player job has finished yet (or none ever ran)." } );
		return Task.FromResult<object>( new { active = false, lastResult = summary } );
	}
}
