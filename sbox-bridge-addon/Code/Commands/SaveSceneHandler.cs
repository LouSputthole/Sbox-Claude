using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Saves the currently open scene in the s&box editor.
/// </summary>
public class SaveSceneHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = EditorScene.Active;
		if ( scene == null )
			throw new System.Exception( "No scene is currently open" );

		// Save to a specific path or the current path
		if ( parameters.TryGetProperty( "path", out var pathProp ) )
		{
			var path = pathProp.GetString();
			if ( !string.IsNullOrEmpty( path ) )
			{
				// Validate path stays within project directory
				var projectRoot = Project.Current?.GetRootPath() ?? "";
				if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
					projectRoot += Path.DirectorySeparatorChar;

				var fullPath = Path.GetFullPath( Path.Combine( projectRoot, path ) );
				if ( !fullPath.StartsWith( projectRoot ) )
					throw new System.Exception( "Path must be within the project directory" );

				EditorScene.SaveAs( path );
				return Task.FromResult<object>( new
				{
					path,
					saved = true,
				} );
			}
		}

		EditorScene.Save();

		return Task.FromResult<object>( new
		{
			saved = true,
		} );
	}
}
