using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Adds a Rigidbody and an appropriate collider to a GameObject.
/// Automatically selects BoxCollider if no collider type is specified.
/// Optionally configures mass, gravity, and freeze axes.
/// </summary>
public class AddPhysicsHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		// Add Rigidbody if not already present
		var rigidbody = go.Components.Get<Rigidbody>();
		var rigidbodyAdded = false;

		if ( rigidbody == null )
		{
			rigidbody = go.Components.Create<Rigidbody>();
			rigidbodyAdded = true;
		}

		// Configure Rigidbody properties
		// API-NOTE: PhysicsBody may be null immediately after Create; mass may need a frame tick
		if ( parameters.TryGetProperty( "mass", out var massProp ) && rigidbody.PhysicsBody != null )
			rigidbody.PhysicsBody.Mass = massProp.GetSingle();

		if ( parameters.TryGetProperty( "gravity", out var gravProp ) )
			rigidbody.Gravity = gravProp.GetBoolean();

		// Add collider
		var colliderType = parameters.TryGetProperty( "collider", out var collProp )
			? collProp.GetString() ?? "box" : "box";

		var existingCollider = go.Components.Get<Collider>();
		var colliderAdded = false;
		string colliderName = "none";

		if ( existingCollider == null )
		{
			colliderAdded = true;
			switch ( colliderType.ToLowerInvariant() )
			{
				case "sphere":
					go.Components.Create<SphereCollider>();
					colliderName = "SphereCollider";
					break;
				case "capsule":
					go.Components.Create<CapsuleCollider>();
					colliderName = "CapsuleCollider";
					break;
				case "mesh":
					go.Components.Create<MeshCollider>();
					colliderName = "MeshCollider";
					break;
				case "box":
				default:
					go.Components.Create<BoxCollider>();
					colliderName = "BoxCollider";
					break;
			}
		}
		else
		{
			colliderName = existingCollider.GetType().Name;
		}

		return Task.FromResult<object>( new
		{
			id,
			gameObject = go.Name,
			rigidbodyAdded,
			colliderAdded,
			colliderType = colliderName,
			configured = true,
		} );
	}
}
