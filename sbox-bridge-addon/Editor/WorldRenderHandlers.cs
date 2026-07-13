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
//  World & Render family (v2.0.x wave) -- three tools:
//
//    add_water_body            WaterVolume physics volume + BoxCollider footprint
//                              + optional tinted visual surface (scene-direct)
//    add_render_target_camera  secondary camera + render-target display surface
//                              (scene-direct camera/display + codegen wiring comp)
//    add_daynight_sun          codegen: drives a DirectionalLight (+ SkyBox2D
//                              tint) from the shipped create_day_night_clock
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, ParseVector3/Flexible, ParseColor, ExtractFloats,
//  SerializeGo) and ScaffoldHelpers (PrepareCodeFile / WriteCode). Handler code
//  here is UNSANDBOXED editor code.
//
//  The C# *strings the codegen handlers WRITE TO DISK* are SANDBOXED game code:
//    - sealed components, no virtual members, no Array.Clone()
//    - MathX / System.MathF (both compile on the current SDK)
//    - foreach over Scene.GetAllComponents<T>() instead of Linq FirstOrDefault
//      (precedent: NpcBrainHandlers / LootEconomyHandlers generated code)
//    - Component .IsValid() used only where null-safety matters (valid API;
//      distinct from the removed Connection.IsValid)
//
//  Live-verified APIs this file leans on (get_method_signature / describe_type,
//  2026-07-12):
//    - Sandbox.WaterVolume: FluidDensity, LinearDrag, AngularDrag, FluidVelocity
//      (Vector3), SurfaceOffset, WaveAmplitude, WaveFrequency, IgnoreTags -- all rw
//    - BoxCollider: Scale, Center, IsTrigger -- all rw
//    - CameraComponent: RenderTarget (Texture, rw), IsMainCamera, Priority,
//      ClearFlags, BackgroundColor, FieldOfView -- all rw
//    - static Texture.CreateRenderTarget() -> TextureBuilder;
//      TextureBuilder.WithSize(int,int)/.WithScreenFormat()/.WithDynamicUsage()
//      /.Create(string name = null, bool anonymous = true, ...) -> Texture
//    - static Material.Create(string, string shader, bool anonymous = true);
//      instance Material.Set(string param, Texture) (also Color/float overloads)
//    - SkyBox2D.Tint (Color, rw); Model.Bounds (BBox, ro)
//
//  Register(...) lines + the _sceneMutatingCommands additions live in
//  MyEditorMenu.cs (the orchestrator integrates them; this file stays decoupled).
// =============================================================================

/// <summary>
/// Shared helpers local to the World &amp; Render handlers. Mirrors
/// GameFeelHelpers/ScaffoldHelpers but kept separate so this file never touches
/// the shipped ones.
/// </summary>
internal static class WorldRenderHelpers
{
	/// <summary>
	/// Standard scaffold placement: attach a generated component class to a scene
	/// GameObject by GUID -- only possible when the type is already compiled into
	/// the TypeLibrary (i.e. after a hotload). Same contract as
	/// GameFeelHelpers.PlaceOnTarget.
	/// </summary>
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

	/// <summary>
	/// Parse a colour param that may arrive as {r,g,b,a} OR a comma string
	/// "r,g,b,a" (the cross-language ColorSchema union). Falls back when absent
	/// or unparseable.
	/// </summary>
	public static Color ParseColorFlexible( JsonElement p, string key, Color fallback )
	{
		if ( !p.TryGetProperty( key, out var col ) ) return fallback;
		if ( col.ValueKind == JsonValueKind.Object ) return ClaudeBridge.ParseColor( col );
		if ( col.ValueKind == JsonValueKind.String )
		{
			var f = ClaudeBridge.ExtractFloats( col.GetString() );
			if ( f.Length >= 3 ) return new Color( f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f );
		}
		return fallback;
	}

	/// <summary>Read an optional float param; null when absent/unparseable.</summary>
	public static float? OptFloat( JsonElement p, string key )
	{
		if ( p.TryGetProperty( key, out var v ) )
		{
			if ( v.ValueKind == JsonValueKind.Number && v.TryGetSingle( out var f ) ) return f;
			if ( v.ValueKind == JsonValueKind.String
			     && float.TryParse( v.GetString(), System.Globalization.NumberStyles.Float,
			                        System.Globalization.CultureInfo.InvariantCulture, out var fs ) ) return fs;
		}
		return null;
	}
}

// -----------------------------------------------------------------------------
// add_water_body -- create a swimmable/buoyant water region: a GameObject with a
// WaterVolume (the engine's buoyancy/drag physics volume) sized by a trigger
// BoxCollider footprint, plus an optional simple tinted surface so the water is
// VISIBLE (a flat scaled dev-box with a translucent-blue Tint -- not a Gerstner
// water shader; honesty over polish).
//
// Scene-direct (no codegen) -- mirrors AddLightHandler / SetFogHandler style.
// -----------------------------------------------------------------------------
public class AddWaterBodyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var scene = SceneEditorSession.Active?.Scene;
			if ( scene == null )
				return Task.FromResult<object>( new { error = "No active scene" } );

			var name = p.TryGetProperty( "name", out var n ) && !string.IsNullOrWhiteSpace( n.GetString() )
				? n.GetString() : "Water";

			// Full extents of the volume, world units. Accepts "x,y,z" / [x,y,z] / {x,y,z}.
			var size = p.TryGetProperty( "size", out var sz )
				? ClaudeBridge.ParseVector3Flexible( sz )
				: new Vector3( 512f, 512f, 128f );
			size = new Vector3( MathF.Max( size.x, 1f ), MathF.Max( size.y, 1f ), MathF.Max( size.z, 1f ) );

			var go = scene.CreateObject( true );
			go.Name = name;
			if ( p.TryGetProperty( "position", out var pos ) )
				go.WorldPosition = ClaudeBridge.ParseVector3( pos );
			if ( p.TryGetProperty( "parentId", out var pid ) && Guid.TryParse( pid.GetString(), out var parentGuid ) )
			{
				var parent = scene.Directory.FindByGuid( parentGuid );
				if ( parent != null ) go.SetParent( parent, keepWorldPosition: true );
			}

			// Trigger box defines the region the WaterVolume acts in.
			var box = go.AddComponent<BoxCollider>();
			box.Scale = size;
			box.Center = Vector3.Zero;
			box.IsTrigger = true;

			// The physics volume itself. Only set what the caller provided so the
			// engine defaults survive untouched otherwise.
			var water = go.AddComponent<WaterVolume>();
			var applied = new List<string>();
			void Set( string key, Action<float> setter )
			{
				var v = WorldRenderHelpers.OptFloat( p, key );
				if ( v.HasValue ) { setter( v.Value ); applied.Add( key ); }
			}
			Set( "density",        v => water.FluidDensity  = v );
			Set( "linearDrag",     v => water.LinearDrag    = v );
			Set( "angularDrag",    v => water.AngularDrag   = v );
			Set( "waveAmplitude",  v => water.WaveAmplitude = v );
			Set( "waveFrequency",  v => water.WaveFrequency = v );
			Set( "surfaceOffset",  v => water.SurfaceOffset = v );
			if ( p.TryGetProperty( "fluidVelocity", out var fv ) )
			{
				water.FluidVelocity = ClaudeBridge.ParseVector3( fv );
				applied.Add( "fluidVelocity" );
			}

			// Optional visual surface: a thin scaled dev box, tinted translucent
			// blue, placed flush with the top of the volume. It is a stand-in --
			// the dev-box material may render the tint opaque.
			object surface = null;
			bool wantSurface = !p.TryGetProperty( "visualSurface", out var vs ) || vs.ValueKind != JsonValueKind.False;
			if ( wantSurface )
			{
				var surfaceColor = WorldRenderHelpers.ParseColorFlexible( p, "surfaceColor", new Color( 0.15f, 0.35f, 0.55f, 0.6f ) );
				var child = scene.CreateObject( true );
				child.Name = $"{name} Surface";
				child.SetParent( go, keepWorldPosition: false );

				var renderer = child.AddComponent<ModelRenderer>();
				var model = Model.Load( "models/dev/box.vmdl" );
				renderer.Model = model;
				renderer.Tint = surfaceColor;

				// Scale the box to the footprint, ~2 units thick, from its REAL bounds.
				var b = model?.Bounds.Size ?? new Vector3( 50f, 50f, 50f );
				if ( b.x < 0.01f || b.y < 0.01f || b.z < 0.01f ) b = new Vector3( 50f, 50f, 50f );
				const float thickness = 2f;
				child.LocalScale = new Vector3( size.x / b.x, size.y / b.y, thickness / b.z );

				// Top face flush with the water surface (top of the box + SurfaceOffset).
				float surfZ = size.z * 0.5f + water.SurfaceOffset - thickness * 0.5f;
				child.LocalPosition = new Vector3( 0f, 0f, surfZ );

				surface = ClaudeBridge.SerializeGo( child );
			}

			return Task.FromResult<object>( new
			{
				created = true,
				gameObject = ClaudeBridge.SerializeGo( go ),
				size = new { x = size.x, y = size.y, z = size.z },
				appliedProperties = applied,
				surface,
				note = "WaterVolume is the buoyancy/drag PHYSICS volume (region = the trigger BoxCollider on the same object). The visual surface is a simple tinted box, not a water shader -- swap it for a real water material when you have one.",
				nextSteps = new[]
				{
					"start_play, then drop a physics prop into the volume (spawn_model + add_physics beforehand) to verify buoyancy.",
					"Tune with set_property on the WaterVolume: FluidDensity, LinearDrag, AngularDrag, WaveAmplitude, WaveFrequency, SurfaceOffset, FluidVelocity.",
					"take_screenshot / screenshot_from to check the surface placement reads as water."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_water_body failed: {ex.Message}" } );
		}
	}
}

// -----------------------------------------------------------------------------
// add_render_target_camera -- CCTV / mirror / portal: a secondary camera
// (IsMainCamera=false) + a display surface + a generated wiring component that,
// at runtime, creates a render-target Texture, assigns camera.RenderTarget and
// puts the texture on the display's material.
//
// Hybrid: scene-direct for the camera + display objects, codegen for the
// runtime wiring (a Texture created in the editor would not serialize anyway).
// If the wiring class is ALREADY compiled (second call after a hotload), it is
// attached and fully wired -- SourceCamera included -- in one shot.
// -----------------------------------------------------------------------------
public class AddRenderTargetCameraHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var scene = SceneEditorSession.Active?.Scene;
			if ( scene == null )
				return Task.FromResult<object>( new { error = "No active scene" } );

			// Resolution "w,h" -> ints, baked into the generated class defaults.
			int resW = 512, resH = 512;
			if ( p.TryGetProperty( "resolution", out var res ) && res.ValueKind == JsonValueKind.String )
			{
				var f = ClaudeBridge.ExtractFloats( res.GetString() );
				if ( f.Length >= 1 ) resW = (int) MathF.Max( f[0], 16f );
				if ( f.Length >= 2 ) resH = (int) MathF.Max( f[1], 16f );
				else resH = resW;
			}
			var attribute = p.TryGetProperty( "attribute", out var at ) && !string.IsNullOrWhiteSpace( at.GetString() )
				? at.GetString() : "Color";
			// The attribute name lands inside a generated string literal -- keep it tame.
			attribute = ClaudeBridge.SanitizeIdentifier( attribute, "Color" );

			// ── 1. The wiring component: reuse if compiled, else generate ──
			var reqName = p.TryGetProperty( "name", out var nm ) && !string.IsNullOrWhiteSpace( nm.GetString() )
				? nm.GetString() : "RenderTargetDisplay";
			var className = ClaudeBridge.SanitizeIdentifier(
				Path.GetFileNameWithoutExtension( reqName.EndsWith( ".cs" ) ? reqName : reqName + ".cs" ) );

			bool typeLoaded = Game.TypeLibrary.GetType( className ) != null;
			bool generated = false;
			string relPath = null;
			if ( !typeLoaded )
			{
				if ( !ScaffoldHelpers.PrepareCodeFile( p, "RenderTargetDisplay", out var fullPath, out relPath, out var cn, out var err ) )
					return Task.FromResult<object>( err );
				className = cn;
				ScaffoldHelpers.WriteCode( fullPath, BuildCode( className, resW, resH, attribute ) );
				generated = true;
			}

			// ── 2. The secondary camera ─────────────────────────────────────
			var camGo = scene.CreateObject( true );
			camGo.Name = p.TryGetProperty( "cameraName", out var cnm ) && !string.IsNullOrWhiteSpace( cnm.GetString() )
				? cnm.GetString() : "RT Camera";
			if ( p.TryGetProperty( "position", out var pos ) )
				camGo.WorldPosition = ClaudeBridge.ParseVector3( pos );
			if ( p.TryGetProperty( "rotation", out var rot ) )
				camGo.WorldRotation = ClaudeBridge.ParseRotation( rot );
			if ( p.TryGetProperty( "lookAtId", out var la ) && Guid.TryParse( la.GetString(), out var lookGuid ) )
			{
				var target = scene.Directory.FindByGuid( lookGuid );
				if ( target != null )
				{
					var dir = target.WorldPosition - camGo.WorldPosition;
					if ( dir.Length > 0.01f ) camGo.WorldRotation = Rotation.LookAt( dir );
				}
			}

			var cam = camGo.AddComponent<CameraComponent>();
			cam.IsMainCamera = false;
			var priF = WorldRenderHelpers.OptFloat( p, "priority" );
			cam.Priority = priF.HasValue ? (int) priF.Value : 1;
			var fov = WorldRenderHelpers.OptFloat( p, "fieldOfView" );
			if ( fov.HasValue ) cam.FieldOfView = MathX.Clamp( fov.Value, 5f, 170f );
			if ( p.TryGetProperty( "backgroundColor", out _ ) )
				cam.BackgroundColor = WorldRenderHelpers.ParseColorFlexible( p, "backgroundColor", cam.BackgroundColor );

			// ── 3. The display surface (optional) ───────────────────────────
			object display = null; string displayId = null; string note = null; bool wired = false;
			bool wantDisplay = !p.TryGetProperty( "createDisplay", out var cd ) || cd.ValueKind != JsonValueKind.False;
			if ( wantDisplay )
			{
				var dispGo = scene.CreateObject( true );
				dispGo.Name = $"{camGo.Name} Display";
				if ( p.TryGetProperty( "displayPosition", out var dp ) )
					dispGo.WorldPosition = ClaudeBridge.ParseVector3( dp );
				if ( p.TryGetProperty( "displayRotation", out var dr ) )
					dispGo.WorldRotation = ClaudeBridge.ParseRotation( dr );

				var dispSize = p.TryGetProperty( "displaySize", out var ds )
					? ClaudeBridge.ParseVector3Flexible( ds )
					: new Vector3( 4f, 128f, 72f );
				dispSize = new Vector3( MathF.Max( dispSize.x, 0.5f ), MathF.Max( dispSize.y, 0.5f ), MathF.Max( dispSize.z, 0.5f ) );

				var renderer = dispGo.AddComponent<ModelRenderer>();
				var model = Model.Load( "models/dev/box.vmdl" );
				renderer.Model = model;
				var b = model?.Bounds.Size ?? new Vector3( 50f, 50f, 50f );
				if ( b.x < 0.01f || b.y < 0.01f || b.z < 0.01f ) b = new Vector3( 50f, 50f, 50f );
				dispGo.WorldScale = new Vector3( dispSize.x / b.x, dispSize.y / b.y, dispSize.z / b.z );

				// If the wiring class is already compiled, attach AND wire it now.
				var typeDesc = Game.TypeLibrary.GetType( className );
				if ( typeDesc != null )
				{
					try
					{
						var comp = dispGo.Components.Create( typeDesc );
						void SetProp( string propName, object value )
						{
							var pd = typeDesc.Properties.FirstOrDefault( x => x.Name == propName );
							pd?.SetValue( comp, value );
						}
						SetProp( "SourceCamera", cam );
						SetProp( "ResolutionWidth", resW );
						SetProp( "ResolutionHeight", resH );
						SetProp( "TextureAttribute", attribute );
						wired = true;
					}
					catch ( Exception ex ) { note = $"Attach/wire failed ({ex.Message})."; }
				}

				display = ClaudeBridge.SerializeGo( dispGo );
				displayId = dispGo.Id.ToString();
			}

			var steps = new List<string>();
			if ( generated )
				steps.Add( $"trigger_hotload to compile {className} into the game assembly." );
			if ( !wired && displayId != null )
				steps.Add( $"Attach + wire after the hotload: add_component_with_properties id={displayId} component=\"{className}\" properties={{\"SourceCamera\":\"{camGo.Id}\"}} (Component refs coerce from a GameObject GUID)." );
			if ( !wantDisplay )
				steps.Add( $"No display was created -- attach {className} to any GameObject with a ModelRenderer and set SourceCamera to the camera ({camGo.Id})." );
			steps.Add( "The wiring runs in OnStart: start_play, then capture_view to verify the feed shows on the display." );
			steps.Add( "Aim the camera later with set_transform on the camera GameObject; retune resolution via the component's ResolutionWidth/Height before play." );

			return Task.FromResult<object>( new
			{
				created = true,
				camera = ClaudeBridge.SerializeGo( camGo ),
				display,
				className,
				path = relPath,
				generatedCode = generated,
				attachedAndWired = wired,
				resolution = new { width = resW, height = resH },
				note,
				nextSteps = steps.ToArray()
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_render_target_camera failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, int resW, int resH, string attribute )
	{
		var ci = System.Globalization.CultureInfo.InvariantCulture;
		string w = resW.ToString( ci );
		string h = resH.ToString( ci );

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- shows a secondary camera's view on this GameObject's
/// ModelRenderer (CCTV monitor, security feed, mirror, simple portal).
///
/// On Start it creates a render-target Texture ({w}x{h}), assigns it to
/// SourceCamera.RenderTarget (forcing IsMainCamera off), builds an anonymous
/// material on the complex shader, sets the texture on the material's
/// ""{attribute}"" attribute and applies it as the renderer's MaterialOverride.
/// Everything happens at runtime -- enter play mode to see the feed.
/// The display is lit by the scene like any surface; it is not an emissive
/// unlit screen.
/// </summary>
public sealed class {className} : Component
{{
	/// The camera whose view feeds this display. Must not be the main camera.
	[Property] public CameraComponent SourceCamera {{ get; set; }}

	/// Render-target width in pixels.
	[Property] public int ResolutionWidth {{ get; set; }} = {w};

	/// Render-target height in pixels.
	[Property] public int ResolutionHeight {{ get; set; }} = {h};

	/// Material attribute that receives the texture (""Color"" = albedo on the complex shader).
	[Property] public string TextureAttribute {{ get; set; }} = ""{attribute}"";

	private Texture _rt;

	protected override void OnStart()
	{{
		if ( SourceCamera == null )
		{{
			Log.Warning( ""{className}: SourceCamera is not assigned on "" + GameObject.Name + "" -- assign the secondary camera."" );
			Enabled = false;
			return;
		}}

		int w = ResolutionWidth < 16 ? 16 : ResolutionWidth;
		int h = ResolutionHeight < 16 ? 16 : ResolutionHeight;

		_rt = Texture.CreateRenderTarget()
			.WithSize( w, h )
			.WithScreenFormat()
			.WithDynamicUsage()
			.Create( ""rt_"" + GameObject.Id.ToString(), true );

		if ( _rt == null )
		{{
			Log.Warning( ""{className}: failed to create the render target."" );
			return;
		}}

		SourceCamera.IsMainCamera = false;
		SourceCamera.RenderTarget = _rt;

		var renderer = GetComponent<ModelRenderer>();
		if ( renderer == null )
		{{
			Log.Warning( ""{className}: no ModelRenderer on "" + GameObject.Name + "" -- the camera renders to the texture but nothing displays it."" );
			return;
		}}

		var mat = Material.Create( ""rt_mat_"" + GameObject.Id.ToString(), ""shaders/complex.shader"", true );
		if ( mat == null )
		{{
			Log.Warning( ""{className}: failed to create the display material."" );
			return;
		}}

		mat.Set( TextureAttribute, _rt );
		renderer.MaterialOverride = mat;
	}}

	protected override void OnDestroy()
	{{
		if ( SourceCamera.IsValid() )
			SourceCamera.RenderTarget = null;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// add_daynight_sun -- codegen: a sealed component that drives an existing
// DirectionalLight (sun pitch arc + LightColor gradient) and optionally a
// SkyBox2D Tint from the day-night clock generated by create_day_night_clock.
// COMPANION to the clock, not a replacement: it reads TimeOfDay / SunriseHour /
// SunsetHour off the clock component every frame.
//
// The clock class name is BAKED into the generated code as a direct typed
// reference (both files compile into the same game assembly), so the handler
// refuses to generate unless the clock class exists -- a missing type would
// break the WHOLE game assembly compile.
// -----------------------------------------------------------------------------
public class AddDayNightSunHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			var clockClass = p.TryGetProperty( "clockClass", out var cc ) && !string.IsNullOrWhiteSpace( cc.GetString() )
				? ClaudeBridge.SanitizeIdentifier( cc.GetString(), "DayNightClock" ) : "DayNightClock";

			// Refuse to bake a type reference that doesn't exist anywhere -- a bad
			// token would fail the whole game-assembly compile. TypeLibrary first
			// (compiled), then a project-file scan (generated but not yet hotloaded).
			bool clockKnown = Game.TypeLibrary.GetType( clockClass ) != null;
			if ( !clockKnown )
			{
				try
				{
					var root = Project.Current.GetRootPath();
					foreach ( var file in Directory.EnumerateFiles( root, "*.cs", SearchOption.AllDirectories ) )
					{
						if ( file.Contains( Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar ) ) continue;
						string text;
						try { text = File.ReadAllText( file ); } catch { continue; }
						if ( text.Contains( "class " + clockClass ) ) { clockKnown = true; break; }
					}
				}
				catch { }
			}
			if ( !clockKnown )
				return Task.FromResult<object>( new { error = $"Clock class '{clockClass}' not found in the TypeLibrary or any project .cs file. Run create_day_night_clock first (or pass clockClass= the name you generated it with) -- add_daynight_sun is a companion that binds to it, not a replacement." } );

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "DayNightSun", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float sunYaw    = WorldRenderHelpers.OptFloat( p, "sunYaw" ) ?? 30f;
			float intensity = WorldRenderHelpers.OptFloat( p, "intensity" ) ?? 1f;
			if ( intensity < 0f ) intensity = 0f;
			bool driveSkybox = !p.TryGetProperty( "driveSkybox", out var dsb ) || dsb.ValueKind != JsonValueKind.False;

			var code = BuildCode( className, clockClass, sunYaw, intensity, driveSkybox, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = WorldRenderHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				clockClass,
				sunYaw,
				intensity,
				driveSkybox,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Attach it to the DirectionalLight's GameObject (add_component_with_properties component=\"{className}\") -- it auto-finds a sibling DirectionalLight, else the first one in the scene; set the Sun property with set_component_reference to override.",
					$"It reads TimeOfDay/SunriseHour/SunsetHour from the scene's {clockClass} every frame -- make sure one is placed (create_day_night_clock's targetId or add_component_with_properties).",
					"start_play and watch: set_runtime_property TimeOfDay on the clock (e.g. 6, 12, 20, 0) + capture_view to verify dawn/noon/dusk/night looks.",
					"Tune SunYaw (arc heading), Intensity (overall brightness) and DriveSkybox with set_property."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_daynight_sun failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string clockClass, float sunYaw, float intensity, bool driveSkybox, System.Globalization.CultureInfo ci )
	{
		string yaw = sunYaw.ToString( ci ) + "f";
		string inten = intensity.ToString( ci ) + "f";
		string sky = driveSkybox ? "true" : "false";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- drives a DirectionalLight (and optionally the SkyBox2D tint)
/// from a {clockClass} day-night clock. COMPANION to the clock component, not a
/// replacement: the clock owns TimeOfDay; this only renders it.
///
/// TimeOfDay maps to a sun arc: sunrise = east horizon (pitch 0), noon =
/// overhead (pitch 90), sunset = west horizon (pitch 180), then the arc
/// continues below the horizon through the night. LightColor runs a hardcoded
/// gradient: warm dawn -&gt; white noon -&gt; orange dusk -&gt; dim blue night.
///
/// Attach anywhere; it prefers a sibling DirectionalLight / {clockClass}, else
/// falls back to the first one in the scene (retrying once a second).
/// </summary>
public sealed class {className} : Component
{{
	/// The sun light to drive. Auto-resolved (sibling, then scene) when unset.
	[Property] public DirectionalLight Sun {{ get; set; }}

	/// Compass heading of the sun's arc, degrees.
	[Property] public float SunYaw {{ get; set; }} = {yaw};

	/// Overall brightness multiplier on the colour gradient (HDR -- &gt;1 is valid).
	[Property] public float Intensity {{ get; set; }} = {inten};

	/// Also tint the scene's SkyBox2D between night blue and day white.
	[Property] public bool DriveSkybox {{ get; set; }} = {sky};

	private {clockClass} _clock;
	private SkyBox2D _sky;
	private TimeUntil _retryResolve;

	// Gradient keyframes (dayFrac 0 = sunrise, 0.5 = noon, 1 = sunset).
	private static readonly Color DawnColor      = new Color( 0.60f, 0.32f, 0.18f );
	private static readonly Color MorningColor   = new Color( 1.00f, 0.87f, 0.70f );
	private static readonly Color NoonColor      = new Color( 1.05f, 1.02f, 0.97f );
	private static readonly Color AfternoonColor = new Color( 1.00f, 0.85f, 0.65f );
	private static readonly Color DuskColor      = new Color( 0.62f, 0.26f, 0.12f );
	private static readonly Color NightColor     = new Color( 0.030f, 0.040f, 0.080f );
	private static readonly Color NightSkyTint   = new Color( 0.05f, 0.07f, 0.16f );

	protected override void OnStart()
	{{
		Resolve();
		_retryResolve = 1f;
	}}

	private void Resolve()
	{{
		if ( _clock == null )
		{{
			_clock = GetComponent<{clockClass}>();
			if ( _clock == null )
			{{
				foreach ( var c in Scene.GetAllComponents<{clockClass}>() ) {{ _clock = c; break; }}
			}}
		}}
		if ( Sun == null )
		{{
			Sun = GetComponent<DirectionalLight>();
			if ( Sun == null )
			{{
				foreach ( var l in Scene.GetAllComponents<DirectionalLight>() ) {{ Sun = l; break; }}
			}}
		}}
		if ( _sky == null )
		{{
			foreach ( var s in Scene.GetAllComponents<SkyBox2D>() ) {{ _sky = s; break; }}
		}}
	}}

	protected override void OnUpdate()
	{{
		if ( _clock == null || Sun == null )
		{{
			if ( _retryResolve ) {{ Resolve(); _retryResolve = 1f; }}
			if ( _clock == null || Sun == null ) return;
		}}

		float tod = _clock.TimeOfDay;
		float sunrise = _clock.SunriseHour;
		float sunset = _clock.SunsetHour;
		float dayLen = MathX.Clamp( sunset - sunrise, 0.1f, 24f );

		float pitch;
		float daylight; // 0 at night, sine-shaped 0..1 across the day (continuous at the edges)
		Color sunColor;

		if ( tod >= sunrise && tod < sunset )
		{{
			float f = ( tod - sunrise ) / dayLen;
			pitch = f * 180f;
			daylight = MathF.Sin( f * MathF.PI );
			sunColor = SampleDayColor( f );
		}}
		else
		{{
			float nightLen = MathX.Clamp( 24f - dayLen, 0.1f, 24f );
			float since = tod >= sunset ? tod - sunset : tod + ( 24f - sunset );
			float f = MathX.Clamp( since / nightLen, 0f, 1f );
			pitch = 180f + f * 180f;
			daylight = 0f;

			// Ease dusk->night and night->dawn so the colour never snaps.
			if ( f < 0.1f )       sunColor = LerpColor( DuskColor, NightColor, f / 0.1f );
			else if ( f > 0.9f )  sunColor = LerpColor( NightColor, DawnColor, ( f - 0.9f ) / 0.1f );
			else                  sunColor = NightColor;
		}}

		Sun.WorldRotation = Rotation.From( pitch, SunYaw, 0f );
		Sun.LightColor = new Color( sunColor.r * Intensity, sunColor.g * Intensity, sunColor.b * Intensity, 1f );

		if ( DriveSkybox && _sky != null )
			_sky.Tint = LerpColor( NightSkyTint, Color.White, daylight );
	}}

	/// <summary>Colour along the daytime arc: dawn -&gt; morning -&gt; noon -&gt; afternoon -&gt; dusk.</summary>
	private static Color SampleDayColor( float f )
	{{
		if ( f < 0.12f )  return LerpColor( DawnColor, MorningColor, f / 0.12f );
		if ( f < 0.50f )  return LerpColor( MorningColor, NoonColor, ( f - 0.12f ) / 0.38f );
		if ( f < 0.88f )  return LerpColor( NoonColor, AfternoonColor, ( f - 0.50f ) / 0.38f );
		return LerpColor( AfternoonColor, DuskColor, ( f - 0.88f ) / 0.12f );
	}}

	private static Color LerpColor( Color a, Color b, float t )
	{{
		t = MathX.Clamp( t, 0f, 1f );
		return new Color( a.r + ( b.r - a.r ) * t, a.g + ( b.g - a.g ) * t, a.b + ( b.b - a.b ) * t, 1f );
	}}
}}
";
	}
}
