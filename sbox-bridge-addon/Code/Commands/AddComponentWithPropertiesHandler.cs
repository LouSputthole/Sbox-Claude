using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Adds a component to a GameObject and sets properties in one call.
/// Uses TypeLibrary to resolve the component type by name.
/// </summary>
public class AddComponentWithPropertiesHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var componentTypeName = parameters.GetProperty( "component" ).GetString()
			?? throw new Exception( "Missing required parameter: component" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		// Resolve component type
		var typeDesc = TypeLibrary.GetType( componentTypeName );
		if ( typeDesc == null )
		{
			// Try searching by short name
			typeDesc = TypeLibrary.GetTypes<Component>()
				.FirstOrDefault( t => t.Name.Equals( componentTypeName, StringComparison.OrdinalIgnoreCase ) );
		}

		if ( typeDesc == null )
			throw new Exception( $"Component type not found: {componentTypeName}" );

		// Add the component
		var component = go.Components.Create( typeDesc );
		if ( component == null )
			throw new Exception( $"Failed to create component: {componentTypeName}" );

		// Set properties if provided
		var setProperties = new List<object>();

		if ( parameters.TryGetProperty( "properties", out var propsProp ) &&
		     propsProp.ValueKind == JsonValueKind.Object )
		{
			foreach ( var kvp in propsProp.EnumerateObject() )
			{
				var prop = typeDesc.Properties
					.FirstOrDefault( p => p.Name.Equals( kvp.Name, StringComparison.OrdinalIgnoreCase ) );

				if ( prop == null )
				{
					setProperties.Add( new { name = kvp.Name, status = "not_found" } );
					continue;
				}

				try
				{
					var value = ComponentHelper.DeserializeValue( kvp.Value, prop.PropertyType );
					prop.SetValue( component, value );
					setProperties.Add( new { name = prop.Name, status = "set" } );
				}
				catch ( Exception ex )
				{
					setProperties.Add( new { name = prop.Name, status = $"error: {ex.Message}" } );
				}
			}
		}

		return Task.FromResult<object>( new
		{
			id,
			gameObject = go.Name,
			component = typeDesc.Name,
			componentId = component.Id.ToString(),
			properties = setProperties,
			added = true,
		} );
	}
}
