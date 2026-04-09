using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Gets all properties of a component on a GameObject.
/// Returns property names, types, and current values.
/// </summary>
public class GetAllPropertiesHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var componentType = parameters.GetProperty( "component" ).GetString()
			?? throw new Exception( "Missing required parameter: component" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		var component = go.Components
			.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );

		if ( component == null )
			throw new Exception( $"Component '{componentType}' not found on '{go.Name}'" );

		var typeDesc = TypeLibrary.GetType( component.GetType() );
		if ( typeDesc == null )
			throw new Exception( $"Could not get type info for '{componentType}'" );

		var properties = new List<object>();

		foreach ( var prop in typeDesc.Properties.Where( p => p.IsPublic ) )
		{
			try
			{
				var value = prop.GetValue( component );
				properties.Add( new
				{
					name = prop.Name,
					type = prop.PropertyType.Name,
					value = ComponentHelper.SerializeValue( value ),
					hasAttribute = prop.HasAttribute<PropertyAttribute>(),
				} );
			}
			catch
			{
				properties.Add( new
				{
					name = prop.Name,
					type = prop.PropertyType.Name,
					value = (object)"<error reading>",
					hasAttribute = prop.HasAttribute<PropertyAttribute>(),
				} );
			}
		}

		return Task.FromResult<object>( new
		{
			id,
			gameObject = go.Name,
			component = componentType,
			propertyCount = properties.Count,
			properties,
		} );
	}
}
