using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Enables or disables a GameObject in the active scene.
/// </summary>
public class SetEnabledHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var enabled = parameters.GetProperty( "enabled" ).GetBoolean();

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		go.Enabled = enabled;

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			enabled = go.Enabled,
		} );
	}
}
