using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a new sound event file (.sound) in the project with configurable
/// volume, pitch, distance falloff, and sound reference.
/// </summary>
public class CreateSoundEventHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		// Ensure trailing separator for safe StartsWith check
		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var path = parameters.GetProperty( "path" ).GetString()
			?? throw new Exception( "Missing required parameter: path" );
		var soundPath = parameters.GetProperty( "sound" ).GetString()
			?? throw new Exception( "Missing required parameter: sound" );

		var volume = parameters.TryGetProperty( "volume", out var volProp )
			? volProp.GetSingle() : 1.0f;
		var pitch = parameters.TryGetProperty( "pitch", out var pitchProp )
			? pitchProp.GetSingle() : 1.0f;
		var minDistance = parameters.TryGetProperty( "minDistance", out var minProp )
			? minProp.GetSingle() : 100f;
		var maxDistance = parameters.TryGetProperty( "maxDistance", out var maxProp )
			? maxProp.GetSingle() : 2000f;
		var loop = parameters.TryGetProperty( "loop", out var loopProp )
			&& loopProp.GetBoolean();

		// Ensure .sound extension
		if ( !path.EndsWith( ".sound", StringComparison.OrdinalIgnoreCase ) )
			path += ".sound";

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, path ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		var dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		// Generate sound event JSON using serializer for safe string escaping
		var inv = System.Globalization.CultureInfo.InvariantCulture;
		var escapedSoundPath = JsonSerializer.Serialize( soundPath ); // includes quotes + escaping

		var sb = new StringBuilder();
		sb.AppendLine( "{" );
		sb.AppendLine( "  \"UI\": false," );
		sb.AppendLine( $"  \"Volume\": {volume.ToString( "F2", inv )}," );
		sb.AppendLine( $"  \"Pitch\": {pitch.ToString( "F2", inv )}," );
		sb.AppendLine( "  \"Decibels\": 70," );
		sb.AppendLine( "  \"SelectionMode\": \"Random\"," );
		sb.AppendLine( $"  \"MaximumDistance\": {maxDistance.ToString( "F0", inv )}," );
		sb.AppendLine( $"  \"MinimumDistance\": {minDistance.ToString( "F0", inv )}," );
		sb.AppendLine( $"  \"Looping\": {(loop ? "true" : "false")}," );
		sb.AppendLine( "  \"Sounds\": [" );
		sb.AppendLine( $"    {escapedSoundPath}" );
		sb.AppendLine( "  ]" );
		sb.AppendLine( "}" );

		File.WriteAllText( fullPath, sb.ToString() );

		return Task.FromResult<object>( new
		{
			path,
			sound = soundPath,
			volume,
			pitch,
			loop,
			minDistance,
			maxDistance,
			created = true,
		} );
	}
}
