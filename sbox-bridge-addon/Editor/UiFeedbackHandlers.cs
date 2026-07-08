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
//  UI / Feedback pack (v1.20.0, Track C) -- three feedback scaffolds
//  (code-gen; scene/disk-mutating):
//
//    create_worldpanel_ui   diegetic clickable WorldPanel Razor UI (+ scss)
//    create_proxy_nametag   billboarded owner-name tag above a networked player
//    create_combo_meter     combo counter + decay + multiplier (.cs) + Razor HUD
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier) and ScaffoldHelpers (PrepareCodeFile / WriteCode /
//  Utf8NoBom). Handler code here is UNSANDBOXED editor code (System.* fine).
//
//  The strings these handlers WRITE TO DISK are SANDBOXED game code (.cs) and
//  Razor (.razor / .razor.scss) and must obey the sandbox + razor_lint rules:
//    - sealed Component; [Property] tunables; System.Math/MathF/MathX all fine;
//      Array.Clone() blocked (not used); fully-qualified generic collections.
//    - Razor: PanelComponent overrides BuildHash folding the visible state,
//      NO switch-expressions in @code, NO non-ASCII anywhere in @code, SCSS
//      root is a class selector (modeled on CreateLeaderboardPanelHandler).
//    - InvariantCulture float formatting with an 'f' suffix.
//    - TimeSince / RealTimeSince timers.
//
//  Live-verified API surface before codegen (describe_type / search_types):
//    - Sandbox.WorldPanel (Renderer): PanelSize, RenderScale, LookAtCamera,
//      InteractionRange -- the world-space render surface. PanelComponent has NO
//      world-panel mode of its own, so a diegetic UI = WorldPanel + PanelComponent
//      on the same GameObject.
//    - Sandbox.WorldInput (Component): LeftMouseAction / RightMouseAction (String),
//      VRHandSource, Hovered (Panel, read-only). It exposes NO writable Ray /
//      MouseLeftPressed on this SDK -- it drives itself from the camera + the named
//      input actions. A WorldPanel's @onclick only fires when a WorldInput exists.
//    - GameObject+NetworkAccessor: Owner AND OwnerConnection (both Connection),
//      IsProxy, IsOwner. Connection.DisplayName (String, read-only).
//    - TextRenderer (Text / Color / FontSize) -- reused from the Game Feel pack.
//
//  Register(...) lines + the _sceneMutatingCommands additions live in
//  MyEditorMenu.cs (Batch 46) to keep the files decoupled.
// =============================================================================

/// <summary>
/// Shared output-path resolver for the UI/feedback Razor scaffolds -- mirrors
/// ScaffoldHelpers.PrepareCodeFile but for arbitrary file names (.razor / .scss),
/// so create_worldpanel_ui / create_combo_meter can emit paired files.
/// </summary>
internal static class UiFeedbackHelpers
{
	public static bool Resolve( string directory, string fileName, out string fullPath, out string relPath, out object error )
	{
		fullPath = null; relPath = null; error = null;
		if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out fullPath, out var pathErr ) )
		{
			error = new { error = pathErr };
			return false;
		}
		if ( File.Exists( fullPath ) )
		{
			error = new { error = $"File already exists: {directory}/{fileName}. Choose a different name." };
			return false;
		}
		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		relPath = $"{directory}/{fileName}";
		return true;
	}
}

// -----------------------------------------------------------------------------
// create_worldpanel_ui -- a diegetic, clickable world-space UI: a Razor
// PanelComponent (+ .razor.scss) meant to sit on a GameObject that ALSO carries
// a Sandbox.WorldPanel. Example buttons wire to @onclick and raise a static
// OnButtonPressed(string id) so game code reacts without touching the panel.
// -----------------------------------------------------------------------------
public class CreateWorldPanelUiHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var name      = p.TryGetProperty( "name",      out var n ) && !string.IsNullOrWhiteSpace( n.GetString() ) ? n.GetString() : "WorldPanelUi";
			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code/UI";
			var title     = p.TryGetProperty( "title",     out var t ) && !string.IsNullOrWhiteSpace( t.GetString() ) ? t.GetString() : "Interact";

			var className = ClaudeBridge.SanitizeIdentifier( name.EndsWith( ".razor" ) ? Path.GetFileNameWithoutExtension( name ) : name );
			var razorFile = className + ".razor";
			var scssFile  = className + ".razor.scss";

			if ( !UiFeedbackHelpers.Resolve( directory, razorFile, out var razorPath, out var relRazor, out var rerr ) )
				return Task.FromResult<object>( rerr );
			if ( !UiFeedbackHelpers.Resolve( directory, scssFile, out var scssPath, out var relScss, out var serr ) )
				return Task.FromResult<object>( serr );

			// Escape any embedded double-quotes so they can't break the generated literals.
			var safeTitle = title.Replace( "\"", "" );

			ScaffoldHelpers.WriteCode( razorPath, BuildRazor( className, safeTitle ) );
			ScaffoldHelpers.WriteCode( scssPath, BuildScss() );

			return Task.FromResult<object>( new
			{
				created = true,
				razorPath = relRazor,
				scssPath = relScss,
				className,
				note = "Diegetic UI needs BOTH a Sandbox.WorldPanel (the world-space surface) AND this PanelComponent on the SAME GameObject, PLUS a Sandbox.WorldInput somewhere in the scene or clicks never register.",
				nextSteps = new[]
				{
					"trigger_hotload to compile the panel into the game assembly.",
					$"Create a GameObject, then add_world_panel to it (the world-space render surface: PanelSize / RenderScale / InteractionRange).",
					$"Add {className} to that SAME GameObject: add_component_with_properties (component=\"{className}\") after the hotload. The WorldPanel renders whatever PanelComponent shares its object.",
					"Add a Sandbox.WorldInput to the scene (usually on the camera or the player) and set its LeftMouseAction to your click action (e.g. \"attack1\"). On this SDK WorldInput drives itself from the camera + that action -- no manual ray. Without a WorldInput, the buttons' @onclick never fires.",
					$"React from ANY game code without editing the panel: {className}.OnButtonPressed += id => Log.Info( id ); -- the example buttons raise ids \"one\" / \"two\".",
					"Verify in play mode: enter play, aim at the panel, click, and confirm your OnButtonPressed handler fires (or watch WorldInput.Hovered light up the panel)."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_worldpanel_ui failed: {ex.Message}" } );
		}
	}

	static string BuildRazor( string className, string title )
	{
		// NOTE: all {{ }} inside the $@"" are C# string-escape doubled braces.
		// The generated .razor uses single { } for Razor/C# expressions.
		return $@"@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent;

@* {className} -- diegetic, clickable world-space UI.
   Place this PanelComponent on a GameObject that ALSO has a Sandbox.WorldPanel
   component -- the WorldPanel is the world-space render surface (PanelSize /
   RenderScale / InteractionRange); this component is the actual UI it renders.

   CLICKS ONLY WORK WITH A WORLDINPUT. Add a Sandbox.WorldInput to the scene
   (typically on the camera or the player) and set its LeftMouseAction to your
   click input action (e.g. attack1). On this SDK WorldInput drives itself from
   the camera plus the named input actions -- there is no manual ray to feed.
   Without a WorldInput present, the @onclick handlers below never fire.
   WorldInput.Hovered (read-only) reflects the panel currently under the cursor.

   React from game code WITHOUT editing this panel by subscribing to the static
   event, e.g.  {className}.OnButtonPressed += id => Log.Info( id ); *@

<div class=""wp-root"">
	<div class=""wp-panel"">
		<div class=""title"">@Title</div>
		<div class=""buttons"">
			<button class=""btn"" onclick=@( () => Press( ""one"" ) )>Option One</button>
			<button class=""btn"" onclick=@( () => Press( ""two"" ) )>Option Two</button>
		</div>
		<div class=""hint"">Last pressed: @_last</div>
	</div>
</div>

@code {{
	[Property] public string Title {{ get; set; }} = ""{title}"";

	/// Fires when any button is pressed, with the button id. Subscribe from game
	/// code so you never have to edit this panel.
	public static event System.Action<string> OnButtonPressed;

	private string _last = ""none"";

	private void Press( string id )
	{{
		_last = id;
		OnButtonPressed?.Invoke( id );
	}}

	protected override int BuildHash() => System.HashCode.Combine( Title, _last );
}}
";
	}

	static string BuildScss()
	{
		return @".wp-root {
	display: flex;
	width: 100%;
	height: 100%;
	justify-content: center;
	align-items: center;
}

.wp-panel {
	display: flex;
	flex-direction: column;
	align-items: center;
	background-color: rgba(0,0,0,0.8);
	border: 2px solid rgba(255,255,255,0.15);
	border-radius: 12px;
	padding: 24px;
	min-width: 360px;
}

.title {
	font-size: 32px;
	font-weight: bold;
	color: white;
	margin-bottom: 16px;
}

.buttons {
	display: flex;
	flex-direction: column;
	width: 100%;
}

.btn {
	background-color: rgba(80,140,255,0.85);
	color: white;
	font-size: 22px;
	text-align: center;
	padding: 12px 20px;
	margin: 6px 0;
	border-radius: 8px;
}

.btn:hover {
	background-color: rgba(120,180,255,1);
}

.hint {
	font-size: 16px;
	color: rgba(255,255,255,0.5);
	margin-top: 12px;
}
";
	}
}

// -----------------------------------------------------------------------------
// create_proxy_nametag -- a sealed Component that floats the OWNER's display
// name above a networked player. TextRenderer-based (justified in the header
// comment of the generated code): a nametag is one short string with a distance
// fade, so a TextRenderer on a managed child object is far simpler than the
// WorldPanel + Razor PanelComponent + WorldInput stack -- no UI assets, no panel
// host, and per-frame alpha is a one-liner. Mirrors create_floating_combat_text.
// -----------------------------------------------------------------------------
public class CreateProxyNametagHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "ProxyNametag", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float maxDistance  = p.TryGetProperty( "maxDistance",  out var mv ) && mv.TryGetSingle( out var mf ) ? mf : 2000f;
			float heightOffset = p.TryGetProperty( "heightOffset", out var hv ) && hv.TryGetSingle( out var hf ) ? hf : 72f;
			if ( maxDistance < 1f )    maxDistance = 1f;
			if ( heightOffset < 0f )   heightOffset = 0f;

			var code = BuildCode( className, maxDistance, heightOffset, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				maxDistance,
				heightOffset,
				note = "Renders only on OTHER clients' copies (GameObject.Network.IsProxy == true), so you never see your own tag. Offline / no networking => IsProxy is false everywhere => no tags (expected).",
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					$"Attach it to the ROOT of your networked player object (the one you NetworkSpawn): add_component_with_properties (component=\"{className}\") after the hotload.",
					"Pairs with create_networked_player -- add this to the generated player prefab so every remote player is labeled.",
					"It spawns a child GameObject holding a TextRenderer so billboarding the text never rotates the player model; the child is cleaned up on disable.",
					"Tune MaxDistance (fade range) / HeightOffset / FontSize with set_property. Verify with two connections, or inspect_networked_object to confirm Owner.DisplayName resolves."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_proxy_nametag failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float maxDistance, float heightOffset, System.Globalization.CultureInfo ci )
	{
		string md = maxDistance.ToString( ci ) + "f";
		string ho = heightOffset.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- billboarded nametag showing the OWNER's display name above a
/// networked player.
///
/// WHY TextRenderer (not WorldPanel): a nametag is a single short string with a
/// distance fade, so a TextRenderer on a managed child object is far simpler than
/// a WorldPanel + Razor PanelComponent + WorldInput stack -- no UI assets, no
/// panel host, and per-frame alpha is a one-liner. (Same call as the
/// create_floating_combat_text scaffold, which is also TextRenderer-based.)
///
/// Attach to the ROOT of a networked player object (the one you NetworkSpawn). It
/// spawns a CHILD holding the TextRenderer so billboarding never rotates the
/// player model.
///
/// Visibility is the INVERSE of the usual proxy guard: the tag renders only when
/// GameObject.Network.IsProxy is true -- i.e. on OTHER clients' copies of this
/// player -- so you never see a tag over your own head. Offline / no networking
/// means IsProxy is false everywhere, so no tags show (expected).
/// </summary>
public sealed class {className} : Component
{{
	/// Height above the object's origin to float the tag, in world units.
	[Property] public float HeightOffset {{ get; set; }} = {ho};

	/// Full alpha up close; fades to zero as the camera approaches this distance,
	/// and is hidden past it. In world units.
	[Property] public float MaxDistance {{ get; set; }} = {md};

	/// Base text size.
	[Property] public float FontSize {{ get; set; }} = 22f;

	private GameObject _tagObject;
	private TextRenderer _text;

	protected override void OnStart()
	{{
		_tagObject = new GameObject( true, ""Nametag"" );
		_tagObject.SetParent( GameObject, false );

		_text = _tagObject.AddComponent<TextRenderer>();
		_text.Text = ResolveName();
		_text.FontSize = FontSize;
		_text.Color = Color.White;
	}}

	protected override void OnDisabled()
	{{
		if ( _tagObject != null )
		{{
			_tagObject.Destroy();
			_tagObject = null;
		}}
		_text = null;
	}}

	protected override void OnUpdate()
	{{
		if ( _tagObject == null || _text == null ) return;

		// Only render over OTHER players' copies of this object -- never your own.
		if ( !GameObject.Network.IsProxy )
		{{
			_tagObject.Enabled = false;
			return;
		}}

		var cam = Scene?.Camera;
		if ( cam == null )
		{{
			_tagObject.Enabled = false;
			return;
		}}

		var headPos = GameObject.WorldPosition + Vector3.Up * HeightOffset;
		float dist = (cam.WorldPosition - headPos).Length;
		float alpha = MaxDistance > 0f ? MathX.Clamp( 1f - dist / MaxDistance, 0f, 1f ) : 1f;

		if ( alpha <= 0.001f )
		{{
			_tagObject.Enabled = false;
			return;
		}}

		_tagObject.Enabled = true;
		_tagObject.WorldPosition = headPos;

		// Billboard: face the same way the camera faces, mirrored toward it.
		_tagObject.WorldRotation = Rotation.LookAt( -cam.WorldRotation.Forward );

		_text.Text = ResolveName();
		_text.Color = Color.White.WithAlpha( alpha );
	}}

	private string ResolveName()
	{{
		// Owner and OwnerConnection are the same Connection on this SDK; Owner is
		// the shorter alias. Fall back to the object name when offline.
		var owner = GameObject.Network.Owner;
		if ( owner != null && !string.IsNullOrEmpty( owner.DisplayName ) )
			return owner.DisplayName;
		return GameObject.Name;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_combo_meter -- a sealed Component combo system (static Bump(), decay
// window, multiplier tiers, static OnComboChanged event) PLUS a small Razor HUD
// (PanelComponent + scss) that shows "12 HITS x3" and pulses on every change.
// Three files: {name}.cs + {name}Hud.razor + {name}Hud.razor.scss.
// -----------------------------------------------------------------------------
public class CreateComboMeterHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			var name      = p.TryGetProperty( "name",      out var n ) && !string.IsNullOrWhiteSpace( n.GetString() ) ? n.GetString() : "ComboMeter";
			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code";

			float comboWindow = p.TryGetProperty( "comboWindowSeconds", out var cv ) && cv.TryGetSingle( out var cf ) ? cf : 3f;
			if ( comboWindow < 0.25f ) comboWindow = 0.25f;

			var className = ClaudeBridge.SanitizeIdentifier( name.EndsWith( ".cs" ) ? Path.GetFileNameWithoutExtension( name ) : name );
			var hudClass  = className + "Hud";

			var csFile    = className + ".cs";
			var razorFile = hudClass + ".razor";
			var scssFile  = hudClass + ".razor.scss";

			if ( !UiFeedbackHelpers.Resolve( directory, csFile, out var csPath, out var relCs, out var cerr ) )
				return Task.FromResult<object>( cerr );
			if ( !UiFeedbackHelpers.Resolve( directory, razorFile, out var razorPath, out var relRazor, out var rerr ) )
				return Task.FromResult<object>( rerr );
			if ( !UiFeedbackHelpers.Resolve( directory, scssFile, out var scssPath, out var relScss, out var serr ) )
				return Task.FromResult<object>( serr );

			ScaffoldHelpers.WriteCode( csPath, BuildComponent( className, comboWindow, ci ) );
			ScaffoldHelpers.WriteCode( razorPath, BuildHudRazor( hudClass, className ) );
			ScaffoldHelpers.WriteCode( scssPath, BuildHudScss() );

			return Task.FromResult<object>( new
			{
				created = true,
				componentPath = relCs,
				razorPath = relRazor,
				scssPath = relScss,
				className,
				hudClassName = hudClass,
				comboWindowSeconds = comboWindow,
				note = "The .cs is the authoritative combo state (headless, static Bump()); the HUD PanelComponent is optional UI that subscribes to the static OnComboChanged event.",
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} + {hudClass} into the game assembly.",
					$"Attach {className} to ONE persistent object (your game manager / HUD root): add_component_with_properties (component=\"{className}\") after the hotload.",
					$"Register a hit from ANY game code: {className}.Bump(); -- the count rises, the multiplier steps up the Tier2/3/4 thresholds, and an idle ComboWindowSeconds resets it.",
					$"Show the readout: add {hudClass} under a ScreenPanel (add_screen_panel) -- it displays \"<count> HITS x<mult>\" and pulses on every change.",
					"Pairs with create_health_system / create_floating_combat_text: call Bump() from the damage path and Spawn a popup that reflects the multiplier.",
					"Tune ComboWindowSeconds / Tier2Hits / Tier3Hits / Tier4Hits with set_property."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_combo_meter failed: {ex.Message}" } );
		}
	}

	static string BuildComponent( string className, float comboWindow, System.Globalization.CultureInfo ci )
	{
		string cw = comboWindow.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- a hit-combo system. Call the static Bump() on every hit: the
/// combo Count rises, an idle window (ComboWindowSeconds, tracked with TimeSince)
/// resets it, and the Multiplier steps up through the Tier2/3/4 thresholds
/// (2x / 3x / 4x). Fires the static OnComboChanged(count, multiplier) event so any
/// HUD or audio reacts without holding a reference.
///
/// LOCAL/gameplay-only. Pairs with create_health_system / create_floating_combat_text:
/// call {className}.Bump() from the damage path and Spawn a popup with the multiplier.
///
/// Bump() is static and targets the active instance, so callers never need a
/// handle. Attach ONE instance to a persistent object (game manager / HUD root).
/// </summary>
public sealed class {className} : Component
{{
	/// Idle seconds before the combo resets back to zero.
	[Property] public float ComboWindowSeconds {{ get; set; }} = {cw};

	/// Hit count at/above which the multiplier becomes 2x.
	[Property] public int Tier2Hits {{ get; set; }} = 5;

	/// Hit count at/above which the multiplier becomes 3x.
	[Property] public int Tier3Hits {{ get; set; }} = 10;

	/// Hit count at/above which the multiplier becomes 4x.
	[Property] public int Tier4Hits {{ get; set; }} = 20;

	/// Live combo count (read-only for HUDs).
	public int Count {{ get; private set; }}

	/// Live multiplier from the tier thresholds (read-only for HUDs).
	public float Multiplier {{ get; private set; }} = 1f;

	/// Fires whenever Count or Multiplier changes: (count, multiplier).
	public static event Action<int, float> OnComboChanged;

	private TimeSince _sinceLastHit;
	private static {className} _active;

	protected override void OnEnabled()
	{{
		_active = this;
	}}

	protected override void OnDisabled()
	{{
		if ( _active == this ) _active = null;
	}}

	/// <summary>Register a hit on the active combo meter. No-op if none is enabled.</summary>
	public static void Bump()
	{{
		_active?.Hit();
	}}

	private void Hit()
	{{
		Count++;
		_sinceLastHit = 0f;
		Multiplier = MultiplierFor( Count );
		OnComboChanged?.Invoke( Count, Multiplier );
	}}

	protected override void OnUpdate()
	{{
		if ( Count > 0 && _sinceLastHit >= ComboWindowSeconds )
			ResetCombo();
	}}

	/// <summary>Force the combo back to zero (e.g. on a miss or the player's death).</summary>
	public void ResetCombo()
	{{
		if ( Count == 0 ) return;
		Count = 0;
		Multiplier = 1f;
		OnComboChanged?.Invoke( Count, Multiplier );
	}}

	private float MultiplierFor( int count )
	{{
		// Plain if-ladder -- kept sandbox/razor friendly and easy to retune.
		if ( count >= Tier4Hits ) return 4f;
		if ( count >= Tier3Hits ) return 3f;
		if ( count >= Tier2Hits ) return 2f;
		return 1f;
	}}
}}
";
	}

	static string BuildHudRazor( string hudClass, string comboClass )
	{
		return $@"@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent;

@* {hudClass} -- HUD readout for {comboClass}. Subscribes to the static
   OnComboChanged event and shows <count> HITS x<mult>, pulsing on every change.
   Host it under a ScreenPanel (add_screen_panel). *@

<div class=""combo @VisClass @PulseClass"">
	<span class=""count"">@_count</span>
	<span class=""hits"">HITS</span>
	<span class=""mult"">@MultText</span>
</div>

@code {{
	private int _count = 0;
	private float _mult = 1f;
	private RealTimeSince _sinceChange = 1f;

	protected override void OnEnabled()
	{{
		base.OnEnabled();
		{comboClass}.OnComboChanged += HandleChanged;
	}}

	protected override void OnDisabled()
	{{
		{comboClass}.OnComboChanged -= HandleChanged;
		base.OnDisabled();
	}}

	private void HandleChanged( int count, float mult )
	{{
		_count = count;
		_mult = mult;
		_sinceChange = 0f;
		StateHasChanged();
	}}

	private string MultText => ""x"" + ( (int) _mult ).ToString();

	private string VisClass => _count > 0 ? ""show"" : ""hide"";

	// Alternate the class name each hit so the CSS animation retriggers even on a
	// back-to-back combo. Count always changes per hit, so parity flips every time.
	private string PulseClass
	{{
		get
		{{
			if ( _sinceChange > 0.2f ) return """";
			return ( _count % 2 == 0 ) ? ""pulse-a"" : ""pulse-b"";
		}}
	}}

	protected override int BuildHash() => System.HashCode.Combine( _count, _mult, _sinceChange < 0.2f );
}}
";
	}

	static string BuildHudScss()
	{
		return @".combo {
	display: flex;
	flex-direction: row;
	align-items: center;
	position: absolute;
	top: 40px;
	right: 40px;
	padding: 8px 16px;
	background-color: rgba(0,0,0,0.55);
	border-radius: 8px;
	transition: opacity 0.25s;
}

.combo.show { opacity: 1; }
.combo.hide { opacity: 0; }

.count {
	font-size: 48px;
	font-weight: bold;
	color: #ffe080;
}

.hits {
	font-size: 20px;
	color: white;
	margin: 0 8px;
	align-self: center;
}

.mult {
	font-size: 32px;
	font-weight: bold;
	color: #ff8040;
}

.pulse-a {
	animation: comboPulse 0.25s ease-out;
}

.pulse-b {
	animation: comboPulse 0.25s ease-out;
}

@keyframes comboPulse {
	0% { transform: scale(1); }
	40% { transform: scale(1.35); }
	100% { transform: scale(1); }
}
";
	}
}
