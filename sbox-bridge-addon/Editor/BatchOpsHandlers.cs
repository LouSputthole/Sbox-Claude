using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════
// Batch 52 — bridge_batch buildout (v2 relaunch wave 2)
//   batch_delete, batch_add_component, batch_reparent — same dryRun
//   convention as batch_set_property: validate + report without applying.
// ═══════════════════════════════════════════════════════════════════

internal static class BatchOps
{
	/// <summary>Shared arg parsing: ids[] (required) + dryRun flag.</summary>
	internal static bool TryParse( JsonElement p, out List<string> ids, out bool dryRun, out string error )
	{
		ids = null; dryRun = false; error = null;
		if ( !p.TryGetProperty( "ids", out var idsEl ) || idsEl.ValueKind != JsonValueKind.Array || idsEl.GetArrayLength() == 0 )
		{
			error = "ids (non-empty array of GameObject GUIDs) is required";
			return false;
		}
		ids = idsEl.EnumerateArray().Select( e => e.GetString() ).ToList();
		dryRun = p.TryGetProperty( "dryRun", out var dr ) && dr.ValueKind == JsonValueKind.True;
		return true;
	}
}

/// <summary>batch_delete — destroy many GameObjects in one call, dry-run first.</summary>
public class BatchDeleteHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !BatchOps.TryParse( p, out var ids, out var dryRun, out var err ) )
			return Task.FromResult<object>( new { error = err } );

		var results = new List<object>();
		int succeeded = 0, failed = 0;
		foreach ( var id in ids )
		{
			var go = ClaudeBridge.ResolveGameObject( scene, id );
			if ( go == null ) { failed++; results.Add( new { id, ok = false, error = "GameObject not found" } ); continue; }

			if ( dryRun )
			{
				succeeded++;
				results.Add( new { id, ok = true, wouldDelete = go.Name, childCount = go.Children.Count } );
				continue;
			}
			var name = go.Name;
			go.Destroy();
			succeeded++;
			results.Add( new { id, ok = true, deleted = name } );
		}

		return Task.FromResult<object>( new
		{
			total = results.Count, succeeded, failed, dryRun, results,
			note = dryRun
				? "Dry run — nothing was deleted. Children counts shown; deletion destroys entire subtrees. Re-run with dryRun:false to apply."
				: $"Deleted {succeeded}/{results.Count} objects (subtrees included). No undo available — recreate via prefab/scene reload if needed."
		} );
	}
}

/// <summary>batch_add_component — add one component type to many GameObjects.</summary>
public class BatchAddComponentHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !BatchOps.TryParse( p, out var ids, out var dryRun, out var err ) )
			return Task.FromResult<object>( new { error = err } );

		var componentType = p.TryGetProperty( "component", out var ct ) ? ct.GetString() : null;
		if ( string.IsNullOrWhiteSpace( componentType ) )
			return Task.FromResult<object>( new { error = "component (type name) is required" } );
		bool skipExisting = !( p.TryGetProperty( "skipExisting", out var se ) && se.ValueKind == JsonValueKind.False );

		var typeDesc = Game.TypeLibrary.GetType( componentType );
		if ( typeDesc == null || typeDesc.IsAbstract )
			return Task.FromResult<object>( new { error = $"Component type not found (or abstract): {componentType}. Use list_available_components to search." } );

		var results = new List<object>();
		int succeeded = 0, failed = 0, skipped = 0;
		foreach ( var id in ids )
		{
			var go = ClaudeBridge.ResolveGameObject( scene, id );
			if ( go == null ) { failed++; results.Add( new { id, ok = false, error = "GameObject not found" } ); continue; }

			bool has = go.Components.GetAll().Any( c => c != null && c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );
			if ( has && skipExisting )
			{
				skipped++;
				results.Add( new { id, ok = true, skipped = true, reason = "already has the component" } );
				continue;
			}

			if ( dryRun )
			{
				succeeded++;
				results.Add( new { id, ok = true, wouldAdd = typeDesc.Name, alreadyHas = has } );
				continue;
			}

			try
			{
				go.Components.Create( typeDesc );
				succeeded++;
				results.Add( new { id, ok = true, added = typeDesc.Name } );
			}
			catch ( Exception ex )
			{
				failed++;
				results.Add( new { id, ok = false, error = ex.Message } );
			}
		}

		return Task.FromResult<object>( new
		{
			total = results.Count, succeeded, failed, skipped, dryRun, results,
			note = dryRun
				? "Dry run — nothing was added. Re-run with dryRun:false to apply, then configure via batch_set_property."
				: $"Added '{typeDesc.Name}' to {succeeded} objects ({skipped} skipped). Configure them via batch_set_property."
		} );
	}
}

/// <summary>batch_reparent — move many GameObjects under a new parent (or scene root).</summary>
public class BatchReparentHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );
		if ( !BatchOps.TryParse( p, out var ids, out var dryRun, out var err ) )
			return Task.FromResult<object>( new { error = err } );

		GameObject parent = null;
		string parentName = "(scene root)";
		if ( p.TryGetProperty( "parent", out var parEl ) && !string.IsNullOrWhiteSpace( parEl.GetString() ) )
		{
			parent = ClaudeBridge.ResolveGameObject( scene, parEl.GetString() );
			if ( parent == null )
				return Task.FromResult<object>( new { error = $"Parent GameObject not found: {parEl.GetString()}" } );
			parentName = parent.Name;
			if ( ids.Contains( parent.Id.ToString() ) )
				return Task.FromResult<object>( new { error = "parent is also in ids — an object can't be its own ancestor" } );
		}
		bool keepWorld = !( p.TryGetProperty( "keepWorldPosition", out var kw ) && kw.ValueKind == JsonValueKind.False );

		var results = new List<object>();
		int succeeded = 0, failed = 0;
		foreach ( var id in ids )
		{
			var go = ClaudeBridge.ResolveGameObject( scene, id );
			if ( go == null ) { failed++; results.Add( new { id, ok = false, error = "GameObject not found" } ); continue; }

			if ( dryRun )
			{
				succeeded++;
				results.Add( new { id, ok = true, name = go.Name, currentParent = go.Parent?.Name ?? "(scene root)", wouldMoveTo = parentName } );
				continue;
			}
			go.SetParent( parent, keepWorld );
			succeeded++;
			results.Add( new { id, ok = true, name = go.Name, movedTo = parentName } );
		}

		return Task.FromResult<object>( new
		{
			total = results.Count, succeeded, failed, dryRun, keepWorldPosition = keepWorld, results,
			note = dryRun
				? "Dry run — nothing was moved. Re-run with dryRun:false to apply."
				: $"Reparented {succeeded}/{results.Count} objects under {parentName}."
		} );
	}
}
