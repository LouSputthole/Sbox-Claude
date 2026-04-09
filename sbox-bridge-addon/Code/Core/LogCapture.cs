using System;
using System.Collections.Generic;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Hooks into s&box's logging system to capture console output
/// for the get_console_output command.
///
/// Uses a circular buffer (capped at 1000 entries) to prevent memory growth.
///
/// NOTE: The exact s&box logging hook API needs verification against the real SDK.
/// There are several candidate hooks — uncomment the one that compiles:
///
///   1. Logger.OnMessage += OnLogMessage;            // Sandbox.Diagnostics
///   2. Event.Register("console.log", OnLogString);  // Event-based
///   3. GlobalGameNamespace.Log.OnMessage += ...;     // Global logger
///
/// Currently uses option 1. If it doesn't compile, try the others.
/// </summary>
public static class LogCapture
{
	private static bool _initialized;
	private const int MaxBufferSize = 1000;

	// Circular buffer implementation
	private static readonly LogEntry[] _buffer = new LogEntry[MaxBufferSize];
	private static int _head = 0; // Next write position
	private static int _count = 0;
	private static readonly object _lock = new();

	[Event( "editor.loaded" )]
	public static void Initialize()
	{
		if ( _initialized ) return;
		_initialized = true;

		// ── Hook candidate 1 (try this first) ──
		Logger.OnMessage += OnLogMessage;

		// ── Hook candidate 2 (if #1 doesn't compile) ──
		// TODO: Try InternalExtensions.Logging.OnConsoleMessage if Logger.OnMessage doesn't exist

		// ── Hook candidate 3 (if #2 doesn't compile) ──
		// TODO: Try EditorPlugin.OnLog or similar editor-specific hook

		Log.Info( "[SboxBridge] Log capture initialized (buffer: 1000 entries)" );
	}

	private static void OnLogMessage( LogMessage msg )
	{
		var severity = msg.Level switch
		{
			LogLevel.Trace => "info",
			LogLevel.Info => "info",
			LogLevel.Warning => "warning",
			LogLevel.Error => "error",
			_ => "info",
		};

		Append( msg.Text, severity, msg.Logger ?? "" );
	}

	/// <summary>
	/// Add a log entry to the circular buffer.
	/// </summary>
	public static void Append( string message, string severity, string source = "" )
	{
		lock ( _lock )
		{
			_buffer[_head] = new LogEntry
			{
				Message = message,
				Severity = severity,
				Source = source,
				Timestamp = DateTime.UtcNow.ToString( "o" ),
			};
			_head = (_head + 1) % MaxBufferSize;
			if ( _count < MaxBufferSize )
				_count++;
		}
	}

	/// <summary>
	/// Get log entries, optionally filtered by severity.
	/// Returns entries in chronological order (oldest first).
	/// </summary>
	public static List<LogEntry> GetEntries( int maxCount = 50, string severity = "all" )
	{
		lock ( _lock )
		{
			var result = new List<LogEntry>();
			var start = _count < MaxBufferSize ? 0 : _head;
			var total = _count;

			for ( int i = 0; i < total; i++ )
			{
				var idx = (start + i) % MaxBufferSize;
				var entry = _buffer[idx];
				if ( entry == null ) continue;

				if ( severity != "all" &&
				     !entry.Severity.Equals( severity, StringComparison.OrdinalIgnoreCase ) )
					continue;

				result.Add( entry );
			}

			// Return last maxCount entries
			if ( result.Count > maxCount )
				return result.GetRange( result.Count - maxCount, maxCount );

			return result;
		}
	}

	/// <summary>
	/// Clear all buffered log entries.
	/// </summary>
	public static void Clear()
	{
		lock ( _lock )
		{
			Array.Clear( _buffer, 0, MaxBufferSize );
			_head = 0;
			_count = 0;
		}
	}

	public class LogEntry
	{
		public string Message { get; set; }
		public string Severity { get; set; }
		public string Source { get; set; }
		public string Timestamp { get; set; }
	}
}
