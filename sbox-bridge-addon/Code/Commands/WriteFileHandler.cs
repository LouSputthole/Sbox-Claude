using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Writes content to a file in the project. Creates directories as needed.
/// </summary>
public class WriteFileHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new System.Exception( "No project is currently open" );

		// Ensure trailing separator for safe StartsWith check
		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var relativePath = parameters.GetProperty( "path" ).GetString()
			?? throw new System.Exception( "Missing required parameter: path" );
		var content = parameters.GetProperty( "content" ).GetString()
			?? throw new System.Exception( "Missing required parameter: content" );

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );

		// Security: ensure the path stays within the project
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new System.Exception( "Path must be within the project directory" );

		// Create parent directories if they don't exist
		var directory = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( directory ) )
			Directory.CreateDirectory( directory );

		var existed = File.Exists( fullPath );
		File.WriteAllText( fullPath, content );

		return Task.FromResult<object>( new
		{
			path = relativePath,
			bytesWritten = System.Text.Encoding.UTF8.GetByteCount( content ),
			created = !existed,
		} );
	}
}
