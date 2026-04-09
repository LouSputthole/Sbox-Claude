using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Edits an existing C# script with find/replace, insert, append, or delete operations.
/// </summary>
public class EditScriptHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		// Ensure trailing separator for safe StartsWith check
		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var relativePath = parameters.GetProperty( "path" ).GetString()
			?? throw new Exception( "Missing required parameter: path" );

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		if ( !File.Exists( fullPath ) )
			throw new Exception( $"File not found: {relativePath}" );

		var content = File.ReadAllText( fullPath );
		var lines = new List<string>( content.Split( '\n' ) );

		var operations = parameters.GetProperty( "operations" );
		var applied = new List<object>();

		foreach ( var op in operations.EnumerateArray() )
		{
			var type = op.GetProperty( "type" ).GetString();

			switch ( type )
			{
				case "replace":
				{
					var find = op.GetProperty( "find" ).GetString()
						?? throw new Exception( "replace operation requires 'find'" );
					var replacement = op.GetProperty( "replacement" ).GetString() ?? "";

					if ( string.IsNullOrEmpty( find ) )
						throw new Exception( "replace operation 'find' must not be empty" );

					var joined = string.Join( "\n", lines );
					if ( !joined.Contains( find ) )
						throw new Exception( $"Could not find text to replace: {find[..Math.Min( find.Length, 50 )]}..." );

					joined = joined.Replace( find, replacement );
					lines = new List<string>( joined.Split( '\n' ) );
					applied.Add( new { type, status = "applied" } );
					break;
				}

				case "insert":
				{
					var line = op.GetProperty( "line" ).GetInt32();
					var text = op.GetProperty( "replacement" ).GetString() ?? "";

					if ( line < 1 || line > lines.Count + 1 )
						throw new Exception( $"Line number {line} is out of range (1-{lines.Count + 1})" );

					var insertLines = text.Split( '\n' );
					lines.InsertRange( line - 1, insertLines );
					applied.Add( new { type, line, status = "applied" } );
					break;
				}

				case "append":
				{
					var text = op.GetProperty( "content" ).GetString() ?? "";
					// Find the last closing brace of the class and insert before it
					var lastBrace = -1;
					for ( int i = lines.Count - 1; i >= 0; i-- )
					{
						if ( lines[i].Trim() == "}" )
						{
							lastBrace = i;
							break;
						}
					}

					if ( lastBrace >= 0 )
					{
						var appendLines = text.Split( '\n' );
						lines.InsertRange( lastBrace, appendLines );
					}
					else
					{
						lines.AddRange( text.Split( '\n' ) );
					}
					applied.Add( new { type, status = "applied" } );
					break;
				}

				case "delete_lines":
				{
					var startLine = op.GetProperty( "line" ).GetInt32();
					var endLine = op.TryGetProperty( "endLine", out var endProp )
						? endProp.GetInt32()
						: startLine;

					if ( startLine < 1 || endLine > lines.Count || startLine > endLine )
						throw new Exception( $"Invalid line range: {startLine}-{endLine} (file has {lines.Count} lines)" );

					lines.RemoveRange( startLine - 1, endLine - startLine + 1 );
					applied.Add( new { type, startLine, endLine, status = "applied" } );
					break;
				}

				default:
					throw new Exception( $"Unknown operation type: {type}" );
			}
		}

		var newContent = string.Join( "\n", lines );
		File.WriteAllText( fullPath, newContent );

		return Task.FromResult<object>( new
		{
			path = relativePath,
			operations = applied,
			lineCount = lines.Count,
		} );
	}
}
