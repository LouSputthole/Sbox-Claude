using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Returns the full scene hierarchy as a tree structure.
/// Each node includes GUID, name, enabled state, components, and children.
/// This is how Claude "sees" the scene.
/// </summary>
public class GetSceneHierarchyHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var maxDepth = parameters.TryGetProperty( "maxDepth", out var depthProp )
			? depthProp.GetInt32() : 10;

		// Get root-level objects (no parent)
		var rootObjects = scene.GetAllObjects( false )
			.Where( go => go.Parent == null || go.Parent == scene )
			.ToList();

		var tree = rootObjects.Select( go => BuildNode( go, 0, maxDepth ) ).ToList();

		var totalCount = CountNodes( tree );

		return Task.FromResult<object>( new
		{
			sceneName = scene.Name,
			totalObjects = totalCount,
			hierarchy = tree,
		} );
	}

	private static object BuildNode( GameObject go, int depth, int maxDepth )
	{
		var components = go.Components
			.Select( c => new
			{
				type = c.GetType().Name,
				enabled = c.Enabled,
			} )
			.ToList();

		var children = new List<object>();
		if ( depth < maxDepth )
		{
			foreach ( var child in go.Children )
			{
				children.Add( BuildNode( child, depth + 1, maxDepth ) );
			}
		}

		return new
		{
			id = go.Id.ToString(),
			name = go.Name,
			enabled = go.Enabled,
			position = CreateGameObjectHandler.FormatVector3( go.WorldPosition ),
			components,
			childCount = go.Children.Count,
			children,
		};
	}

	private static int CountNodes( List<object> nodes )
	{
		// Approximate count — each node is 1 plus its children
		return nodes.Count;
	}
}
