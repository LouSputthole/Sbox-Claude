using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Gets a single property value from a component on a GameObject.
/// </summary>
public class GetPropertyHandler : ICommandHandler
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
		var propertyName = parameters.GetProperty( "property" ).GetString()
			?? throw new Exception( "Missing required parameter: property" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		// Find the component by type name
		var component = go.Components
			.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );

		if ( component == null )
			throw new Exception( $"Component '{componentType}' not found on '{go.Name}'" );

		// Get the property via reflection through TypeLibrary
		var typeDesc = TypeLibrary.GetType( component.GetType() );
		if ( typeDesc == null )
			throw new Exception( $"Could not get type info for '{componentType}'" );

		var prop = typeDesc.Properties.FirstOrDefault( p => p.Name.Equals( propertyName, StringComparison.OrdinalIgnoreCase ) );
		if ( prop == null )
			throw new Exception( $"Property '{propertyName}' not found on '{componentType}'" );

		var value = prop.GetValue( component );

		return Task.FromResult<object>( new
		{
			id,
			component = componentType,
			property = prop.Name,
			type = prop.PropertyType.Name,
			value = ComponentHelper.SerializeValue( value ),
		} );
	}
}
