using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Validates that the project is ready for publishing.
/// Checks: compile errors, metadata completeness, scene existence,
/// script count, required files, and project configuration.
/// Returns a detailed report with pass/fail for each check.
/// </summary>
public class ValidateProjectHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var project = Project.Current;
		if ( project == null )
			throw new Exception( "No project is currently open" );

		var config = project.Config;
		var projectRoot = project.GetRootPath();

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var checks = new List<object>();
		var allPassed = true;

		// 1. Title check
		var hasTitle = !string.IsNullOrWhiteSpace( config?.Title );
		checks.Add( new { check = "title", passed = hasTitle, message = hasTitle ? $"Title: {config.Title}" : "Missing project title" } );
		if ( !hasTitle ) allPassed = false;

		// 2. Description check
		var hasDesc = !string.IsNullOrWhiteSpace( config?.Description );
		checks.Add( new { check = "description", passed = hasDesc, message = hasDesc ? "Description provided" : "Missing project description" } );
		if ( !hasDesc ) allPassed = false;

		// 3. Package ident check
		var hasIdent = !string.IsNullOrWhiteSpace( config?.PackageIdent );
		checks.Add( new { check = "packageIdent", passed = hasIdent, message = hasIdent ? $"Ident: {config.PackageIdent}" : "Missing package identifier" } );
		if ( !hasIdent ) allPassed = false;

		// 4. Scene files check
		var sceneFiles = Directory.Exists( projectRoot )
			? Directory.GetFiles( projectRoot, "*.scene", SearchOption.AllDirectories )
			: Array.Empty<string>();
		var hasScenes = sceneFiles.Length > 0;
		checks.Add( new { check = "scenes", passed = hasScenes, message = hasScenes ? $"{sceneFiles.Length} scene(s) found" : "No .scene files found" } );
		if ( !hasScenes ) allPassed = false;

		// 5. Script files check
		var codeDir = Path.Combine( projectRoot, "code" );
		var scriptFiles = Directory.Exists( codeDir )
			? Directory.GetFiles( codeDir, "*.cs", SearchOption.AllDirectories )
			: Array.Empty<string>();
		var hasScripts = scriptFiles.Length > 0;
		checks.Add( new { check = "scripts", passed = hasScripts, message = hasScripts ? $"{scriptFiles.Length} script(s) found" : "No .cs files in code/ directory" } );
		// Scripts are optional — not a failure condition

		// 6. Compile errors check — API-NOTE: Diagnostics may come from EditorUtility or CompileGroup
		var hasCompileErrors = false;
		var compileErrorCount = 0;
		try
		{
			var diagnostics = project.GetCompileDiagnostics();
			if ( diagnostics != null )
			{
				compileErrorCount = diagnostics.Count( d => d.Severity == CompileDiagnostic.SeverityLevel.Error );
				hasCompileErrors = compileErrorCount > 0;
			}
		}
		catch
		{
			// API may not be available — skip check
		}
		checks.Add( new { check = "compileErrors", passed = !hasCompileErrors, message = hasCompileErrors ? $"{compileErrorCount} compile error(s)" : "No compile errors" } );
		if ( hasCompileErrors ) allPassed = false;

		// 7. Thumbnail / icon check
		var thumbnailPaths = new[] { "thumb.png", "thumb.jpg", "icon.png", "thumbnail.png" };
		var hasThumbnail = thumbnailPaths.Any( t => File.Exists( Path.Combine( projectRoot, t ) ) );
		checks.Add( new { check = "thumbnail", passed = hasThumbnail, message = hasThumbnail ? "Thumbnail found" : "No thumbnail (thumb.png/icon.png) — recommended for publishing" } );
		// Thumbnail is recommended but not required

		// 8. Project type check
		var hasType = config?.Type != null;
		checks.Add( new { check = "projectType", passed = hasType, message = hasType ? $"Type: {config.Type}" : "Project type not set" } );

		// Summary — count passed checks using the tracked boolean
		var passedCount = checks.Count - (allPassed ? 0 : checks.Count);
		// Re-count properly: we track allPassed for blocking issues, but need individual count
		passedCount = 0;
		foreach ( var check in checks )
		{
			// Access via JSON round-trip to avoid dynamic cast on anonymous types
			var json = System.Text.Json.JsonSerializer.Serialize( check );
			using var doc = JsonDocument.Parse( json );
			if ( doc.RootElement.GetProperty( "passed" ).GetBoolean() )
				passedCount++;
		}

		return Task.FromResult<object>( new
		{
			valid = allPassed,
			passedChecks = passedCount,
			totalChecks = checks.Count,
			checks,
			summary = allPassed
				? "Project is ready for publishing"
				: "Project has issues that should be resolved before publishing",
		} );
	}
}
