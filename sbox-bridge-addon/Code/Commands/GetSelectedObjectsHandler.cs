using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Returns the currently selected GameObjects in the s&box editor.
/// </summary>
public class GetSelectedObjectsHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var selection = EditorScene.Selection;
		var objects = new List<object>();

		if ( selection != null )
		{
			foreach ( var go in selection.OfType<GameObject>() )
			{
				var components = go.Components
					.Select( c => c.GetType().Name )
					.ToList();

				objects.Add( new
				{
					id = go.Id.ToString(),
					name = go.Name,
					enabled = go.Enabled,
					position = CreateGameObjectHandler.FormatVector3( go.WorldPosition ),
					components,
				} );
			}
		}

		return Task.FromResult<object>( new
		{
			count = objects.Count,
			selected = objects,
		} );
	}
}
