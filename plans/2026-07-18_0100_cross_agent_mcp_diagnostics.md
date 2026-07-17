# Make Harmony Resolver diagnostics MCP available across Claude Code, Codex CLI, and pi-coding-agent

## Context

The Auth0-gated MCP diagnostics server (`src/Harmony.Resolver.Mcp`, reached via the existing stdio bridge at `src/Harmony.Resolver.McpBridge`) is now live at `https://harmony-resolver.duckdns.org/mcp`, and the user has already created the Auth0 M2M app (`harmony-resolver-mcp-bridge`, Client ID + Secret in hand). The user wants these diagnostic tools usable from every AI coding agent on this machine, scoped to the two relevant projects — `Harmony-Resolver` (this repo) and `Harmony-Music` (the Flutter client, at `C:\MyRepositories\Harmony-Music`) — not machine-wide for unrelated projects.

Investigation found three different tools in active use here, each with a different config mechanism:
- **Claude Code**: project-scoped `.mcp.json` (JSON, `mcpServers` key). Harmony-Music already has one (for its own `harmony-flutter-dart` local server), Harmony-Resolver doesn't yet.
- **Codex CLI**: global `~/.codex/config.toml`, `[mcp_servers.<name>]` tables (confirmed from the existing `node_repl` entry already there — no project scoping exists in Codex, so this is necessarily machine-global).
- **pi-coding-agent**: explicitly has **no built-in MCP support** ("intentionally does not include built-in MCP" per its own docs) — the only way to expose these tools is a custom pi extension package. Harmony-Music already has exactly this kind of package (`.pi/local-packages/harmony-plan-agent-mode`), which spawns a target MCP server as a stdio subprocess and hand-rolls the JSON-RPC `initialize`/`tools/call` exchange, then wraps each tool via `pi.registerTool()`. This is the pattern to replicate.

This session's own "Claude Desktop" app turned out to be a different product (no `mcpServers` config key at all) — confirmed out of scope per the user.

**Secret handling**: rather than duplicating the Auth0 Client ID/Secret across three different config files/formats (JSON env blocks, TOML env tables, and whatever the pi subprocess needs), the plan centralizes them in one gitignored `.env` file colocated with `Harmony.Resolver.McpBridge` (already covered by the repo's existing bare `.env` gitignore rule — no gitignore change needed). The bridge self-loads it at startup via a `[CallerFilePath]`-anchored path (reliable regardless of which client/CWD launches it), so **no client config needs to carry secrets or rely on client-specific `${VAR}` expansion syntax** — every client just runs the same plain `dotnet run --project <absolute path>` command.

## Files to add/change

**`src/Harmony.Resolver.McpBridge/Program.cs`** — add a small `.env` loader at the very top (before `Host.CreateApplicationBuilder`), using `[CallerFilePath]` to reliably locate `.env` next to `Program.cs` regardless of invoking CWD/client. Doesn't override real env vars if already set.

**`src/Harmony.Resolver.McpBridge/.env.example`** (new, committed) — documents the five vars: `HARMONY_MCP_URL`, `AUTH0_DOMAIN`, `AUTH0_CLIENT_ID`, `AUTH0_CLIENT_SECRET`, `AUTH0_AUDIENCE`.

**`src/Harmony.Resolver.McpBridge/.env`** (new, gitignored) — the real file with `HARMONY_MCP_URL=https://harmony-resolver.duckdns.org/mcp`, `AUTH0_DOMAIN=dev-bozmund.eu.auth0.com`, `AUTH0_AUDIENCE=https://harmony-resolver-diagnostics`, and the actual M2M Client ID/Secret.

**`Harmony-Resolver/.mcp.json`** (new, repo root) — Claude Code project config pointing `dotnet run --project C:/MyRepositories/Harmony-Resolver/src/Harmony.Resolver.McpBridge`.

**`Harmony-Music/.mcp.json`** — add the same `harmony-resolver-diagnostics` entry alongside the existing `harmony-flutter-dart` one.

**`~/.codex/config.toml`** — add `[mcp_servers.harmony_resolver_diagnostics]` following the existing `[mcp_servers.node_repl]` entry's shape (underscore name, `startup_timeout_sec = 120`).

**`Harmony-Resolver/.pi/local-packages/harmony-resolver-diagnostics/`** (new pi package, mirrors `Harmony-Music/.pi/local-packages/harmony-plan-agent-mode/`) — `package.json` + `extensions/index.ts` with a `callMcpTool` helper (same hand-rolled stdio JSON-RPC pattern as the existing `harmony-plan-agent-mode` package) and 10 `pi.registerTool()` calls mirroring `BridgeTools` in `Harmony.Resolver.McpBridge/Program.cs`.

**`Harmony-Resolver/.pi/settings.json`** (new) and **`Harmony-Music/.pi/settings.json`** (updated) — reference the new package, same cross-repo absolute-path pattern already used for `plan-agent-mode`.

## Verification

1. `dotnet build Harmony.Resolver.slnx -c Release` — confirms the `.env` loader compiles cleanly.
2. With the real `.env` in place, exercise the MCP server (via Claude Code, or a manual stdio JSON-RPC exchange) and confirm a tool like `get_deployment_info` returns real data from the live VPS instead of an auth error.
3. For pi: run `pi` inside a trusted `Harmony-Resolver` or `Harmony-Music` session and invoke one of the new tools.
4. For Codex: restart Codex CLI, confirm `harmony_resolver_diagnostics` appears as an available MCP server, and call one tool.
