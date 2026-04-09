using System.Text.Json;
using System.Threading.Tasks;

namespace SboxBridge;

/// <summary>
/// Clears all console log entries.
/// </summary>
public class ClearConsoleHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		LogCapture.Clear();

		return Task.FromResult<object>( new
		{
			cleared = true,
		} );
	}
}
