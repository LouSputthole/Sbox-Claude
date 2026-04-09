using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a new material file in the project with specified shader and properties.
/// </summary>
public class CreateMaterialHandler : ICommandHandler
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

		var shader = parameters.TryGetProperty( "shader", out var shaderProp )
			? shaderProp.GetString() ?? "shaders/complex.shader"
			: "shaders/complex.shader";

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, path ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		// Ensure .vmat extension
		if ( !path.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
			path += ".vmat";
		fullPath = Path.GetFullPath( Path.Combine( projectRoot, path ) );

		var dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		// Build material JSON
		var sb = new StringBuilder();
		sb.AppendLine( "{" );
		sb.AppendLine( $"  \"Shader\": \"{shader}\"," );
		sb.AppendLine( "  \"Properties\": {" );

		// Set properties if provided
		if ( parameters.TryGetProperty( "properties", out var propsProp ) &&
		     propsProp.ValueKind == JsonValueKind.Object )
		{
			var entries = new System.Collections.Generic.List<string>();
			foreach ( var kvp in propsProp.EnumerateObject() )
			{
				// Use JsonSerializer for safe escaping of keys and string values
				var escapedKey = JsonSerializer.Serialize( kvp.Name );
				var val = kvp.Value.ValueKind == JsonValueKind.String
					? JsonSerializer.Serialize( kvp.Value.GetString() )
					: kvp.Value.GetRawText();
				entries.Add( $"    {escapedKey}: {val}" );
			}
			sb.AppendLine( string.Join( ",\n", entries ) );
		}

		sb.AppendLine( "  }" );
		sb.AppendLine( "}" );

		File.WriteAllText( fullPath, sb.ToString() );

		return Task.FromResult<object>( new
		{
			path,
			shader,
			created = true,
		} );
	}
}
