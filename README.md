# Power BI Modeling MCP

A [Model Context Protocol](https://modelcontextprotocol.io) server that
lets an AI assistant inspect and safely mutate Power BI / Fabric semantic
models via the XMLA endpoint. Authenticates with whatever identity
`Azure.Identity.DefaultAzureCredential` finds — your `az login` user
locally, a User-Assigned Managed Identity when deployed to Azure App
Service via the bundled [`azd`](https://learn.microsoft.com/azure/developer/azure-developer-cli/)
template.

Write tools (`add_measure`, `update_measure`, `delete_measure`) ship
behind an audit log, TMSL backups, dry-run preview, and confirm-on-destroy
— see [`SPEC.md`](./SPEC.md) for the wire contract and
[`docs/architecture.md`](./docs/architecture.md) for how it's built.

---

## Status

This is a **reference implementation**, not a packaged product. It is
a working, end-to-end example of how to build an MCP server over Power
BI's tabular object model with a real safety pipeline (audit, backup,
dry-run, confirm-on-destroy) and a real Azure deploy story (App
Service + UAMI via `azd`). Every file in the repo is intended to be
read, copied, and adapted.

The shipped HTTP transport authenticates callers with a single shared
secret — it's **single-tenant**: every authenticated caller acts on
Power BI as the server's identity. That's the simplest shape that
proves the seam works; for a multi-user production deployment the
two `PRODUCTION SWAP:` comments in the source mark exactly where to
replace inbound auth with JWT bearer (against your Entra tenant) and
backend auth with MSAL on-behalf-of. Neither swap requires a
re-architecture; see [SECURITY.md › Production hardening](./SECURITY.md#production-hardening).

---

## Why

Power BI's tabular object model (TOM) is .NET-only. This server bridges TOM to
MCP so any compliant client (Claude Desktop, etc.) can drive structural model
changes — add measures, list tables, etc. — through an LLM, with the safety
rails (audit log, TMSL backups, dry-run, confirm-on-destroy) you'd want in
production.

Ships as a single self-contained binary. No JS or Python runtime to install.
The agent supplies the workspace and dataset on every call (the server
holds no "current connection"), and identity comes from `az login`
(locally) or Managed Identity (in Azure).

---

## Quickstart

### 1. Prerequisites

Development is **Windows-only**. The Microsoft Analysis Services .NET
client library (TOM) does not load reliably on Linux despite the
managed-only NuGet shape; macOS hasn't been validated. Deployment
targets Linux App Service (the platform handles the runtime), so the
*server* is cross-platform — only the *dev loop* is Windows.

Install on your Windows dev box:

- [**.NET 8 SDK**](https://dotnet.microsoft.com/download/dotnet/8.0) — build, test, run.
- [**Azure CLI**](https://learn.microsoft.com/cli/azure/install-azure-cli-windows) — `az login` for local Power BI auth, prerequisite for `azd`.
- [**Azure Developer CLI (`azd`)**](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) — only needed if you want to deploy to Azure (`winget install Microsoft.Azd`).
- [**PowerShell 7+**](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) — recommended; the .NET-class snippets in `.env.sample` and the `Add-PowerBIWorkspaceUser` workspace-grant snippet are easier on `pwsh` than on legacy Windows PowerShell 5.1.
- [**MicrosoftPowerBIMgmt PowerShell module**](https://learn.microsoft.com/powershell/power-bi/overview) — only needed once for the workspace grant: `Install-Module -Name MicrosoftPowerBIMgmt -Scope CurrentUser`.

Power BI side:

- **Premium / Fabric** workspace (XMLA write requires it; shared capacity won't work).
- An identity with **Member** or **Admin** access to the target workspace
  (Contributor isn't enough). Either:
  - **You**, signed in via `az login` — the local-dev path.
  - A **Managed Identity** (in production on Azure), with the tenant
    setting *Allow service principals (and managed identities) to use
    Power BI APIs* enabled and the MI added to the workspace via
    `Add-PowerBIWorkspaceUser ... -PrincipalType App`.
- Tenant: *Allow XMLA endpoints — Read Write* turned on.

### 2. Run

```powershell
cp .env.sample .env       # ships with: Transport=http, HttpDisableAuth=true (loopback)
cp .vscode/mcp.json.sample .vscode/mcp.json
az login
dotnet run --project src/PbiModelingMcp
```

Logs go to stderr and to `~/.pbi-modeling-mcp/logs/server-YYYYMMDD.log`.
Stdout is reserved for MCP JSON-RPC.

The shipped defaults run the **HTTP transport on loopback with auth
disabled** — safe because `HttpHost=127.0.0.1` is unreachable from
anywhere off your box, the server prints a loud `AUTH DISABLED` banner,
and fail-closed startup checks refuse to bind anywhere reachable in
this configuration without an explicit second opt-in.

For VS Code: open the MCP panel, the `pbi-modeling` server connects to
`http://127.0.0.1:5000/` with no headers — just go. For other MCP
clients (Claude Desktop, etc.) wire the same URL in.

#### Switching to stdio

If you want stdio (one MCP client co-located with the process), edit
`.env`:

```
PBI_MCP__Transport=stdio
```

Drop the `Http*` keys; they're ignored. Stdout becomes the JSON-RPC
transport; logs continue to stderr + file.

#### Enabling auth on a non-loopback bind

Don't bind anywhere reachable without an API key. To switch the local
dev shape to authenticated HTTP:

```env
# in .env
PBI_MCP__Transport=http
PBI_MCP__HttpHost=127.0.0.1
PBI_MCP__HttpPort=5000
# remove PBI_MCP__HttpDisableAuth
PBI_MCP__HttpAuthToken=<32+ char random string>
```

Generate a key in pwsh:

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$b = New-Object byte[] 32
$rng.GetBytes($b)
[Convert]::ToBase64String($b)
```

The Azure deploy uses this exact shape — see
[`docs/deploy-azure.md`](./docs/deploy-azure.md). The wire shape is a
custom `X-Api-Key` header (not `Authorization: Bearer`, deliberately —
spec-compliant MCP clients interpret a `Bearer` 401 as the trigger for
OAuth 2.1 protected-resource discovery, which we don't want here). A
VS Code `mcp.json` shape with auth on:

```jsonc
{
  "inputs": [
    { "id": "pbi-mcp-key", "type": "promptString", "password": true,
      "description": "API key for the PBI MCP server." }
  ],
  "servers": {
    "pbi-modeling": {
      "type": "http",
      "url": "https://<your-deployment>/",
      "headers": { "X-Api-Key": "${input:pbi-mcp-key}" }
    }
  }
}
```

### 3. Optional advanced settings

All of these have sensible defaults; set them only if you need to deviate.

| Variable | Default | Purpose |
|---|---|---|
| `PBI_MCP__AuditDir` | `~/.pbi-modeling-mcp` | Audit log + backup root |
| `PBI_MCP__RequireConfirmDelete` | `true` | Require confirm on `delete_*` tools |
| `PBI_MCP__LogLevel` | `Information` | `Verbose`/`Debug`/`Information`/`Warning`/`Error` |
| `PBI_MCP__Actor` | `{user}@{host}` | Identity recorded in audit events |
| `PBI_MCP__Transport` | `stdio` | `stdio` or `http` |
| `PBI_MCP__HttpHost` | `127.0.0.1` | Bind IP when `Transport=http` |
| `PBI_MCP__HttpPort` | `5000` | Bind port when `Transport=http` |
| `PBI_MCP__HttpAuthToken` | (none) | **Required** when `Transport=http` (unless `HttpDisableAuth=true`). Min 32 chars; sentinel placeholders rejected. |
| `PBI_MCP__HttpDisableAuth` | `false` | Opt out of API-key auth. Loopback / local-dev only; emits a loud stderr banner. The shipped `.env.sample` sets this to `true`. |
| `PBI_MCP__HttpAllowInsecure` | `false` | Required to bind non-loopback without TLS (self-hosted shapes only) |
| `PBI_MCP__HttpListenAllInterfaces` | `false` | Platform-managed hosting (App Service / ACA): bind on `*:{port}`, port from `PORT` → `WEBSITES_PORT` → `HttpPort`, skip the loopback / cleartext guards |

A gitignored `.env` next to the binary is also read if present.

---

## Tools

| Tool | Description |
|---|---|
| `list_workspaces()` | Power BI REST: workspaces visible to the resolved identity. |
| `list_datasets(workspace)` | Power BI REST: datasets in a workspace. |
| `list_tables(workspace, dataset)` | Tables in a model. |
| `list_measures(workspace, dataset, table)` | Measures on a table. |
| `get_measure(workspace, dataset, table, name)` | Full detail of one measure. |
| `add_measure(workspace, dataset, table, name, dax, ...)` | Add a measure (`dryRun` to preview). |
| `update_measure(workspace, dataset, table, name, ...)` | Partial update; `null`=unchanged, `""`=clear. |
| `delete_measure(workspace, dataset, table, name, ...)` | Delete a measure (confirms by default). |

Full contract is in [`SPEC.md`](./SPEC.md).

---

## Deploy to Azure

The repo ships with an [`azd`](https://learn.microsoft.com/azure/developer/azure-developer-cli/)
template that puts the server on Azure App Service Linux with a
User-Assigned Managed Identity. Four resources, ~$0/mo on the F1 Free
tier, ~2-minute first deploy.

```powershell
winget install Microsoft.Azd
azd auth login
azd up
```

Then grant the deployed UAMI Member access to your Power BI workspace
(one-liner in the full guide), copy
[`.vscode/mcp.json.sample`](./.vscode/mcp.json.sample) to
`.vscode/mcp.json`, paste the deployed URL into its `pbi-modeling-remote`
entry, and you're done.

Full step-by-step instructions — prerequisites, deployment, workspace
grant, verification, key rotation, production hardening, common
gotchas — are in [`docs/deploy-azure.md`](./docs/deploy-azure.md).

---

## Project Layout

```
custom-pbi-modeling-mcp/
├── README.md                         you are here
├── SPEC.md                           public contract (tools, env, wire formats)
├── ROADMAP.md                        what's next
├── docs/
│   ├── architecture.md               implementation notes (non-normative)
│   ├── audit-schema.md               versioned audit-log schema
│   └── deploy-azure.md               operator runbook for `azd up` to App Service
├── src/
│   └── PbiModelingMcp/               the MCP server
│       ├── Auth/                     ITokenProvider + DefaultAzureCredentialTokenProvider
│       ├── Configuration/            ServerOptions, DotEnv loader
│       ├── Connection/               ConnectionManager + TomServerFactory
│       ├── Modeling/                 IModelingService — TOM ops live here
│       ├── Tools/                    MCP tool methods (attribute-based)
│       ├── Http/                     HTTP-transport host: API-key auth, /healthz, startup checks
│       ├── PowerBi/                  PowerBiRestClient (workspace/dataset listing)
│       ├── Audit/                    AuditLogger + BackupWriter
│       └── Program.cs                host + MCP wiring (stdio + HTTP branches)
└── tests/
    ├── PbiModelingMcp.Tests/         xUnit unit tests
    └── PbiModelingMcp.IntegrationTests/  in-process HTTP host tests
```

---

## Development

```bash
dotnet build                 # solution-wide build, warnings-as-errors
dotnet test                  # run unit tests
dotnet format                # apply code style
```

Engineering hygiene is enforced via [`Directory.Build.props`](./Directory.Build.props)
(nullable, analyzers, warnings-as-errors), [`Directory.Packages.props`](./Directory.Packages.props)
(central package management), and [`.editorconfig`](./.editorconfig).

---

## Troubleshooting

**`The specified Power BI workspace ('...') is not found.`**
The XMLA endpoint requires the workspace **name**, not the GUID. Also
confirm the configured identity is a Member or Admin of that workspace.

**`OptionsValidationException` at startup**
A `PBI_MCP__*` setting (e.g. `LogLevel`) is malformed. The server validates
on start — the message names the offending field.

---

## Contributing

See [`CONTRIBUTING.md`](./CONTRIBUTING.md). Issues and PRs welcome.

---

## License

[MIT](./LICENSE)
