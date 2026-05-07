# Security Policy

## Trust model

The server has two independent trust boundaries. Each is described
below so it can be reasoned about — and replaced — on its own.

### 1. Inbound auth — who can call this server

| Transport | Today | Future (ROADMAP) |
|---|---|---|
| `stdio` | OS process boundary: whoever can spawn the server can call it. | (no change planned) |
| `http` | **Shared-secret API key** (`PBI_MCP__HttpAuthToken`), sent in the custom `X-Api-Key` header. Required at startup, ≥ 32 chars, sentinel placeholders rejected, constant-time compare. `/healthz` exempt. **Single-tenant only** — the server cannot tell two callers apart. | JWT bearer with Entra-issued tokens (or platform-level Easy Auth in front of the process); per-caller authorization. |

Operator hygiene for HTTP:

- Generate the key from a CSPRNG; never reuse a previous one when
  rotating.
- Bind to `127.0.0.1` and front with a TLS-terminating reverse proxy.
  Direct non-loopback binding requires `PBI_MCP__HttpAllowInsecure=true`
  (off by default precisely so the API key is never shipped over
  cleartext by accident).
- Rotate by env-var change + restart.

### 2. Backend Power BI auth — what the server can do once a call is in

| Today | Future (ROADMAP) |
|---|---|
| **Server's ambient identity** via `Azure.Identity.DefaultAzureCredential` (Managed Identity in Azure, the developer's `az login` user locally). Every authenticated inbound call — stdio or HTTP — acts as this same identity on Power BI. | Entra **on-behalf-of**: each inbound JWT user token gets exchanged for a PBI token via MSAL OBO, so PBI sees the actual end-user (and the audit log records `principal`). Implementation lives behind the existing `ITokenProvider` seam. |

The two layers swap independently. Going to OBO doesn't require keeping
shared-secret API-key auth; going to JWT bearer doesn't require OBO. Today's
shipped combination is **`stdio` ∨ shared-secret HTTP** for inbound,
`DefaultAzureCredential` for backend, and that's it.

A loud one-line stderr banner is emitted on every HTTP launch summarising
the trust model so operators can't miss the single-tenant constraint.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Instead, use
GitHub's private vulnerability reporting via this repository's **Security**
tab → *Report a vulnerability*. We aim to acknowledge reports within
**3 business days** and provide a remediation plan within **14 days** for
confirmed issues.

If GitHub private reporting is unavailable to you, open an issue titled
*"Security: please contact me privately"* (no details) and a maintainer
will follow up over a private channel.

## Supported versions

Until a `1.0.0` release, only the **latest tagged release** receives
security fixes. Pre-1.0 the codebase moves quickly; fixes may ship as
new minor versions rather than patches.

## Scope

In scope:

- The MCP server under `src/PbiModelingMcp/`.
- Build / release tooling under `.github/workflows/`.

Out of scope:

- Vulnerabilities in upstream dependencies (`Microsoft.AnalysisServices.*`,
  `Azure.Identity`, `ModelContextProtocol`, etc.) — please report those
  upstream. We will pick up patched versions promptly.
- Misconfiguration on the operator side: workspace roles, tenant
  settings, or a Managed Identity granted broader access than it needs.
  The README and `SPEC.md` document the required least-privilege setup.

## Handling secrets

This server uses `Azure.Identity.DefaultAzureCredential` to resolve a
Power BI identity. In production on Azure that's a Managed Identity and
**no secret material lives in the running container**. Locally it's the
developer's `az login` user (also no secret).

- **Never** include real secrets in issues, PRs, logs, or screenshots.
- Audit-event `args` are scrubbed by key name (any key matching
  `secret`/`password`/`token`/`credential`/`apikey` is replaced with
  `[REDACTED]` in `ModelingService.SanitizeArgs`). No code path today
  passes credentials to `ILogger`/Serilog — if you find one that does,
  treat it as a security bug and report it via the process above.
- The TMSL backups under `${AuditDir}/backups/` may contain model
  metadata (table names, DAX, descriptions). They never contain
  credentials, but treat the audit dir like any other sensitive log
  directory — do not check it in or share it publicly.

## Hardening notes

- **Connection-string injection** is prevented by building the XMLA
  connection string with `DbConnectionStringBuilder`, which quotes/escapes
  any value containing `;`, `=`, or quote characters. Workspace names go
  inside the `Data Source` URL and are URL-encoded.
- **stdout is reserved** for the MCP JSON-RPC transport; logging goes to
  stderr or to a file. A stray `Console.WriteLine` in `src/` would
  corrupt the transport and is treated as a bug.
- **Per-call connection lifecycle** (no pooling) avoids stale sessions
  and credential-expiry edge cases. TOM `Server` access is serialized
  per descriptor via a `SemaphoreSlim`.
- **Stateless on model identity.** Every model-touching tool requires
  `workspace` + `dataset` arguments; there is no implicit "current
  connection" that one HTTP caller could mutate underneath another.
- **HTTP fail-closed startup checks.** The process refuses to start when
  `Transport=http` and any of these is true: missing `HttpAuthToken`,
  token matches a known placeholder, token shorter than 32 chars,
  non-loopback bind without `HttpAllowInsecure=true`. See
  `src/PbiModelingMcp/Http/HttpTransportValidation.cs`.
- **Constant-time API-key compare** in `ApiKeyAuthMiddleware` via
  `CryptographicOperations.FixedTimeEquals`. Failed checks return 401
  with no body and **no `WWW-Authenticate` header** — no information
  leak about whether the header was missing or wrong. The key is
  carried in a custom `X-Api-Key` header rather than
  `Authorization: Bearer` deliberately, so spec-compliant MCP clients
  (VS Code's MCP client included) do not interpret a 401 as the
  trigger for OAuth 2.1 protected-resource discovery + dynamic client
  registration.
- **Destructive ops require confirmation** by default
  (`PBI_MCP__RequireConfirmDelete=true`).

## Production hardening

This server is shipped as a **reference implementation** — see
[README › Status](./README.md#status). The HTTP-transport inbound
auth and the backend Power BI auth shipped today are deliberately the
simplest shapes that exercise the seams. Two well-marked code
locations carry `PRODUCTION SWAP:` comments where a real multi-user
deployment will diverge. Neither swap requires a re-architecture;
both are localised and the rest of the pipeline is unaware of them.

**Inbound auth** — what the *server* requires from the *caller*.

| Where the gate is | Today | Production |
|---|---|---|
| `Http/HttpServerHost.cs` (`Build`) | Custom `ApiKeyAuthMiddleware` checking a static shared secret (`PBI_MCP__HttpAuthToken`) carried in `X-Api-Key`, with `PBI_MCP__HttpDisableAuth=true` opt-out for local dev. The custom header sidesteps the OAuth 2.1 state machine that an `Authorization: Bearer` 401 would trigger in spec-compliant MCP clients. | Either (a) turn on App Service Easy Auth in front of the process and set `PBI_MCP__HttpDisableAuth=true`, or (b) replace the middleware with `Microsoft.AspNetCore.Authentication.JwtBearer` configured against your tenant; `app.MapMcp().RequireAuthorization()`. With JWT bearer in place the spec-aligned `/.well-known/oauth-protected-resource` (RFC 9728) flow takes over, since real OAuth metadata is what the MCP spec was designed around. |

**Backend Power BI auth** — what the *server* sends to *Power BI*.

| Where the seam is | Today | Production |
|---|---|---|
| `Auth/DefaultAzureCredentialTokenProvider.cs` | Server's ambient identity (Managed Identity in Azure, `az login` user locally). Every authenticated caller acts on Power BI as this single identity — *single-tenant only*. | Second `ITokenProvider` registered for the HTTP DI graph. Pulls the inbound user's JWT off `HttpContext` (via `IHttpContextAccessor`) and exchanges it for a Power BI token via MSAL on-behalf-of (OBO). The `ITokenProvider` contract doesn't change; nothing downstream of `TomServerFactory` cares which principal the access token belongs to. |

The two swaps are *independent*. A deployment can ship JWT-bearer
inbound auth without OBO (every caller still hits PBI as the server
identity, but at least each caller is a verified user). A deployment
can ship OBO without changing inbound auth (single-tenant API-key
gating, but PBI sees the actual end-user — not generally useful, but
the pieces compose).

Grep the source for `PRODUCTION SWAP:` to find the two lines that
change.

## Responsible disclosure

We ask reporters to give us a reasonable window (default 90 days, less
for actively-exploited issues, more by mutual agreement) before public
disclosure. We will credit reporters in release notes unless asked
otherwise.
