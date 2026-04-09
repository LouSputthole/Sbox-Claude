using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Programmatically selects a GameObject in the s&box editor.
/// Supports selecting a single object or adding to the current selection.
/// </summary>
public class SelectObjectHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );

		var addToSelection = parameters.TryGetProperty( "addToSelection", out var addProp )
			&& addProp.GetBoolean();

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		if ( !addToSelection )
		{
			EditorScene.Selection.Clear();
		}

		EditorScene.Selection.Add( go );

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			selected = true,
			addedToExisting = addToSelection,
		} );
	}
}
