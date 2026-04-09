using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Lists all available component types that can be added to GameObjects.
/// Includes both built-in s&box components and custom project components.
/// </summary>
public class ListAvailableComponentsHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var filter = parameters.TryGetProperty( "filter", out var filterProp )
			? filterProp.GetString() ?? ""
			: "";

		var category = parameters.TryGetProperty( "category", out var catProp )
			? catProp.GetString() ?? ""
			: "";

		var components = new List<object>();

		// Get all types that extend Component
		var componentTypes = TypeLibrary.GetTypes<Component>();

		foreach ( var type in componentTypes )
		{
			if ( type.IsAbstract ) continue;

			// Apply name filter
			if ( !string.IsNullOrEmpty( filter ) &&
			     !type.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) &&
			     !(type.Title?.Contains( filter, StringComparison.OrdinalIgnoreCase ) ?? false) )
			{
				continue;
			}

			// Apply category filter
			if ( !string.IsNullOrEmpty( category ) &&
			     !(type.Group?.Contains( category, StringComparison.OrdinalIgnoreCase ) ?? false) )
			{
				continue;
			}

			var propertyCount = type.Properties.Count( p => p.IsPublic && p.HasAttribute<PropertyAttribute>() );

			components.Add( new
			{
				name = type.Name,
				title = type.Title ?? type.Name,
				description = type.Description ?? "",
				group = type.Group ?? "Uncategorized",
				propertyCount,
				isCustom = !type.FullName.StartsWith( "Sandbox." ),
			} );
		}

		var sorted = components.OrderBy( c => c.ToString() ).ToList();

		return Task.FromResult<object>( new
		{
			count = sorted.Count,
			filter = string.IsNullOrEmpty( filter ) ? null : filter,
			category = string.IsNullOrEmpty( category ) ? null : category,
			components = sorted,
		} );
	}
}
