# Contributing to Sbox-Claude

Thanks for your interest in contributing! This project lets non-coders build s&box games through conversation with Claude Code.

## Architecture Overview

```
Claude Code  --stdio-->  MCP Server (TypeScript)  --WebSocket-->  Bridge Addon (C#, inside s&box)
```

Every tool has exactly **two parts**:
1. **MCP tool** (TypeScript) — defines the tool name, description, parameters, and forwards the call
2. **Bridge handler** (C#) — receives the command inside the s&box editor and calls engine APIs

The command name is the same on both sides. `create_gameobject` in TypeScript sends `"create_gameobject"` over WebSocket, which the C# `CreateGameObjectHandler` picks up.

## Adding a New Tool

### 1. Create the C# handler

`sbox-bridge-addon/Code/Commands/YourHandler.cs`:

```csharp
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

public class YourHandler : ICommandHandler
{
    public Task<object> Execute( JsonElement parameters )
    {
        // Read params
        var name = parameters.GetProperty( "name" ).GetString()
            ?? throw new System.Exception( "Missing required parameter: name" );

        // Call s&box APIs
        // ...

        // Return result (gets serialized to JSON)
        return Task.FromResult<object>( new { result = "ok" } );
    }
}
```

### 2. Register it in `BridgeAddon.cs`

```csharp
BridgeServer.RegisterHandler( "your_tool_name", new YourHandler() );
```

The string must match the MCP tool name exactly.

### 3. Add the MCP tool (TypeScript)

Add to an existing file in `sbox-mcp-server/src/tools/` or create a new one:

```typescript
server.tool(
  "your_tool_name",
  "Description of what this tool does",
  {
    name: z.string().describe("What this param is"),
  },
  async (params) => {
    const res = await bridge.send("your_tool_name", params);
    if (!res.success) {
      return { content: [{ type: "text", text: `Error: ${res.error}` }] };
    }
    return {
      content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
    };
  }
);
```

### 4. If you created a new tool file, register it in `index.ts`

```typescript
import { registerYourTools } from "./tools/your-domain.js";
registerYourTools(server, bridge);
```

### 5. Build and test

```bash
cd sbox-mcp-server && npm run build
```

## File Path Security

All C# handlers that accept file paths **must** validate that the resolved path stays within the project directory:

```csharp
var projectRoot = Project.Current?.GetRootPath();
// Ensure trailing separator to prevent /project-evil matching /project
if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
    projectRoot += Path.DirectorySeparatorChar;

var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );
if ( !fullPath.StartsWith( projectRoot ) )
    throw new Exception( "Path must be within the project directory" );
```

## Coding Conventions

### C# (Bridge Addon)
- One handler class per command in `Code/Commands/`
- Class name = `{CommandPascalCase}Handler`
- Tab indentation, Allman-ish braces with s&box spacing
- Use `Log.Info()` / `Log.Warning()` for debug output
- Use `ComponentHelper` for serializing/deserializing property values

### TypeScript (MCP Server)
- Tools grouped by domain in `src/tools/`
- Use Zod schemas for parameter validation
- Every tool returns `{ content: [{ type: "text", text: ... }] }`
- Error format: `Error: ${res.error}`

### Protocol
- WebSocket port: 29015 (configurable via `SBOX_BRIDGE_PORT`)
- Request: `{ id: string, command: string, params: object }`
- Response: `{ id: string, success: boolean, data?: any, error?: string }`
- Batch: `{ id: string, commands: [{ command, params }, ...] }`
- Timeout: 30 seconds per request

## Development Setup

```bash
# Build the MCP Server
cd sbox-mcp-server
npm install
npm run build

# Watch mode (auto-rebuild)
npm run dev

# Connect to Claude Code for testing
claude mcp add sbox -- node $(pwd)/dist/index.js
```

The Bridge Addon compiles automatically when s&box loads it from the addons directory.

## Known Limitations

Some s&box APIs in the handlers need verification against the real SDK — look for `API-NOTE` comments. These are areas where we guessed the API shape and may need adjustments when compiled in s&box.
