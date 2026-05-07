# Architecture (non-normative)

How the server is built today. This document follows the code; if it disagrees
with `src/`, the code wins. For stable contracts (tools, resources, env vars,
safety guarantees), see [SPEC.md](../SPEC.md).

---

## Project Layout

One concern per folder under `src/PbiModelingMcp/`. Most folders contain one
interface plus its default implementation, so swapping providers is a
constructor change, not a refactor.

| Folder | Concern |
|---|---|
| `Configuration/` | POCOs bound from env vars, validated on start |
| `Auth/` | Token acquisition (`ITokenProvider`) |
| `Connection/` | Connection descriptors, per-call `Server` lifecycle |
| `Modeling/` | TOM operations (`IModelingService`) |
| `Tools/` | MCP tool surface — thin wrappers over `IModelingService` |
| `Http/` | HTTP transport: API-key auth middleware, startup validation, host builder |
| `Audit/` | Audit log + TMSL backups |

---

## Connection Model

**Stateless on model identity.**

The server holds no "current connection." Every model-touching tool
takes `workspace` + `dataset` as required arguments and the connection
manager opens a fresh TOM `Server` per call. There is no `connect` /
`disconnect` / `status` step, no implicit fallback descriptor, and no
per-session state.

Why: the HTTP transport (see below) accepts requests from arbitrary
callers in any interleaving. A single mutable "current" descriptor would
either need session affinity (which the spec does not guarantee) or
race between callers. Making model identity an explicit argument removes
the whole class of "which dataset did the previous request set?" bugs
and keeps stdio and HTTP behaving identically.

### Concurrency model

A per-`(workspace, dataset)` `SemaphoreSlim` in `ConnectionManager`
serializes calls against the *same* model so two concurrent writes
can't race through one TOM `Server`. Calls against *different* models
run in parallel. The lock is process-global; under HTTP transport that
means concurrent HTTP requests from different callers serialize when
they hit the same model and proceed in parallel otherwise.

Every Power BI call — read or write, stdio or HTTP — runs as the
server process's ambient identity (Managed Identity in Azure, the
developer's `az login` user locally). The HTTP API key authenticates
the *caller to the server*; it does not propagate to Power BI. See the
separate **inbound auth** vs **backend PBI auth** treatment in
[SECURITY.md](../SECURITY.md).

### `Server` lifecycle

A fresh TOM `Server` is opened *per tool call* and disposed at the end. No
pooling, no long-lived sessions. This:

- avoids stale model state when another editor changes the model
- avoids token-expiry edge cases entirely
- sidesteps TOM's thread-unsafety on a per-`Server` basis

### Cancellation

Every public async method takes a `CancellationToken`. Cancellation is
honored *between* TOM phases (resolve → connect → operate → save). A
`SaveChanges()` already in flight will run to completion — TOM's call is
synchronous and not cancellable mid-write — and the audit will still record
its outcome. Operators should treat Ctrl+C as "stop after the current write,"
not "roll back."

---

## Authentication

Pluggable behind `ITokenProvider`. The default implementation uses
`Azure.Identity.DefaultAzureCredential` and hands the access token to TOM
via the XMLA connection string's `Password=<token>` slot (no `User ID`).
The token already encodes the principal, so the same code path works in
both environments: in production on Azure it resolves to a Managed
Identity (no secrets in the container); locally it resolves to the
developer's `az login` user.

This seam exists so a future provider (e.g. an explicit
`ClientCertificateCredential` pulling a cert from Key Vault) can land
without touching `ModelingService`. Per-workspace credential overrides are
supported by a `CredentialName` on the connection descriptor mapping to a
named entry in config; absent that, the ambient identity from
`DefaultAzureCredential` is used.

### Identity resolution per host

The same code path resolves to different principals depending on where
the server is running. The principal that lands in the XMLA `Password=`
slot is whoever's access token came back from the credential chain — so
*that* principal is what must be a Member of the target Power BI
workspace.

| Host | Credential reached | Principal that needs workspace access |
|---|---|---|
| Laptop | `AzureCliCredential` | The signed-in `az login` user |
| Azure VM, system-assigned MI | `ManagedIdentityCredential` | The VM's SAMI principal (dies if the VM is recreated) |
| Azure VM, user-assigned MI | `ManagedIdentityCredential` | The UAMI's principal (stable across redeploys) |

When more than one managed identity is reachable on the same host (e.g.
SAMI + UAMI both enabled, or multiple UAMIs attached), IMDS cannot
guess which one the app wants. Set `AZURE_CLIENT_ID=<uami-clientId>` on
the server process so `ManagedIdentityCredential` requests a token for
that specific identity. With a single MI attached, the env var is
unnecessary.

The chain order (`Environment → WorkloadIdentity → ManagedIdentity →
VisualStudio → AzureCli → AzurePowerShell`) means an Azure host with an
MI attached *will* use the MI even if a developer has also done
`az login` on the same box — `AzureCliCredential` is later in the chain
and only reached when no earlier credential succeeds. This is
intentional: the MI is the "production" identity even on dev VMs.

UAMI is preferred over SAMI for any Azure host beyond a throwaway dev
VM. SAMI's principal is bound to the host's lifecycle (recreate the VM,
switch from ACI to ACA, principal changes — every workspace grant has
to be redone); a UAMI is a standalone resource with a stable principal
that survives host redeploys and can be attached to multiple hosts.

Note that the server supports two host shapes: stdio (one MCP client
co-located with the server process) and HTTP (Streamable HTTP transport
over TCP, one or many remote callers). Both run the same DI graph and
the same tool surface. See **Hosting & Wiring** below.

---

## Hosting & Wiring

One process binary, two host shapes selected at startup by
`PBI_MCP__Transport`:

- `stdio` (default) — `Host.CreateApplicationBuilder` +
  `WithStdioServerTransport`. One MCP client over stdin/stdout.
- `http` — `WebApplication.CreateBuilder` + `WithHttpTransport` (the MCP
  C# SDK's Streamable HTTP transport, *not* the deprecated SSE one).
  Stateless mode; API-key auth middleware (custom `X-Api-Key` header,
  not `Authorization: Bearer` — see `ApiKeyAuthMiddleware` for why) in
  front of every route except `/healthz`; fail-closed startup checks
  (see `Http/HttpTransportValidation.cs`).

Both shapes share `RegisterCoreServices` in `Program.cs`, which binds
`ServerOptions` with `ValidateDataAnnotations().ValidateOnStart()` and
registers auth, connection, modeling, and audit services as singletons.

The HTTP shape additionally:

- Refuses to start if `HttpAuthToken` is missing, matches a known
  placeholder, or is shorter than 32 chars.
- Refuses to bind a non-loopback `HttpHost` without `HttpAllowInsecure=true`,
  so the API key is never shipped over cleartext by accident.
- Emits a one-line stderr banner on every launch summarising the trust
  model (“shared-secret API-key auth enabled … not multi-user safe”).

There is no startup-time Power BI connection in either shape; tools
take `workspace` + `dataset` explicitly.

### Logging

Stdout is reserved for the MCP JSON-RPC transport. Any stray write corrupts
it. Logging therefore:

- routes to **stderr** (above an info threshold) and a **rolling file** under
  the audit dir
- never uses `System.Console.*` — the
  `Microsoft.CodeAnalysis.BannedApiAnalyzers` package + `BannedSymbols.txt`
  in `src/PbiModelingMcp/` ban the type wholesale, and `RS0030` is bumped
  to `error` in `.editorconfig` so any use fails the build. If you
  genuinely need stderr (e.g. emergency diagnostics before Serilog is up),
  suppress `RS0030` with `#pragma` + a justifying comment.

---

## Engineering Hygiene

- `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`,
  `EnableNETAnalyzers=true`, `AnalysisLevel=latest-recommended`.
- `Directory.Packages.props`: central package management; versions pinned in
  one place.
- `.editorconfig`: standard Microsoft C# style, enforced via `dotnet format`
  in CI.
- Tests:
  - `tests/PbiModelingMcp.Tests` — pure unit tests (argument validation,
    descriptor resolution, audit serialization). No live deps.
  - `tests/PbiModelingMcp.IntegrationTests` — in-process HTTP host tests
    via `Microsoft.AspNetCore.TestHost` (auth gate, `/healthz`).

---

## Build & Distribution

Local dev:

```bash
dotnet run --project src/PbiModelingMcp
```

Self-contained binary (no .NET runtime needed on the target machine):

```bash
dotnet publish src/PbiModelingMcp -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Verified RID: `win-x64`. Other RIDs (Linux/macOS) build but the
Microsoft Analysis Services .NET package's native bindings haven't been
validated off Windows — dev is Windows-only for that reason. The Azure
deploy uses Linux App Service successfully because the runtime is
hosted by the platform, not the publish-self-contained shape.
