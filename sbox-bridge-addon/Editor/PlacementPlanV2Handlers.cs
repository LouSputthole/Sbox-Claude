using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

internal enum PlacementPlanV2Kind
{
	Path,
	Grid,
	Scatter
}

internal sealed class PlacementSpecV2
{
	public int Slot { get; set; }
	public string Name { get; set; }
	public Vector3 Position { get; set; }
	public Rotation Rotation { get; set; }
	public Vector3 Scale { get; set; }
	public bool GroundResolved { get; set; }
	public float GroundingOffsetZ { get; set; }
}

internal sealed class PlacementPlanV2
{
	public string Id { get; set; }
	public PlacementPlanV2Kind Kind { get; set; }
	public Scene Scene { get; set; }
	public DateTime CreatedUtc { get; set; }
	public DateTime ExpiresUtc { get; set; }
	public LinkedListNode<string> OrderNode { get; set; }
	public bool Committing { get; set; }
	public string ModelPath { get; set; }
	public Model Model { get; set; }
	public string SourceId { get; set; }
	public string SourceParentId { get; set; }
	public Vector3 SourcePosition { get; set; }
	public Rotation SourceRotation { get; set; }
	public Vector3 SourceScale { get; set; }
	public bool CreateGroup { get; set; }
	public string GroupName { get; set; }
	public Vector3 GroupPosition { get; set; }
	public bool HasTint { get; set; }
	public Color Tint { get; set; }
	public int RequestedCount { get; set; }
	public object Metadata { get; set; }
	public List<PlacementSpecV2> Placements { get; } = new();
	public List<string> Warnings { get; } = new();
}

internal static class PlacementPlanV2Store
{
	internal const int MaxPlans = 64;
	internal const int MaxPlacementsPerPlan = 2048;
	internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes( 10 );

	private static readonly object Gate = new();
	private static readonly Dictionary<string, PlacementPlanV2> Plans = new( StringComparer.Ordinal );
	private static readonly LinkedList<string> Order = new();

	internal static PlacementPlanV2 Add( PlacementPlanV2 plan )
	{
		if ( plan == null ) throw new ArgumentNullException( nameof( plan ) );
		if ( plan.Scene == null ) throw new Exception( "Placement plans require an active scene." );
		if ( plan.Placements.Count > MaxPlacementsPerPlan )
			throw new Exception( $"Placement plan exceeds the {MaxPlacementsPerPlan} item limit." );

		lock ( Gate )
		{
			var now = DateTime.UtcNow;
			PruneExpiredLocked( now );
			while ( Plans.Count >= MaxPlans )
			{
				var evictable = Order.First;
				while ( evictable != null
					&& Plans.TryGetValue( evictable.Value, out var candidate )
					&& candidate.Committing )
				{
					evictable = evictable.Next;
				}
				if ( evictable == null )
					throw new Exception( "Placement plan capacity is temporarily full with in-flight commits; retry shortly." );
				RemoveLocked( evictable.Value );
			}

			plan.Id = "pp_" + Guid.NewGuid().ToString( "N" );
			plan.CreatedUtc = now;
			plan.ExpiresUtc = now.Add( Lifetime );
			plan.OrderNode = Order.AddLast( plan.Id );
			Plans.Add( plan.Id, plan );
			return plan;
		}
	}

	internal static bool TryBeginCommit( string planId, Scene activeScene, out PlacementPlanV2 plan, out string error )
	{
		plan = null;
		error = null;
		if ( string.IsNullOrWhiteSpace( planId ) )
		{
			error = "planId is required";
			return false;
		}

		lock ( Gate )
		{
			if ( !Plans.TryGetValue( planId, out plan ) )
			{
				error = $"Placement plan not found: {planId}";
				return false;
			}
			if ( plan.Committing )
			{
				plan = null;
				error = $"Placement plan is already committing: {planId}";
				return false;
			}
			if ( DateTime.UtcNow >= plan.ExpiresUtc )
			{
				RemoveLocked( planId );
				plan = null;
				error = $"Placement plan expired: {planId}";
				return false;
			}
			if ( !ReferenceEquals( plan.Scene, activeScene ) )
			{
				plan = null;
				error = "Placement plan belongs to a different editor scene; preview it again in the active scene.";
				return false;
			}
			plan.Committing = true;
			return true;
		}
	}

	internal static void Complete( PlacementPlanV2 plan )
	{
		if ( plan == null ) return;
		lock ( Gate )
		{
			if ( Plans.TryGetValue( plan.Id, out var stored ) && ReferenceEquals( stored, plan ) )
				RemoveLocked( plan.Id );
		}
	}

	internal static bool RestoreAfterCompleteRollback( PlacementPlanV2 plan )
	{
		if ( plan == null ) return false;
		lock ( Gate )
		{
			if ( !Plans.TryGetValue( plan.Id, out var stored ) || !ReferenceEquals( stored, plan ) )
				return false;
			if ( DateTime.UtcNow >= plan.ExpiresUtc )
			{
				RemoveLocked( plan.Id );
				return false;
			}
			plan.Committing = false;
			return true;
		}
	}

	private static void PruneExpiredLocked( DateTime now )
	{
		while ( Order.First != null )
		{
			var id = Order.First.Value;
			if ( !Plans.TryGetValue( id, out var plan ) )
			{
				Order.RemoveFirst();
				continue;
			}
			if ( plan.ExpiresUtc > now || plan.Committing ) break;
			RemoveLocked( id );
		}
	}

	private static void RemoveLocked( string id )
	{
		if ( !Plans.TryGetValue( id, out var plan ) ) return;
		Plans.Remove( id );
		if ( plan.OrderNode != null ) Order.Remove( plan.OrderNode );
		plan.OrderNode = null;
	}
}

internal static class PlacementPlanV2Helpers
{
	internal static bool IsDryRun( JsonElement p ) =>
		p.TryGetProperty( "dryRun", out var value ) && value.ValueKind == JsonValueKind.True;

	internal static bool HasValue( JsonElement p, string key, out JsonElement value )
	{
		if ( p.TryGetProperty( key, out value )
			&& value.ValueKind != JsonValueKind.Null
			&& value.ValueKind != JsonValueKind.Undefined ) return true;
		value = default;
		return false;
	}

	internal static string RequiredString( JsonElement p, string key )
	{
		if ( !p.TryGetProperty( key, out var value ) || value.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace( value.GetString() ) )
			throw new Exception( $"{key} is required" );
		return value.GetString();
	}

	internal static float OptionalFloat( JsonElement p, string key, float fallback )
	{
		return HasValue( p, key, out var value ) ? FiniteNumber( value, key ) : fallback;
	}

	internal static int OptionalInt( JsonElement p, string key, int fallback )
	{
		if ( !HasValue( p, key, out var value ) ) return fallback;
		if ( value.ValueKind != JsonValueKind.Number || !value.TryGetInt32( out var result ) )
			throw new Exception( $"{key} must be an integer" );
		return result;
	}

	internal static bool OptionalBool( JsonElement p, string key, bool fallback )
	{
		if ( !HasValue( p, key, out var value ) ) return fallback;
		if ( value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False )
			throw new Exception( $"{key} must be a boolean" );
		return value.GetBoolean();
	}

	internal static Vector3 ParseVector( JsonElement value, string label, bool allowUniform = false )
	{
		if ( allowUniform && value.ValueKind == JsonValueKind.Number )
		{
			var uniform = FiniteNumber( value, label );
			return new Vector3( uniform, uniform, uniform );
		}

		if ( value.ValueKind == JsonValueKind.Object )
		{
			float x = Component( value, "x", label );
			float y = Component( value, "y", label );
			float z = Component( value, "z", label );
			return new Vector3( x, y, z );
		}

		if ( value.ValueKind == JsonValueKind.Array )
		{
			var parts = value.EnumerateArray().ToArray();
			if ( parts.Length != 3 ) throw new Exception( $"{label} must contain exactly three numbers" );
			return new Vector3( FiniteNumber( parts[0], label ), FiniteNumber( parts[1], label ), FiniteNumber( parts[2], label ) );
		}

		if ( value.ValueKind == JsonValueKind.String )
		{
			var parts = ( value.GetString() ?? "" ).Split( ',', StringSplitOptions.TrimEntries );
			if ( parts.Length != 3 ) throw new Exception( $"{label} must be a comma string with exactly three numbers" );
			return new Vector3( ParseFiniteString( parts[0], label ), ParseFiniteString( parts[1], label ), ParseFiniteString( parts[2], label ) );
		}

		throw new Exception( $"{label} must be an object, three-item array, or comma string" );
	}

	internal static Rotation ParseRotation( JsonElement value, string label )
	{
		if ( ClaudeBridge.TryUnwrapStructuredJsonString( value, out var structured ) )
			return ParseRotation( structured, label );

		if ( value.ValueKind == JsonValueKind.Object )
		{
			float pitch = Component( value, "pitch", label );
			float yaw = Component( value, "yaw", label );
			float roll = Component( value, "roll", label );
			return Rotation.From( pitch, yaw, roll );
		}
		var angles = ParseVector( value, label );
		return Rotation.From( angles.x, angles.y, angles.z );
	}

	internal static Model LoadModel( string modelPath )
	{
		Model model;
		try { model = Model.Load( modelPath ); }
		catch ( Exception ex ) { throw new Exception( $"Could not load model '{modelPath}': {ex.Message}" ); }
		if ( model == null ) throw new Exception( $"Model not found: {modelPath}" );
		if ( model.IsError ) throw new Exception( $"Model.Load returned the error model for '{modelPath}'" );
		return model;
	}

	internal static object PublishPlan( PlacementPlanV2 plan )
	{
		PlacementPlanV2Store.Add( plan );
		try
		{
			var receipt = PlanReceipt( plan );
			_ = JsonSerializer.Serialize( receipt );
			return receipt;
		}
		catch
		{
			PlacementPlanV2Store.Complete( plan );
			throw;
		}
	}

	internal static object PlanReceipt( PlacementPlanV2 plan )
	{
		return new
		{
			dryRun = true,
			planId = plan.Id,
			kind = KindName( plan.Kind ),
			scene = plan.Scene.Name,
			created = 0,
			requested = plan.RequestedCount,
			proposed = plan.Placements.Count,
			expiresAt = plan.ExpiresUtc.ToString( "O", CultureInfo.InvariantCulture ),
			limits = new { maxPlans = PlacementPlanV2Store.MaxPlans, maxPlacements = PlacementPlanV2Store.MaxPlacementsPerPlan },
			metadata = plan.Metadata,
			warnings = plan.Warnings.ToArray(),
			modelGeometry = plan.Model == null ? null : BoundsInspection.DescribeBox(
				plan.Model.Bounds,
				"model_local",
				"Model.Bounds",
				"placement-plan source model in its default pose" ),
			placements = plan.Placements.Select( SpecReceipt ).ToArray()
		};
	}

	internal static object SpecReceipt( PlacementSpecV2 spec ) => new
	{
		slot = spec.Slot,
		name = spec.Name,
		transform = TransformReceipt( spec.Position, spec.Rotation, spec.Scale ),
		groundResolved = spec.GroundResolved,
		groundingOffsetZ = spec.GroundingOffsetZ
	};

	internal static object TransformReceipt( Vector3 position, Rotation rotation, Vector3 scale ) => new
	{
		position = Vec( position ),
		rotation = Rot( rotation ),
		scale = Vec( scale )
	};

	internal static object Snapshot( GameObject go, BBox bounds )
	{
		var center = bounds.Center;
		var size = bounds.Size;
		var extents = bounds.Extents;
		return new
		{
			parentId = go.Parent?.Id.ToString(),
			local = TransformReceipt( go.LocalPosition, go.LocalRotation, go.LocalScale ),
			world = TransformReceipt( go.WorldPosition, go.WorldRotation, go.WorldScale ),
			bounds = new
			{
				center = Vec( center ),
				size = Vec( size ),
				extents = Vec( extents ),
				mins = Vec( center - extents ),
				maxs = Vec( center + extents ),
				radius = size.Length * 0.5f,
				empty = size.Length < 0.01f
			}
		};
	}

	internal static string KindName( PlacementPlanV2Kind kind ) => kind switch
	{
		PlacementPlanV2Kind.Path => "place_along_path",
		PlacementPlanV2Kind.Grid => "grid_duplicate",
		_ => "scatter_props"
	};

	internal static object Vec( Vector3 value ) => new { value.x, value.y, value.z };
	internal static object Rot( Rotation value ) => new { pitch = value.Pitch(), yaw = value.Yaw(), roll = value.Roll() };

	internal static Rotation AlignRotation( Vector3 direction )
	{
		var forward = direction.Normal;
		var up = MathF.Abs( forward.z ) > 0.999f ? Vector3.Forward : Vector3.Up;
		return Rotation.LookAt( forward, up );
	}

	internal static void ValidateScaleRange( float minimum, float maximum )
	{
		if ( minimum <= 0f || maximum <= 0f )
			throw new Exception( "scale values must be greater than zero" );
		if ( minimum > maximum )
			throw new Exception( "minimum scale cannot be greater than maximum scale" );
	}

	internal static bool NearlyEqual( Vector3 left, Vector3 right, float tolerance = 0.001f ) =>
		MathF.Abs( left.x - right.x ) <= tolerance
		&& MathF.Abs( left.y - right.y ) <= tolerance
		&& MathF.Abs( left.z - right.z ) <= tolerance;

	internal static bool NearlyEqual( Rotation left, Rotation right, float tolerance = 0.01f ) =>
		AngleDelta( left.Pitch(), right.Pitch() ) <= tolerance
		&& AngleDelta( left.Yaw(), right.Yaw() ) <= tolerance
		&& AngleDelta( left.Roll(), right.Roll() ) <= tolerance;

	private static float AngleDelta( float left, float right )
	{
		float delta = MathF.Abs( (left - right) % 360f );
		return delta > 180f ? 360f - delta : delta;
	}

	private static float Component( JsonElement value, string key, string label )
	{
		return value.TryGetProperty( key, out var component ) ? FiniteNumber( component, $"{label}.{key}" ) : 0f;
	}

	private static float FiniteNumber( JsonElement value, string label )
	{
		if ( value.ValueKind != JsonValueKind.Number || !value.TryGetSingle( out var result )
			|| float.IsNaN( result ) || float.IsInfinity( result ) )
			throw new Exception( $"{label} must be a finite number" );
		return result;
	}

	private static float ParseFiniteString( string value, string label )
	{
		if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result )
			|| float.IsNaN( result ) || float.IsInfinity( result ) )
			throw new Exception( $"{label} must contain finite numbers" );
		return result;
	}
}

public class SetTransformV2Handler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		try
		{
			var id = PlacementPlanV2Helpers.RequiredString( p, "id" );
			if ( !Guid.TryParse( id, out var guid ) ) throw new Exception( "Invalid GUID" );
			var go = scene.Directory.FindByGuid( guid );
			if ( go == null ) throw new Exception( $"GameObject not found: {id}" );

			bool hasLocal = PlacementPlanV2Helpers.HasValue( p, "local", out var localElement );
			bool legacyLocal = hasLocal && PlacementPlanV2Helpers.OptionalBool( p, "local", false );
			bool hasSpace = PlacementPlanV2Helpers.HasValue( p, "space", out var spaceElement );
			string space = hasSpace && spaceElement.ValueKind == JsonValueKind.String
				? spaceElement.GetString()?.Trim().ToLowerInvariant() : null;
			if ( hasSpace && space != "local" && space != "world" )
				throw new Exception( "space must be 'world' or 'local'" );
			if ( hasLocal && hasSpace && legacyLocal != ( space == "local" ) )
				throw new Exception( "space and legacy local parameters conflict" );
			bool useLocal = hasSpace ? space == "local" : legacyLocal;

			bool hasPosition = PlacementPlanV2Helpers.HasValue( p, "position", out var positionElement );
			bool hasRotation = PlacementPlanV2Helpers.HasValue( p, "rotation", out var rotationElement );
			bool hasScale = PlacementPlanV2Helpers.HasValue( p, "scale", out var scaleElement );
			Vector3 position = hasPosition ? PlacementPlanV2Helpers.ParseVector( positionElement, "position" ) : default;
			Rotation rotation = hasRotation ? PlacementPlanV2Helpers.ParseRotation( rotationElement, "rotation" ) : default;
			Vector3 scale = hasScale ? PlacementPlanV2Helpers.ParseVector( scaleElement, "scale", true ) : default;

			var oldLocalPosition = go.LocalPosition;
			var oldLocalRotation = go.LocalRotation;
			var oldLocalScale = go.LocalScale;
			var oldWorldPosition = go.WorldPosition;
			var oldWorldRotation = go.WorldRotation;
			var oldWorldScale = go.WorldScale;
			var before = PlacementPlanV2Helpers.Snapshot( go, go.GetBounds() );

			try
			{
				if ( hasPosition )
				{
					if ( useLocal ) go.LocalPosition = position; else go.WorldPosition = position;
				}
				if ( hasRotation )
				{
					if ( useLocal ) go.LocalRotation = rotation; else go.WorldRotation = rotation;
				}
				if ( hasScale )
				{
					if ( useLocal ) go.LocalScale = scale; else go.WorldScale = scale;
				}

				var changed = new List<string>( 3 );
				if ( hasPosition && !PlacementPlanV2Helpers.NearlyEqual(
					useLocal ? oldLocalPosition : oldWorldPosition,
					useLocal ? go.LocalPosition : go.WorldPosition ) ) changed.Add( "position" );
				if ( hasRotation && !PlacementPlanV2Helpers.NearlyEqual(
					useLocal ? oldLocalRotation : oldWorldRotation,
					useLocal ? go.LocalRotation : go.WorldRotation ) ) changed.Add( "rotation" );
				if ( hasScale && !PlacementPlanV2Helpers.NearlyEqual(
					useLocal ? oldLocalScale : oldWorldScale,
					useLocal ? go.LocalScale : go.WorldScale ) ) changed.Add( "scale" );
				var gameObject = ClaudeBridge.SerializeGo( go );
				var after = PlacementPlanV2Helpers.Snapshot( go, go.GetBounds() );
				return Task.FromResult<object>( new
				{
					transformed = true,
					gameObject,
					spaceApplied = useLocal ? "local" : "world",
					changed = changed.ToArray(),
					noOp = changed.Count == 0,
					before,
					after
				} );
			}
			catch ( Exception applyError )
			{
				bool rollbackComplete = true;
				try
				{
					go.LocalPosition = oldLocalPosition;
					go.LocalRotation = oldLocalRotation;
					go.LocalScale = oldLocalScale;
				}
				catch { rollbackComplete = false; }
				return Task.FromResult<object>( new
				{
					error = $"set_transform failed while applying values or building its receipt: {applyError.Message}",
					rollbackAttempted = true,
					rolledBack = rollbackComplete,
					rollbackComplete
				} );
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"set_transform failed: {ex.Message}" } );
		}
	}
}

public class PlaceAlongPathV2Handler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !PlacementPlanV2Helpers.IsDryRun( p ) ) return new PlaceAlongPathHandler().Execute( p );
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		try
		{
			string modelPath = PlacementPlanV2Helpers.RequiredString( p, "model" );
			float spacing = PlacementPlanV2Helpers.OptionalFloat( p, "spacing", 200f );
			if ( spacing <= 0f ) throw new Exception( "spacing must be greater than zero" );
			float jitter = PlacementPlanV2Helpers.OptionalFloat( p, "jitter", 0f );
			if ( jitter < 0f ) throw new Exception( "jitter cannot be negative" );
			float minScale = PlacementPlanV2Helpers.OptionalFloat( p, "min_scale", 1f );
			float maxScale = PlacementPlanV2Helpers.OptionalFloat( p, "max_scale", 1f );
			PlacementPlanV2Helpers.ValidateScaleRange( minScale, maxScale );
			int seed = PlacementPlanV2Helpers.OptionalInt( p, "seed", 42 );
			string name = p.TryGetProperty( "name", out var nameElement ) && nameElement.ValueKind == JsonValueKind.String
				? nameElement.GetString() : "PathItem";
			bool align = PlacementPlanV2Helpers.OptionalBool( p, "align", false );
			bool randomizeYaw = PlacementPlanV2Helpers.OptionalBool( p, "randomizeYaw", false );

			if ( !p.TryGetProperty( "points", out var pointsElement ) || pointsElement.ValueKind != JsonValueKind.Array )
				throw new Exception( "points must be an array" );
			var points = pointsElement.EnumerateArray()
				.Select( ( point, index ) => PlacementPlanV2Helpers.ParseVector( point, $"points[{index}]" ) )
				.ToList();
			if ( points.Count < 2 ) throw new Exception( "need at least 2 points" );

			var plan = new PlacementPlanV2
			{
				Kind = PlacementPlanV2Kind.Path,
				Scene = scene,
				ModelPath = modelPath,
				Model = PlacementPlanV2Helpers.LoadModel( modelPath ),
				CreateGroup = true,
				GroupName = $"== {name}s ==",
				GroupPosition = Vector3.Zero
			};
			var rng = new Random( seed );
			int placed = 0;
			for ( int i = 0; i < points.Count - 1; i++ )
			{
				var from = points[i];
				var segment = points[i + 1] - from;
				float length = segment.Length;
				if ( length < 0.01f ) continue;
				var direction = segment / length;
				int steps = Math.Max( 1, (int)( length / spacing ) );
				for ( int step = 0; step <= steps; step++ )
				{
					if ( plan.Placements.Count >= PlacementPlanV2Store.MaxPlacementsPerPlan )
						throw new Exception( $"path preview exceeds the {PlacementPlanV2Store.MaxPlacementsPerPlan} item limit" );
					float fraction = (float)step / steps;
					float jitterX = (float)( rng.NextDouble() * 2.0 - 1.0 ) * jitter;
					float jitterY = (float)( rng.NextDouble() * 2.0 - 1.0 ) * jitter;
					var position = from + segment * fraction + new Vector3( jitterX, jitterY, 0f );
					Rotation rotation = align
						? PlacementPlanV2Helpers.AlignRotation( direction )
						: randomizeYaw ? Rotation.FromYaw( (float)( rng.NextDouble() * 360.0 ) ) : Rotation.Identity;
					float uniformScale = MathX.Lerp( minScale, maxScale, (float)rng.NextDouble() );
					plan.Placements.Add( new PlacementSpecV2
					{
						Slot = plan.Placements.Count,
						Name = $"{name} {++placed}",
						Position = position,
						Rotation = rotation,
						Scale = new Vector3( uniformScale )
					} );
				}
			}
			if ( plan.Placements.Count == 0 )
				throw new Exception( "path produced no placements; provide at least one non-zero segment" );
			plan.RequestedCount = plan.Placements.Count;
			plan.Metadata = new { seed, spacing, jitter, minScale, maxScale, align, randomizeYaw, duplicateSegmentEndpoints = points.Count > 2 };
			if ( points.Count > 2 )
				plan.Warnings.Add( "Adjacent path segments each include their shared endpoint, preserving legacy placement behavior." );
			return Task.FromResult( PlacementPlanV2Helpers.PublishPlan( plan ) );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"place_along_path preview failed: {ex.Message}" } );
		}
	}
}

public class GridDuplicateV2Handler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !PlacementPlanV2Helpers.IsDryRun( p ) ) return new GridDuplicateHandler().Execute( p );
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		try
		{
			string sourceId = PlacementPlanV2Helpers.RequiredString( p, "id" );
			if ( !Guid.TryParse( sourceId, out var sourceGuid ) ) throw new Exception( "id must be a GameObject GUID" );
			var source = scene.Directory.FindByGuid( sourceGuid );
			if ( source == null ) throw new Exception( $"GameObject not found: {sourceId}" );
			int requestedCountX = PlacementPlanV2Helpers.OptionalInt( p, "countX", 1 );
			int requestedCountY = PlacementPlanV2Helpers.OptionalInt( p, "countY", 1 );
			int requestedCountZ = PlacementPlanV2Helpers.OptionalInt( p, "countZ", 1 );
			int countX = Math.Clamp( requestedCountX, 1, 50 );
			int countY = Math.Clamp( requestedCountY, 1, 50 );
			int countZ = Math.Clamp( requestedCountZ, 1, 50 );
			int requestedCopies = countX * countY * countZ - 1;
			var spacing = PlacementPlanV2Helpers.HasValue( p, "spacing", out var spacingElement )
				? PlacementPlanV2Helpers.ParseVector( spacingElement, "spacing" ) : new Vector3( 100f, 100f, 100f );
			var basePosition = source.WorldPosition;
			var plan = new PlacementPlanV2
			{
				Kind = PlacementPlanV2Kind.Grid,
				Scene = scene,
				SourceId = sourceId,
				SourceParentId = source.Parent?.Id.ToString(),
				SourcePosition = source.WorldPosition,
				SourceRotation = source.WorldRotation,
				SourceScale = source.WorldScale,
				CreateGroup = false,
				RequestedCount = requestedCopies,
				Metadata = new
				{
					sourceId,
					requestedGrid = new { countX = requestedCountX, countY = requestedCountY, countZ = requestedCountZ },
					effectiveGrid = new { countX, countY, countZ },
					spacing = PlacementPlanV2Helpers.Vec( spacing ),
					cap = 500
				}
			};

			for ( int x = 0; x < countX && plan.Placements.Count < 500; x++ )
				for ( int y = 0; y < countY && plan.Placements.Count < 500; y++ )
					for ( int z = 0; z < countZ && plan.Placements.Count < 500; z++ )
					{
						if ( x == 0 && y == 0 && z == 0 ) continue;
						plan.Placements.Add( new PlacementSpecV2
						{
							Slot = plan.Placements.Count,
							Name = source.Name,
							Position = basePosition + new Vector3( x * spacing.x, y * spacing.y, z * spacing.z ),
							Rotation = source.WorldRotation,
							Scale = source.WorldScale
						} );
					}
			if ( plan.Placements.Count == 0 )
				throw new Exception( "grid produces no clones; increase at least one count above 1" );
			if ( requestedCountX != countX || requestedCountY != countY || requestedCountZ != countZ )
				plan.Warnings.Add( "One or more grid dimensions were clamped to the supported range 1-50." );
			if ( requestedCopies > plan.Placements.Count )
				plan.Warnings.Add( $"Grid requested {requestedCopies} clones; preview is capped at {plan.Placements.Count}." );
			return Task.FromResult( PlacementPlanV2Helpers.PublishPlan( plan ) );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"grid_duplicate preview failed: {ex.Message}" } );
		}
	}
}

public class ScatterPropsV2Handler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !PlacementPlanV2Helpers.IsDryRun( p ) ) return new ScatterPropsHandler().Execute( p );
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		try
		{
			string modelPath = PlacementPlanV2Helpers.RequiredString( p, "model" );
			var center = PlacementPlanV2Helpers.HasValue( p, "center", out var centerElement )
				? PlacementPlanV2Helpers.ParseVector( centerElement, "center" ) : Vector3.Zero;
			float radius = PlacementPlanV2Helpers.OptionalFloat( p, "radius", 256f );
			if ( radius < 0f ) throw new Exception( "radius cannot be negative" );
			int requestedCount = PlacementPlanV2Helpers.OptionalInt( p, "count", 10 );
			int count = Math.Clamp( requestedCount, 1, 300 );
			bool randomYaw = PlacementPlanV2Helpers.OptionalBool( p, "randomYaw", true );
			bool snapToGround = PlacementPlanV2Helpers.OptionalBool( p, "snapToGround", true );
			float scaleMin = PlacementPlanV2Helpers.OptionalFloat( p, "scaleMin", 1f );
			float scaleMax = PlacementPlanV2Helpers.OptionalFloat( p, "scaleMax", 1f );
			PlacementPlanV2Helpers.ValidateScaleRange( scaleMin, scaleMax );
			int signedSeed = PlacementPlanV2Helpers.OptionalInt( p, "seed", 1 );
			uint seed = unchecked( (uint)signedSeed );
			string name = p.TryGetProperty( "name", out var nameElement ) && nameElement.ValueKind == JsonValueKind.String
				? nameElement.GetString() : "Prop";
			bool createGroup = PlacementPlanV2Helpers.OptionalBool( p, "group", true );
			bool hasTint = PlacementPlanV2Helpers.HasValue( p, "tint", out var tintElement );
			Color tint = hasTint ? VisualHelpers.ParseColorElement( tintElement, Color.White ) : Color.White;

			var plan = new PlacementPlanV2
			{
				Kind = PlacementPlanV2Kind.Scatter,
				Scene = scene,
				ModelPath = modelPath,
				Model = PlacementPlanV2Helpers.LoadModel( modelPath ),
				CreateGroup = createGroup,
				GroupName = $"{name} Scatter",
				GroupPosition = center,
				HasTint = hasTint,
				Tint = tint,
				RequestedCount = requestedCount,
				Metadata = new { seed = signedSeed, radius, requestedCount, effectiveCount = count, randomYaw, snapToGround, scaleMin, scaleMax, grouped = createGroup, grounding = "trace.EndPosition.z - Model.Bounds.Mins.z * uniformScale" }
			};
			var rng = new Lcg( seed );
			int groundMisses = 0;
			for ( int i = 0; i < count; i++ )
			{
				var direction = Rotation.FromYaw( rng.Range( 0f, 360f ) ).Forward;
				var position = center + direction * ( radius * rng.Float01() );
				var rotation = randomYaw ? Rotation.FromYaw( rng.Range( 0f, 360f ) ) : Rotation.Identity;
				float uniformScale = scaleMax > scaleMin ? rng.Range( scaleMin, scaleMax ) : scaleMin;
				var scale = new Vector3( uniformScale );
				bool groundResolved = false;
				float groundingOffsetZ = 0f;
				if ( snapToGround )
				{
					try
					{
						var trace = scene.Trace.Ray( position + Vector3.Up * 2000f, position + Vector3.Down * 20000f ).Run();
						if ( trace.Hit )
						{
							groundingOffsetZ = -plan.Model.Bounds.Mins.z * uniformScale;
							position = new Vector3( position.x, position.y, trace.EndPosition.z + groundingOffsetZ );
							groundResolved = true;
						}
					}
					catch { }
					if ( !groundResolved ) groundMisses++;
				}
				plan.Placements.Add( new PlacementSpecV2
				{
					Slot = i,
					Name = $"{name} {i + 1}",
					Position = position,
					Rotation = rotation,
					Scale = scale,
					GroundResolved = groundResolved,
					GroundingOffsetZ = groundingOffsetZ
				} );
			}
			if ( requestedCount != count )
				plan.Warnings.Add( $"Requested count {requestedCount} was clamped to {count} (supported range 1-300)." );
			if ( snapToGround && groundMisses > 0 )
				plan.Warnings.Add( $"Ground trace missed or failed for {groundMisses}/{count} placements; their original Z values are preserved." );
			return Task.FromResult( PlacementPlanV2Helpers.PublishPlan( plan ) );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"scatter_props preview failed: {ex.Message}" } );
		}
	}
}

public class CommitPlacementPlanHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );
		string planId;
		try { planId = PlacementPlanV2Helpers.RequiredString( p, "planId" ); }
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = ex.Message } ); }
		if ( !PlacementPlanV2Store.TryBeginCommit( planId, scene, out var plan, out var planError ) )
			return Task.FromResult<object>( new { error = planError } );

		var createdObjects = new List<GameObject>();
		var receipts = new List<object>( plan.Placements.Count );
		GameObject group = null;
		try
		{
			GameObject source = null;
			if ( plan.Kind == PlacementPlanV2Kind.Grid )
			{
				if ( !Guid.TryParse( plan.SourceId, out var sourceGuid ) ) throw new Exception( "Stored source GUID is invalid" );
				source = scene.Directory.FindByGuid( sourceGuid );
				if ( source == null ) throw new Exception( $"Source GameObject no longer exists: {plan.SourceId}" );
				string currentParentId = source.Parent?.Id.ToString();
				if ( !string.Equals( currentParentId, plan.SourceParentId, StringComparison.Ordinal )
					|| !PlacementPlanV2Helpers.NearlyEqual( source.WorldPosition, plan.SourcePosition )
					|| !PlacementPlanV2Helpers.NearlyEqual( source.WorldRotation, plan.SourceRotation )
					|| !PlacementPlanV2Helpers.NearlyEqual( source.WorldScale, plan.SourceScale ) )
				{
					throw new Exception( "Grid source transform or parent changed after preview; no clones were created. Preview again." );
				}
			}
			else if ( plan.Model == null || plan.Model.IsError )
			{
				throw new Exception( $"Stored model is no longer available: {plan.ModelPath}" );
			}

			if ( plan.CreateGroup )
			{
				group = scene.CreateObject( true );
				createdObjects.Add( group );
				group.Name = plan.GroupName;
				group.WorldPosition = plan.GroupPosition;
			}

			if ( plan.Kind == PlacementPlanV2Kind.Grid )
			{
				using ( scene.Push() )
				{
					foreach ( var spec in plan.Placements )
					{
						var clone = source.Clone();
						createdObjects.Add( clone );
						clone.Name = spec.Name;
						ApplyTransform( clone, spec );
						receipts.Add( Receipt( spec, clone ) );
					}
				}
			}
			else
			{
				foreach ( var spec in plan.Placements )
				{
					var go = scene.CreateObject( true );
					createdObjects.Add( go );
					go.Name = spec.Name;
					if ( group != null ) go.SetParent( group, true );
					ApplyTransform( go, spec );
					var renderer = go.AddComponent<ModelRenderer>();
					renderer.Model = plan.Model;
					if ( plan.HasTint ) renderer.Tint = plan.Tint;
					receipts.Add( Receipt( spec, go ) );
				}
			}

			var result = new
			{
				committed = true,
				planId,
				kind = PlacementPlanV2Helpers.KindName( plan.Kind ),
				requested = plan.RequestedCount,
				created = receipts.Count,
				groupId = group?.Id.ToString(),
				warnings = plan.Warnings.ToArray(),
				receipts = receipts.ToArray()
			};
			PlacementPlanV2Store.Complete( plan );
			return Task.FromResult<object>( result );
		}
		catch ( Exception ex )
		{
			int rolledBack = 0;
			int rollbackFailures = 0;
			for ( int i = createdObjects.Count - 1; i >= 0; i-- )
			{
				var go = createdObjects[i];
				if ( go == null || !go.IsValid() ) continue;
				try { go.Destroy(); rolledBack++; }
				catch { rollbackFailures++; }
			}
			bool rollbackComplete = rollbackFailures == 0;
			bool retryable = rollbackComplete
				&& PlacementPlanV2Store.RestoreAfterCompleteRollback( plan );
			if ( !rollbackComplete ) PlacementPlanV2Store.Complete( plan );
			return Task.FromResult<object>( new
			{
				error = $"commit_placement_plan failed: {ex.Message}",
				planId,
				rolledBack,
				rollbackFailures,
				rollbackComplete,
				retryable,
				note = retryable
					? "All created objects were removed and the plan was restored for a retry."
					: rollbackComplete
						? "All created objects were removed, but the plan expired or became unavailable and cannot be retried."
						: "Rollback was incomplete, so the plan was consumed to prevent duplicate creation."
			} );
		}
	}

	private static void ApplyTransform( GameObject go, PlacementSpecV2 spec )
	{
		go.WorldPosition = spec.Position;
		go.WorldRotation = spec.Rotation;
		go.WorldScale = spec.Scale;
	}

	private static object Receipt( PlacementSpecV2 spec, GameObject go ) => new
	{
		slot = spec.Slot,
		id = go.Id.ToString(),
		name = go.Name,
		groundResolved = spec.GroundResolved,
		groundingOffsetZ = spec.GroundingOffsetZ,
		transform = PlacementPlanV2Helpers.TransformReceipt( go.WorldPosition, go.WorldRotation, go.WorldScale )
	};
}
