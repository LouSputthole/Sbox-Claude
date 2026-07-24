# s&box Codex Bridge

> **Build s&box games with Codex.** Describe the result; Codex can write the C#, drive the editor, wire components, playtest, and inspect screenshots.

First-party Codex distribution from [sboxskins.gg](https://sboxskins.gg), generated from the same source as the s&box Claude Bridge.

## Included

- Native s&box editor MCP at `http://127.0.0.1:7269/mcp`.
- Optional `sbox-lifeline` diagnostics while the editor is unavailable, bundled but disabled by default.
- The `sbox-api` brain and code-grounded `sbox-cookbook`.
- Design, build/verify, playable-scaffold, setup, and focused game-development skills.
- Product license, notice, and third-party provenance inside the package.

## Prerequisites

- A current Codex release with plugin support. In Codex CLI, `codex plugin --help` must succeed and `/plugins` must open the plugin browser.
- s&box with the `sboxskinsgg.claudebridge` editor library installed in the project and the native MCP server enabled.
- Node.js 18 or newer, npm/npx, and npm registry access only if you enable the bundled `sbox-lifeline` diagnostics server. The native editor bridge and bundled skills do not require Node.js.

## Install

### Rolling channel (default)

The default Git-backed install tracks the repository main branch. This is a rolling channel: marketplace upgrades fetch the latest compatible package from main.

~~~bash
codex plugin marketplace add LouSputthole/Sbox-Claude --ref main
codex plugin add sbox-codex-bridge@sboxskins
~~~

### Immutable release

After codex-v2.2.0 is published, pin that tag for a reproducible 2.2.0 install. A pinned marketplace does not advance when main changes.

~~~bash
codex plugin marketplace add LouSputthole/Sbox-Claude --ref codex-v2.2.0
codex plugin add sbox-codex-bridge@sboxskins
~~~

After adding either marketplace, you can also launch Codex, enter /plugins, select the **sboxskins.gg s&box plugins** marketplace, and install **s&box Codex Bridge** from the browser.

### Manual GitHub download

After codex-v2.2.0 is published, if the owner/repository install is unavailable, download and extract the [`codex-v2.2.0` source archive](https://github.com/LouSputthole/Sbox-Claude/archive/refs/tags/codex-v2.2.0.zip), or clone that tag. Then register the extracted repository folder and install the plugin:

```bash
codex plugin marketplace add <extracted-repo-folder>
codex plugin add sbox-codex-bridge@sboxskins
```

Then install or update the editor addon, confirm the native MCP server is enabled, and start a **new Codex session** so the plugin's skills and native MCP server load. The optional lifeline remains disabled until you enable it.

## Update

### Rolling channel

If the marketplace was added with --ref main, refresh it in place and reinstall the current plugin package:

~~~bash
codex plugin marketplace upgrade sboxskins
codex plugin add sbox-codex-bridge@sboxskins
~~~

### Pinned release

A marketplace pinned to codex-v2.2.0 is immutable. To move to a later Codex release, remove the installed plugin and marketplace, then add the next published tag and reinstall:

~~~bash
codex plugin remove sbox-codex-bridge@sboxskins
codex plugin marketplace remove sboxskins
codex plugin marketplace add LouSputthole/Sbox-Claude --ref <next-codex-release-tag>
codex plugin add sbox-codex-bridge@sboxskins
~~~

### Manual archive

For a manual archive installation, remove the old local marketplace, download and extract the new immutable release archive, then register that new extracted repository folder and reinstall:

```bash
codex plugin remove sbox-codex-bridge@sboxskins
codex plugin marketplace remove sboxskins
codex plugin marketplace add <new-extracted-repo-folder>
codex plugin add sbox-codex-bridge@sboxskins
```

Update the `sboxskinsgg.claudebridge` library through s&box's Library Manager as well, then start a new Codex session.

## Uninstall

```bash
codex plugin remove sbox-codex-bridge@sboxskins
```

If you no longer use anything from the sboxskins marketplace, remove that marketplace too:

```bash
codex plugin marketplace remove sboxskins
```

Removing the Codex plugin does not remove the editor library from an s&box project. Remove `sboxskinsgg.claudebridge` separately through the Library Manager when you no longer need it.

## Enable lifeline diagnostics (optional)

The bundled `sbox-lifeline` server is disabled by default. Enable it in Codex's MCP settings when you want log and compile diagnostics to remain available while the editor is down, then start a new Codex session. Enabling it requires Node.js 18 or newer, npm/npx, and network access to fetch the pinned package on first use.

If your Codex surface cannot toggle a plugin-provided MCP server, register the same pinned server as a user-level fallback:

```bash
codex mcp add sbox-lifeline -- npx -y sbox-mcp-server@2.2.0 --lifeline
```

Remove that user-level fallback separately when you no longer need it:

```bash
codex mcp remove sbox-lifeline
```

## MCP-only fallback

If you want the live editor tools without the bundled skills:

```bash
codex mcp add sbox --url http://127.0.0.1:7269/mcp
```

Optionally add editor-down diagnostics; this separate lifeline command requires Node.js, npm/npx, and registry access:

```bash
codex mcp add sbox-lifeline -- npx -y sbox-mcp-server@2.2.0 --lifeline
```

## Editor addon (required)

1. In the s&box editor open **Editor → Library Manager** (this is *not* the Asset Browser — the bridge is a **Library**), search for **`sboxskinsgg.claudebridge`**, and install it *into your project*. It lands in `<your-project>/Libraries/`.
2. In **Editor → Preferences → MCP Server**, confirm the native server is enabled on port 7269. The legacy Claude Bridge dock is optional on v2 and does not need to remain open.
3. Start a new Codex session and ask: **"Use $sbox-setup to check the bridge and orient me."**

## Verify

- `codex plugin list` shows `sbox-codex-bridge@sboxskins` installed.
- A new session exposes the bundled s&box skills.
- `mcp__sbox__search_tools` answers, and a wrapped `get_bridge_status` call reports the addon compiled and connected.
- A visual smoke test returns an inline image from `capture_view` or `screenshot_orbit`.

## Development

Do not hand-edit this generated directory. Update `plugins/sbox-claude/` or the generator's explicit Codex transforms, then run `node scripts/gen-codex-plugin.mjs` and `node scripts/gen-codex-plugin.mjs --check`.

## License

Source-available. See the bundled [LICENSE](LICENSE), [NOTICE](NOTICE), and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). "s&box Codex Bridge" / "sboxskins.gg" are reserved marks.

<!-- Generated by scripts/gen-codex-plugin.mjs from plugins/sbox-claude/. Do not hand-edit. -->
