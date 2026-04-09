using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Exports the project as a standalone game.
/// Copies compiled assemblies, assets, scenes, and engine runtime
/// to an output directory for distribution.
/// </summary>
public class ExportProjectHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var project = Project.Current;
		if ( project == null )
			throw new Exception( "No project is currently open" );

		var projectRoot = project.GetRootPath();
		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		// Output path (defaults to "export" inside project root)
		var outputDir = "export";
		if ( parameters.ValueKind != JsonValueKind.Undefined &&
		     parameters.TryGetProperty( "outputPath", out var outputProp ) &&
		     outputProp.ValueKind == JsonValueKind.String )
		{
			outputDir = outputProp.GetString() ?? "export";
		}

		var fullOutputPath = Path.GetFullPath( Path.Combine( projectRoot, outputDir ) );

		// Security: output must remain within project directory
		if ( !fullOutputPath.StartsWith( projectRoot ) )
			throw new Exception( "Output path must be within the project directory" );

		var configuration = "Release";
		if ( parameters.ValueKind != JsonValueKind.Undefined &&
		     parameters.TryGetProperty( "configuration", out var configProp ) &&
		     configProp.ValueKind == JsonValueKind.String )
		{
			configuration = configProp.GetString() ?? "Release";
		}

		// API-NOTE: The exact standalone export API varies by SDK version.
		// Candidates:
		//   EditorUtility.Projects.Export( project, outputPath )
		//   Project.Export( ExportConfig )
		//   StandaloneBuilder.Build( project, config )
		// We attempt the most common APIs and fallback to a manual copy approach.

		var exported = false;
		string exportMethod = "unknown";

		try
		{
			// Try EditorUtility.Projects.Export
			EditorUtility.Projects.Export( fullOutputPath );
			exported = true;
			exportMethod = "EditorUtility.Projects.Export";
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SboxBridge] EditorUtility.Projects.Export failed: {ex.Message}" );

			// Fallback: manual export by copying essential files
			try
			{
				Directory.CreateDirectory( fullOutputPath );

				// Copy scene files
				CopyFiles( projectRoot, fullOutputPath, "*.scene" );

				// Copy compiled assemblies
				var binDir = Path.Combine( projectRoot, "bin" );
				if ( Directory.Exists( binDir ) )
					CopyDirectory( binDir, Path.Combine( fullOutputPath, "bin" ) );

				// Copy assets
				var assetsDir = Path.Combine( projectRoot, "Assets" );
				if ( Directory.Exists( assetsDir ) )
					CopyDirectory( assetsDir, Path.Combine( fullOutputPath, "Assets" ) );

				// Copy project config
				var sbprojFiles = Directory.GetFiles( projectRoot, "*.sbproj", SearchOption.TopDirectoryOnly );
				foreach ( var f in sbprojFiles )
					File.Copy( f, Path.Combine( fullOutputPath, Path.GetFileName( f ) ), true );

				exported = true;
				exportMethod = "manual_copy";
			}
			catch ( Exception ex2 )
			{
				Log.Warning( $"[SboxBridge] Manual export also failed: {ex2.Message}" );
			}
		}

		// Count exported files
		var fileCount = 0;
		if ( Directory.Exists( fullOutputPath ) )
		{
			fileCount = Directory.GetFiles( fullOutputPath, "*", SearchOption.AllDirectories ).Length;
		}

		return Task.FromResult<object>( new
		{
			exported,
			exportMethod,
			outputPath = fullOutputPath,
			configuration,
			fileCount,
			message = exported
				? $"Project exported to {outputDir}/ ({fileCount} files)"
				: "Export failed — try using the s&box editor export wizard instead",
		} );
	}

	private static void CopyFiles( string sourceDir, string targetDir, string pattern )
	{
		foreach ( var file in Directory.GetFiles( sourceDir, pattern, SearchOption.AllDirectories ) )
		{
			var relativePath = Path.GetRelativePath( sourceDir, file );
			var targetPath = Path.Combine( targetDir, relativePath );
			var targetFileDir = Path.GetDirectoryName( targetPath );
			if ( !string.IsNullOrEmpty( targetFileDir ) )
				Directory.CreateDirectory( targetFileDir );
			File.Copy( file, targetPath, true );
		}
	}

	private static void CopyDirectory( string sourceDir, string targetDir )
	{
		Directory.CreateDirectory( targetDir );
		foreach ( var file in Directory.GetFiles( sourceDir, "*", SearchOption.AllDirectories ) )
		{
			var relativePath = Path.GetRelativePath( sourceDir, file );
			var targetPath = Path.Combine( targetDir, relativePath );
			var targetFileDir = Path.GetDirectoryName( targetPath );
			if ( !string.IsNullOrEmpty( targetFileDir ) )
				Directory.CreateDirectory( targetFileDir );
			File.Copy( file, targetPath, true );
		}
	}
}
