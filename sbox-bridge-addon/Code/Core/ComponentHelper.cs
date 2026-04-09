using System;
using System.Text.Json;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Helper methods for serializing/deserializing component property values
/// between JSON and s&box types.
/// </summary>
public static class ComponentHelper
{
	/// <summary>
	/// Convert a C# value to a JSON-safe representation.
	/// </summary>
	public static object SerializeValue( object value )
	{
		if ( value == null ) return null;

		return value switch
		{
			Vector3 v => new { x = v.x, y = v.y, z = v.z },
			Vector2 v => new { x = v.x, y = v.y },
			Rotation r => new { pitch = r.Angles().pitch, yaw = r.Angles().yaw, roll = r.Angles().roll },
			Angles a => new { pitch = a.pitch, yaw = a.yaw, roll = a.roll },
			Color c => new { r = c.r, g = c.g, b = c.b, a = c.a },
			GameObject go => new { id = go.Id.ToString(), name = go.Name },
			string s => s,
			bool b => b,
			int i => i,
			float f => f,
			double d => d,
			Enum e => e.ToString(),
			_ => value.ToString(),
		};
	}

	/// <summary>
	/// Convert a JSON value to the target C# type.
	/// </summary>
	public static object DeserializeValue( JsonElement element, Type targetType )
	{
		if ( element.ValueKind == JsonValueKind.Null )
			return null;

		// Primitives
		if ( targetType == typeof( string ) )
			return element.GetString();
		if ( targetType == typeof( bool ) )
			return element.GetBoolean();
		if ( targetType == typeof( int ) )
			return element.GetInt32();
		if ( targetType == typeof( float ) )
			return element.GetSingle();
		if ( targetType == typeof( double ) )
			return element.GetDouble();

		// Vectors
		if ( targetType == typeof( Vector3 ) )
			return CreateGameObjectHandler.ParseVector3( element );

		if ( targetType == typeof( Vector2 ) )
		{
			if ( element.ValueKind == JsonValueKind.Object )
			{
				var x = element.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
				var y = element.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
				return new Vector2( x, y );
			}
		}

		// Rotation
		if ( targetType == typeof( Rotation ) )
			return CreateGameObjectHandler.ParseRotation( element );

		// Angles
		if ( targetType == typeof( Angles ) )
		{
			if ( element.ValueKind == JsonValueKind.Object )
			{
				var pitch = element.TryGetProperty( "pitch", out var pp ) ? pp.GetSingle() : 0f;
				var yaw = element.TryGetProperty( "yaw", out var yp ) ? yp.GetSingle() : 0f;
				var roll = element.TryGetProperty( "roll", out var rp ) ? rp.GetSingle() : 0f;
				return new Angles( pitch, yaw, roll );
			}
		}

		// Color
		if ( targetType == typeof( Color ) )
		{
			if ( element.ValueKind == JsonValueKind.Object )
			{
				var r = element.TryGetProperty( "r", out var rp ) ? rp.GetSingle() : 1f;
				var g = element.TryGetProperty( "g", out var gp ) ? gp.GetSingle() : 1f;
				var b = element.TryGetProperty( "b", out var bp ) ? bp.GetSingle() : 1f;
				var a = element.TryGetProperty( "a", out var ap ) ? ap.GetSingle() : 1f;
				return new Color( r, g, b, a );
			}
			if ( element.ValueKind == JsonValueKind.String )
			{
				return Color.Parse( element.GetString() ) ?? Color.White;
			}
		}

		// Enums
		if ( targetType.IsEnum )
		{
			var str = element.GetString();
			if ( str != null && Enum.TryParse( targetType, str, true, out var result ) )
				return result;
		}

		// Fallback: try to use the string value
		if ( element.ValueKind == JsonValueKind.String )
			return element.GetString();

		return null;
	}
}
