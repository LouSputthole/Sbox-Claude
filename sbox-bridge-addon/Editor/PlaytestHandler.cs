using Editor;
using Sandbox;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// PLAYTEST HARNESS — playtest / playtest_status  (the gameplay-verification frontier)
//
// Same assembly as MyEditorMenu.cs (reuses IBridgeHandler + ClaudeBridge helpers).
// Unsandboxed editor code → System.Math / System.Reflection are fine here.
//
// WHY AN IN-ADDON RUNNER (not TS round-trips):
// Verifying a gameplay LOOP needs input + state-reads + assertions that time-align
// with the game's frames. Two facts (proven live on the Gravehold player) force this:
//   1. The facepunch PlayerController reads Input.AnalogMove each frame and OVERWRITES
//      a WishVelocity you set — UNLESS you set `UseInputControls=false` first. With it
//      off, setting WishVelocity moved the player 0→526u. So a move step must flip that
//      toggle, drive WishVelocity per frame, and ZERO it after (it persists otherwise).
//   2. Transient state (a jump's z-velocity) is gone by the time a SEPARATE bridge call
//      lands — so assertions must be evaluated IN-FRAME, inside the editor frame loop.
// => one async job, ticked by [EditorEvent.Frame], runs a step list and records a
//    pass/fail transcript. TS only starts it (playtest) and polls it (playtest_status).
//
// Step verbs: move · look · lookDelta · action · jump · set · wait · capture · assert
//   { "move": {"x":1}, "frames":60 }                 analog move (auto UseInputControls=false)
//   { "look": {"pitch":0,"yaw":90,"roll":0} }        set EyeAngles
//   { "lookDelta": {"yaw":2}, "frames":30 }          sweep EyeAngles
//   { "action": "use", "frames":20 }                 hold a named input action (rising-edge safe)
//   { "jump": "0,0,400" }                            invoke the controller's Jump(velocity)
//   { "set": {"component":"PlayerController","property":"UseInputControls","to":"false"} }
//   { "wait": 10 }                                   advance N frames
//   { "capture": "after-jump" }                      screenshot the live player POV → path in transcript
//   { "assert": {"read":"Displacement","op":">","value":50,"desc":"moved >50u from start"} }
//
// assert.read = "WorldPosition[.x|.y|.z]" (the controller's GameObject), "Displacement"
//               (scalar distance moved from job start — the facing-independent movement proof), OR
//               "<Component>.<Property>[.x|.y|.z|.Count]" (a component on the player).
// assert.op   = > < >= <= == != changed   (changed = differs from the value at job start)
// ═══════════════════════════════════════════════════════════════════════════

internal static class PlaytestRunner
{
	internal class StepSpec
	{
		public string Kind;
		public int Frames = 1;
		public Vector2 Move;
		public Angles Look; public bool HasLook;
		public Angles LookDelta;
		public string Action;
		public Vector3 JumpVel;
		public string SetComponent, SetProperty, SetValue;
		public string AssertRead, AssertOp, AssertValue, AssertDesc;
		public string CaptureLabel;
		public float MoveSpeed = 160f;
	}

	internal class Job
	{
		public Guid TargetId;
		public string ComponentType;
		public Component Controller;     // resolved once
		public GameObject Anchor;        // controller.GameObject — the player object
		public Vector3 StartPos;         // Anchor.WorldPosition at job start (for the "Displacement" read)
		public List<StepSpec> Steps;
		public int Index;
		public int FrameInStep;
		public List<object> Transcript = new();
		public int Passed, Failed;
		public bool DisabledInput;       // we flipped UseInputControls=false → restore at teardown
		public string HeldAction;        // currently-held action (release at step exit / teardown)
		public Dictionary<string, string> Baselines = new(); // read-key → value at job start (for "changed")
		public bool Done;
		public string EndReason;
		public bool Started;
	}

	private static Job _job;
	private static readonly object _lock = new();
	private static object _lastSummary;

	internal static void Start( Job job ) { lock ( _lock ) { _job = job; _lastSummary = null; } }
	internal static object ConsumeSummary() { lock ( _lock ) { return _lastSummary; } }
	internal static bool IsActive() { lock ( _lock ) { return _job != null; } }

	/// <summary>Stop the running job NOW: teardown (restore input state) + summarize as aborted.</summary>
	internal static object Abort()
	{
		lock ( _lock )
		{
			if ( _job == null )
				return new { aborted = false, note = "No playtest job is running. playtest_status shows the last summary." };
			var j = _job;
			Teardown( j );
			_lastSummary = Summarize( j, "aborted via playtest_abort" );
			_job = null;
			return new
			{
				aborted = true,
				stepsRun = j.Index,
				passed = j.Passed,
				failed = j.Failed,
				note = "Job stopped, input state restored. The partial transcript is available via playtest_status."
			};
		}
	}

	internal static object LiveSnapshot()
	{
		lock ( _lock )
		{
			if ( _job == null ) return null;
			return new { active = true, step = _job.Index, totalSteps = _job.Steps.Count, passed = _job.Passed, failed = _job.Failed };
		}
	}

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		Job j;
		lock ( _lock ) { j = _job; }
		if ( j == null ) return;

		if ( !Game.IsPlaying )
		{
			Teardown( j );
			lock ( _lock ) { _lastSummary = Summarize( j, "play mode ended before completion" ); _job = null; }
			return;
		}

		try
		{
			// Resolve the controller + anchor once.
			if ( !j.Started )
			{
				ResolveAnchor( j );
				CaptureBaselines( j );
				j.Started = true;
			}

			if ( j.Index >= j.Steps.Count )
			{
				Teardown( j );
				lock ( _lock ) { _lastSummary = Summarize( j, "completed" ); _job = null; }
				return;
			}

			var step = j.Steps[j.Index];
			if ( j.FrameInStep == 0 ) StepEnter( j, step );
			StepTick( j, step );
			j.FrameInStep++;

			if ( j.FrameInStep >= System.Math.Max( 1, step.Frames ) )
			{
				StepExit( j, step );
				j.Index++;
				j.FrameInStep = 0;
			}
		}
		catch ( Exception ex )
		{
			// Never let the ticker throw (it'd spam every frame). Record + stop.
			j.Transcript.Add( new { step = j.Index, kind = j.Index < j.Steps.Count ? j.Steps[j.Index].Kind : "?", error = ex.Message } );
			Teardown( j );
			lock ( _lock ) { _lastSummary = Summarize( j, $"runner error: {ex.Message}" ); _job = null; }
		}
	}

	// ── Step lifecycle ─────────────────────────────────────────────────────────
	static void StepEnter( Job j, StepSpec s )
	{
		switch ( s.Kind )
		{
			case "move":
				EnsureInputDisabled( j );   // so WishVelocity isn't overwritten by the controller
				break;
			case "jump":
				DoJump( j, s );
				break;
			case "set":
				DoSet( j, s );
				break;
			case "assert":
				DoAssert( j, s );
				break;
			case "capture":
				DoCapture( j, s );
				break;
		}
	}

	static void StepTick( Job j, StepSpec s )
	{
		switch ( s.Kind )
		{
			case "move":
			{
				if ( j.Controller == null ) return;
				var td = Game.TypeLibrary.GetType( j.Controller.GetType() );
				var yaw = ( ReadAngles( j.Controller, td, "EyeAngles" ) ?? j.Controller.WorldRotation.Angles() ).yaw;
				var rot = Rotation.From( 0f, yaw, 0f );
				var wish = rot.Forward * s.Move.x + rot.Left * s.Move.y;
				if ( wish.Length > 1f ) wish = wish.Normal;
				wish *= s.MoveSpeed;
				TrySetVector3( j.Controller, td, "WishVelocity", wish );
				break;
			}
			case "look":
			{
				if ( j.Controller == null ) return;
				var td = Game.TypeLibrary.GetType( j.Controller.GetType() );
				var a = s.Look; a.pitch = System.Math.Clamp( a.pitch, -89f, 89f );
				TrySetAngles( j.Controller, td, "EyeAngles", a );
				break;
			}
			case "lookDelta":
			{
				if ( j.Controller == null ) return;
				var td = Game.TypeLibrary.GetType( j.Controller.GetType() );
				var cur = ReadAngles( j.Controller, td, "EyeAngles" ) ?? new Angles();
				cur.pitch = System.Math.Clamp( cur.pitch + s.LookDelta.pitch, -89f, 89f );
				cur.yaw += s.LookDelta.yaw;
				cur.roll += s.LookDelta.roll;
				TrySetAngles( j.Controller, td, "EyeAngles", cur );
				break;
			}
			case "action":
				try { Sandbox.Input.SetAction( s.Action, true ); } catch { }
				j.HeldAction = s.Action;
				break;
		}
	}

	static void StepExit( Job j, StepSpec s )
	{
		switch ( s.Kind )
		{
			case "move":
				if ( j.Controller != null )
				{
					var td = Game.TypeLibrary.GetType( j.Controller.GetType() );
					TrySetVector3( j.Controller, td, "WishVelocity", Vector3.Zero );  // stop — WishVelocity persists otherwise
				}
				j.Transcript.Add( new { step = j.Index, kind = "move", frames = s.Frames, move = $"{s.Move.x},{s.Move.y}" } );
				break;
			case "action":
				try { Sandbox.Input.SetAction( s.Action, false ); } catch { }
				j.HeldAction = null;
				j.Transcript.Add( new { step = j.Index, kind = "action", action = s.Action, frames = s.Frames } );
				break;
			case "look":
				j.Transcript.Add( new { step = j.Index, kind = "look", look = $"{s.Look.pitch},{s.Look.yaw},{s.Look.roll}" } );
				break;
			case "lookDelta":
				j.Transcript.Add( new { step = j.Index, kind = "lookDelta", frames = s.Frames } );
				break;
			case "wait":
				j.Transcript.Add( new { step = j.Index, kind = "wait", frames = s.Frames } );
				break;
			// jump/set/assert already recorded their result in StepEnter.
		}
	}

	// ── Actions ────────────────────────────────────────────────────────────────
	static void DoJump( Job j, StepSpec s )
	{
		if ( j.Controller == null )
		{
			j.Transcript.Add( new { step = j.Index, kind = "jump", ok = false, error = "no controller" } );
			j.Failed++;
			return;
		}
		try
		{
			var m = j.Controller.GetType().GetMethod( "Jump", new[] { typeof( Vector3 ) } );
			if ( m == null )
			{
				j.Transcript.Add( new { step = j.Index, kind = "jump", ok = false, error = "controller has no Jump(Vector3)" } );
				j.Failed++;
				return;
			}
			m.Invoke( j.Controller, new object[] { s.JumpVel } );
			j.Transcript.Add( new { step = j.Index, kind = "jump", ok = true, velocity = $"{s.JumpVel.x},{s.JumpVel.y},{s.JumpVel.z}" } );
		}
		catch ( Exception ex )
		{
			j.Transcript.Add( new { step = j.Index, kind = "jump", ok = false, error = ex.Message } );
			j.Failed++;
		}
	}

	static void DoSet( Job j, StepSpec s )
	{
		try
		{
			var comp = FindComponent( j, s.SetComponent );
			if ( comp == null )
			{
				j.Transcript.Add( new { step = j.Index, kind = "set", ok = false, error = $"component '{s.SetComponent}' not found" } );
				j.Failed++; return;
			}
			var td = Game.TypeLibrary.GetType( comp.GetType() );
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == s.SetProperty );
			if ( pd == null )
			{
				j.Transcript.Add( new { step = j.Index, kind = "set", ok = false, error = $"property '{s.SetProperty}' not found" } );
				j.Failed++; return;
			}
			object typed = CoerceTo( pd.PropertyType, s.SetValue );
			pd.SetValue( comp, typed );
			j.Transcript.Add( new { step = j.Index, kind = "set", ok = true, target = $"{s.SetComponent}.{s.SetProperty}", to = s.SetValue } );
		}
		catch ( Exception ex )
		{
			j.Transcript.Add( new { step = j.Index, kind = "set", ok = false, error = ex.Message } );
			j.Failed++;
		}
	}

	static void DoAssert( Job j, StepSpec s )
	{
		string actual = null;
		bool ok = false;
		string err = null;
		try
		{
			object val = ResolveRead( j, s.AssertRead, out err );
			if ( err == null )
			{
				actual = ValueToString( val );
				ok = Compare( j, s.AssertRead, val, s.AssertOp, s.AssertValue, out err );
			}
		}
		catch ( Exception ex ) { err = ex.Message; }

		if ( ok ) j.Passed++; else j.Failed++;
		j.Transcript.Add( new
		{
			step = j.Index,
			kind = "assert",
			ok,
			desc = s.AssertDesc,
			read = s.AssertRead,
			op = s.AssertOp,
			expected = s.AssertValue,
			actual,
			error = err,
		} );
	}

	// ── Capture: screenshot the live player-POV camera (diagnostic, never pass/fail) ──
	static void DoCapture( Job j, StepSpec s )
	{
		try
		{
			var scene = Game.ActiveScene;
			var cam = scene != null ? VisualHelpers.FindMainCamera( scene ) : null;
			if ( cam == null )
			{
				j.Transcript.Add( new { step = j.Index, kind = "capture", ok = false, label = s.CaptureLabel, error = "no main camera in the running scene" } );
				return;
			}
			using var bmp = new Bitmap( 1280, 720 );
			cam.RenderToBitmap( bmp, true );   // renderUI=true → the running game incl. HUD
			string path = System.IO.Path.Combine( System.IO.Path.GetTempPath(), $"bridge_playtest_{System.Guid.NewGuid():N}.png" );
			System.IO.File.WriteAllBytes( path, bmp.ToPng() );
			j.Transcript.Add( new { step = j.Index, kind = "capture", ok = true, label = s.CaptureLabel, path } );
		}
		catch ( Exception ex )
		{
			j.Transcript.Add( new { step = j.Index, kind = "capture", ok = false, label = s.CaptureLabel, error = ex.Message } );
		}
	}

	// ── Read resolution: "WorldPosition.x" | "<Component>.<Prop>[.sub]" ──────────
	static object ResolveRead( Job j, string read, out string err )
	{
		err = null;
		if ( string.IsNullOrEmpty( read ) ) { err = "empty read"; return null; }
		var parts = read.Split( '.' );
		object cur;
		int sub;

		var head = parts[0];
		if ( head == "Displacement" )
		{
			if ( j.Anchor == null ) { err = "no player object resolved"; return null; }
			return (object) ( j.Anchor.WorldPosition - j.StartPos ).Length;   // scalar — facing-independent movement proof
		}
		if ( head == "WorldPosition" || head == "LocalPosition" || head == "WorldRotation" || head == "WorldScale" )
		{
			if ( j.Anchor == null ) { err = "no player object resolved"; return null; }
			cur = head switch
			{
				"WorldPosition" => (object) j.Anchor.WorldPosition,
				"LocalPosition" => j.Anchor.LocalPosition,
				"WorldRotation" => j.Anchor.WorldRotation.Angles(),
				"WorldScale"    => j.Anchor.WorldScale,
				_ => null,
			};
			sub = 1;
		}
		else
		{
			if ( parts.Length < 2 ) { err = $"read '{read}' needs <Component>.<Property>"; return null; }
			var comp = FindComponent( j, head );
			if ( comp == null ) { err = $"component '{head}' not found on player"; return null; }
			var td = Game.TypeLibrary.GetType( comp.GetType() );
			var pd = td?.Properties.FirstOrDefault( pp => pp.Name == parts[1] );
			if ( pd == null ) { err = $"property '{head}.{parts[1]}' not found"; return null; }
			cur = pd.GetValue( comp );
			sub = 2;
		}

		for ( int i = sub; i < parts.Length && cur != null; i++ )
			cur = SubAccess( cur, parts[i] );

		return cur;
	}

	static object SubAccess( object v, string sub )
	{
		if ( v is Vector3 v3 ) return sub switch { "x" => v3.x, "y" => v3.y, "z" => v3.z, _ => null };
		if ( v is Vector2 v2 ) return sub switch { "x" => v2.x, "y" => v2.y, _ => null };
		if ( v is Angles an ) return sub switch { "pitch" => an.pitch, "yaw" => an.yaw, "roll" => an.roll, _ => null };
		if ( sub == "Count" )
		{
			if ( v is ICollection col ) return col.Count;
			if ( v is IEnumerable en ) return en.Cast<object>().Count();
		}
		// generic property fallback
		try { return v.GetType().GetProperty( sub )?.GetValue( v ); } catch { return null; }
	}

	static bool Compare( Job j, string readKey, object actual, string op, string expected, out string err )
	{
		err = null;
		if ( op == "changed" )
			return j.Baselines.TryGetValue( readKey, out var b ) ? ValueToString( actual ) != b : true;

		// numeric comparison when both sides are numbers
		if ( TryNum( actual, out var an ) && float.TryParse( expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var en ) )
		{
			return op switch
			{
				">"  => an > en, "<" => an < en, ">=" => an >= en, "<=" => an <= en,
				"==" => System.Math.Abs( an - en ) < 0.0001f, "!=" => System.Math.Abs( an - en ) >= 0.0001f,
				_ => SetErr( out err, $"bad numeric op '{op}'" ),
			};
		}

		// bool / string equality
		var astr = ValueToString( actual );
		return op switch
		{
			"==" => string.Equals( astr, expected, StringComparison.OrdinalIgnoreCase ),
			"!=" => !string.Equals( astr, expected, StringComparison.OrdinalIgnoreCase ),
			_ => SetErr( out err, $"op '{op}' needs numeric operands (got '{astr}' vs '{expected}')" ),
		};
	}

	static bool SetErr( out string err, string msg ) { err = msg; return false; }

	static bool TryNum( object v, out float f )
	{
		f = 0f;
		switch ( v )
		{
			case float ff: f = ff; return true;
			case double dd: f = (float) dd; return true;
			case int ii: f = ii; return true;
			case long ll: f = ll; return true;
			case short ss: f = ss; return true;
			case byte bb: f = bb; return true;
			default: return false;
		}
	}

	static string ValueToString( object v )
	{
		if ( v == null ) return "null";
		if ( v is bool b ) return b ? "True" : "False";
		if ( v is Vector3 v3 ) return $"{v3.x},{v3.y},{v3.z}";
		if ( v is float f ) return f.ToString( CultureInfo.InvariantCulture );
		return v.ToString();
	}

	// ── Setup / teardown ────────────────────────────────────────────────────────
	static void ResolveAnchor( Job j )
	{
		var scene = Game.ActiveScene;
		if ( scene == null ) return;
		Component c = null;

		if ( j.TargetId != Guid.Empty )
		{
			var go = ClaudeBridge.ResolveGameObject( scene, j.TargetId.ToString() );
			if ( go != null ) c = FindControllerOn( go, j.ComponentType );
		}
		if ( c == null )
		{
			foreach ( var obj in scene.GetAllObjects( true ) )
			{
				c = FindControllerOn( obj, j.ComponentType );
				if ( c != null ) break;
			}
		}
		j.Controller = c;
		j.Anchor = c?.GameObject;
	}

	static void CaptureBaselines( Job j )
	{
		// Anchor position at job start — the origin for the "Displacement" read.
		if ( j.Anchor != null ) j.StartPos = j.Anchor.WorldPosition;
		// Record the initial value of every "changed" read so we can diff later.
		foreach ( var s in j.Steps.Where( x => x.Kind == "assert" && x.AssertOp == "changed" ) )
		{
			var v = ResolveRead( j, s.AssertRead, out var e );
			if ( e == null ) j.Baselines[s.AssertRead] = ValueToString( v );
		}
	}

	static void EnsureInputDisabled( Job j )
	{
		if ( j.DisabledInput || j.Controller == null ) return;
		var td = Game.TypeLibrary.GetType( j.Controller.GetType() );
		var pd = td?.Properties.FirstOrDefault( pp => pp.Name == "UseInputControls" );
		if ( pd != null && pd.PropertyType == typeof( bool ) )
		{
			pd.SetValue( j.Controller, false );
			j.DisabledInput = true;
		}
	}

	static void Teardown( Job j )
	{
		try
		{
			if ( !string.IsNullOrEmpty( j.HeldAction ) )
				try { Sandbox.Input.SetAction( j.HeldAction, false ); } catch { }

			if ( j.Controller != null && j.Controller.IsValid() )
			{
				var td = Game.TypeLibrary.GetType( j.Controller.GetType() );
				TrySetVector3( j.Controller, td, "WishVelocity", Vector3.Zero );
				if ( j.DisabledInput )
				{
					var pd = td?.Properties.FirstOrDefault( pp => pp.Name == "UseInputControls" );
					pd?.SetValue( j.Controller, true );
				}
			}
		}
		catch { }
	}

	static object Summarize( Job j, string reason )
	{
		return new
		{
			finished = true,
			reason,
			verdict = j.Failed == 0 ? "PASS" : "FAIL",
			passed = j.Passed,
			failed = j.Failed,
			stepsRun = j.Index,
			totalSteps = j.Steps.Count,
			controller = j.Controller?.GetType().Name,
			controllerResolved = j.Controller != null,
			transcript = j.Transcript,
		};
	}

	// ── Reflection helpers (self-contained; mirror PlayInputDriver's idiom) ──────
	internal static Component FindControllerOn( GameObject go, string componentType )
	{
		if ( go == null ) return null;
		var all = go.Components.GetAll().ToList();
		if ( !string.IsNullOrEmpty( componentType ) )
			return all.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );
		var exact = all.FirstOrDefault( c => c.GetType().Name == "PlayerController" );
		if ( exact != null ) return exact;
		return all.FirstOrDefault( c =>
		{
			var n = c.GetType().Name;
			if ( !n.EndsWith( "Controller", StringComparison.OrdinalIgnoreCase ) ) return false;
			var td = Game.TypeLibrary.GetType( c.GetType() );
			return td != null && td.Properties.Any( pp => pp.Name == "EyeAngles" || pp.Name == "WishVelocity" );
		} );
	}

	static Component FindComponent( Job j, string typeName )
	{
		if ( j.Anchor == null || string.IsNullOrEmpty( typeName ) ) return null;
		return j.Anchor.Components.GetAll().FirstOrDefault( c => c.GetType().Name.Equals( typeName, StringComparison.OrdinalIgnoreCase ) );
	}

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

	static object CoerceTo( Type t, string raw )
	{
		if ( t == typeof( bool ) ) return raw == "true" || raw == "True" || raw == "1";
		if ( t == typeof( float ) ) return float.Parse( raw, NumberStyles.Float, CultureInfo.InvariantCulture );
		if ( t == typeof( int ) ) return (int) float.Parse( raw, NumberStyles.Float, CultureInfo.InvariantCulture );
		if ( t == typeof( Vector3 ) ) return ClaudeBridge.ParseVector3Flexible( ParseElement( raw ) );
		return raw;
	}

	static JsonElement ParseElement( string raw )
	{
		// Wrap a bare "x,y,z" or scalar as a JSON string element for ParseVector3Flexible.
		using var doc = JsonDocument.Parse( JsonSerializer.Serialize( raw ) );
		return doc.RootElement.Clone();
	}
}

/// <summary>
/// playtest — run a scripted gameplay-verification sequence in play mode (async, in the
/// editor frame loop) and record a pass/fail transcript. Requires start_play first.
/// </summary>
public class PlaytestHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "playtest requires play mode — call start_play first" } );
		if ( PlaytestRunner.IsActive() )
			return Task.FromResult<object>( new { error = "a playtest is already running — poll playtest_status until it finishes" } );
		if ( !p.TryGetProperty( "steps", out var stepsEl ) || stepsEl.ValueKind != JsonValueKind.Array )
			return Task.FromResult<object>( new { error = "steps (an array of step objects) is required" } );

		try
		{
			var job = new PlaytestRunner.Job { Steps = new List<PlaytestRunner.StepSpec>() };

			if ( p.TryGetProperty( "id", out var idEl ) && idEl.ValueKind == JsonValueKind.String
				 && Guid.TryParse( idEl.GetString(), out var gid ) )
				job.TargetId = gid;
			if ( p.TryGetProperty( "component", out var compEl ) && compEl.ValueKind == JsonValueKind.String )
				job.ComponentType = compEl.GetString();

			int idx = 0;
			foreach ( var stepEl in stepsEl.EnumerateArray() )
			{
				var spec = ParseStep( stepEl, idx, out var perr );
				if ( spec == null )
					return Task.FromResult<object>( new { error = $"step {idx}: {perr}" } );
				job.Steps.Add( spec );
				idx++;
			}
			if ( job.Steps.Count == 0 )
				return Task.FromResult<object>( new { error = "steps is empty" } );

			PlaytestRunner.Start( job );
			return Task.FromResult<object>( new
			{
				started = true,
				steps = job.Steps.Count,
				note = "Playtest running ASYNC in the editor frame loop. Poll playtest_status until finished:true, then read the transcript (pass/fail per step).",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"playtest failed: {ex.Message}" } );
		}
	}

	static PlaytestRunner.StepSpec ParseStep( JsonElement e, int idx, out string err )
	{
		err = null;
		if ( e.ValueKind != JsonValueKind.Object ) { err = "not an object"; return null; }
		var s = new PlaytestRunner.StepSpec();
		int? framesOverride = ( e.TryGetProperty( "frames", out var fEl ) && fEl.TryGetInt32( out var fi ) )
			? System.Math.Clamp( fi, 1, 1800 ) : (int?) null;
		if ( e.TryGetProperty( "moveSpeed", out var msEl ) && msEl.TryGetSingle( out var ms ) ) s.MoveSpeed = ms;

		if ( e.TryGetProperty( "move", out var mEl ) )
		{
			s.Kind = "move"; s.Move = ParseMove( mEl ); s.Frames = framesOverride ?? 30;
		}
		else if ( e.TryGetProperty( "look", out var lEl ) )
		{
			s.Kind = "look"; s.Look = ParseAngles( lEl ); s.HasLook = true; s.Frames = framesOverride ?? 1;
		}
		else if ( e.TryGetProperty( "lookDelta", out var ldEl ) )
		{
			s.Kind = "lookDelta"; s.LookDelta = ParseAngles( ldEl ); s.Frames = framesOverride ?? 30;
		}
		else if ( e.TryGetProperty( "action", out var aEl ) && aEl.ValueKind == JsonValueKind.String )
		{
			s.Kind = "action"; s.Action = aEl.GetString(); s.Frames = framesOverride ?? 20;
		}
		else if ( e.TryGetProperty( "jump", out var jEl ) )
		{
			s.Kind = "jump"; s.JumpVel = ClaudeBridge.ParseVector3Flexible( jEl ); s.Frames = 1;
		}
		else if ( e.TryGetProperty( "set", out var setEl ) && setEl.ValueKind == JsonValueKind.Object )
		{
			s.Kind = "set"; s.Frames = 1;
			s.SetComponent = GetStr( setEl, "component" );
			s.SetProperty = GetStr( setEl, "property" );
			s.SetValue = GetStr( setEl, "to" ) ?? GetStr( setEl, "value" );
			if ( s.SetComponent == null || s.SetProperty == null ) { err = "set needs {component, property, to}"; return null; }
		}
		else if ( e.TryGetProperty( "wait", out var wEl ) && wEl.TryGetInt32( out var wf ) )
		{
			s.Kind = "wait"; s.Frames = System.Math.Clamp( wf, 1, 1800 );
		}
		else if ( e.TryGetProperty( "capture", out var capEl ) )
		{
			s.Kind = "capture"; s.Frames = 1;
			s.CaptureLabel = capEl.ValueKind == JsonValueKind.String ? capEl.GetString() : null;
		}
		else if ( e.TryGetProperty( "assert", out var asEl ) && asEl.ValueKind == JsonValueKind.Object )
		{
			s.Kind = "assert"; s.Frames = 1;
			s.AssertRead = GetStr( asEl, "read" );
			s.AssertOp = GetStr( asEl, "op" ) ?? "==";
			s.AssertDesc = GetStr( asEl, "desc" );
			if ( asEl.TryGetProperty( "value", out var vEl ) )
				s.AssertValue = vEl.ValueKind == JsonValueKind.String ? vEl.GetString() : vEl.GetRawText();
			if ( s.AssertRead == null ) { err = "assert needs {read, op, value}"; return null; }
		}
		else
		{
			err = "unknown step (expected one of: move, look, lookDelta, action, jump, set, wait, capture, assert)";
			return null;
		}
		return s;
	}

	static string GetStr( JsonElement o, string key )
		=> o.TryGetProperty( key, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	static Vector2 ParseMove( JsonElement el )
	{
		float x = 0f, y = 0f;
		if ( el.ValueKind == JsonValueKind.Object )
		{
			if ( el.TryGetProperty( "x", out var xp ) && xp.TryGetSingle( out var xf ) ) x = xf;
			if ( el.TryGetProperty( "y", out var yp ) && yp.TryGetSingle( out var yf ) ) y = yf;
		}
		else if ( el.ValueKind == JsonValueKind.String )
		{
			var pr = ( el.GetString() ?? "" ).Split( ',' );
			if ( pr.Length > 0 ) float.TryParse( pr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x );
			if ( pr.Length > 1 ) float.TryParse( pr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y );
		}
		var v = new Vector2( x, y );
		if ( v.Length > 1f ) v = v.Normal;
		return v;
	}

	static Angles ParseAngles( JsonElement el )
	{
		float pitch = 0f, yaw = 0f, roll = 0f;
		if ( el.ValueKind == JsonValueKind.Object )
		{
			if ( el.TryGetProperty( "pitch", out var pp ) && pp.TryGetSingle( out var pf ) ) pitch = pf;
			if ( el.TryGetProperty( "yaw", out var yp ) && yp.TryGetSingle( out var yf ) ) yaw = yf;
			if ( el.TryGetProperty( "roll", out var rp ) && rp.TryGetSingle( out var rf ) ) roll = rf;
		}
		else if ( el.ValueKind == JsonValueKind.String )
		{
			var pr = ( el.GetString() ?? "" ).Split( ',' );
			if ( pr.Length > 0 ) float.TryParse( pr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out pitch );
			if ( pr.Length > 1 ) float.TryParse( pr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out yaw );
			if ( pr.Length > 2 ) float.TryParse( pr[2], NumberStyles.Float, CultureInfo.InvariantCulture, out roll );
		}
		return new Angles( pitch, yaw, roll );
	}
}

/// <summary>
/// playtest_status — poll the running/finished playtest: live progress while running,
/// or the full pass/fail transcript once finished.
/// </summary>
public class PlaytestStatusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var summary = PlaytestRunner.ConsumeSummary();
		if ( summary != null )
			return Task.FromResult<object>( summary );

		var live = PlaytestRunner.LiveSnapshot();
		if ( live != null )
			return Task.FromResult<object>( live );

		return Task.FromResult<object>( new { active = false, finished = false, note = "No playtest has run yet." } );
	}
}

/// <summary>
/// playtest_abort — stop the running playtest immediately, restoring input state.
/// The partial transcript stays available via playtest_status.
/// </summary>
public class PlaytestAbortHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
		=> Task.FromResult<object>( PlaytestRunner.Abort() );
}
