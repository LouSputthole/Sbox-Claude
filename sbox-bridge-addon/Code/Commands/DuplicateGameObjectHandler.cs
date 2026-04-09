using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Duplicates an existing GameObject (with all components) in the active scene.
/// </summary>
public class DuplicateGameObjectHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var original = scene.Directory.FindByGuid( guid );
		if ( original == null )
			throw new Exception( $"GameObject not found: {id}" );

		var clone = original.Clone();

		// Optional: offset position so it doesn't overlap
		if ( parameters.TryGetProperty( "offset", out var offsetProp ) )
		{
			clone.WorldPosition += CreateGameObjectHandler.ParseVector3( offsetProp );
		}

		// Optional: new name
		if ( parameters.TryGetProperty( "name", out var nameProp ) )
		{
			var newName = nameProp.GetString();
			if ( !string.IsNullOrEmpty( newName ) )
				clone.Name = newName;
		}

		return Task.FromResult<object>( new
		{
			originalId = id,
			newId = clone.Id.ToString(),
			name = clone.Name,
			position = CreateGameObjectHandler.FormatVector3( clone.WorldPosition ),
			duplicated = true,
		} );
	}
}
