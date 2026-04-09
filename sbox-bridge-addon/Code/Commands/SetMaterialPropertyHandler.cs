using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Changes a property on a material assigned to a GameObject's renderer.
/// Supports color, float, texture, and vector properties.
/// </summary>
public class SetMaterialPropertyHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var propertyName = parameters.GetProperty( "property" ).GetString()
			?? throw new Exception( "Missing required parameter: property" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		var renderer = go.Components.Get<ModelRenderer>();
		if ( renderer == null )
			throw new Exception( $"No ModelRenderer on '{go.Name}'" );

		var material = renderer.MaterialOverride ?? renderer.Material;
		if ( material == null )
			throw new Exception( $"No material on '{go.Name}'" );

		// Determine value type and set
		if ( !parameters.TryGetProperty( "value", out var valueProp ) )
			throw new Exception( "Missing required parameter: value" );

		{
			switch ( valueProp.ValueKind )
			{
				case JsonValueKind.Number:
					material.Set( propertyName, valueProp.GetSingle() );
					break;

				case JsonValueKind.String:
					var strVal = valueProp.GetString() ?? "";
					// Try parsing as color
					if ( Color.TryParse( strVal, out var color ) )
						material.Set( propertyName, color );
					else
						material.Set( propertyName, Texture.Load( strVal ) );
					break;

				case JsonValueKind.Object:
					// Check if it's a color {r,g,b,a}
					if ( valueProp.TryGetProperty( "r", out _ ) )
					{
						var r = valueProp.TryGetProperty( "r", out var rp ) ? rp.GetSingle() : 1f;
						var g = valueProp.TryGetProperty( "g", out var gp ) ? gp.GetSingle() : 1f;
						var b = valueProp.TryGetProperty( "b", out var bp ) ? bp.GetSingle() : 1f;
						var a = valueProp.TryGetProperty( "a", out var ap ) ? ap.GetSingle() : 1f;
						material.Set( propertyName, new Color( r, g, b, a ) );
					}
					// Or a vector {x,y,z}
					else if ( valueProp.TryGetProperty( "x", out _ ) )
					{
						var vec = CreateGameObjectHandler.ParseVector3( valueProp );
						material.Set( propertyName, vec );
					}
					break;

				case JsonValueKind.True:
				case JsonValueKind.False:
					material.Set( propertyName, valueProp.GetBoolean() ? 1f : 0f );
					break;
			}
		}

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			property = propertyName,
			set = true,
		} );
	}
}
