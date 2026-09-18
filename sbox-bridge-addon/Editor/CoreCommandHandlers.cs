using Editor;
using Sandbox;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// get_bridge_status used to be an INLINE special case in ProcessRequest, invisible to
// ClaudeBridge.GetHandler (and therefore to the native-MCP McpGate). Registered like
// every other handler so both transports share one dispatch path. (set_prefab_ref had
// the same problem — its long-dormant SetPrefabRefHandler in MyEditorMenu.cs is now
// registered and the inline copy deleted.)

/// <summary>get_bridge_status — bridge liveness, version, and handler inventory.</summary>
public class GetBridgeStatusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
		=> Task.FromResult<object>( new
		{
			connected = true,
			running = ClaudeBridge.IsRunning,
			version = ClaudeBridge.BridgeVersion,
			protocolVersion = ClaudeBridge.IpcProtocolVersion,
			handlerCount = ClaudeBridge.HandlerCount,
			ipcDir = ClaudeBridge.IpcDirectory,
			// Changes on every successful recompile — pair with trigger_hotload.assemblyBefore
			// to know when an edit has actually landed (issue #15).
			gameAssembly = ClaudeBridge.GetGameAssemblyFingerprint(),
			registeredCommands = ClaudeBridge.RegisteredCommands
		} );
}
