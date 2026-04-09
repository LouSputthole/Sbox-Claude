using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Renames a GameObject in the active scene.
/// </summary>
public class RenameGameObjectHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var newName = parameters.GetProperty( "name" ).GetString()
			?? throw new Exception( "Missing required parameter: name" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		var oldName = go.Name;
		go.Name = newName;

		return Task.FromResult<object>( new
		{
			id,
			oldName,
			newName = go.Name,
			renamed = true,
		} );
	}
}
