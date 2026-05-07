# Power BI Semantic Model MCP Server — Spec

This is the **public contract**: tool surface, resource surface, env vars,
wire formats, and audit-schema reference. Anything an external consumer
(an MCP client, a log shipper, a deploy script) could observe and depend
on lives here.

> Design rationale lives in [docs/architecture.md](docs/architecture.md).
> What's next lives in [ROADMAP.md](ROADMAP.md).

---

## Transport

One process binary, two transports. Selected at startup via
`PBI_MCP__Transport`:

- **`stdio`** (default) — single MCP client over stdin/stdout. Stdout is
  reserved for the MCP JSON-RPC transport; all logging routes to stderr
  or to a rolling file under `${AuditDir}/logs/`.
- **`http`** — ASP.NET Core host running the MCP **Streamable HTTP**
  transport (spec 2025-11-25). Bound to `PBI_MCP__HttpHost`:`HttpPort`
  (defaults `127.0.0.1:5000`). Single endpoint at `/`; `POST` for
  client→server JSON-RPC, `GET` upgrades to `text/event-stream` for
  server→client streams. Stateless (no `Mcp-Session-Id`). The deprecated
  HTTP+SSE transport is **not** enabled.
    - Every authenticated request requires a custom
      `X-Api-Key: <PBI_MCP__HttpAuthToken>` header. Constant-time
      compare; missing or wrong key returns **401** with no body and
      no `WWW-Authenticate` header. `/healthz` is exempt.
    - We deliberately use a custom header rather than
      `Authorization: Bearer` so that spec-compliant MCP clients (notably
      VS Code's MCP client) do not interpret a 401 as the trigger for
      OAuth 2.1 protected-resource discovery + dynamic client
      registration. With `X-Api-Key` the OAuth state machine never
      activates and the static shared secret in `mcp.json` works
      directly.
    - The API key authenticates the *caller to the server*. Power BI calls
      still run as the server's ambient identity (Managed Identity in
      Azure, the developer's `az login` user locally) — single-tenant
      only. See [SECURITY.md](SECURITY.md) for the full trust model.

---

## Configuration (env vars)

The server takes **no required configuration for stdio**. The agent
supplies the workspace and dataset on every call, and identity is resolved
by `Azure.Identity.DefaultAzureCredential` (Managed Identity in Azure, the
developer's `az login` cached credentials locally).

HTTP transport requires one extra value: `PBI_MCP__HttpAuthToken`.

Standard .NET double-underscore convention so values bind to a POCO. Real
environment variables always win over a sibling `.env` file.

| Variable | Default | Purpose |
|---|---|---|
| `PBI_MCP__AuditDir` | `~/.pbi-modeling-mcp` | Audit + backup + log root. |
| `PBI_MCP__RequireConfirmDelete` | `true` | When true, `delete_*` tools call MCP `elicitInput`. |
| `PBI_MCP__LogLevel` | `Information` | One of `Verbose`/`Debug`/`Information`/`Warning`/`Error`/`Fatal`. |
| `PBI_MCP__Actor` | `{user}@{host}` | Identity recorded in audit events. |
| `PBI_MCP__Transport` | `stdio` | `stdio` or `http`. |
| `PBI_MCP__HttpHost` | `127.0.0.1` | IP to bind when `Transport=http`. |
| `PBI_MCP__HttpPort` | `5000` | Port to bind when `Transport=http`. |
| `PBI_MCP__HttpAuthToken` | (none) | **Required when `Transport=http`** unless `HttpDisableAuth=true`. Min 32 chars; sentinel placeholders are rejected at startup. |
| `PBI_MCP__HttpAllowInsecure` | `false` | Required to bind a non-loopback `HttpHost` without TLS (self-hosted shapes only — see `HttpListenAllInterfaces` for App Service / ACA). |
| `PBI_MCP__HttpDisableAuth` | `false` | Opt out of the API-key auth middleware. Loopback / local-dev only; emits a loud stderr banner. |
| `PBI_MCP__HttpListenAllInterfaces` | `false` | **Platform-managed hosting** mode (App Service, Azure Container Apps, AKS behind ingress, any reverse proxy that picks the listen port). When true: bind on `*:{port}`, resolve `{port}` from `PORT` → `WEBSITES_PORT` → `HttpPort`, skip the loopback / IP-parse / cleartext-secret guardrails (TLS termination is the platform's job). |

Fail-closed startup checks for `Transport=http`:

1. Missing `HttpAuthToken` → refuse to start (skipped when `HttpDisableAuth=true`).
2. `HttpAuthToken` matches a known placeholder (e.g.
   `REPLACE_ME_WITH_A_SECURE_RANDOM_STRING`) → refuse to start.
3. `HttpAuthToken` shorter than 32 chars → refuse to start.
4. Non-loopback `HttpHost` + `HttpAllowInsecure=false` → refuse to
   start (so the API key is never shipped over cleartext by accident).
   Skipped when `HttpListenAllInterfaces=true` because TLS termination
   is upstream of the process.

A loud one-line stderr banner is emitted on every HTTP launch summarising
the trust model (“shared-secret API-key auth enabled … not multi-user
safe”, or “AUTH DISABLED … do not run anywhere reachable” when the
opt-out is on).

### Deployment recipes

Three supported deployment shapes for the HTTP transport. All run the
same binary, selected by env vars.

**1. Local development (loopback, optional auth)**

```
PBI_MCP__Transport=http
PBI_MCP__HttpHost=127.0.0.1
PBI_MCP__HttpPort=5000
# Either set a real API key …
PBI_MCP__HttpAuthToken=<32+ char random string>
# … or for loopback-only local dev, opt out of auth entirely:
# PBI_MCP__HttpDisableAuth=true
```

Identity comes from `az login`. Reachable only from the same box. This
is the shape that matches `.vscode/mcp.json` pointing at
`http://127.0.0.1:5000/`.

**2. Self-hosted on a VM behind a reverse proxy**

```
PBI_MCP__Transport=http
PBI_MCP__HttpHost=127.0.0.1     # bind loopback; proxy talks to it
PBI_MCP__HttpPort=5000
PBI_MCP__HttpAuthToken=<32+ char random string>
PBI_MCP__AuditDir=/var/lib/pbi-modeling-mcp     # or a Windows path
```

Caddy / nginx / IIS terminates TLS on 443, forwards to `127.0.0.1:5000`.
Identity comes from a UAMI attached to the VM (UAMI preferred over SAMI:
its principal is stable across host redeploys, so the workspace grant
doesn't have to be redone every time the VM is recreated).
The fail-closed startup checks keep `HttpHost=0.0.0.0` from being used
without an explicit `PBI_MCP__HttpAllowInsecure=true`.

**3. Platform-managed hosting (Azure App Service, Azure Container Apps)**

```
PBI_MCP__Transport=http
PBI_MCP__HttpListenAllInterfaces=true   # bind *:{port}, port from PORT/WEBSITES_PORT
PBI_MCP__HttpAuthToken=<32+ char random string>
PBI_MCP__AuditDir=D:\home\pbi-modeling-mcp     # App Service persistent path
```

The platform injects `PORT` (App Service / ACA convention) and terminates
TLS upstream on the default `*.azurewebsites.net` /
`*.azurecontainerapps.io` URL. The loopback / cleartext-secret
guardrails are skipped because they don't apply when the platform owns
the bind address and the cert.

App Service deployment recipe:

1. Create the App Service (Linux, .NET 8) and a UAMI; attach the UAMI.
2. Grant the UAMI **Member** access on the target Power BI workspace
   (`Add-PowerBIWorkspaceUser ... -PrincipalType App`) and confirm the
   tenant setting *Allow service principals to use Power BI APIs* is
   enabled.
3. Set the four App Settings above. If multiple MIs are attached, also
   set `AZURE_CLIENT_ID=<uami-clientId>` so `DefaultAzureCredential`
   picks the right one.
4. `dotnet publish src/PbiModelingMcp -c Release` and zip-deploy
   (`az webapp deploy --src-path …` or `az webapp up`).
5. Smoke-test `https://<app>.azurewebsites.net/healthz` (no auth) and
   `POST /` with the X-Api-Key header (returns 406 without proper Accept,
   which still proves the gate let it through).
6. Remote MCP client config (laptop side):

   ```json
   {
     "inputs": [
       { "id": "pbi-mcp-key", "type": "promptString", "password": true,
         "description": "API key for the PBI MCP server." }
     ],
     "servers": {
       "pbi-modeling": {
         "type": "http",
         "url": "https://<app>.azurewebsites.net/",
         "headers": { "X-Api-Key": "${input:pbi-mcp-key}" }
       }
     }
   }
   ```

ACA deployment is identical except the listen-port convention is the
same `PORT` env var (already honored), the persistent path is wherever
the container image puts `AuditDir`, and you need a Dockerfile +
`az containerapp up`.

---

## Tool Surface

All tools take a `CancellationToken` (provided by the SDK). Errors throw
exceptions with clear messages — the SDK marshals them into MCP error
responses. Every model-touching tool requires `workspace` + `dataset`;
there is no implicit current connection.

### Discovery

- `list_workspaces()` — Power BI REST. Workspaces visible to the current
  identity. Each item: `{id, name, isReadOnly?, isOnDedicatedCapacity?, capacityId?}`.
- `list_datasets(workspace)` — Power BI REST. Required `workspace` (name
  or GUID). Each item:
  `{id, name, configuredBy?, isRefreshable?, webUrl?}`.

### Read

- `list_tables(workspace, dataset)` —
  `[{name, isHidden, measureCount, columnCount}]`.
- `list_measures(workspace, dataset, table)` —
  `[{name, expression, formatString?, displayFolder?, description?, isHidden}]`.
- `get_measure(workspace, dataset, table, name)` —
  `{table, name, expression, formatString?, displayFolder?, description?, isHidden, dataType?, modifiedTimeUtc?}`.

### Write

- `add_measure(workspace, dataset, table, name, dax, formatString?, displayFolder?, description?, dryRun?)`
- `update_measure(workspace, dataset, table, name, dax?, formatString?, displayFolder?, description?, dryRun?)`
- `delete_measure(workspace, dataset, table, name, confirm?, dryRun?)`

All write tools return a `WriteResult`:
`{action, outcome, table, measure?, durationMs, backupPath?, diffBefore?, diffAfter?}`,
where `outcome` is `"applied"` or `"preview"` (errors throw).

#### `update_measure` partial-update convention

JSON-RPC binding through the MCP SDK cannot distinguish *omitted* from
*explicitly null* — both arrive as `null` at the .NET layer. The
convention:

- **`null`** (or omitted) → leave existing value unchanged.
- **`""`** (empty string) → clear the field, where the model permits.
  `FormatString`, `DisplayFolder`, and `Description` accept this; `dax`
  does not (a measure must have an expression).
- `name` and `table` are not updatable here. A `rename_measure` tool is
  on the [roadmap](ROADMAP.md).

This also matches TOM's native representation, where empty string is the
unset state for these fields.

#### Confirmation for destructive ops

When `PBI_MCP__RequireConfirmDelete=true` (default) and `dryRun` is false,
`delete_measure` calls MCP `elicitInput` with a yes/no prompt before
applying. Per-call override: pass `confirm: true`. If the client doesn't
support elicitation, the call fails with a clear error suggesting either
`confirm: true` or disabling the env var globally.

---

## Resources

No MCP resources are exposed. Equivalent reads are available via the
`list_tables`, `list_measures`, and `get_measure` tools, which take
`workspace` + `dataset` explicitly.

---

## Safety pipeline (write tools)

Every mutating tool runs through:

1. **Pre-validate** — table exists, no duplicate, etc. Fails fast with a
   clear error before anything is logged or written.
2. **Audit `pre`** — append a `pre` event with action, args, descriptor,
   actor, dry-run flag, request id.
3. **If `dryRun`** — apply in-memory, serialize before/after TMSL of the
   affected scope (the table), then `UndoLocalChanges()`. Audit `outcome:
   "preview"`. No save, no backup file.
4. **If real** — write a TMSL backup to
   `{AuditDir}/backups/{workspace}/{dataset}/{stamp}-{action}.bim`, apply
   via TOM, `SaveChanges()`. Audit `outcome: "applied"` with `backupPath`
   and `durationMs`.
5. **On error** — best-effort `UndoLocalChanges()`, audit `outcome:
   "error"` with structured `{type, message}`, re-throw.

`CancellationToken` is honored *between* phases. A `SaveChanges()` already
in flight runs to completion (TOM is synchronous mid-write); the audit
will still record its outcome. Treat Ctrl+C as "stop after the current
write."

---

## Audit log

JSONL, append-only, one file per UTC date under
`{AuditDir}/audit/audit-YYYY-MM-DD.log`. Versioned schema, currently `1`.
For the field list, examples, and the schema changelog see
[docs/audit-schema.md](docs/audit-schema.md).

---

## Backups

TMSL snapshots written before every non-dry-run mutation:

- Path: `{AuditDir}/backups/{workspace}/{dataset}/{stamp}-{action}.bim`,
  where `{stamp}` is `yyyyMMddTHHmmssfffZ` UTC.
- Format: TMSL JSON, exactly as TOM emits it.
- Retention: not bounded by the server (see [ROADMAP.md](ROADMAP.md)).

---

## Threat model

There are two trust boundaries that move independently. They are written
up in detail in [SECURITY.md](SECURITY.md); the summary:

- **Inbound auth** — stdio: the OS process boundary (whoever can spawn
  the server can call it). HTTP: shared-secret API key.
  **Single-tenant only.** Multi-user / OBO is on the
  [ROADMAP](ROADMAP.md).
- **Backend Power BI auth** — the resolved server identity (Managed
  Identity in Azure, the developer's `az login` user locally). Power BI
  workspace permissions are the outermost guardrail; the server cannot
  perform any action this identity is not authorized for.
- **The LLM is also a trust boundary** — prompt-injected DAX or
  destructive tool calls are mitigated by (a) audit + backup of every
  write, (b) explicit confirmation for deletes, and (c) `dryRun` for
  preview. We do **not** sandbox DAX semantics; a measure expression can
  reference any table the resolved identity can see.

See [SECURITY.md](SECURITY.md) for hardening notes and the disclosure
process.

---

## Prerequisites (Power BI / Azure side)

These are configuration on the Power BI tenant and workspace, not on the
server itself, but they are a hard requirement for the server to do
anything useful:

- Workspace on **Premium** or **Microsoft Fabric** capacity. XMLA write
  does not exist on shared capacity.
- Tenant: *Allow XMLA endpoints and Analyze in Excel with on-premises
  datasets* set to **Read Write**.
- Tenant: *Allow service principals (and managed identities) to use Power
  BI APIs* enabled — required when the resolved identity is a Managed
  Identity, not required when it's a regular user.
- Resolved identity added to the workspace as **Member** or **Admin**
  (Contributor is not enough for write). For an MI, add it via
  `Add-PowerBIWorkspaceUser ... -PrincipalType App`.
- The XMLA endpoint requires the workspace **name** (not GUID) in the
  `Data Source` URL. Datasets accept either.
