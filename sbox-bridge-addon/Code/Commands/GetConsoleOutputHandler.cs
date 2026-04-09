using System.Text.Json;
using System.Threading.Tasks;

namespace SboxBridge;

/// <summary>
/// Reads recent console log entries from s&box.
/// Delegates to the LogCapture circular buffer.
/// </summary>
public class GetConsoleOutputHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var count = parameters.TryGetProperty( "count", out var countProp )
			? countProp.GetInt32() : 50;
		var severity = parameters.TryGetProperty( "severity", out var sevProp )
			? sevProp.GetString() ?? "all" : "all";

		var entries = LogCapture.GetEntries( count, severity );

		return Task.FromResult<object>( new
		{
			count = entries.Count,
			entries,
		} );
	}
}
