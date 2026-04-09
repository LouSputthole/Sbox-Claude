using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a new GameObject in the active scene.
/// Supports optional name, position, rotation, scale, and parent.
/// </summary>
public class CreateGameObjectHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? "New Object"
			: "New Object";

		// Create the object
		var go = scene.CreateObject();
		go.Name = name;

		// Set transform
		if ( parameters.TryGetProperty( "position", out var posProp ) )
		{
			go.WorldPosition = ParseVector3( posProp );
		}

		if ( parameters.TryGetProperty( "rotation", out var rotProp ) )
		{
			go.WorldRotation = ParseRotation( rotProp );
		}

		if ( parameters.TryGetProperty( "scale", out var scaleProp ) )
		{
			if ( scaleProp.ValueKind == JsonValueKind.Number )
			{
				var uniform = scaleProp.GetSingle();
				go.WorldScale = new Vector3( uniform, uniform, uniform );
			}
			else
			{
				go.WorldScale = ParseVector3( scaleProp );
			}
		}

		// Set parent
		if ( parameters.TryGetProperty( "parent", out var parentProp ) )
		{
			var parentGuid = parentProp.GetString();
			if ( !string.IsNullOrEmpty( parentGuid ) && Guid.TryParse( parentGuid, out var guid ) )
			{
				var parent = scene.Directory.FindByGuid( guid );
				if ( parent != null )
				{
					go.SetParent( parent );
				}
			}
		}

		return Task.FromResult<object>( new
		{
			id = go.Id.ToString(),
			name = go.Name,
			position = FormatVector3( go.WorldPosition ),
			created = true,
		} );
	}

	public static Vector3 ParseVector3( JsonElement element )
	{
		if ( element.ValueKind == JsonValueKind.Object )
		{
			var x = element.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
			var y = element.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
			var z = element.TryGetProperty( "z", out var zp ) ? zp.GetSingle() : 0f;
			return new Vector3( x, y, z );
		}

		if ( element.ValueKind == JsonValueKind.String )
		{
			var parts = element.GetString()?.Split( ',' ) ?? Array.Empty<string>();
			if ( parts.Length >= 3 )
			{
				return new Vector3(
					float.Parse( parts[0].Trim() ),
					float.Parse( parts[1].Trim() ),
					float.Parse( parts[2].Trim() )
				);
			}
		}

		return Vector3.Zero;
	}

	public static Rotation ParseRotation( JsonElement element )
	{
		if ( element.ValueKind == JsonValueKind.Object )
		{
			// Accept either euler angles (pitch, yaw, roll) or quaternion (x, y, z, w)
			if ( element.TryGetProperty( "w", out _ ) )
			{
				var x = element.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
				var y = element.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
				var z = element.TryGetProperty( "z", out var zp ) ? zp.GetSingle() : 0f;
				var w = element.TryGetProperty( "w", out var wp ) ? wp.GetSingle() : 1f;
				return new Rotation( x, y, z, w );
			}
			else
			{
				var pitch = element.TryGetProperty( "pitch", out var pp ) ? pp.GetSingle() : 0f;
				var yaw = element.TryGetProperty( "yaw", out var yp ) ? yp.GetSingle() : 0f;
				var roll = element.TryGetProperty( "roll", out var rp ) ? rp.GetSingle() : 0f;
				return Rotation.From( pitch, yaw, roll );
			}
		}

		return Rotation.Identity;
	}

	public static object FormatVector3( Vector3 v )
	{
		return new { x = v.x, y = v.y, z = v.z };
	}

	public static object FormatRotation( Rotation r )
	{
		var angles = r.Angles();
		return new { pitch = angles.pitch, yaw = angles.yaw, roll = angles.roll };
	}
}
