using System.Text.Json;
using System.Threading.Tasks;

namespace SboxBridge;

/// <summary>
/// Interface for all Bridge command handlers.
/// Each command handler processes a specific MCP tool request.
/// Implement this interface for each Bridge command. Example:
/// <code>
/// public class MyHandler : ICommandHandler
/// {
///     public Task&lt;object&gt; Execute(JsonElement parameters)
///     {
///         var name = parameters.GetProperty("name").GetString();
///         return Task.FromResult&lt;object&gt;(new { result = name });
///     }
/// }
/// </code>
/// </summary>
public interface ICommandHandler
{
	/// <summary>
	/// Execute the command with the given parameters.
	/// </summary>
	/// <param name="parameters">JSON parameters from the MCP request.</param>
	/// <returns>Result object that will be serialized to JSON.</returns>
	Task<object> Execute( JsonElement parameters );
}
