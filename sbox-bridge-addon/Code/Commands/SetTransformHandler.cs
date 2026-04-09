using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Sets position, rotation, and/or scale on a GameObject.
/// Any combination can be provided — only specified values are changed.
/// </summary>
public class SetTransformHandler : ICommandHandler
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

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		var useLocal = parameters.TryGetProperty( "local", out var localProp ) && localProp.GetBoolean();

		if ( parameters.TryGetProperty( "position", out var posProp ) )
		{
			var pos = CreateGameObjectHandler.ParseVector3( posProp );
			if ( useLocal )
				go.LocalPosition = pos;
			else
				go.WorldPosition = pos;
		}

		if ( parameters.TryGetProperty( "rotation", out var rotProp ) )
		{
			var rot = CreateGameObjectHandler.ParseRotation( rotProp );
			if ( useLocal )
				go.LocalRotation = rot;
			else
				go.WorldRotation = rot;
		}

		if ( parameters.TryGetProperty( "scale", out var scaleProp ) )
		{
			if ( scaleProp.ValueKind == JsonValueKind.Number )
			{
				var uniform = scaleProp.GetSingle();
				var scaleVec = new Vector3( uniform, uniform, uniform );
				if ( useLocal )
					go.LocalScale = scaleVec;
				else
					go.WorldScale = scaleVec;
			}
			else
			{
				var scaleVec = CreateGameObjectHandler.ParseVector3( scaleProp );
				if ( useLocal )
					go.LocalScale = scaleVec;
				else
					go.WorldScale = scaleVec;
			}
		}

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			worldPosition = CreateGameObjectHandler.FormatVector3( go.WorldPosition ),
			worldRotation = CreateGameObjectHandler.FormatRotation( go.WorldRotation ),
			worldScale = CreateGameObjectHandler.FormatVector3( go.WorldScale ),
			localPosition = CreateGameObjectHandler.FormatVector3( go.LocalPosition ),
			localRotation = CreateGameObjectHandler.FormatRotation( go.LocalRotation ),
		} );
	}
}
