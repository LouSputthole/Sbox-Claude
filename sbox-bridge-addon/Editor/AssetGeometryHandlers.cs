using Editor;
using Sandbox;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Shared, read-only geometry inspection helpers.
///
/// Newer s&box SDKs expose Model.RenderBounds, Model.PhysicsBounds, and
/// Collider.GetWorldBounds(). Those members are read through reflection so an
/// older installed SDK reports an honest "unavailable" result instead of
/// preventing the bridge addon from compiling.
/// </summary>
internal static class BoundsInspection
{
	internal const int DefaultContributorLimit = 100;

	internal sealed class BoxUnion
	{
		public bool HasBounds { get; private set; }
		public Vector3 Mins { get; private set; }
		public Vector3 Maxs { get; private set; }

		public void Add( BBox box )
		{
			if ( !IsFinite( box.Mins ) || !IsFinite( box.Maxs ) )
				return;

			if ( !HasBounds )
			{
				Mins = box.Mins;
				Maxs = box.Maxs;
				HasBounds = true;
				return;
			}

			Mins = Vector3.Min( Mins, box.Mins );
			Maxs = Vector3.Max( Maxs, box.Maxs );
		}

		public BBox ToBBox()
		{
			return new BBox( Mins, Maxs );
		}
	}

	internal static object Vector( Vector3 value )
	{
		return new { value.x, value.y, value.z };
	}

	internal static bool IsEmpty( BBox box )
	{
		return box.Size.Length < 0.01f;
	}

	internal static bool IsFinite( Vector3 value )
	{
		return float.IsFinite( value.x )
			&& float.IsFinite( value.y )
			&& float.IsFinite( value.z );
	}

	internal static object DescribeBox(
		BBox box,
		string space,
		string source,
		string scope,
		string note = null )
	{
		var center = box.Center;
		var size = box.Size;
		var extents = box.Extents;
		return new
		{
			available = true,
			space,
			center = Vector( center ),
			size = Vector( size ),
			extents = Vector( extents ),
			mins = Vector( box.Mins ),
			maxs = Vector( box.Maxs ),
			radius = size.Length * 0.5f,
			empty = IsEmpty( box ),
			provenance = new { source, scope },
			note
		};
	}

	internal static object DescribeUnavailableBox(
		string space,
		string source,
		string scope,
		string note )
	{
		return new
		{
			available = false,
			space,
			center = (object)null,
			size = (object)null,
			extents = (object)null,
			mins = (object)null,
			maxs = (object)null,
			radius = (float?)null,
			empty = true,
			provenance = new { source, scope },
			note
		};
	}

	internal static bool TryReadBBoxProperty(
		object target,
		string propertyName,
		out BBox box )
	{
		box = default;
		if ( target == null ) return false;

		try
		{
			var property = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.Instance );
			if ( property?.GetValue( target ) is BBox value )
			{
				box = value;
				return true;
			}
		}
		catch
		{
			// The property is optional across SDK versions.
		}

		return false;
	}

	internal static bool TryInvokeBBox(
		object target,
		string methodName,
		out BBox box )
	{
		box = default;
		if ( target == null ) return false;

		try
		{
			var method = target.GetType().GetMethod(
				methodName,
				BindingFlags.Public | BindingFlags.Instance,
				null,
				Type.EmptyTypes,
				null );
			if ( method?.Invoke( target, null ) is BBox value )
			{
				box = value;
				return true;
			}
		}
		catch
		{
			// The method is optional across SDK versions.
		}

		return false;
	}

	internal static bool TryReadIntProperty(
		object target,
		string propertyName,
		out int value )
	{
		value = 0;
		if ( target == null ) return false;

		try
		{
			var property = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.Instance );
			var raw = property?.GetValue( target );
			if ( raw == null ) return false;
			value = Convert.ToInt32( raw );
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static object ReadNames(
		object target,
		string propertyName,
		int limit = 64 )
	{
		if ( target == null ) return null;

		try
		{
			var property = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.Instance );
			if ( property?.GetValue( target ) is not IEnumerable values )
				return null;

			var names = new List<string>();
			int total = 0;
			foreach ( var item in values )
			{
				string name = item as string;
				if ( name == null && item != null )
				{
					var itemType = item.GetType();
					name = itemType.GetProperty( "Name" )?.GetValue( item ) as string
						?? itemType.GetProperty( "ResourceName" )?.GetValue( item ) as string;
				}

				if ( string.IsNullOrWhiteSpace( name ) )
					continue;

				total++;
				if ( names.Count < limit )
					names.Add( name );
			}

			return new
			{
				total,
				showing = names.Count,
				truncated = total > names.Count,
				names
			};
		}
		catch
		{
			return null;
		}
	}

	internal static Dictionary<string, object> ReadModelMetadata( Model model )
	{
		var counts = new Dictionary<string, object>();
		void AddCount( string outputName, string propertyName )
		{
			if ( TryReadIntProperty( model, propertyName, out var value ) )
				counts[outputName] = value;
		}

		AddCount( "meshes", "MeshCount" );
		AddCount( "bones", "BoneCount" );
		AddCount( "animations", "AnimationCount" );
		AddCount( "morphs", "MorphCount" );
		AddCount( "materials", "MaterialCount" );
		AddCount( "materialGroups", "MaterialGroupCount" );
		AddCount( "bodyGroups", "BodyGroupCount" );

		var metadata = new Dictionary<string, object>
		{
			["name"] = model.Name,
			["counts"] = counts
		};

		void AddNames( string outputName, params string[] propertyNames )
		{
			foreach ( var propertyName in propertyNames )
			{
				var snapshot = ReadNames( model, propertyName );
				if ( snapshot == null ) continue;
				metadata[outputName] = snapshot;
				return;
			}
		}

		AddNames( "animationNames", "AnimationNames" );
		AddNames( "boneNames", "BoneNames" );
		AddNames( "materialNames", "Materials", "MaterialNames" );
		AddNames( "materialGroupNames", "MaterialGroupNames" );
		AddNames( "bodyGroupNames", "BodyGroupNames" );

		return metadata;
	}

	internal static object InspectGameObject(
		GameObject go,
		int contributorLimit = DefaultContributorLimit )
	{
		var aggregateBox = go.GetBounds();
		var aggregateCenter = aggregateBox.Center;
		var aggregateSize = aggregateBox.Size;
		var aggregateExtents = aggregateBox.Extents;
		bool aggregateEmpty = IsEmpty( aggregateBox );
		var renderAggregate = new BoxUnion();
		var renderContributors = new List<object>();
		int rendererCount = 0;
		int rendererContributorCount = 0;
		int unsupportedRendererCount = 0;
		foreach ( var renderer in go.Components.GetAll<ModelRenderer>(
			FindMode.EnabledInSelfAndDescendants ) )
		{
			if ( renderer == null ) continue;
			rendererCount++;
			if ( !TryReadBBoxProperty( renderer, "Bounds", out var rendererBox )
				|| !IsFinite( rendererBox.Mins )
				|| !IsFinite( rendererBox.Maxs ) )
			{
				unsupportedRendererCount++;
				continue;
			}

			renderAggregate.Add( rendererBox );
			rendererContributorCount++;
			if ( renderContributors.Count < contributorLimit )
			{
				renderContributors.Add( new
				{
					gameObjectId = renderer.GameObject.Id.ToString(),
					gameObjectName = renderer.GameObject.Name,
					component = renderer.GetType().Name,
					model = renderer.Model?.Name,
					source = "ModelRenderer.Bounds",
					bounds = new
					{
						mins = Vector( rendererBox.Mins ),
						maxs = Vector( rendererBox.Maxs )
					}
				} );
			}
		}

		var allPhysics = new BoxUnion();
		var solidPhysics = new BoxUnion();
		var allContributors = new List<object>();
		var solidContributors = new List<object>();
		int colliderCount = 0;
		int solidColliderCount = 0;
		int physicsContributorCount = 0;
		int solidContributorCount = 0;
		int triggerCount = 0;
		int physicsUnsupportedCount = 0;
		int solidUnsupportedCount = 0;

		foreach ( var collider in go.Components.GetAll<Collider>(
			FindMode.EnabledInSelfAndDescendants ) )
		{
			if ( collider == null ) continue;
			colliderCount++;
			bool isTrigger = collider.IsTrigger;
			if ( isTrigger ) triggerCount++; else solidColliderCount++;

			if ( !TryInvokeBBox( collider, "GetWorldBounds", out var colliderBox )
				|| !IsFinite( colliderBox.Mins )
				|| !IsFinite( colliderBox.Maxs ) )
			{
				physicsUnsupportedCount++;
				if ( !isTrigger ) solidUnsupportedCount++;
				continue;
			}

			var contributor = new
			{
				gameObjectId = collider.GameObject.Id.ToString(),
				gameObjectName = collider.GameObject.Name,
				component = collider.GetType().Name,
				isTrigger = collider.IsTrigger,
				source = "Collider.GetWorldBounds()",
				bounds = new
				{
					mins = Vector( colliderBox.Mins ),
					maxs = Vector( colliderBox.Maxs )
				}
			};

			allPhysics.Add( colliderBox );
			physicsContributorCount++;
			if ( allContributors.Count < contributorLimit )
				allContributors.Add( contributor );

			if ( !collider.IsTrigger )
			{
				solidPhysics.Add( colliderBox );
				solidContributorCount++;
				if ( solidContributors.Count < contributorLimit )
					solidContributors.Add( contributor );
			}
		}

		object DescribePhysicsAggregate(
			BoxUnion aggregate,
			bool includesTriggers,
			int totalColliders,
			int contributorCount,
			int unsupportedContributors,
			List<object> contributors )
		{
			string source = "Collider.GetWorldBounds()";
			string scope = "enabled Collider components on the target and descendants";
			string note = null;
			if ( totalColliders == 0 )
				note = includesTriggers
					? "No enabled Collider components were found on the target or its descendants."
					: "No enabled non-trigger Collider components were found on the target or its descendants.";
			else if ( contributorCount == 0 )
				note = "No collider in this scope returned finite world bounds; Collider.GetWorldBounds() may be unavailable in this installed SDK.";
			else if ( unsupportedContributors > 0 )
				note = $"{unsupportedContributors} collider(s) in this scope could not be sampled as finite world bounds.";

			if ( !aggregate.HasBounds )
			{
				return new
				{
					available = false,
					space = "world",
					center = (object)null,
					size = (object)null,
					extents = (object)null,
					mins = (object)null,
					maxs = (object)null,
					radius = (float?)null,
					empty = true,
					includesTriggers,
					totalColliders,
					totalContributors = contributorCount,
					showing = contributors.Count,
					truncated = contributorCount > contributors.Count,
					unsupportedContributors,
					contributors,
					provenance = new { source, scope },
					note
				};
			}

			var box = aggregate.ToBBox();
			var center = box.Center;
			var size = box.Size;
			var extents = box.Extents;
			return new
			{
				available = true,
				space = "world",
				center = Vector( center ),
				size = Vector( size ),
				extents = Vector( extents ),
				mins = Vector( box.Mins ),
				maxs = Vector( box.Maxs ),
				radius = size.Length * 0.5f,
				empty = IsEmpty( box ),
				includesTriggers,
				totalColliders,
				totalContributors = contributorCount,
				showing = contributors.Count,
				truncated = contributorCount > contributors.Count,
				unsupportedContributors,
				contributors,
				provenance = new { source, scope },
				note
			};
		}

		return new
		{
			id = go.Id.ToString(),
			name = go.Name,

			// Legacy aliases: keep the original get_bounds contract exactly.
			center = Vector( aggregateCenter ),
			size = Vector( aggregateSize ),
			extents = Vector( aggregateExtents ),
			mins = Vector( aggregateBox.Mins ),
			maxs = Vector( aggregateBox.Maxs ),
			radius = aggregateSize.Length * 0.5f,
			position = Vector( go.WorldPosition ),
			empty = aggregateEmpty,
			note = aggregateEmpty
				? "Zero/near-zero aggregate bounds - using the engine result at the world position; orbit will frame a default radius."
				: null,

			aggregate = DescribeBox(
				aggregateBox,
				"world",
				"GameObject.GetBounds()",
				"enabled IHasBounds components on the target and descendants",
				"This legacy engine aggregate can include renderers, colliders, and other IHasBounds components." ),
			render = renderAggregate.HasBounds
				? DescribeBox(
					renderAggregate.ToBBox(),
					"world",
					"union of ModelRenderer.Bounds",
					"enabled ModelRenderer components on the target and descendants" )
				: DescribeUnavailableBox(
					"world",
					"ModelRenderer.Bounds",
					"enabled ModelRenderer components on the target and descendants",
					rendererCount == 0
						? "No enabled ModelRenderer components were found."
						: "No enabled ModelRenderer returned finite world bounds in this installed SDK." ),
			renderContributors = new
			{
				totalRenderers = rendererCount,
				totalContributors = rendererContributorCount,
				showing = renderContributors.Count,
				truncated = rendererContributorCount > renderContributors.Count,
				unsupportedContributors = unsupportedRendererCount,
				contributors = renderContributors,
				provenance = new
				{
					source = "ModelRenderer.Bounds",
					space = "world",
					scope = "enabled ModelRenderer components on the target and descendants"
				}
			},
			physics = DescribePhysicsAggregate(
				allPhysics,
				true,
				colliderCount,
				physicsContributorCount,
				physicsUnsupportedCount,
				allContributors ),
			solidPhysics = DescribePhysicsAggregate(
				solidPhysics,
				false,
				solidColliderCount,
				solidContributorCount,
				solidUnsupportedCount,
				solidContributors ),
			provenance = new
			{
				aggregate = "GameObject.GetBounds() over enabled IHasBounds components",
				render = "union of enabled ModelRenderer.Bounds",
				physics = "Collider.GetWorldBounds() for enabled colliders, including triggers",
				solidPhysics = "Collider.GetWorldBounds() for enabled non-trigger colliders"
			},
			colliderSummary = new
			{
				total = colliderCount,
				triggers = triggerCount,
				solids = colliderCount - triggerCount,
				unsupported = physicsUnsupportedCount,
				solidUnsupported = solidUnsupportedCount,
				contributorLimit
			}
		};
	}
}

/// <summary>
/// inspect_model_geometry - inspect model-local/default-pose placement geometry
/// without creating or changing a scene object.
/// </summary>
public class InspectModelGeometryHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var path = p.TryGetProperty( "path", out var pathElement )
			? pathElement.GetString()
			: null;
		if ( string.IsNullOrWhiteSpace( path ) )
			return Task.FromResult<object>( new { error = "path (model asset path) is required" } );

		try
		{
			var model = Model.Load( path );
			if ( model == null )
				return Task.FromResult<object>( new { error = $"Model not found: {path}" } );
			if ( model.IsError )
				return Task.FromResult<object>( new
				{
					error = $"Model.Load returned the error model for '{path}'. Check the asset path and model compile state."
				} );

			var aggregateBox = model.Bounds;
			var renderBox = aggregateBox;
			bool hasRenderBounds = BoundsInspection.TryReadBBoxProperty(
				model,
				"RenderBounds",
				out var sdkRenderBounds );
			if ( hasRenderBounds )
				renderBox = sdkRenderBounds;

			bool hasPhysicsBounds = BoundsInspection.TryReadBBoxProperty(
				model,
				"PhysicsBounds",
				out var physicsBox );

			var size = renderBox.Size;
			var center = renderBox.Center;
			var mins = renderBox.Mins;
			var maxs = renderBox.Maxs;
			var limits = new
			{
				coordinateSpace = "model_local",
				pose = "default_pose",
				scale = "asset scale only; no GameObject world scale is applied",
				rotation = "asset axes only; no GameObject world rotation is applied",
				bodyGroups = "default model state; body-group changes can alter visible geometry",
				animation = "default pose only; animated/skinned poses can exceed these bounds",
				physics = "Physics bounds are reported only when Model.PhysicsBounds exists in the installed SDK."
			};

			return Task.FromResult<object>( new
			{
				path,
				modelName = model.Name,
				coordinateSpace = "model_local",
				pose = "default_pose",
				aggregate = BoundsInspection.DescribeBox(
					aggregateBox,
					"model_local",
					"Model.Bounds",
					"engine aggregate in the model's default pose" ),
				render = BoundsInspection.DescribeBox(
					renderBox,
					"model_local",
					hasRenderBounds ? "Model.RenderBounds" : "Model.Bounds fallback",
					"default-pose render geometry",
					hasRenderBounds
						? null
						: "Model.RenderBounds is unavailable in this installed SDK; render uses Model.Bounds as a labeled fallback." ),
				physics = hasPhysicsBounds
					? BoundsInspection.DescribeBox(
						physicsBox,
						"model_local",
						"Model.PhysicsBounds",
						"default model physics geometry" )
					: BoundsInspection.DescribeUnavailableBox(
						"model_local",
						"Model.PhysicsBounds",
						"default model physics geometry",
						"Model.PhysicsBounds is unavailable in this installed SDK; render bounds are not substituted for physics bounds." ),
				placement = new
				{
					footprint = new
					{
						x = MathF.Abs( size.x ),
						y = MathF.Abs( size.y )
					},
					height = MathF.Abs( size.z ),
					centerOffset = BoundsInspection.Vector( center ),
					bottomZ = mins.z,
					topZ = maxs.z,
					groundingOffsetZ = -mins.z,
					pivotToCenter = BoundsInspection.Vector( center ),
					source = hasRenderBounds ? "Model.RenderBounds" : "Model.Bounds fallback",
					space = "model_local",
					pose = "default_pose"
				},
				metadata = BoundsInspection.ReadModelMetadata( model ),
				limits
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new
			{
				error = $"inspect_model_geometry failed for '{path}': {ex.Message}"
			} );
		}
	}
}

/// <summary>
/// get_bounds v2 - preserve the original render aliases and add provenance-rich
/// render, physics, and non-trigger ("solid") physics aggregates.
/// </summary>
public class GetBoundsV2Handler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idElement )
			|| idElement.ValueKind != JsonValueKind.String
			|| !Guid.TryParse( idElement.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );

		var go = ClaudeBridge.ResolveGameObject( scene, idElement.GetString() );
		if ( go == null )
			return Task.FromResult<object>( new
			{
				error = $"GameObject not found: {idElement.GetString()}"
			} );

		try
		{
			return Task.FromResult(
				BoundsInspection.InspectGameObject( go ) );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new
			{
				error = $"get_bounds failed for '{idElement.GetString()}': {ex.Message}"
			} );
		}
	}
}

/// <summary>
/// find_objects_near - read-only, play-aware pivot-distance search.
/// </summary>
public class FindObjectsNearHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		try
		{
			Vector3 center;
			GameObject origin = null;
			string centerSource;
			bool hasPosition = p.TryGetProperty( "position", out var positionElement )
				&& positionElement.ValueKind != JsonValueKind.Null
				&& positionElement.ValueKind != JsonValueKind.Undefined;
			bool hasOrigin = p.TryGetProperty( "originId", out var originElement )
				&& originElement.ValueKind != JsonValueKind.Null
				&& originElement.ValueKind != JsonValueKind.Undefined;
			if ( hasPosition && hasOrigin )
				return Task.FromResult<object>( new { error = "Pass position OR originId, not both" } );

			if ( hasPosition )
			{
				center = CameraCaptureService.ParseVector3Element( positionElement, "position" );
				centerSource = "position";
			}
			else if ( hasOrigin )
			{
				if ( originElement.ValueKind != JsonValueKind.String
					|| !Guid.TryParse( originElement.GetString(), out _ ) )
					return Task.FromResult<object>( new { error = "originId must be a GameObject GUID" } );
				origin = ClaudeBridge.ResolveGameObject( scene, originElement.GetString() );
				if ( origin == null )
					return Task.FromResult<object>( new
					{
						error = $"Origin GameObject not found: {originElement.GetString()}"
					} );
				center = origin.WorldPosition;
				centerSource = "originId.worldPosition";
			}
			else
			{
				return Task.FromResult<object>( new
				{
					error = "position or originId (GameObject GUID) is required"
				} );
			}

			if ( !BoundsInspection.IsFinite( center ) )
				return Task.FromResult<object>( new { error = "Search center must contain finite coordinates" } );

			float radius = p.TryGetProperty( "radius", out var radiusElement )
				&& radiusElement.TryGetSingle( out var parsedRadius )
					? parsedRadius
					: 256f;
			if ( !float.IsFinite( radius ) || radius <= 0f )
				return Task.FromResult<object>( new { error = "radius must be a finite number greater than zero" } );
			float requestedRadius = radius;
			radius = MathF.Min( radius, 1_000_000f );

			int limit = p.TryGetProperty( "limit", out var limitElement )
				&& limitElement.TryGetInt32( out var requestedLimit )
					? requestedLimit
					: 50;
			limit = Math.Clamp( limit, 1, 500 );

			string nameQuery = p.TryGetProperty( "name", out var nameElement )
				&& nameElement.ValueKind == JsonValueKind.String
					? nameElement.GetString()
					: null;
			string componentQuery = p.TryGetProperty( "component", out var componentElement )
				&& componentElement.ValueKind == JsonValueKind.String
					? componentElement.GetString()
					: null;
			string tagQuery = p.TryGetProperty( "tag", out var tagElement )
				&& tagElement.ValueKind == JsonValueKind.String
					? tagElement.GetString()
					: null;
			bool includeOrigin = p.TryGetProperty( "includeOrigin", out var includeOriginElement )
				&& includeOriginElement.ValueKind == JsonValueKind.True;

			var matches = new List<(GameObject GameObject, float Distance)>();
			int scanned = 0;
			foreach ( var go in scene.GetAllObjects( true ) )
			{
				if ( go == null || ReferenceEquals( go, scene ) ) continue;
				scanned++;
				if ( origin != null && !includeOrigin && go.Id == origin.Id ) continue;
				if ( !string.IsNullOrWhiteSpace( nameQuery )
					&& ( go.Name == null
						|| !go.Name.Contains( nameQuery, StringComparison.OrdinalIgnoreCase ) ) )
					continue;
				if ( !string.IsNullOrWhiteSpace( tagQuery ) && !go.Tags.Has( tagQuery ) )
					continue;
				if ( !string.IsNullOrWhiteSpace( componentQuery )
					&& !go.Components.GetAll().Any( component =>
						component != null
						&& component.GetType().Name.Equals(
							componentQuery,
							StringComparison.OrdinalIgnoreCase ) ) )
					continue;

				float distance = ( go.WorldPosition - center ).Length;
				if ( !float.IsFinite( distance ) || distance > radius ) continue;
				matches.Add( (go, distance) );
			}

			var objects = matches
				.OrderBy( match => match.Distance )
				.ThenBy( match => match.GameObject.Name )
				.Take( limit )
				.Select( match => new
				{
					id = match.GameObject.Id.ToString(),
					name = match.GameObject.Name,
					position = BoundsInspection.Vector( match.GameObject.WorldPosition ),
					distance = match.Distance
				} )
				.ToArray();

			return Task.FromResult<object>( new
			{
				center = BoundsInspection.Vector( center ),
				centerSource,
				origin = origin == null
					? null
					: new { id = origin.Id.ToString(), name = origin.Name },
				radius,
				requestedRadius,
				radiusClamped = requestedRadius != radius,
				distanceMode = "pivot",
				total = matches.Count,
				showing = objects.Length,
				truncated = matches.Count > objects.Length,
				scanned,
				filters = new
				{
					name = nameQuery,
					component = componentQuery,
					tag = tagQuery,
					includeOrigin
				},
				objects,
				note = "Distance is Euclidean distance between GameObject world positions; render and physics bounds are not sampled."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new
			{
				error = $"find_objects_near failed: {ex.Message}"
			} );
		}
	}
}
