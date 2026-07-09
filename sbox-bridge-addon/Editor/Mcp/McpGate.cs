using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor;
using Sandbox;

/// <summary>
/// The single entry point every generated [McpTool] wrapper calls. Bridges the native
/// editor MCP server (Editor.Mcp.ToolRegistry discovery) into the existing IBridgeHandler
/// dispatch: play-mode guard, handler lookup, JsonObject→JsonElement conversion, and
/// error-object→exception conversion (native MCP semantics are throw-to-report).
/// </summary>
internal static class McpGate
{
	/// <summary>
	/// Build a JsonObject from (key, value) pairs, skipping null values so handlers
	/// see the same "absent param" they'd get from the TS server and apply their own
	/// defaults. Values serialize via System.Text.Json (primitives, arrays, JsonNode).
	/// </summary>
	internal static JsonObject Args( params (string Key, object Value)[] pairs )
	{
		var obj = new JsonObject();
		foreach ( var (key, value) in pairs )
		{
			if ( value is null )
				continue;

			// A JsonNode argument may already have a parent — clone through text.
			if ( value is JsonNode node )
			{
				obj[key] = JsonNode.Parse( node.ToJsonString() );
				continue;
			}

			obj[key] = JsonSerializer.SerializeToNode( value );
		}
		return obj;
	}

	/// <summary>
	/// Execute a bridge command through the existing handler dispatch. Throws on unknown
	/// commands, play-mode refusals, and handler-reported errors — the native MCP layer
	/// delivers exception messages to the agent as readable tool errors.
	/// </summary>
	internal static async Task<object> Run( string command, JsonObject args )
	{
		if ( ClaudeBridge.IsSceneMutating( command ) && Game.IsPlaying )
			throw new Exception( $"'{command}' mutates the scene and is refused during play mode. Stop play first (stop_play or the built-in play_stop)." );

		var handler = ClaudeBridge.GetHandler( command )
			?? throw new Exception( $"Unknown bridge command: {command}" );

		// Deserialize (not JsonDocument.Parse + using) so the resulting JsonElement owns
		// its data — handler results may lazily reference it after we return.
		var element = JsonSerializer.Deserialize<JsonElement>( args?.ToJsonString() ?? "{}" );
		var result = await handler.Execute( element );

		// Handlers signal failure via an `error` property instead of throwing —
		// convert to native throw semantics so agents see a real tool error.
		if ( ClaudeBridge.TryGetHandlerError( result, out var err ) )
			throw new Exception( err );

		// Native-server convention: "Every tool that edits the scene pushes an undo step."
		// UndoSystem.Snapshot is documented "call AFTER you make a change" — the snapshot
		// diff against the previous one becomes the undo entry, so the built-in `undo`
		// restores the pre-call scene. Generic full snapshot: the right (only) option for
		// a gate over heterogeneous handlers.
		if ( ClaudeBridge.IsSceneMutating( command ) )
		{
			try { SceneEditorSession.Active?.UndoSystem?.Snapshot( $"bridge: {command}" ); }
			catch { /* undo is best-effort — never block the actual command */ }
		}

		return result;
	}
}
