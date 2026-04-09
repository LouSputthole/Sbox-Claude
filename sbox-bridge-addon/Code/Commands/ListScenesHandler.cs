using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Lists all .scene files in the project.
/// </summary>
public class ListScenesHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new System.Exception( "No project is currently open" );

		var scenes = new System.Collections.Generic.List<object>();

		// Search the entire project for .scene files
		var sceneFiles = Directory.Exists( projectRoot )
			? Directory.GetFiles( projectRoot, "*.scene", SearchOption.AllDirectories )
			: System.Array.Empty<string>();

		foreach ( var file in sceneFiles )
		{
			var rel = Path.GetRelativePath( projectRoot, file ).Replace( '\\', '/' );
			var info = new FileInfo( file );

			scenes.Add( new
			{
				path = rel,
				name = Path.GetFileNameWithoutExtension( file ),
				size = info.Length,
				lastModified = info.LastWriteTimeUtc.ToString( "o" ),
			} );
		}

		return Task.FromResult<object>( new
		{
			count = scenes.Count,
			scenes = scenes.OrderBy( s => JsonSerializer.Serialize( s ) ).ToList(),
		} );
	}
}
