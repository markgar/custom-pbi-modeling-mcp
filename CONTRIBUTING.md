# Contributing

Thanks for your interest. This project is small and opinionated, so a few
ground rules keep it that way.

## Ground rules

- **Read [`SPEC.md`](./SPEC.md) first.** It has the public contract
  (tools, env vars, wire formats) and the safety pipeline. Most surprises
  are explained there.
- **One concern per PR.** Easier to review, easier to revert.
- **Tests for behavior changes.** Pure unit tests for argument validation /
  audit serialization / etc. Live-model integration tests go under
  `tests/PbiModelingMcp.IntegrationTests/` and are gated on `PBI__*` env vars.
- **No new `Console.WriteLine` in `src/`.** Stdout is the MCP transport.
  Logging goes through `ILogger` (Serilog → stderr + file).
- **No secrets in commits.** `.env` is gitignored; never check in real
  credentials. Rotate immediately if you do.

## Local development

Development is **Windows-only** (TOM doesn't load reliably on Linux,
macOS unverified). See [README.md](./README.md#1-prerequisites) for
the install list.

```powershell
# Bootstrap
cp .env.sample .env                      # then edit
cp .vscode/mcp.json.sample .vscode/mcp.json
dotnet restore

# Build everything (warnings-as-errors)
dotnet build

# Tests
dotnet test

# Code style
dotnet format
```

## Project layout

See [`README.md` › Project Layout](./README.md#project-layout) for the
top-level map. In short: business logic lives under `src/PbiModelingMcp/`
in folders aligned with `SPEC.md`'s architecture sections; tests mirror
the source structure.

## Adding a tool

1. Add a method on a `[McpServerToolType]`-decorated class under
   `src/PbiModelingMcp/Tools/` and decorate it with `[McpServerTool]`
   plus `[Description]`.
2. Delegate to `IModelingService` (read tools) or extend it (writes).
   Tools never touch TOM directly.
3. For write tools, route the operation through the audit + backup
   pipeline (see the *Safety pipeline* section in `SPEC.md`).
4. Add a unit test for argument validation; an integration test if it
   touches the model.

## Reporting issues

A clear repro is worth more than a long description. Include:

- The command you ran or tool call you made
- The full error from stderr (`server-YYYYMMDD.log` is also fine)
- The exact `workspace` + `dataset` arguments you passed
- Power BI side: capacity type, tenant settings, identity's role on the workspace

## License

By contributing you agree your contributions are licensed under the
[MIT License](./LICENSE).
