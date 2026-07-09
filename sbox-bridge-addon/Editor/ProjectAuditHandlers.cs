using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════
// Batch 51 — Project audit & batch operations (v2 relaunch wave 1)
//   find_broken_references  — scene-wide broken/dead reference scan
//   batch_set_property      — one property across many objects, with dry-run
//   describe_project        — one-call project orientation summary
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// find_broken_references — scan the open scene for null models on renderers,
/// destroyed-but-still-referenced GameObjects/Components in component properties,
/// and unresolvable (null) component entries.
/// </summary>
public class FindBrokenReferencesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		int limit = p.TryGetProperty( "limit", out var l ) ? l.GetInt32() : 100;
		if ( limit < 1 ) limit = 1; if ( limit > 500 ) limit = 500;

		var issues = new List<object>();
		int total = 0;
		int objectsScanned = 0;

		void AddIssue( GameObject go, string component, string kind, string detail )
		{
			total++;
			if ( issues.Count < limit )
				issues.Add( new { id = go.Id.ToString(), name = go.Name, component, kind, detail } );
		}

		foreach ( var go in scene.GetAllObjects( true ) )
		{
			if ( go == null ) continue;
			objectsScanned++;

			foreach ( var comp in go.Components.GetAll() )
			{
				if ( comp == null )
				{
					AddIssue( go, "(null)", "missing_component", "Component entry is null — its type may no longer exist/compile" );
					continue;
				}

				if ( comp is ModelRenderer mr && mr.Model == null )
					AddIssue( go, comp.GetType().Name, "missing_model", "Renderer has no Model assigned" );

				// Destroyed-but-referenced objects/components: a null ref is usually a
				// legitimate 'unset optional', but a ref to a DESTROYED thing is broken.
				var typeDesc = Game.TypeLibrary.GetType( comp.GetType().Name );
				if ( typeDesc == null ) continue;
				foreach ( var prop in typeDesc.Properties )
				{
					var pt = prop.PropertyType;
					bool isGo = pt == typeof( GameObject );
					bool isComp = typeof( Component ).IsAssignableFrom( pt );
					if ( !isGo && !isComp ) continue;

					object val;
					try { val = prop.GetValue( comp ); }
					catch { continue; }
					if ( val == null ) continue;

					if ( val is GameObject g && !g.IsValid() )
						AddIssue( go, comp.GetType().Name, "dead_gameobject_ref", $"{prop.Name} references a destroyed GameObject" );
					else if ( val is Component c && !c.IsValid() )
						AddIssue( go, comp.GetType().Name, "dead_component_ref", $"{prop.Name} references a destroyed Component" );
				}
			}
		}

		// v2 round 2: scan .scene/.prefab FILES for prefab references to files that no
		// longer exist ({"_type":"gameobject","prefab":"prefabs/x.prefab"} with x deleted
		// or renamed) — the break class scene-level checks can't see.
		int filesScanned = 0;
		bool scanFiles = !( p.TryGetProperty( "scanFiles", out var sf ) && sf.ValueKind == JsonValueKind.False );
		if ( scanFiles )
		{
			try
			{
				var root = Project.Current?.GetRootPath();
				if ( root != null )
				{
					var rx = new System.Text.RegularExpressions.Regex( "\"prefab\":\\s*\"([^\"]+)\"" );
					var files = Directory.GetFiles( root, "*.scene", SearchOption.AllDirectories )
						.Concat( Directory.GetFiles( root, "*.prefab", SearchOption.AllDirectories ) )
						.Where( f => { var r = Path.GetRelativePath( root, f ).Replace( '\\', '/' ); return !r.StartsWith( "Libraries/" ) && !r.StartsWith( ".sbox/" ); } );
					foreach ( var file in files )
					{
						filesScanned++;
						var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
						foreach ( System.Text.RegularExpressions.Match m in rx.Matches( File.ReadAllText( file ) ) )
						{
							var refPath = m.Groups[1].Value;
							bool exists = File.Exists( Path.Combine( root, refPath ) )
								|| File.Exists( Path.Combine( root, "Assets", refPath ) );
							if ( !exists )
							{
								total++;
								if ( issues.Count < limit )
									issues.Add( new { id = (string)null, name = rel, component = "(file)", kind = "missing_prefab_file", detail = $"references '{refPath}' which does not exist in the project" } );
							}
						}
					}
				}
			}
			catch { /* file scan is best-effort — scene checks above already reported */ }
		}

		return Task.FromResult<object>( new
		{
			total,
			showing = issues.Count,
			truncated = total > issues.Count,
			objectsScanned,
			filesScanned,
			issues,
			note = total == 0
				? "No broken references found."
				: "Fix missing_model with assign_model; clear dead refs with set_property (value null) or set_component_reference to a live target; missing_prefab_file means a .scene/.prefab references a deleted/renamed prefab — fix the path or recreate it with create_prefab."
		} );
	}
}

/// <summary>
/// batch_set_property — set one component property to the same value across many
/// GameObjects, with a dry-run mode that validates and reports without applying.
/// </summary>
public class BatchSetPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "ids", out var idsEl ) || idsEl.ValueKind != JsonValueKind.Array || idsEl.GetArrayLength() == 0 )
			return Task.FromResult<object>( new { error = "ids (non-empty array of GameObject GUIDs) is required" } );
		var componentType = p.TryGetProperty( "component", out var ct ) ? ct.GetString() : null;
		if ( string.IsNullOrWhiteSpace( componentType ) )
			return Task.FromResult<object>( new { error = "component (type name) is required" } );
		var propertyName = p.TryGetProperty( "property", out var pn ) ? pn.GetString() : null;
		if ( string.IsNullOrWhiteSpace( propertyName ) )
			return Task.FromResult<object>( new { error = "property (name) is required" } );
		if ( !p.TryGetProperty( "value", out var valueEl ) )
			return Task.FromResult<object>( new { error = "value is required" } );
		bool dryRun = p.TryGetProperty( "dryRun", out var dr ) && dr.ValueKind == JsonValueKind.True;

		var results = new List<object>();
		int succeeded = 0, failed = 0;

		foreach ( var idEl in idsEl.EnumerateArray() )
		{
			var id = idEl.GetString();
			void Fail( string why ) { failed++; results.Add( new { id, ok = false, error = why } ); }

			var go = ClaudeBridge.ResolveGameObject( scene, id );
			if ( go == null ) { Fail( "GameObject not found" ); continue; }

			var component = go.Components.GetAll()
				.FirstOrDefault( c => c != null && c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );
			if ( component == null ) { Fail( $"No '{componentType}' component" ); continue; }

			var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name.Equals( propertyName, StringComparison.OrdinalIgnoreCase ) );
			if ( propDesc == null ) { Fail( $"Property '{propertyName}' not found on {componentType}" ); continue; }

			object current = null;
			try { current = propDesc.GetValue( component ); } catch { }

			if ( dryRun )
			{
				succeeded++;
				results.Add( new { id, ok = true, wouldChange = true, currentValue = current?.ToString() } );
				continue;
			}

			try
			{
				// Same coercion path as set_property: object-form value types first,
				// then the audited string-coercion (primitives, enums, asset refs).
				var pt = propDesc.PropertyType;
				if ( valueEl.ValueKind == JsonValueKind.Object )
				{
					object typed = null;
					if ( pt == typeof( Vector3 ) ) typed = ClaudeBridge.ParseVector3( valueEl );
					else if ( pt == typeof( Vector2 ) ) typed = ClaudeBridge.ParseVector2( valueEl );
					else if ( pt == typeof( Color ) ) typed = ClaudeBridge.ParseColor( valueEl );
					else if ( pt == typeof( Rotation ) ) typed = ClaudeBridge.ParseRotation( valueEl );
					if ( typed != null )
					{
						propDesc.SetValue( component, typed );
						succeeded++;
						results.Add( new { id, ok = true, previous = current?.ToString() } );
						continue;
					}
				}

				var valueStr = ClaudeBridge.ElementToValueString( valueEl );
				if ( !ClaudeBridge.CoercePropertyAndSet( pt, v => propDesc.SetValue( component, v ), propDesc.Name, valueStr, out var setErr ) )
				{ Fail( setErr ); continue; }

				succeeded++;
				results.Add( new { id, ok = true, previous = current?.ToString() } );
			}
			catch ( Exception ex )
			{
				Fail( ex.Message );
			}
		}

		return Task.FromResult<object>( new
		{
			total = results.Count,
			succeeded,
			failed,
			dryRun,
			results,
			note = dryRun
				? "Dry run — nothing was changed. Re-run with dryRun:false to apply."
				: $"Applied to {succeeded}/{results.Count} objects."
		} );
	}
}

/// <summary>
/// describe_project — a one-call orientation summary: project identity, scenes,
/// prefabs, code footprint, custom component types, and installed libraries.
/// </summary>
public class DescribeProjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var project = Project.Current;
		if ( project == null )
			return Task.FromResult<object>( new { error = "No current project" } );

		var root = project.GetRootPath();
		var scene = SceneEditorSession.Active?.Scene;

		string[] Rel( IEnumerable<string> paths, int cap ) =>
			paths.Select( f => Path.GetRelativePath( root, f ).Replace( '\\', '/' ) )
				.Where( f => !f.StartsWith( "Libraries/" ) && !f.StartsWith( ".sbox/" ) )
				.Take( cap ).ToArray();

		string[] scenes = Array.Empty<string>(), prefabs = Array.Empty<string>();
		int codeFiles = 0, razorFiles = 0;
		try { scenes = Rel( Directory.GetFiles( root, "*.scene", SearchOption.AllDirectories ), 50 ); } catch { }
		try { prefabs = Rel( Directory.GetFiles( root, "*.prefab", SearchOption.AllDirectories ), 50 ); } catch { }
		try { codeFiles = Directory.GetFiles( Path.Combine( root, "Code" ), "*.cs", SearchOption.AllDirectories ).Length; } catch { }
		try { razorFiles = Directory.GetFiles( Path.Combine( root, "Code" ), "*.razor", SearchOption.AllDirectories ).Length; } catch { }

		// Custom components = Component subclasses outside the engine namespaces.
		string[] customComponents = Array.Empty<string>();
		try
		{
			customComponents = Game.TypeLibrary.GetTypes<Component>()
				.Where( t => !t.IsAbstract && t.FullName != null
					&& !t.FullName.StartsWith( "Sandbox." ) && !t.FullName.StartsWith( "Editor." )
					&& !t.FullName.StartsWith( "Facepunch." ) )
				.Select( t => t.Name ).OrderBy( n => n ).Take( 100 ).ToArray();
		}
		catch { }

		string[] libraries = Array.Empty<string>();
		try
		{
			var libDir = Path.Combine( root, "Libraries" );
			if ( Directory.Exists( libDir ) )
				libraries = Directory.GetDirectories( libDir ).Select( Path.GetFileName ).OrderBy( n => n ).ToArray();
		}
		catch { }

		return Task.FromResult<object>( new
		{
			name = project.Config.Title,
			ident = project.Config.Ident,
			org = project.Config.Org,
			type = project.Config.Type,
			rootPath = root.Replace( '\\', '/' ),
			openScene = scene == null ? null : new { name = scene.Name, objectCount = scene.GetAllObjects( true ).Count() },
			scenes = new { total = scenes.Length, files = scenes },
			prefabs = new { total = prefabs.Length, files = prefabs },
			code = new { csFiles = codeFiles, razorFiles },
			customComponents = new { total = customComponents.Length, names = customComponents },
			libraries,
			note = "Orient here, then: get_scene_hierarchy for the open scene, describe_type for any component, list_prefabs/get_prefab_info for prefabs, find_broken_references for health."
		} );
	}
}
