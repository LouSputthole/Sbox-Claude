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
//  Interaction pack + Carry (v1.20.0) -- three interaction scaffolds
//  (code-gen; scene-mutating):
//
//    add_interaction_prompt   eye-traced "Press E" HUD bound to IPressable targets
//                             (a .razor + .razor.scss pair, like create_leaderboard_panel)
//    create_hold_to_confirm   hold-to-fill progress action + static OnConfirmed
//    create_carry_system      pickup / carry / throw with host-routed RPCs + ownership
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, SerializeGo) and ScaffoldHelpers (PrepareCodeFile /
//  WriteCode / Utf8NoBom). Handler code here is UNSANDBOXED editor code.
//
//  The C# / Razor *strings these handlers WRITE TO DISK* are SANDBOXED game code
//  and must obey the s&box sandbox rules:
//    - MathX preferred; System.Math/MathF also compile on the current SDK.
//      Array.Clone() is still whitelist-blocked (not used here).
//    - only reflection-verified APIs (checked live against this SDK before codegen):
//        * Scene.Trace.Ray(from,to).WithTag(t).IgnoreGameObjectHierarchy(go).Run()
//          -> SceneTraceResult { bool Hit; GameObject GameObject; float Distance }
//          (compile-proven in NpcBrainHandlers' generated LOS code).
//        * GameObject.Components.GetAll() -> IEnumerable<Component>, filtered with
//          `is Component.IPressable` (avoids any generic-constraint question on an
//          interface T); Components.Get<Rigidbody>() proven in scene_validate.
//        * Component.IPressable surface: CanPress/Press/Hover/Blur + GetTooltip(Event)
//          (Event is a struct { Component Source; Ray? Ray }). GetTooltip returns a
//          Nullable<T>; the prompt reads it defensively (HasValue + ToString), else
//          falls back to DefaultPrompt.
//        * Rigidbody: MotionEnabled (bool), Velocity (Vector3), ApplyImpulse(Vector3).
//        * GameObject.Network.AssignOwnership(Connection) / TakeOwnership() and
//          Rpc.Caller (Connection) -- guarded by Networking.IsActive (single-player
//          safe: IsProxy is false and RPCs run locally with no session).
//    - networking: host is the single writer of [Sync(SyncFlags.FromHost)] state;
//      clients request changes through an [Rpc.Host] that re-validates the caller.
//      Input handling is guarded with IsProxy so only the owner reads input.
//
//  Register(...) lines + the _sceneMutatingCommands additions live in
//  MyEditorMenu.cs (Batch 45) to keep the files decoupled.
// =============================================================================

// -----------------------------------------------------------------------------
// add_interaction_prompt -- generate a PanelComponent HUD (.razor + .razor.scss)
// that eye-traces from the scene camera every frame and shows a centered prompt
// when the crosshair is on a component implementing Component.IPressable within
// Range. Pairs with create_interactable / add_interaction_station.
//
// Razor is razor_lint-safe by construction: PanelComponent + BuildHash override,
// no switch-expressions and no non-ASCII in @code, and the SCSS root selector is
// a class (.interaction-prompt), not a bare type selector.
// -----------------------------------------------------------------------------
public class AddInteractionPromptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			var name      = p.TryGetProperty( "name",      out var n ) && !string.IsNullOrWhiteSpace( n.GetString() ) ? n.GetString() : "InteractionPrompt";
			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code/UI";
			var action    = p.TryGetProperty( "action",    out var a ) && !string.IsNullOrWhiteSpace( a.GetString() ) ? a.GetString() : "use";
			float range   = p.TryGetProperty( "range",     out var rv ) && rv.TryGetSingle( out var rf ) ? rf : 120f;

			if ( range < 1f ) range = 1f;
			action = action.Replace( "\"", "" ).Trim();
			if ( action.Length == 0 ) action = "use";

			var className = ClaudeBridge.SanitizeIdentifier( name.EndsWith( ".razor" ) ? Path.GetFileNameWithoutExtension( name ) : name );
			var razorFile = className + ".razor";
			var scssFile  = className + ".razor.scss";

			if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, razorFile ), out var razorPath, out var razorErr ) )
				return Task.FromResult<object>( new { error = razorErr } );
			if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, scssFile ), out var scssPath, out var scssErr ) )
				return Task.FromResult<object>( new { error = scssErr } );

			if ( File.Exists( razorPath ) )
				return Task.FromResult<object>( new { error = $"File already exists: {directory}/{razorFile}. Choose a different name." } );

			Directory.CreateDirectory( Path.GetDirectoryName( razorPath ) );

			string rangeStr      = range.ToString( ci ) + "f";
			string defaultPrompt = $"Press E to {action}";

			string razor = BuildRazor( className, rangeStr, defaultPrompt );
			string scss  = BuildScss();

			ScaffoldHelpers.WriteCode( razorPath, razor );
			ScaffoldHelpers.WriteCode( scssPath, scss );

			var rootPath = Project.Current?.GetRootPath() ?? "";
			var relRazor = Path.GetRelativePath( rootPath, razorPath ).Replace( '\\', '/' );
			var relScss  = Path.GetRelativePath( rootPath, scssPath ).Replace( '\\', '/' );

			return Task.FromResult<object>( new
			{
				created = true,
				razorPath = relRazor,
				scssPath = relScss,
				className,
				action,
				range,
				defaultPrompt,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					$"Host it on a screen UI: add_screen_panel, then add_component_with_properties (component=\"{className}\") to that panel object -- a PanelComponent only renders under a ScreenPanel.",
					$"It eye-traces from Scene.Camera every frame; when the crosshair is on a component implementing Component.IPressable within Range ({range}), it shows the centered prompt. Pairs directly with create_interactable / add_interaction_station.",
					$"Prompt text: it tries the target's IPressable.GetTooltip() (most interactables don't override it), else shows DefaultPrompt (\"{defaultPrompt}\"). Tune Range / DefaultPrompt with set_property.",
					"Verify in play mode: start_play, look at an interactable, and capture_view -- the pill appears; look away and it clears."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_interaction_prompt failed: {ex.Message}" } );
		}
	}

	static string BuildRazor( string className, string rangeStr, string defaultPrompt )
	{
		// NOTE: all {{ }} inside the $@"" are C# string-escape doubled braces.
		// The generated .razor uses single { } for Razor/C# expressions.
		return $@"@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent;

@* {className} -- eye-traced interaction prompt. Host it under a ScreenPanel.
   Each frame it traces from the scene camera; when it looks at a component
   implementing Component.IPressable within Range it shows a centered prompt.
   Pairs with create_interactable / add_interaction_station. *@

@if ( _visible )
{{
	<div class=""interaction-prompt"">
		<div class=""prompt-pill"">@_promptText</div>
	</div>
}}

@code {{
	[Property] public float Range {{ get; set; }} = {rangeStr};
	[Property] public string DefaultPrompt {{ get; set; }} = ""{defaultPrompt}"";

	private bool _visible;
	private string _promptText = """";

	protected override void OnUpdate()
	{{
		var cam = Scene?.Camera;
		if ( !cam.IsValid() )
		{{
			SetState( false, """" );
			return;
		}}

		var from = cam.WorldPosition;
		var to = from + cam.WorldRotation.Forward * Range;
		var tr = Scene.Trace.Ray( from, to ).Run();

		if ( tr.Hit && tr.GameObject.IsValid() )
		{{
			var pressable = FindPressable( tr.GameObject );
			if ( pressable != null )
			{{
				SetState( true, ResolvePromptText( pressable ) );
				return;
			}}
		}}

		SetState( false, """" );
	}}

	// Iterate components and match the interface with `is` -- no generic-constraint
	// question about calling Components.Get with an interface type argument.
	private Component.IPressable FindPressable( GameObject go )
	{{
		foreach ( var c in go.Components.GetAll() )
		{{
			if ( c is Component.IPressable p )
				return p;
		}}
		return null;
	}}

	// IPressable can expose a per-object description via GetTooltip. Most
	// interactables do not override it (the default returns null), so we fall back
	// to DefaultPrompt. Guarded so a custom GetTooltip can never crash the HUD.
	private string ResolvePromptText( Component.IPressable pressable )
	{{
		try
		{{
			var tip = pressable.GetTooltip( new Component.IPressable.Event {{ Source = this }} );
			if ( tip.HasValue )
			{{
				var text = tip.Value.ToString();
				if ( !string.IsNullOrWhiteSpace( text ) )
					return text;
			}}
		}}
		catch ( System.Exception )
		{{
		}}
		return DefaultPrompt;
	}}

	private void SetState( bool visible, string text )
	{{
		if ( _visible == visible && _promptText == text ) return;
		_visible = visible;
		_promptText = text;
		StateHasChanged();
	}}

	protected override int BuildHash() => System.HashCode.Combine( _visible, _promptText );
}}
";
	}

	static string BuildScss()
	{
		return @".interaction-prompt {
	position: absolute;
	top: 0;
	left: 0;
	width: 100%;
	height: 100%;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	pointer-events: none;
}

.prompt-pill {
	padding: 10px 22px;
	background-color: rgba(0, 0, 0, 0.6);
	border: 1px solid rgba(255, 255, 255, 0.15);
	border-radius: 8px;
	color: white;
	font-size: 24px;
	font-weight: 500;
	text-align: center;
}
";
	}
}

// -----------------------------------------------------------------------------
// create_hold_to_confirm -- a sealed Component: while a named input action is
// held, Progress fills 0->1 over HoldSeconds; releasing early resets (or decays
// if DecayOnRelease); reaching 1 fires the static OnConfirmed(GameObject) event
// then cools down. LOCAL/owner-only (IsProxy-guarded) -- no UI generated; read
// Progress from your own HUD.
// -----------------------------------------------------------------------------
public class CreateHoldToConfirmHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "HoldToConfirm", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var action = p.TryGetProperty( "action", out var a ) && !string.IsNullOrWhiteSpace( a.GetString() ) ? a.GetString() : "use";
			float hold = p.TryGetProperty( "holdSeconds", out var hv ) && hv.TryGetSingle( out var hf ) ? hf : 1.5f;
			bool decay = p.TryGetProperty( "decayOnRelease", out var dv ) && dv.ValueKind == JsonValueKind.True;

			action = action.Replace( "\"", "" ).Trim();
			if ( action.Length == 0 ) action = "use";
			if ( hold < 0.05f ) hold = 0.05f;

			var code = BuildCode( className, action, hold, decay, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = InteractionPackHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				action,
				holdSeconds = hold,
				decayOnRelease = decay,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach it to the player (or any owned object that reads input): add_component_with_properties (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Hold the '{action}' action for {hold}s to confirm. Subscribe to the result: {className}.OnConfirmed += go => {{ /* your effect */ }}; -- filter to your LOCAL player if it can fire on several.",
					"Draw the fill from your own HUD: read the public Progress (0..1) each frame (a radial/bar). This component owns the timing, not the UI.",
					"LOCAL/owner-only (IsProxy-guarded). For a host-authoritative outcome, call an [Rpc.Host] from inside the OnConfirmed subscriber. Tune HoldSeconds / DecayOnRelease / CooldownSeconds with set_property."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_hold_to_confirm failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string action, float hold, bool decay, System.Globalization.CultureInfo ci )
	{
		string holdStr  = hold.ToString( ci ) + "f";
		string decayStr = decay ? "true" : "false";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- a hold-to-confirm action. While the '{action}' input action is
/// held, Progress fills 0..1 over HoldSeconds; releasing early resets it (or
/// decays it back down if DecayOnRelease). Reaching 1 fires the static
/// OnConfirmed(GameObject) event, then a short cooldown blocks re-triggering.
///
/// LOCAL / owner-only: input is read only on the machine that owns this object
/// (IsProxy-guarded), so it is single-player safe and never fires on proxies.
/// No UI is generated -- read the public Progress from your own HUD to draw a
/// radial/bar. For a host-authoritative outcome, call an [Rpc.Host] from inside
/// the OnConfirmed subscriber.
///
/// Usage:
///   {className}.OnConfirmed += go =&gt; {{ /* run the confirmed action */ }};
/// </summary>
public sealed class {className} : Component
{{
	/// <summary>Input action name that must be held (see the project's Input settings).</summary>
	[Property] public string Action {{ get; set; }} = ""{action}"";

	/// <summary>Seconds of continuous hold required to confirm.</summary>
	[Property] public float HoldSeconds {{ get; set; }} = {holdStr};

	/// <summary>If true, releasing early drains Progress back down instead of snapping to 0.</summary>
	[Property] public bool DecayOnRelease {{ get; set; }} = {decayStr};

	/// <summary>Cooldown after a confirm before another hold can begin.</summary>
	[Property] public float CooldownSeconds {{ get; set; }} = 0.4f;

	/// <summary>0..1 fill progress -- read this from your HUD to draw a radial/bar.</summary>
	public float Progress {{ get; private set; }}

	/// <summary>True while a hold is partway (0 &lt; Progress &lt; 1).</summary>
	public bool IsHolding => Progress > 0f && Progress < 1f;

	/// <summary>Fires (on the owning machine) when a hold completes.</summary>
	public static Action<GameObject> OnConfirmed {{ get; set; }}

	private TimeUntil _cooldownDone;
	private bool _cooling;

	protected override void OnUpdate()
	{{
		if ( IsProxy ) return;                 // only the owner drives their own hold

		if ( _cooling )
		{{
			if ( !_cooldownDone ) return;
			_cooling = false;
			Progress = 0f;
		}}

		bool held = !string.IsNullOrEmpty( Action ) && Input.Down( Action );
		float step = Time.Delta / HoldSeconds;

		if ( held )
		{{
			Progress = MathX.Clamp( Progress + step, 0f, 1f );
			if ( Progress >= 1f ) Confirm();
		}}
		else if ( Progress > 0f )
		{{
			Progress = DecayOnRelease ? MathX.Clamp( Progress - step, 0f, 1f ) : 0f;
		}}

		#region Feedback hook
		// Optional: drive a world/screen effect from Progress here (rise a sound
		// pitch, scale a ring, pulse a light...). Left empty on purpose -- this
		// component owns the timing, not the UI. Read Progress from your HUD.
		#endregion
	}}

	private void Confirm()
	{{
		Progress = 1f;
		OnConfirmed?.Invoke( GameObject );     // LOCAL -- wrap host-authoritative
		                                       // effects in an [Rpc.Host] in the subscriber
		_cooldownDone = CooldownSeconds;
		_cooling = true;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_carry_system -- a sealed Component for first-person pickup / carry /
// throw of physics props. Eye-traces for a Rigidbody-bearing GameObject tagged
// CarryTag within Range; grabbing routes a host-authoritative [Rpc.Host] that
// re-validates the caller, hands network ownership to the carrier, and disables
// the rigidbody's motion while held. The held object follows a hold point in
// front of the camera each FixedUpdate; drop restores physics, throw applies an
// impulse. HeldObjectId is [Sync(SyncFlags.FromHost)] so proxies see the carry.
// -----------------------------------------------------------------------------
public class CreateCarrySystemHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "CarrySystem", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float range      = p.TryGetProperty( "range",      out var rv ) && rv.TryGetSingle( out var rf ) ? rf : 130f;
			float throwForce = p.TryGetProperty( "throwForce", out var tv ) && tv.TryGetSingle( out var tf ) ? tf : 20000f;
			var carryTag     = p.TryGetProperty( "carryTag",   out var cv ) && !string.IsNullOrWhiteSpace( cv.GetString() ) ? cv.GetString() : "carryable";

			if ( range < 1f ) range = 1f;
			if ( throwForce < 0f ) throwForce = 0f;
			// Tags are lower-case, no whitespace by s&box convention.
			carryTag = carryTag.Replace( "\"", "" ).Trim().ToLowerInvariant().Replace( ' ', '_' );
			if ( carryTag.Length == 0 ) carryTag = "carryable";

			var code = BuildCode( className, range, throwForce, carryTag, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = InteractionPackHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				range,
				throwForce,
				carryTag,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject (make sure it is the PLAYER -- the object that owns the camera)."
						: $"Attach it to the PLAYER (the object with the camera): add_component_with_properties (component=\"{className}\") after the hotload, or re-run with targetId pointing at the player.",
					$"Make objects carryable: give each prop a Rigidbody + Collider and the tag '{carryTag}' (set_tags). Only tagged, rigidbody-bearing objects within Range ({range}) can be grabbed.",
					"Inputs: GrabAction (default 'use') grabs / drops, ThrowAction (default 'attack1') throws. HoldOffset positions the held object in front of the camera. Tune ThrowForce (impulse -- scales with the prop's mass) / Range / HoldOffset with set_property.",
					"Multiplayer: network-spawn the carryables so ownership + transform replicate; the grab routes through the [Rpc.Host] RequestPickup (re-validate Rpc.Caller for a competitive game). Single-player works with no networking. Subscribe to {className}.OnPickedUp / OnDropped for SFX/VFX."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_carry_system failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float range, float throwForce, string carryTag, System.Globalization.CultureInfo ci )
	{
		string rangeStr = range.ToString( ci ) + "f";
		string forceStr = throwForce.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- first-person pickup / carry / throw for physics props. Attach
/// to the PLAYER (the object that owns the camera). It eye-traces from Scene.Camera
/// for a Rigidbody-bearing GameObject tagged CarryTag within Range; grabbing routes
/// a host-authoritative [Rpc.Host] request that re-validates the target, hands the
/// object's network ownership to the carrier, and disables the rigidbody's motion
/// while held. The held object follows a hold point in front of the camera every
/// FixedUpdate; dropping restores physics, throwing adds an impulse.
///
/// HeldObjectId is [Sync(SyncFlags.FromHost)] (GameObject is not [Sync]-able, so we
/// sync the Id and resolve it via Scene.Directory.FindByGuid). Single-player safe:
/// IsProxy is false and RPCs run locally with no session. In multiplayer, network-
/// spawn the carryables so ownership + transform replicate.
///
/// Static events:
///   {className}.OnPickedUp += (carrier, item) =&gt; ...;
///   {className}.OnDropped  += (carrier, item) =&gt; ...;
/// </summary>
public sealed class {className} : Component
{{
	/// <summary>Eye-trace reach for grabbing a carryable, in world units.</summary>
	[Property] public float Range {{ get; set; }} = {rangeStr};

	/// <summary>Impulse applied on throw. Scales with the prop's mass -- tune per game.</summary>
	[Property] public float ThrowForce {{ get; set; }} = {forceStr};

	/// <summary>Only objects with this tag (and a Rigidbody) can be picked up.</summary>
	[Property] public string CarryTag {{ get; set; }} = ""{carryTag}"";

	/// <summary>Local-space offset from the camera where the held object is held (X is forward).</summary>
	[Property] public Vector3 HoldOffset {{ get; set; }} = new Vector3( 60f, 0f, 0f );

	/// <summary>Input action that grabs a carryable (and drops the held one).</summary>
	[Property] public string GrabAction {{ get; set; }} = ""use"";

	/// <summary>Input action that throws the held object.</summary>
	[Property] public string ThrowAction {{ get; set; }} = ""attack1"";

	// Host-authoritative held-object Id (Guid.Empty = empty hands). FromHost because
	// the host is the only writer; [Sync] so proxies see the carrying state.
	[Sync( SyncFlags.FromHost )] public Guid HeldObjectId {{ get; set; }}

	/// <summary>True while this carrier is holding something.</summary>
	public bool IsCarrying => HeldObjectId != Guid.Empty;

	/// <summary>Fires (on every machine) when a carrier picks up an item.</summary>
	public static Action<GameObject, GameObject> OnPickedUp {{ get; set; }}

	/// <summary>Fires (on every machine) when a carrier drops / throws an item.</summary>
	public static Action<GameObject, GameObject> OnDropped {{ get; set; }}

	private Guid _observedHeldId;
	private bool _released;

	protected override void OnUpdate()
	{{
		// Fire pickup/drop events uniformly on host + proxies when the id changes.
		if ( HeldObjectId != _observedHeldId )
		{{
			if ( HeldObjectId == Guid.Empty )
			{{
				var prev = Scene?.Directory?.FindByGuid( _observedHeldId );
				if ( prev.IsValid() ) OnDropped?.Invoke( GameObject, prev );
				_released = false;
			}}
			else
			{{
				var curr = Scene?.Directory?.FindByGuid( HeldObjectId );
				if ( curr.IsValid() ) OnPickedUp?.Invoke( GameObject, curr );
			}}
			_observedHeldId = HeldObjectId;
		}}

		// Only the carrier (owner, not a proxy) reads input.
		if ( IsProxy ) return;

		if ( HeldObjectId == Guid.Empty )
		{{
			if ( !string.IsNullOrEmpty( GrabAction ) && Input.Pressed( GrabAction ) )
				TryGrab();
		}}
		else if ( !string.IsNullOrEmpty( ThrowAction ) && Input.Pressed( ThrowAction ) )
		{{
			Release( ThrowForce );
		}}
		else if ( !string.IsNullOrEmpty( GrabAction ) && Input.Pressed( GrabAction ) )
		{{
			Release( 0f );
		}}
	}}

	protected override void OnFixedUpdate()
	{{
		if ( IsProxy || _released || HeldObjectId == Guid.Empty ) return;

		var held = Scene?.Directory?.FindByGuid( HeldObjectId );
		if ( !held.IsValid() ) return;

		var rb = held.Components.Get<Rigidbody>();
		if ( rb.IsValid() && rb.MotionEnabled ) rb.MotionEnabled = false;

		var cam = Scene?.Camera;
		var basePos = cam.IsValid() ? cam.WorldPosition : WorldPosition;
		var baseRot = cam.IsValid() ? cam.WorldRotation : WorldRotation;

		held.WorldPosition = basePos + baseRot * HoldOffset;
		held.WorldRotation = baseRot;
	}}

	private void TryGrab()
	{{
		var cam = Scene?.Camera;
		var from = cam.IsValid() ? cam.WorldPosition : WorldPosition;
		var rot = cam.IsValid() ? cam.WorldRotation : WorldRotation;
		var to = from + rot.Forward * Range;

		var tr = Scene.Trace.Ray( from, to )
			.WithTag( CarryTag )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() ) return;
		if ( !tr.GameObject.Components.Get<Rigidbody>().IsValid() ) return;

		_released = false;
		RequestPickup( tr.GameObject.Id );
	}}

	// Host-authoritative claim, routed here from the pressing client. Re-validates
	// the target on the host (never trusts the client's tag) and hands ownership to
	// the requester so their machine can drive the held transform. In a competitive
	// game, also confirm Rpc.Caller maps to this carrier before honoring the grab
	// (NetFlags is not security).
	[Rpc.Host]
	public void RequestPickup( Guid targetId )
	{{
		if ( HeldObjectId != Guid.Empty ) return;

		var target = Scene?.Directory?.FindByGuid( targetId );
		if ( !target.IsValid() ) return;
		if ( !string.IsNullOrEmpty( CarryTag ) && !target.Tags.Has( CarryTag ) ) return;
		if ( !target.Components.Get<Rigidbody>().IsValid() ) return;

		if ( Networking.IsActive && Rpc.Caller != null )
			target.Network.AssignOwnership( Rpc.Caller );

		HeldObjectId = targetId;
	}}

	private void Release( float force )
	{{
		var held = Scene?.Directory?.FindByGuid( HeldObjectId );
		if ( held.IsValid() )
		{{
			var rb = held.Components.Get<Rigidbody>();
			if ( rb.IsValid() )
			{{
				rb.MotionEnabled = true;
				if ( force > 0f )
				{{
					var cam = Scene?.Camera;
					var dir = cam.IsValid() ? cam.WorldRotation.Forward : WorldRotation.Forward;
					rb.Velocity = Vector3.Zero;
					rb.ApplyImpulse( dir * force );
				}}
			}}
		}}

		// Stop the follow loop immediately so a laggy id-clear cannot re-grab the
		// throw; the host clears the synced id authoritatively.
		_released = true;
		HostRelease();
	}}

	[Rpc.Host]
	public void HostRelease()
	{{
		HeldObjectId = Guid.Empty;
	}}
}}
";
	}
}

/// <summary>
/// Shared placement helper for the interaction-pack handlers -- mirrors the
/// standard scaffold placement (create_interactable / add_interaction_station).
/// </summary>
internal static class InteractionPackHelpers
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
