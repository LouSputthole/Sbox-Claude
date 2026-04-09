using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Adds an RPC method to an existing C# script.
/// Supports [Rpc.Broadcast], [Rpc.Host], and [Rpc.Owner] attributes.
/// </summary>
public class AddRpcMethodHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var scriptPath = parameters.GetProperty( "path" ).GetString()
			?? throw new Exception( "Missing required parameter: path" );
		var methodName = parameters.GetProperty( "methodName" ).GetString()
			?? throw new Exception( "Missing required parameter: methodName" );

		var rpcType = parameters.TryGetProperty( "rpcType", out var rpcProp )
			? rpcProp.GetString() ?? "Broadcast" : "Broadcast";

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, scriptPath ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		if ( !File.Exists( fullPath ) )
			throw new Exception( $"Script not found: {scriptPath}" );

		var content = File.ReadAllText( fullPath );

		// Build the RPC method
		var rpcAttr = rpcType.ToLowerInvariant() switch
		{
			"broadcast" => "[Rpc.Broadcast]",
			"host" => "[Rpc.Host]",
			"owner" => "[Rpc.Owner]",
			_ => throw new Exception( $"Unknown RPC type: {rpcType}. Use: Broadcast, Host, Owner" ),
		};

		// Parse parameters for the method
		var methodParams = parameters.TryGetProperty( "methodParams", out var paramsProp )
			? paramsProp.GetString() ?? "" : "";

		// Build method body
		var body = parameters.TryGetProperty( "body", out var bodyProp )
			? bodyProp.GetString() ?? "" : "";

		var sb = new StringBuilder();
		sb.AppendLine();
		sb.AppendLine( $"\t{rpcAttr}" );
		sb.Append( $"\tpublic void {methodName}(" );
		sb.Append( methodParams );
		sb.AppendLine( " )" );
		sb.AppendLine( "\t{" );

		if ( !string.IsNullOrEmpty( body ) )
		{
			foreach ( var line in body.Split( '\n' ) )
			{
				sb.AppendLine( $"\t\t{line.TrimEnd()}" );
			}
		}
		else
		{
			sb.AppendLine( $"\t\tLog.Info( $\"RPC {methodName} called\" );" );
		}

		sb.AppendLine( "\t}" );

		// Insert before the closing brace of the class.
		// Use regex to find the class declaration and its closing brace,
		// handling both traditional and file-scoped namespaces.
		var classMatch = System.Text.RegularExpressions.Regex.Match( content, @"\bclass\s+\w+[^{]*\{" );
		if ( !classMatch.Success )
			throw new Exception( "Could not find class declaration in script" );

		// Count braces from the class opening to find its closing brace
		var braceDepth = 0;
		var classCloseBrace = -1;
		for ( int i = classMatch.Index + classMatch.Length - 1; i < content.Length; i++ )
		{
			if ( content[i] == '{' ) braceDepth++;
			else if ( content[i] == '}' )
			{
				braceDepth--;
				if ( braceDepth == 0 )
				{
					classCloseBrace = i;
					break;
				}
			}
		}

		if ( classCloseBrace < 0 )
			throw new Exception( "Could not find closing brace of class" );

		content = content.Insert( classCloseBrace, sb.ToString() );

		File.WriteAllText( fullPath, content );

		return Task.FromResult<object>( new
		{
			path = scriptPath,
			methodName,
			rpcType = rpcAttr,
			added = true,
		} );
	}
}
