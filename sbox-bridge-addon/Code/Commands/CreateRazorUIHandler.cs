using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a Razor UI component file (.razor) with optional SCSS stylesheet.
/// Generates boilerplate for a PanelComponent with configurable properties.
/// </summary>
public class CreateRazorUIHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var name = parameters.GetProperty( "name" ).GetString()
			?? throw new Exception( "Missing required parameter: name" );

		var directory = parameters.TryGetProperty( "directory", out var dirProp )
			? dirProp.GetString() ?? "UI" : "UI";

		var description = parameters.TryGetProperty( "description", out var descProp )
			? descProp.GetString() ?? "" : "";

		var includeStyles = !parameters.TryGetProperty( "includeStyles", out var styleProp )
			|| styleProp.GetBoolean();

		// Build the .razor file
		var razorPath = $"code/{directory}/{name}.razor";
		var fullRazorPath = Path.GetFullPath( Path.Combine( projectRoot, razorPath ) );
		if ( !fullRazorPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		var dir = Path.GetDirectoryName( fullRazorPath );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		// If raw content is provided, use that directly
		if ( parameters.TryGetProperty( "content", out var contentProp ) )
		{
			File.WriteAllText( fullRazorPath, contentProp.GetString() ?? "" );

			if ( includeStyles && parameters.TryGetProperty( "styles", out var stylesProp ) )
			{
				var scssPath = $"code/{directory}/{name}.razor.scss";
				var fullScssPath = Path.GetFullPath( Path.Combine( projectRoot, scssPath ) );
				if ( !fullScssPath.StartsWith( projectRoot ) )
					throw new Exception( "SCSS path must be within the project directory" );
				File.WriteAllText( fullScssPath, stylesProp.GetString() ?? "" );
			}

			return Task.FromResult<object>( new
			{
				razorPath,
				name,
				generated = false,
				created = true,
			} );
		}

		// Generate boilerplate
		var sb = new StringBuilder();
		sb.AppendLine( "@using Sandbox;" );
		sb.AppendLine( "@using Sandbox.UI;" );
		sb.AppendLine( "@inherits PanelComponent" );
		sb.AppendLine();
		sb.AppendLine( "<root>" );

		// Add HTML structure based on type
		var panelType = parameters.TryGetProperty( "panelType", out var typeProp )
			? typeProp.GetString() ?? "basic" : "basic";

		switch ( panelType.ToLowerInvariant() )
		{
			case "hud":
				sb.AppendLine( "\t<div class=\"hud-container\">" );
				sb.AppendLine( "\t\t<div class=\"health\">Health: @Health</div>" );
				sb.AppendLine( "\t\t<div class=\"score\">Score: @Score</div>" );
				sb.AppendLine( "\t</div>" );
				break;
			case "menu":
				sb.AppendLine( "\t<div class=\"menu-container\">" );
				sb.AppendLine( "\t\t<h1>@Title</h1>" );
				sb.AppendLine( "\t\t<div class=\"menu-buttons\">" );
				sb.AppendLine( "\t\t\t<button onclick=\"@OnPlay\">Play</button>" );
				sb.AppendLine( "\t\t\t<button onclick=\"@OnQuit\">Quit</button>" );
				sb.AppendLine( "\t\t</div>" );
				sb.AppendLine( "\t</div>" );
				break;
			default:
				sb.AppendLine( "\t<div class=\"panel-content\">" );
				if ( !string.IsNullOrEmpty( description ) )
					sb.AppendLine( $"\t\t<label>@Text</label>" );
				sb.AppendLine( "\t</div>" );
				break;
		}

		sb.AppendLine( "</root>" );
		sb.AppendLine();
		sb.AppendLine( "@code {" );

		switch ( panelType.ToLowerInvariant() )
		{
			case "hud":
				sb.AppendLine( "\t[Property] public float Health { get; set; } = 100f;" );
				sb.AppendLine( "\t[Property] public int Score { get; set; }" );
				break;
			case "menu":
				sb.AppendLine( "\t[Property] public string Title { get; set; } = \"Game Menu\";" );
				sb.AppendLine();
				sb.AppendLine( "\tvoid OnPlay() { /* Start game logic */ }" );
				sb.AppendLine( "\tvoid OnQuit() { Game.Close(); }" );
				break;
			default:
				sb.AppendLine( "\t[Property] public string Text { get; set; } = \"Hello World\";" );
				break;
		}

		sb.AppendLine( "}" );

		File.WriteAllText( fullRazorPath, sb.ToString() );

		// Generate SCSS file if requested
		string scssPathResult = null;
		if ( includeStyles )
		{
			var scssRelPath = $"code/{directory}/{name}.razor.scss";
			var fullScss = Path.GetFullPath( Path.Combine( projectRoot, scssRelPath ) );
			if ( !fullScss.StartsWith( projectRoot ) )
				throw new Exception( "SCSS path must be within the project directory" );

			var scss = new StringBuilder();
			scss.AppendLine( $"// Styles for {name} component" );
			scss.AppendLine();
			switch ( panelType.ToLowerInvariant() )
			{
				case "hud":
					scss.AppendLine( ".hud-container {" );
					scss.AppendLine( "\tposition: absolute;" );
					scss.AppendLine( "\ttop: 20px;" );
					scss.AppendLine( "\tleft: 20px;" );
					scss.AppendLine( "\tfont-family: Poppins;" );
					scss.AppendLine( "\tcolor: white;" );
					scss.AppendLine( "\tfont-size: 24px;" );
					scss.AppendLine( "}" );
					break;
				case "menu":
					scss.AppendLine( ".menu-container {" );
					scss.AppendLine( "\tposition: absolute;" );
					scss.AppendLine( "\ttop: 0; left: 0; right: 0; bottom: 0;" );
					scss.AppendLine( "\tdisplay: flex;" );
					scss.AppendLine( "\tflex-direction: column;" );
					scss.AppendLine( "\talign-items: center;" );
					scss.AppendLine( "\tjustify-content: center;" );
					scss.AppendLine( "\tbackground-color: rgba(0, 0, 0, 0.8);" );
					scss.AppendLine( "\tcolor: white;" );
					scss.AppendLine( "}" );
					break;
				default:
					scss.AppendLine( ".panel-content {" );
					scss.AppendLine( "\tpadding: 16px;" );
					scss.AppendLine( "\tcolor: white;" );
					scss.AppendLine( "}" );
					break;
			}

			File.WriteAllText( fullScss, scss.ToString() );
			scssPathResult = scssRelPath;
		}

		return Task.FromResult<object>( new
		{
			razorPath,
			scssPath = scssPathResult,
			name,
			panelType,
			generated = true,
			created = true,
		} );
	}
}
