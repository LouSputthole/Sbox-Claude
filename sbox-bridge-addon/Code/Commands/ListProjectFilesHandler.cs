using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Lists files in the project, optionally filtered by directory and extension.
/// </summary>
public class ListProjectFilesHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new System.Exception( "No project is currently open" );

		var relativePath = parameters.TryGetProperty( "path", out var pathProp )
			? pathProp.GetString() ?? ""
			: "";
		var extension = parameters.TryGetProperty( "extension", out var extProp )
			? extProp.GetString()
			: null;
		var recursive = !parameters.TryGetProperty( "recursive", out var recProp )
			|| recProp.GetBoolean();

		// Ensure trailing separator for safe StartsWith check
		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new System.Exception( "Path must be within the project directory" );

		if ( !Directory.Exists( fullPath ) )
			throw new System.Exception( $"Directory not found: {relativePath}" );

		var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
		var pattern = string.IsNullOrEmpty( extension ) ? "*" : $"*{extension}";

		var files = Directory.GetFiles( fullPath, pattern, searchOption )
			.Select( f =>
			{
				var rel = Path.GetRelativePath( projectRoot, f ).Replace( '\\', '/' );
				var info = new FileInfo( f );
				return new
				{
					path = rel,
					name = info.Name,
					extension = info.Extension,
					size = info.Length,
					lastModified = info.LastWriteTimeUtc.ToString( "o" ),
				};
			} )
			.OrderBy( f => f.path )
			.ToList();

		var result = new
		{
			root = relativePath,
			count = files.Count,
			files,
		};

		return Task.FromResult<object>( result );
	}
}
