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
//  Game Feel pack (v1.19.0) -- three "juice" scaffolds (code-gen; scene-mutating):
//
//    create_camera_shake         trauma-based Perlin camera shake component
//    add_flicker_light           flicker/pulse animator for an existing light
//    create_floating_combat_text rising/fading world-space damage popups
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, SerializeGo) and ScaffoldHelpers (PrepareCodeFile /
//  WriteCode / Utf8NoBom). Handler code here is UNSANDBOXED editor code.
//
//  The C# *strings these handlers WRITE TO DISK* are SANDBOXED game code and must
//  obey the s&box sandbox rules:
//    - MathX preferred; System.Math/MathF also compile on the current SDK.
//      Array.Clone() is still whitelist-blocked (not used here).
//    - only sandbox-proven APIs: Component, [Property], List<T>, TimeSince,
//      Game.Random.Float (compile-verified in create_weighted_loot_table),
//      Sandbox.Utility.Noise.Perlin (fully qualified to dodge a using),
//      new GameObject(...) for runtime spawns.
//    - all three generated components are LOCAL/visual-only -- no [Sync], no
//      RPCs. Multiplayer note lands in the nextSteps (wrap the calls in an
//      [Rpc.Broadcast] so every client sees the juice).
//
//  Register(...) lines + the _sceneMutatingCommands additions live in
//  MyEditorMenu.cs (Batch 44) to keep the files decoupled.
// =============================================================================

// -----------------------------------------------------------------------------
// create_camera_shake -- trauma-based camera shake (the corpus-standard model:
// shake magnitude = Trauma^2, Perlin-driven offsets, decays over time).
//
// Applied in OnPreRender AFTER controllers have positioned the camera. The
// un-apply guard (compare against what we last WROTE) makes it correct on both
// a static camera (no accumulation) and a controller-driven one (no fighting).
// -----------------------------------------------------------------------------
public class CreateCameraShakeHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "CameraShake", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float maxOffset = p.TryGetProperty( "maxOffset",      out var ov ) && ov.TryGetSingle( out var of ) ? of : 6f;
			float maxAngle  = p.TryGetProperty( "maxAngle",       out var av ) && av.TryGetSingle( out var af ) ? af : 4f;
			float frequency = p.TryGetProperty( "frequency",      out var fv ) && fv.TryGetSingle( out var ff ) ? ff : 10f;
			float decay     = p.TryGetProperty( "decayPerSecond", out var dv ) && dv.TryGetSingle( out var df ) ? df : 1.5f;

			// Defensive clamps so a silly value can't emit a nauseating component.
			if ( maxOffset < 0f )    maxOffset = 0f;
			if ( maxAngle  < 0f )    maxAngle  = 0f;
			if ( frequency < 0.1f )  frequency = 0.1f;
			if ( decay     < 0.05f ) decay     = 0.05f;

			var code = BuildCode( className, maxOffset, maxAngle, frequency, decay, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = GameFeelHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				maxOffset,
				maxAngle,
				frequency,
				decayPerSecond = decay,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach it to the CAMERA GameObject: add_component_with_properties (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Fire a shake from any game code: {className}.Shake( 0.4f ) -- explosions ~0.6-1.0, hits ~0.2-0.4, footsteps ~0.05. Trauma stacks and clamps at 1.",
					"LOCAL-only: call it inside an [Rpc.Broadcast] handler if every client should feel the shake.",
					"Tune MaxOffset / MaxAngle / Frequency / DecayPerSecond with set_property, then verify in play mode: playtest with a capture step, or set_runtime_property Trauma=1 and take_screenshot."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_camera_shake failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float maxOffset, float maxAngle, float frequency, float decay, System.Globalization.CultureInfo ci )
	{
		string mo = maxOffset.ToString( ci ) + "f";
		string ma = maxAngle.ToString( ci ) + "f";
		string fq = frequency.ToString( ci ) + "f";
		string dc = decay.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- trauma-based camera shake. Attach to the camera GameObject.
///
/// The standard game-feel model: an event adds Trauma (0..1), shake magnitude
/// is Trauma^2 (small hits barely register, big hits slam), offsets are smooth
/// Perlin noise (not white-noise jitter), and Trauma decays every frame.
///
/// Usage from anywhere:  {className}.Shake( 0.5f );
/// LOCAL-only -- wrap the call in an [Rpc.Broadcast] if all clients should shake.
/// </summary>
public sealed class {className} : Component
{{
	/// Current shake energy, 0..1. Add via Shake(); decays by DecayPerSecond.
	[Property] public float Trauma {{ get; set; }}

	/// Positional shake at full trauma, in world units.
	[Property] public float MaxOffset {{ get; set; }} = {mo};

	/// Rotational shake at full trauma, in degrees (pitch/yaw/roll).
	[Property] public float MaxAngle {{ get; set; }} = {ma};

	/// Noise speed -- higher = more violent rattle, lower = drunken sway.
	[Property] public float Frequency {{ get; set; }} = {fq};

	/// How much trauma drains per second.
	[Property] public float DecayPerSecond {{ get; set; }} = {dc};

	private static readonly List<{className}> _active = new List<{className}>();

	private Vector3 _lastWrittenPos;
	private Rotation _lastWrittenRot;
	private Vector3 _appliedOffset;
	private Rotation _appliedRot = Rotation.Identity;
	private bool _hasApplied;

	/// <summary>Add trauma to every active {className} (usually the one on the local camera).</summary>
	public static void Shake( float trauma )
	{{
		foreach ( var s in _active )
			s.Trauma = MathX.Clamp( s.Trauma + trauma, 0f, 1f );
	}}

	protected override void OnEnabled()
	{{
		_active.Add( this );
	}}

	protected override void OnDisabled()
	{{
		_active.Remove( this );
		RemoveAppliedShake();
	}}

	protected override void OnPreRender()
	{{
		var go = GameObject;

		// Recover the unshaken base. If a controller re-wrote the camera since our
		// last write, ITS value is the new base and our old offset is already gone --
		// only un-apply when the transform still equals exactly what we wrote.
		var basePos = go.WorldPosition;
		var baseRot = go.WorldRotation;
		if ( _hasApplied && basePos == _lastWrittenPos ) basePos -= _appliedOffset;
		if ( _hasApplied && baseRot == _lastWrittenRot ) baseRot = baseRot * _appliedRot.Inverse;
		_hasApplied = false;

		Trauma = MathX.Clamp( Trauma - DecayPerSecond * Time.Delta, 0f, 1f );
		float shake = Trauma * Trauma;

		if ( shake < 0.0005f )
		{{
			go.WorldPosition = basePos;
			go.WorldRotation = baseRot;
			return;
		}}

		// Smooth signed noise per axis (-1..1), decorrelated by row offset.
		float t = Time.Now * Frequency;
		float N( float row ) => (Sandbox.Utility.Noise.Perlin( t, row ) - 0.5f) * 2f;

		_appliedOffset = new Vector3( N( 0f ), N( 17f ), N( 31f ) ) * (MaxOffset * shake);
		_appliedRot = Rotation.From( N( 47f ) * MaxAngle * shake, N( 61f ) * MaxAngle * shake, N( 83f ) * MaxAngle * shake );

		go.WorldPosition = basePos + _appliedOffset;
		go.WorldRotation = baseRot * _appliedRot;
		_lastWrittenPos = go.WorldPosition;
		_lastWrittenRot = go.WorldRotation;
		_hasApplied = true;
	}}

	private void RemoveAppliedShake()
	{{
		if ( !_hasApplied ) return;
		var go = GameObject;
		if ( go.WorldPosition == _lastWrittenPos ) go.WorldPosition -= _appliedOffset;
		if ( go.WorldRotation == _lastWrittenRot ) go.WorldRotation = go.WorldRotation * _appliedRot.Inverse;
		_hasApplied = false;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// add_flicker_light -- generate a light-flicker animator and (optionally) attach
// it to an existing light GameObject. Presets: Candle, Fluorescent, Faulty,
// Pulse, Lightning. Modulates Light.LightColor around a captured base color;
// restores the base on disable.
// -----------------------------------------------------------------------------
public class AddFlickerLightHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "FlickerLight", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var style = p.TryGetProperty( "style", out var sv ) && !string.IsNullOrWhiteSpace( sv.GetString() )
				? sv.GetString() : "Candle";
			// Validate against the generated enum so a typo can't emit uncompilable code.
			var validStyles = new[] { "Candle", "Fluorescent", "Faulty", "Pulse", "Lightning" };
			var matched = validStyles.FirstOrDefault( s => s.Equals( style, StringComparison.OrdinalIgnoreCase ) );
			if ( matched == null )
				return Task.FromResult<object>( new { error = $"Unknown style '{style}'. Valid: {string.Join( ", ", validStyles )}" } );
			style = matched;

			float intensity = p.TryGetProperty( "intensity", out var iv ) && iv.TryGetSingle( out var iff ) ? iff : 0.5f;
			float speed     = p.TryGetProperty( "speed",     out var spv ) && spv.TryGetSingle( out var spf ) ? spf : 1f;
			intensity = intensity < 0f ? 0f : intensity > 1f ? 1f : intensity;
			if ( speed < 0.05f ) speed = 0.05f;

			var code = BuildCode( className, style, intensity, speed, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			// `lightId` is the ergonomic param name; `targetId` also accepted (sibling convention).
			string target = null;
			if ( p.TryGetProperty( "lightId", out var lid ) && lid.ValueKind == JsonValueKind.String ) target = lid.GetString();
			else if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String ) target = tid.GetString();
			if ( target != null )
				placedOn = GameFeelHelpers.PlaceOnTarget( target, className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				style,
				intensity,
				speed,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach it to a GameObject that has a light component (PointLight / SpotLight / DirectionalLight): add_component_with_properties (component=\"{className}\") after the hotload, or re-run with lightId.",
					"The animator modulates the light's LightColor around its starting color and restores it on disable -- tune Style / Intensity / Speed with set_property.",
					"Verify in play mode: start_play, then take_screenshot twice ~a second apart and compare the light's brightness (or capture_view for a scene-only frame)."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_flicker_light failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string style, float intensity, float speed, System.Globalization.CultureInfo ci )
	{
		string it = intensity.ToString( ci ) + "f";
		string sp = speed.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- flickers the light on this GameObject. Attach next to a
/// PointLight / SpotLight / DirectionalLight; it modulates LightColor around
/// the color it found on enable and restores it on disable.
///
/// Styles: Candle (soft organic sway), Fluorescent (mostly steady, random
/// dips), Faulty (hard on/off cuts), Pulse (slow sine breathing), Lightning
/// (dim baseline, rare bright flashes).
/// </summary>
public sealed class {className} : Component
{{
	public enum FlickerStyle {{ Candle, Fluorescent, Faulty, Pulse, Lightning }}

	[Property] public FlickerStyle Style {{ get; set; }} = FlickerStyle.{style};

	/// Flicker depth: 0 = steady, 1 = full blackouts / double-bright flashes.
	[Property] public float Intensity {{ get; set; }} = {it};

	/// Speed multiplier for the whole pattern.
	[Property] public float Speed {{ get; set; }} = {sp};

	private Light _light;
	private Color _baseColor;
	private float _seed;
	private float _mult = 1f;

	protected override void OnEnabled()
	{{
		_light = GetComponent<Light>();
		if ( _light == null )
		{{
			Log.Warning( $""{className}: no Light component on {{GameObject.Name}} -- disabling."" );
			Enabled = false;
			return;
		}}
		_baseColor = _light.LightColor;
		_seed = Game.Random.Float( 0f, 512f );
	}}

	protected override void OnDisabled()
	{{
		if ( _light != null ) _light.LightColor = _baseColor;
	}}

	protected override void OnUpdate()
	{{
		if ( _light == null ) return;

		float t = (Time.Now + _seed) * Speed;
		float n = Sandbox.Utility.Noise.Perlin( t * 6f, _seed ); // smooth 0..1

		float target = Style switch
		{{
			FlickerStyle.Candle      => MathX.Lerp( 1f - Intensity * 0.6f, 1f, n ),
			FlickerStyle.Fluorescent => n > 0.75f ? 1f - Intensity : 1f,
			FlickerStyle.Faulty      => Sandbox.Utility.Noise.Perlin( t * 14f, _seed ) > 0.55f ? 1f : 1f - Intensity,
			FlickerStyle.Pulse       => MathX.Lerp( 1f - Intensity, 1f, 0.5f + 0.5f * MathF.Sin( t * 4f ) ),
			FlickerStyle.Lightning   => n > 0.92f ? 1f + Intensity * 2f : 1f - Intensity * 0.85f,
			_ => 1f
		}};

		// Smooth toward the target so hard styles read as a light, not strobe noise.
		_mult = MathX.Lerp( _mult, target, MathX.Clamp( Time.Delta * 24f, 0f, 1f ) );
		_light.LightColor = _baseColor * _mult;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_floating_combat_text -- rising/fading world-space text popups
// (damage numbers, "+10 gold", pickup names). TextRenderer-based -- no Razor,
// no WorldPanel, works with zero UI setup. The generated class IS the popup
// behavior and carries a static Spawn() factory; nothing to place in the scene.
// -----------------------------------------------------------------------------
public class CreateFloatingCombatTextHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "FloatingCombatText", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float riseSpeed = p.TryGetProperty( "riseSpeed", out var rv ) && rv.TryGetSingle( out var rf ) ? rf : 48f;
			float lifetime  = p.TryGetProperty( "lifetime",  out var lv ) && lv.TryGetSingle( out var lf ) ? lf : 1.1f;
			float fontSize  = p.TryGetProperty( "fontSize",  out var fv ) && fv.TryGetSingle( out var ff ) ? ff : 24f;
			if ( riseSpeed < 0f )   riseSpeed = 0f;
			if ( lifetime  < 0.1f ) lifetime  = 0.1f;
			if ( fontSize  < 1f )   fontSize  = 1f;

			var code = BuildCode( className, riseSpeed, lifetime, fontSize, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				riseSpeed,
				lifetime,
				fontSize,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					$"Nothing to place -- spawn popups from any game code: {className}.Spawn( hitPosition + Vector3.Up * 32f, \"-25\", Color.Red ) (optional 4th arg scales the text).",
					$"Pairs with create_health_system: call {className}.Spawn from the damage path so every hit prints its number.",
					"LOCAL-only: spawn inside an [Rpc.Broadcast] handler if every client should see the popup.",
					"Verify in play mode: execute a spawn (e.g. via invoke_method on a test component), then take_screenshot -- the text rises and fades over Lifetime seconds."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_floating_combat_text failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float riseSpeed, float lifetime, float fontSize, System.Globalization.CultureInfo ci )
	{
		string rs = riseSpeed.ToString( ci ) + "f";
		string lt = lifetime.ToString( ci ) + "f";
		string fs = fontSize.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- a rising, fading world-space text popup (damage numbers,
/// ""+10 gold"", pickup names). TextRenderer-based: no Razor, no panels.
///
/// Spawn from anywhere:
///   {className}.Spawn( position, ""-25"", Color.Red );
///   {className}.Spawn( position, ""+10 gold"", Color.Yellow, 1.5f );
///
/// The popup billboards to the camera, rises, fades out, and destroys itself.
/// LOCAL-only -- spawn inside an [Rpc.Broadcast] if all clients should see it.
/// </summary>
public sealed class {className} : Component
{{
	/// World units risen per second.
	[Property] public float RiseSpeed {{ get; set; }} = {rs};

	/// Seconds until fully faded and destroyed.
	[Property] public float Lifetime {{ get; set; }} = {lt};

	private TextRenderer _text;
	private Color _startColor;
	private TimeSince _age;

	/// <summary>Spawn a popup at a world position. Returns the popup GameObject.</summary>
	public static GameObject Spawn( Vector3 position, string text, Color color, float size = 1f )
	{{
		var go = new GameObject( true, ""FloatingText"" );
		go.WorldPosition = position;

		var tr = go.AddComponent<TextRenderer>();
		tr.Text = text;
		tr.Color = color;
		tr.FontSize = {fs} * size;

		go.AddComponent<{className}>();
		return go;
	}}

	protected override void OnStart()
	{{
		_age = 0f;
		_text = GetComponent<TextRenderer>();
		if ( _text != null ) _startColor = _text.Color;
	}}

	protected override void OnUpdate()
	{{
		var go = GameObject;
		go.WorldPosition += Vector3.Up * (RiseSpeed * Time.Delta);

		// Billboard: face the same way the camera faces, mirrored toward it.
		var cam = Scene?.Camera;
		if ( cam != null )
			go.WorldRotation = Rotation.LookAt( -cam.WorldRotation.Forward );

		if ( _text != null )
			_text.Color = _startColor.WithAlpha( _startColor.a * MathX.Clamp( 1f - _age / Lifetime, 0f, 1f ) );

		if ( _age >= Lifetime )
			go.Destroy();
	}}
}}
";
	}
}

/// <summary>
/// Shared placement helper for the game-feel handlers -- mirrors the standard
/// scaffold placement (create_weighted_loot_table / create_event_director).
/// </summary>
internal static class GameFeelHelpers
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
