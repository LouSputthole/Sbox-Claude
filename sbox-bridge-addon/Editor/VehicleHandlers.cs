using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════
// Batch 54 — bridge_vehicle (v2 wave 4): the corpus vehicles theme.
//   create_vehicle_controller — make any Rigidbody prop drivable (raycast car
//     with suspension, engine, steering, grip + built-in driver seat)
//   create_seat_system       — standalone generic seat (enter/exit/safe-exit)
//   tune_vehicle             — apply arcade/drift/offroad/race presets
//   create_physics_grab_tool — physgun-style spring grab + throw
// Generated code APIs verified live: Rigidbody.ApplyForceAt/GetVelocityAtPoint/
// ApplyTorque/Velocity/Mass (describe_type, 2026-07-09). Driving FEEL needs a
// human playtest — compiles+runs ≠ fun (BRIDGE_GOTCHAS #1).
// ═══════════════════════════════════════════════════════════════════

/// <summary>create_vehicle_controller — scaffold a drivable raycast-car component.</summary>
public class CreateVehicleControllerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "VehicleController", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float engine = p.TryGetProperty( "engineForce", out var ef ) && ef.TryGetSingle( out var eff ) ? eff : 900f;
			float steer  = p.TryGetProperty( "steerStrength", out var ss ) && ss.TryGetSingle( out var ssf ) ? ssf : 2.0f;
			float grip   = p.TryGetProperty( "grip", out var g ) && g.TryGetSingle( out var gf ) ? gf : 0.85f;

			ScaffoldHelpers.WriteCode( fullPath, BuildCode( className, engine, steer, grip ) );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				nextSteps = new[]
				{
					"trigger_hotload, then check compile_status",
					$"Attach {className} + a Rigidbody + a collider to your vehicle prop (batch_add_component works)",
					"Enter play mode and press E on the vehicle to drive (WASD; E again to exit)",
					"tune_vehicle applies arcade/drift/offroad/race presets to the attached component",
					"HUMAN PLAYTEST REQUIRED for feel — tune EngineForce/SteerStrength/GripFactor from the inspector while playing"
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_vehicle_controller failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float engine, float steer, float grip )
	{
		var ci = System.Globalization.CultureInfo.InvariantCulture;
		return $@"using Sandbox;
using System;

/// <summary>
/// {className} — makes a Rigidbody prop drivable: a 4-corner raycast car with
/// spring/damper suspension, engine force, yaw steering, and lateral grip
/// (lower grip = drift). Built-in driver seat: press E (use) to enter — the
/// driver is hidden while driving (no controller transform fights), the host
/// assigns them vehicle ownership, and a chase camera follows — E to exit.
/// Requires a Rigidbody + collider on the same GameObject. Tune from the
/// inspector while playing; tune_vehicle applies ready-made presets.
/// </summary>
public sealed class {className} : Component, Component.IPressable
{{
	[Property, Group( ""Engine"" )] public float EngineForce {{ get; set; }} = {engine.ToString( ci )}f;
	[Property, Group( ""Engine"" )] public float MaxSpeed {{ get; set; }} = 800f;
	[Property, Group( ""Steering"" )] public float SteerStrength {{ get; set; }} = {steer.ToString( ci )}f;   // yaw rate, rad/s at full speed factor
	[Property, Group( ""Handling"" ), Range( 0f, 1f )] public float GripFactor {{ get; set; }} = {grip.ToString( ci )}f;
	[Property, Group( ""Suspension"" )] public float SuspensionRest {{ get; set; }} = 24f;
	[Property, Group( ""Suspension"" )] public float SuspensionStrength {{ get; set; }} = 90f;
	[Property, Group( ""Suspension"" )] public float SuspensionDamping {{ get; set; }} = 8f;
	[Property, Group( ""Seat"" )] public Vector3 ExitOffset {{ get; set; }} = new( 0, 80, 20 );
	[Property, Group( ""Camera"" )] public float CameraDistance {{ get; set; }} = 260f;
	[Property, Group( ""Camera"" )] public float CameraHeight {{ get; set; }} = 110f;

	[Sync] public Guid DriverId {{ get; set; }}

	public bool HasDriver => DriverId != Guid.Empty;
	public static event Action<GameObject, bool> OnDriverChanged; // (vehicle, entered)

	Rigidbody _rb;
	Vector3[] _corners;
	TimeSince _sinceEnter;

	protected override void OnStart()
	{{
		_rb = GetComponent<Rigidbody>();
		if ( _rb == null )
		{{
			Log.Warning( $""{className} needs a Rigidbody on {{GameObject.Name}}"" );
			Enabled = false;
			return;
		}}
		var bounds = GameObject.GetBounds();
		var ext = ( bounds.Size * 0.4f ).WithZ( 0 );
		_corners = new[]
		{{
			new Vector3(  ext.x,  ext.y, 0 ), new Vector3(  ext.x, -ext.y, 0 ),
			new Vector3( -ext.x,  ext.y, 0 ), new Vector3( -ext.x, -ext.y, 0 ),
		}};
	}}

	// ── Seat (IPressable) ────────────────────────────────────────────
	public bool Press( Component.IPressable.Event e )
	{{
		var presser = e.Source?.GameObject;
		if ( presser == null ) return false;
		RequestSeat( presser.Id );
		return true;
	}}

	[Rpc.Host]
	void RequestSeat( Guid pressGuid )
	{{
		var presser = Scene.Directory.FindByGuid( pressGuid );
		if ( presser == null ) return;
		if ( HasDriver && DriverId != pressGuid ) return;

		if ( DriverId == pressGuid )
		{{
			Exit( presser );
			return;
		}}

		// Enter: hide the player entirely while driving — parenting a live
		// PlayerController to a moving vehicle makes two systems fight over the
		// transform (the classic seat jitter). Hidden driver + chase camera instead.
		DriverId = pressGuid;
		_sinceEnter = 0;
		var owner = presser.Network.Owner;
		if ( owner != null ) GameObject.Network.AssignOwnership( owner );
		presser.Enabled = false;
		OnDriverChanged?.Invoke( GameObject, true );
	}}

	void Exit( GameObject driver )
	{{
		DriverId = Guid.Empty;
		if ( driver != null )
		{{
			driver.WorldPosition = WorldPosition + WorldRotation * ExitOffset;
			driver.Enabled = true;   // their controller re-takes the camera next frame
		}}
		GameObject.Network.DropOwnership();
		OnDriverChanged?.Invoke( GameObject, false );
	}}

	// Chase camera while driving (runs on the driver's client — they own the vehicle).
	protected override void OnPreRender()
	{{
		if ( IsProxy || !HasDriver ) return;
		var cam = Scene.Camera;
		if ( cam == null ) return;

		var targetPos = WorldPosition - WorldRotation.Forward.WithZ( 0 ).Normal * CameraDistance + Vector3.Up * CameraHeight;
		cam.WorldPosition = cam.WorldPosition.LerpTo( targetPos, MathX.Clamp( Time.Delta * 6f, 0f, 1f ) );
		cam.WorldRotation = Rotation.LookAt( ( WorldPosition + Vector3.Up * 30f - cam.WorldPosition ).Normal, Vector3.Up );
	}}

	// ── Driving (vehicle owner only) ─────────────────────────────────
	protected override void OnFixedUpdate()
	{{
		if ( _rb == null || IsProxy || !HasDriver ) return;

		// E again to exit (edge-guarded so the entering press cannot instantly exit).
		if ( _sinceEnter > 0.4f && Input.Pressed( ""use"" ) )
		{{
			RequestSeat( DriverId );
			return;
		}}

		var dt = Time.Delta;
		var input = Input.AnalogMove;   // x = forward/back, y = left/right
		int grounded = 0;

		// Suspension: 4 corner rays, spring + damper applied at each corner.
		foreach ( var corner in _corners )
		{{
			var worldCorner = WorldPosition + WorldRotation * corner;
			var tr = Scene.Trace.Ray( worldCorner, worldCorner + Vector3.Down * SuspensionRest * 2f )
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();
			if ( !tr.Hit ) continue;
			grounded++;
			var compression = 1f - ( tr.Distance / ( SuspensionRest * 2f ) );
			var pointVel = _rb.GetVelocityAtPoint( worldCorner );
			var force = Vector3.Up * ( compression * SuspensionStrength - pointVel.z * SuspensionDamping ) * _rb.Mass * dt * 50f;
			_rb.ApplyForceAt( worldCorner, force );
		}}

		if ( grounded == 0 ) return;   // airborne — no engine/steer/grip

		var forward = WorldRotation.Forward.WithZ( 0 ).Normal;
		var speed = _rb.Velocity.WithZ( 0 ).Length;

		// Engine (mass-scaled so feel survives different props).
		if ( MathF.Abs( input.x ) > 0.01f && speed < MaxSpeed )
			_rb.ApplyForce( forward * input.x * EngineForce * _rb.Mass );

		// Steering: set yaw angular velocity directly — arcade-reliable, immune to the
		// prop's moment of inertia (torque was far too weak on heavy boxes — playtested).
		var steerFactor = MathX.Clamp( speed / 150f, 0.25f, 1f );
		var direction = _rb.Velocity.Dot( forward ) < -10f ? -1f : 1f;   // reverse steers mirrored
		var yawRate = MathF.Abs( input.y ) > 0.01f
			? input.y * SteerStrength * steerFactor * direction
			: 0f;
		_rb.AngularVelocity = _rb.AngularVelocity.WithZ( MathX.Lerp( _rb.AngularVelocity.z, yawRate, MathX.Clamp( dt * 12f, 0f, 1f ) ) );

		// Lateral grip: kill a fraction of sideways velocity each tick. Low grip = drift.
		var right = WorldRotation.Right.WithZ( 0 ).Normal;
		var lateral = right * _rb.Velocity.Dot( right );
		_rb.Velocity -= lateral * GripFactor * MathX.Clamp( dt * 10f, 0f, 1f );
	}}
}}
";
	}
}

/// <summary>create_seat_system — scaffold a standalone enter/exit seat component.</summary>
public class CreateSeatSystemHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "Seat", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			ScaffoldHelpers.WriteCode( fullPath, BuildCode( className ) );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				nextSteps = new[]
				{
					"trigger_hotload, then check compile_status",
					$"Attach {className} to any prop (chair, bench, turret mount) — press E to sit, E to stand",
					"SeatOffset positions the occupant; exit tries ExitOffsets in order and takes the first clear spot",
					$"Subscribe to {className}.OnOccupantChanged for camera/UI logic"
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_seat_system failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className )
	{
		return $@"using Sandbox;
using System;
using System.Linq;

/// <summary>
/// {className} — a networked one-occupant seat: press E (use) to sit, E again
/// to stand. Claims route through the host so two players can't share a seat;
/// the occupant is parented to the seat with their controller input disabled
/// (UseInputControls=false, restored on exit). Exit tries each ExitOffsets
/// entry and takes the first spot with clearance. Works for chairs, benches,
/// turret mounts — anything sittable.
/// </summary>
public sealed class {className} : Component, Component.IPressable
{{
	[Property] public Vector3 SeatOffset {{ get; set; }} = new( 0, 0, 10 );
	[Property] public System.Collections.Generic.List<Vector3> ExitOffsets {{ get; set; }} = new()
		{{ new( 0, 60, 10 ), new( 0, -60, 10 ), new( 60, 0, 10 ), new( -60, 0, 10 ) }};

	[Sync] public Guid OccupantId {{ get; set; }}

	public bool IsOccupied => OccupantId != Guid.Empty;
	public static event Action<GameObject, GameObject, bool> OnOccupantChanged; // (seat, occupant, seated)

	public bool Press( Component.IPressable.Event e )
	{{
		var presser = e.Source?.GameObject;
		if ( presser == null ) return false;
		RequestSeat( presser.Id );
		return true;
	}}

	[Rpc.Host]
	void RequestSeat( Guid pressGuid )
	{{
		var presser = Scene.Directory.FindByGuid( pressGuid );
		if ( presser == null ) return;

		if ( OccupantId == pressGuid )
		{{
			SetControls( presser, true );
			presser.SetParent( null, true );
			presser.WorldPosition = FindExitSpot( presser );
			OccupantId = Guid.Empty;
			OnOccupantChanged?.Invoke( GameObject, presser, false );
			return;
		}}
		if ( IsOccupied ) return;

		OccupantId = pressGuid;
		presser.SetParent( GameObject, true );
		presser.LocalPosition = SeatOffset;
		SetControls( presser, false );
		OnOccupantChanged?.Invoke( GameObject, presser, true );
	}}

	Vector3 FindExitSpot( GameObject occupant )
	{{
		foreach ( var offset in ExitOffsets )
		{{
			var spot = WorldPosition + WorldRotation * offset;
			var tr = Scene.Trace.Ray( spot + Vector3.Up * 32f, spot )
				.IgnoreGameObjectHierarchy( GameObject )
				.IgnoreGameObjectHierarchy( occupant )
				.Run();
			if ( !tr.Hit ) return spot;
		}}
		return WorldPosition + Vector3.Up * 48f;   // all blocked — pop up top
	}}

	static void SetControls( GameObject occupant, bool enabled )
	{{
		foreach ( var comp in occupant.Components.GetAll() )
		{{
			if ( comp is null ) continue;
			var type = Game.TypeLibrary?.GetType( comp.GetType() );
			var prop = type?.Properties?.FirstOrDefault( pr => pr.Name == ""UseInputControls"" );
			prop?.SetValue( comp, enabled );
		}}
	}}
}}
";
	}
}

/// <summary>tune_vehicle — apply a handling preset to a vehicle controller component.</summary>
public class TuneVehicleHandler : IBridgeHandler
{
	static readonly Dictionary<string, Dictionary<string, float>> Presets = new( StringComparer.OrdinalIgnoreCase )
	{
		["arcade"]  = new() { ["EngineForce"] = 900f,  ["MaxSpeed"] = 800f,  ["SteerStrength"] = 2.0f, ["GripFactor"] = 0.85f, ["SuspensionStrength"] = 90f,  ["SuspensionDamping"] = 8f },
		["drift"]   = new() { ["EngineForce"] = 1100f, ["MaxSpeed"] = 900f,  ["SteerStrength"] = 2.8f, ["GripFactor"] = 0.35f, ["SuspensionStrength"] = 80f,  ["SuspensionDamping"] = 6f },
		["offroad"] = new() { ["EngineForce"] = 750f,  ["MaxSpeed"] = 600f,  ["SteerStrength"] = 1.5f, ["GripFactor"] = 0.7f,  ["SuspensionStrength"] = 130f, ["SuspensionDamping"] = 12f },
		["race"]    = new() { ["EngineForce"] = 1400f, ["MaxSpeed"] = 1400f, ["SteerStrength"] = 1.7f, ["GripFactor"] = 0.95f, ["SuspensionStrength"] = 110f, ["SuspensionDamping"] = 10f },
	};

	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.TryGetProperty( "id", out var idEl ) ? idEl.GetString() : null;
		var go = ClaudeBridge.ResolveGameObject( scene, id );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var presetName = p.TryGetProperty( "preset", out var pr ) ? pr.GetString() : null;
		if ( presetName == null || !Presets.TryGetValue( presetName, out var preset ) )
			return Task.FromResult<object>( new { error = $"preset must be one of: {string.Join( " | ", Presets.Keys )}" } );

		var compName = p.TryGetProperty( "component", out var cn ) ? cn.GetString() : null;
		var component = go.Components.GetAll().FirstOrDefault( c => c != null &&
			( compName != null
				? c.GetType().Name.Equals( compName, StringComparison.OrdinalIgnoreCase )
				: c.GetType().Name.Contains( "Vehicle", StringComparison.OrdinalIgnoreCase ) ) );
		if ( component == null )
			return Task.FromResult<object>( new { error = compName != null
				? $"No '{compName}' component on the object"
				: "No component with 'Vehicle' in its type name found — pass component explicitly" } );

		var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
		var applied = new List<object>();
		var missing = new List<string>();
		foreach ( var (propName, value) in preset )
		{
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name == propName );
			if ( propDesc == null ) { missing.Add( propName ); continue; }
			try
			{
				propDesc.SetValue( component, value );
				applied.Add( new { property = propName, value } );
			}
			catch ( Exception ex ) { missing.Add( $"{propName} ({ex.Message})" ); }
		}

		return Task.FromResult<object>( new
		{
			tuned = applied.Count > 0,
			preset = presetName.ToLowerInvariant(),
			component = component.GetType().Name,
			applied,
			missing,
			note = missing.Count > 0
				? "Some preset properties don't exist on this component — presets target create_vehicle_controller scaffolds; others tune partially."
				: "Preset applied. Enter play mode and drive to feel it; fine-tune the same properties with set_property."
		} );
	}
}

/// <summary>create_physics_grab_tool — scaffold a physgun-style spring grab + throw.</summary>
public class CreatePhysicsGrabToolHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "PhysicsGrabTool", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			ScaffoldHelpers.WriteCode( fullPath, BuildCode( className ) );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				nextSteps = new[]
				{
					"trigger_hotload, then check compile_status",
					$"Attach {className} to the player object (needs a camera child or PlayerController for aim)",
					"Hold attack2 (right mouse) on a Rigidbody prop to grab; scroll-free: it follows at grab distance; attack1 throws",
					"ensure_input_action if your project lacks attack1/attack2 bindings"
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_physics_grab_tool failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className )
	{
		return $@"using Sandbox;
using System;
using System.Linq;

/// <summary>
/// {className} — a physgun-lite for the player: hold GrabAction (default
/// attack2) while looking at a Rigidbody prop to grab it; it spring-follows a
/// point in front of your view (physics stays LIVE — it collides and swings,
/// unlike a parented carry); press ThrowAction (default attack1) to launch it.
/// Grab requests route through the host, which assigns the grabber network
/// ownership of the prop. Owner-only logic; attach to the player object.
/// </summary>
public sealed class {className} : Component
{{
	[Property] public float Range {{ get; set; }} = 300f;
	[Property] public float SpringStrength {{ get; set; }} = 12f;
	[Property] public float ThrowForce {{ get; set; }} = 600f;
	[Property] public float MaxMass {{ get; set; }} = 2000f;
	[Property] public string GrabAction {{ get; set; }} = ""attack2"";
	[Property] public string ThrowAction {{ get; set; }} = ""attack1"";

	GameObject _held;
	float _holdDistance;

	public bool IsHolding => _held.IsValid();
	public static event Action<GameObject, GameObject, bool> OnGrabChanged; // (player, prop, grabbed)

	protected override void OnFixedUpdate()
	{{
		if ( IsProxy ) return;

		var eye = GetEye( out var dir );

		if ( IsHolding && Input.Pressed( ThrowAction ) )
		{{
			var rb = _held.GetComponent<Rigidbody>();
			rb?.ApplyImpulse( dir * ThrowForce * ( rb.Mass ) );
			Release();
			return;
		}}

		if ( Input.Down( GrabAction ) )
		{{
			if ( !IsHolding ) TryGrab( eye, dir );
			else Hold( eye, dir );
		}}
		else if ( IsHolding )
		{{
			Release();
		}}
	}}

	Vector3 GetEye( out Vector3 dir )
	{{
		var cam = Scene.Camera;
		if ( cam != null )
		{{
			dir = cam.WorldRotation.Forward;
			return cam.WorldPosition;
		}}
		dir = WorldRotation.Forward;
		return WorldPosition + Vector3.Up * 64f;
	}}

	void TryGrab( Vector3 eye, Vector3 dir )
	{{
		var tr = Scene.Trace.Ray( eye, eye + dir * Range )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		if ( !tr.Hit || tr.GameObject == null ) return;

		var rb = tr.GameObject.GetComponent<Rigidbody>();
		if ( rb == null || rb.Mass > MaxMass ) return;

		_held = tr.GameObject;
		_holdDistance = MathX.Clamp( tr.Distance, 60f, Range );
		RequestGrabOwnership( _held.Id );
		OnGrabChanged?.Invoke( GameObject, _held, true );
	}}

	void Hold( Vector3 eye, Vector3 dir )
	{{
		if ( !_held.IsValid() ) {{ _held = null; return; }}
		var rb = _held.GetComponent<Rigidbody>();
		if ( rb == null ) {{ Release(); return; }}

		var target = eye + dir * _holdDistance;
		// Velocity-set spring: stiff, stable, still collides with the world.
		rb.Velocity = ( target - _held.WorldPosition ) * SpringStrength;
		rb.AngularVelocity = rb.AngularVelocity.LerpTo( Vector3.Zero, Time.Delta * 5f );
	}}

	void Release()
	{{
		if ( _held.IsValid() )
			OnGrabChanged?.Invoke( GameObject, _held, false );
		_held = null;
	}}

	[Rpc.Host]
	void RequestGrabOwnership( Guid propId )
	{{
		var prop = Scene.Directory.FindByGuid( propId );
		var caller = Rpc.Caller;
		if ( prop == null || caller is null ) return;
		prop.Network.AssignOwnership( caller );
	}}
}}
";
	}
}
