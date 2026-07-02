# Contributing to Sbox-Claude

Thanks for your interest in contributing! This project lets non-coders build s&box games through conversation with Claude Code.

## Architecture Overview

```
Claude Code  --stdio-->  MCP Server (TypeScript)  --file IPC-->  Bridge Addon (C#, inside s&box)
```

**Not WebSocket** — s&box's sandboxed C# blocks `System.Net`. The MCP server writes `req_<id>.json` into a shared temp dir; the addon polls, processes on the main editor thread, and writes `res_<id>.json` back. (Older docs mention WebSocket / port 29015 — that's obsolete; `SBOX_BRIDGE_HOST`/`PORT` are cosmetic.)

Most tools have **two parts**:
1. **MCP tool** (TypeScript) — defines the tool name, description, parameters, and forwards the call over IPC.
2. **Bridge handler** (C#) — receives the command inside the s&box editor and calls engine APIs.

The command name is the same on both sides. `create_gameobject` in TypeScript sends `"create_gameobject"`, which the C# bridge dispatches to its `create_gameobject` handler.

**Exception — MCP-server-side tools.** Six tools have *no* editor handler: `read_log`, `get_compile_errors`, `execute_csharp`, `search_docs`, `get_doc_page`, `list_doc_categories`. They run entirely in the Node server (reading `sbox-dev.log`, fetching docs, or hotload-evaluating), which is why they keep working when the editor has crashed. This is why the MCP server exposes more tools than `get_bridge_status` reports handlers (the extras run server-side).

## Adding a New Tool

> Decide first whether the tool needs the editor at all. If it can be done entirely in the Node server (reading the log, fetching a URL), make it **MCP-server-side** — add only the TypeScript tool (step 3) and skip the C# handler. That's how `read_log`, `get_compile_errors`, and the docs-search tools work. Everything that touches the scene/engine needs a C# handler.

### 1. Add the C# handler (for tools that touch the editor)

The bridge addon is a **single file**: `sbox-bridge-addon/Editor/MyEditorMenu.cs`. Each handler is a small class implementing `IBridgeHandler`:

```csharp
public interface IBridgeHandler
{
    Task<object> Execute( JsonElement parameters );
}

public class YourHandler : IBridgeHandler
{
    public Task<object> Execute( JsonElement parameters )
    {
        var name = parameters.GetProperty( "name" ).GetString()
            ?? throw new System.Exception( "Missing required parameter: name" );

        // Call s&box APIs (runs on the main editor thread)…

        // Return the result object (serialized to JSON). To signal failure,
        // return an object with an `error` field — the dispatch reports
        // success=false when a handler result carries `error`.
        return Task.FromResult<object>( new { result = "ok" } );
    }
}
```

### 2. Register it in `RegisterHandlers()` (same file)

Registration uses a **factory** so a broken handler can't take the whole bridge offline (construction is try/caught and logged):

```csharp
Register( "your_tool_name", () => new YourHandler() );
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

## Tool Count in Docs

The tool/handler count drifts every release, so don't hardcode it in docs. Keep the exact number only in CHANGELOG.md entries (per release) and via get_bridge_status (live); elsewhere say 200+ tools or describe it relatively. This avoids a repo-wide doc sweep whenever a tool is added.

## File Path Security

All C# handlers that accept file paths **must** resolve them through the shared `ClaudeBridge.TryResolveProjectPath` helper, which canonicalizes the path and enforces project containment (separator-safe — `/project-evil` can't match `/project`). As of v1.5.0 this is centralized in one helper across all 25 file/asset call sites — do **not** hand-roll a new containment check.

```csharp
if ( !ClaudeBridge.TryResolveProjectPath( userPath, out var fullPath, out var error ) )
    return Task.FromResult<object>( new { error } );   // reported as success=false
// fullPath is now safe to use
```

When you generate a C# type/member name from a user-supplied string, run it through `ClaudeBridge.SanitizeIdentifier` so spaces/punctuation/keywords don't emit uncompilable code.

## Generated-code templates

Several tools emit C# source code as multi-line interpolated strings (`$@"..."`). A few escaping rules have each caused bugs that shipped -- learn them before writing a new scaffold:

- **Literal braces inside `$@""`** -- a single `{` or `}` in the output must be written as `{{` or `}}`. Forgetting this compiles fine in the generator but emits a broken interpolation site in the generated file. This is standard C# interpolated-string escaping and easy to overlook in long templates.
- **Literal double-quotes** -- a `"` in the generated output must be written as `""` (two double-quotes) inside a verbatim string. Do NOT use backslash escaping; verbatim strings do not recognize `\"`.
- **Empty string literal** -- an empty string `""` in the generated output must be written as `""""` (four double-quotes) inside `$@"..."`. This exact bug shipped twice and was caught by the verify-gate; do not eyeball it.
- **Always run `scripts/check-csharp-syntax.py` on the scaffold output** before calling a tool done. Tree-sitter catches unbalanced braces and broken interpolation regions that compile fine in the host but produce malformed C# in the generated file.
- **Always generate -> `trigger_hotload` -> `get_compile_errors` -> confirm class in TypeLibrary.** Never trust API names from docs or the corpus without `describe_type` first. The `GetMouseRay` incident (v1.13.0): the sbox-cookbook named it `GetMouseRay`; the real SDK method is `ScreenPixelToRay`. The verify-gate caught it; a code review did not.

## Coding Conventions

### C# (Bridge Addon)
- One handler class per command, all in the single `Editor/MyEditorMenu.cs`
- Class name = `{CommandPascalCase}Handler`, implementing `IBridgeHandler`
- Register via `Register( "command_name", () => new XHandler() )` in `RegisterHandlers()`
- Tab indentation, Allman-ish braces with s&box spacing
- Use `Log.Info()` / `Log.Warning()` for debug output (prefix bridge logs with `[SboxBridge]`)
- Resolve file paths via `ClaudeBridge.TryResolveProjectPath`; sanitize generated identifiers via `ClaudeBridge.SanitizeIdentifier`
- Return an object with an `error` field to signal failure (dispatch maps it to `success=false`)

### TypeScript (MCP Server)
- Tools grouped by domain in `src/tools/`
- Use Zod schemas for parameter validation
- Every tool returns `{ content: [{ type: "text", text: ... }] }`
- Error format: `Error: ${res.error}`

### Protocol
- Transport: file-based IPC in a shared temp dir (no socket). `SBOX_BRIDGE_IPC_DIR` overrides the dir on the MCP-server side and must match the addon's `Path.GetTempPath()/sbox-bridge-ipc`. `SBOX_BRIDGE_HOST`/`PORT` are cosmetic.
- The server writes request files to a temp path then **atomically renames** (v1.5.0), so the editor never reads a half-written payload. Write IPC files BOM-less (`new UTF8Encoding(false)` on the C# side); the server strips any BOM on read.
- `status.json` is a heartbeat (refreshed from the editor frame loop, and carrying `ipcDir` + `BridgeVersion`); a heartbeat older than 5s reads as disconnected.
- Request: `{ id: string, command: string, params: object }`
- Response: `{ id: string, success: boolean, data?: any, error?: string }` — `success` is `false` when the handler result carries an `error` field.
- Batch: `{ id: string, commands: [{ command, params }, ...] }`
- Timeout: 30 seconds per request (the timeout message names which side stalled)

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

The Bridge Addon compiles automatically when s&box loads it from your **project's `Libraries/claudebridge/`** folder (NOT the global `addons/` folder — that's built-in only and won't compile custom code). C# changes require an s&box restart (or `trigger_hotload`) to recompile; MCP-server (TypeScript) changes require a Claude Code restart to reconnect.

## Known Limitations

Some s&box APIs in the handlers need verification against the real SDK — look for `API-NOTE` comments. These are areas where we guessed the API shape and may need adjustments when compiled in s&box.
