using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Reparents a GameObject to a new parent (or to scene root if parent is null).
/// </summary>
public class SetParentHandler : ICommandHandler
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

		// If parentId is null/empty, reparent to scene root
		if ( parameters.TryGetProperty( "parentId", out var parentProp ) &&
		     parentProp.ValueKind != JsonValueKind.Null )
		{
			var parentIdStr = parentProp.GetString();
			if ( !string.IsNullOrEmpty( parentIdStr ) && Guid.TryParse( parentIdStr, out var parentGuid ) )
			{
				var parent = scene.Directory.FindByGuid( parentGuid );
				if ( parent == null )
					throw new Exception( $"Parent GameObject not found: {parentIdStr}" );

				go.SetParent( parent );

				return Task.FromResult<object>( new
				{
					id,
					parentId = parentIdStr,
					parentName = parent.Name,
					reparented = true,
				} );
			}
		}

		// Reparent to root
		go.SetParent( null );

		return Task.FromResult<object>( new
		{
			id,
			parentId = (string)null,
			parentName = "Scene Root",
			reparented = true,
		} );
	}
}
