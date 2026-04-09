using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Moves the editor camera to focus on a specific GameObject.
/// Equivalent to double-clicking an object in the hierarchy.
/// </summary>
public class FocusObjectHandler : ICommandHandler
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

		// Select and focus the object in the editor
		EditorScene.Selection.Clear();
		EditorScene.Selection.Add( go );
		EditorScene.FocusSelection();

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			position = CreateGameObjectHandler.FormatVector3( go.WorldPosition ),
			focused = true,
		} );
	}
}
