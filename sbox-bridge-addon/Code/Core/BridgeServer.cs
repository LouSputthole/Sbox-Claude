using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// WebSocket server that runs inside the s&box editor.
/// Listens on a configurable port (default 29015) for JSON commands from the MCP Server,
/// dispatches them to registered <see cref="ICommandHandler"/> instances, and returns
/// JSON responses. Supports both single and batch command requests.
/// </summary>
public static class BridgeServer
{
	private static HttpListener _listener;
	private static CancellationTokenSource _cts;
	private static readonly Dictionary<string, ICommandHandler> _handlers = new();
	private static bool _running;

	public const int DefaultPort = 29015;
	public const int MaxMessageSize = 1024 * 1024; // 1 MB

	// Structured error codes
	public const string ErrorInvalidRequest = "INVALID_REQUEST";
	public const string ErrorUnknownCommand = "UNKNOWN_COMMAND";
	public const string ErrorHandlerError = "HANDLER_ERROR";
	public const string ErrorMessageTooLarge = "MESSAGE_TOO_LARGE";

	/// <summary>
	/// Register a command handler for a given command name.
	/// The <paramref name="command"/> string must match the corresponding MCP tool name exactly
	/// (e.g. "create_gameobject") since the MCP server forwards requests using the same name.
	/// </summary>
	/// <param name="command">Command name, must match the MCP tool name 1:1.</param>
	/// <param name="handler">Handler instance that will execute the command.</param>
	public static void RegisterHandler( string command, ICommandHandler handler )
	{
		_handlers[command] = handler;
	}

	/// <summary>
	/// Start the WebSocket server on the specified port.
	/// Creates an HTTP listener on 127.0.0.1 that upgrades incoming connections to WebSocket.
	/// Each connected client is handled in its own background task.
	/// </summary>
	/// <param name="port">TCP port to listen on (default <see cref="DefaultPort"/> = 29015).</param>
	public static async Task Start( int port = DefaultPort )
	{
		if ( _running ) return;

		_cts = new CancellationTokenSource();
		_listener = new HttpListener();
		_listener.Prefixes.Add( $"http://127.0.0.1:{port}/" );
		_listener.Start();
		_running = true;

		Log.Info( $"[SboxBridge] WebSocket server started on port {port}" );

		_ = Task.Run( () => AcceptLoop( _cts.Token ), _cts.Token );
	}

	/// <summary>
	/// Stop the WebSocket server. Cancels the accept loop and all pending client connections,
	/// then disposes the HTTP listener.
	/// </summary>
	public static void Stop()
	{
		if ( !_running ) return;

		_cts?.Cancel();
		_listener?.Stop();
		_running = false;

		Log.Info( "[SboxBridge] WebSocket server stopped" );
	}

	/// <summary>
	/// Returns the names of all registered command handlers.
	/// Useful for diagnostics (e.g. the get_bridge_status tool).
	/// </summary>
	public static IReadOnlyCollection<string> GetRegisteredCommands()
	{
		return _handlers.Keys;
	}

	private static async Task AcceptLoop( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested )
		{
			try
			{
				var context = await _listener.GetContextAsync();

				if ( context.Request.IsWebSocketRequest )
				{
					var wsContext = await context.AcceptWebSocketAsync( null );
					_ = Task.Run( () => HandleClient( wsContext.WebSocket, ct ), ct );
				}
				else
				{
					context.Response.StatusCode = 400;
					context.Response.Close();
				}
			}
			catch ( Exception ex ) when ( !ct.IsCancellationRequested )
			{
				Log.Warning( $"[SboxBridge] Accept error: {ex.Message}" );
			}
		}
	}

	/// <summary>
	/// Message receive loop for a single WebSocket client. Reads frames into a 64 KB buffer,
	/// rejects messages larger than <see cref="MaxMessageSize"/> (1 MB), parses the UTF-8 text
	/// as JSON, dispatches to <see cref="ProcessRequest"/>, and sends the response back.
	/// </summary>
	private static async Task HandleClient( WebSocket ws, CancellationToken ct )
	{
		var buffer = new byte[65536];

		Log.Info( "[SboxBridge] MCP client connected" );

		try
		{
			while ( ws.State == WebSocketState.Open && !ct.IsCancellationRequested )
			{
				var result = await ws.ReceiveAsync( new ArraySegment<byte>( buffer ), ct );

				if ( result.MessageType == WebSocketMessageType.Close )
				{
					await ws.CloseAsync( WebSocketCloseStatus.NormalClosure, "Closing", ct );
					break;
				}

				if ( result.MessageType == WebSocketMessageType.Text )
				{
					// Guard: reject messages over 1 MB
					if ( result.Count > MaxMessageSize )
					{
						var errorResponse = MakeError( null, ErrorMessageTooLarge,
							$"Message exceeds maximum size of {MaxMessageSize / 1024}KB" );
						await SendResponse( ws, errorResponse, ct );
						continue;
					}

					var json = Encoding.UTF8.GetString( buffer, 0, result.Count );
					var response = await ProcessRequest( json );
					await SendResponse( ws, response, ct );
				}
			}
		}
		catch ( Exception ex ) when ( !ct.IsCancellationRequested )
		{
			Log.Warning( $"[SboxBridge] Client error: {ex.Message}" );
		}
		finally
		{
			ws.Dispose();
		}

		Log.Info( "[SboxBridge] MCP client disconnected" );
	}

	private static async Task SendResponse( WebSocket ws, string response, CancellationToken ct )
	{
		var responseBytes = Encoding.UTF8.GetBytes( response );
		await ws.SendAsync( new ArraySegment<byte>( responseBytes ),
			WebSocketMessageType.Text, true, ct );
	}

	/// <summary>
	/// Parse a JSON request and route it. A request containing a "commands" array is treated
	/// as a batch and forwarded to <see cref="ProcessBatch"/>; otherwise it is treated as a
	/// single command and forwarded to <see cref="ExecuteSingle"/>.
	/// </summary>
	private static async Task<string> ProcessRequest( string json )
	{
		string id = null;

		try
		{
			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;

			id = root.TryGetProperty( "id", out var idProp ) ? idProp.GetString() : null;

			// Validate id
			if ( string.IsNullOrEmpty( id ) )
				return MakeError( null, ErrorInvalidRequest, "Missing or empty 'id' field" );

			// ── Batch request: { id, commands: [...] } ──
			if ( root.TryGetProperty( "commands", out var commandsArray ) &&
			     commandsArray.ValueKind == JsonValueKind.Array )
			{
				return await ProcessBatch( id, commandsArray );
			}

			// ── Single request: { id, command, params } ──
			var command = root.TryGetProperty( "command", out var cmdProp ) ? cmdProp.GetString() : null;

			if ( string.IsNullOrEmpty( command ) )
				return MakeError( id, ErrorInvalidRequest, "Missing or empty 'command' field" );

			var paramsElement = root.TryGetProperty( "params", out var p ) ? p : default;

			return await ExecuteSingle( id, command, paramsElement );
		}
		catch ( Exception ex )
		{
			return MakeError( id, ErrorInvalidRequest, $"Failed to parse request: {ex.Message}" );
		}
	}

	/// <summary>
	/// Look up the registered <see cref="ICommandHandler"/> for the given command name,
	/// execute it, and wrap the result in a success/error JSON envelope.
	/// Returns an <see cref="ErrorUnknownCommand"/> response if no handler is registered.
	/// </summary>
	private static async Task<string> ExecuteSingle( string id, string command, JsonElement paramsElement )
	{
		if ( _handlers.TryGetValue( command, out var handler ) )
		{
			try
			{
				var result = await handler.Execute( paramsElement );
				return JsonSerializer.Serialize( new { id, success = true, data = result } );
			}
			catch ( Exception ex )
			{
				return MakeError( id, ErrorHandlerError, ex.Message );
			}
		}

		return MakeError( id, ErrorUnknownCommand, $"Unknown command: {command}" );
	}

	/// <summary>
	/// Execute an array of commands sequentially, collecting each result (success or error)
	/// into a "results" array. Returns a single JSON response containing all outcomes.
	/// </summary>
	private static async Task<string> ProcessBatch( string id, JsonElement commandsArray )
	{
		var results = new List<object>();

		foreach ( var item in commandsArray.EnumerateArray() )
		{
			var command = item.TryGetProperty( "command", out var cmdProp ) ? cmdProp.GetString() : null;
			var paramsElement = item.TryGetProperty( "params", out var p ) ? p : default;

			if ( string.IsNullOrEmpty( command ) )
			{
				results.Add( new { command = (string)null, success = false, error = "Missing command", errorCode = ErrorInvalidRequest } );
				continue;
			}

			if ( _handlers.TryGetValue( command, out var handler ) )
			{
				try
				{
					var result = await handler.Execute( paramsElement );
					results.Add( new { command, success = true, data = result } );
				}
				catch ( Exception ex )
				{
					results.Add( new { command, success = false, error = ex.Message, errorCode = ErrorHandlerError } );
				}
			}
			else
			{
				results.Add( new { command, success = false, error = $"Unknown command: {command}", errorCode = ErrorUnknownCommand } );
			}
		}

		return JsonSerializer.Serialize( new { id, success = true, results } );
	}

	/// <summary>
	/// Build a structured error JSON response with the given request id, error code
	/// (one of the Error* constants), and human-readable message.
	/// </summary>
	private static string MakeError( string id, string code, string message )
	{
		return JsonSerializer.Serialize( new
		{
			id,
			success = false,
			error = message,
			errorCode = code,
		} );
	}
}
