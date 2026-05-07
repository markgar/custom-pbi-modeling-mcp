# Roadmap

Real things worth doing next, in rough priority order. **No** dates,
no checkboxes, no aspirational engineering chores. When an item ships,
delete it from this file. Shipped history lives in Git / GitHub
release notes.

## Tools the server doesn't do yet

- **`rename_measure`** — preserves report dependencies (vs the current
  delete + add, which silently breaks every visual referencing the
  measure). High-value, isolated change in `ModelingService` + a new
  `Tools/` method.
- **`refresh_table` / `refresh_partition`** — the obvious complement
  to write tools for an agent that just modified a model.
- **`add_calculated_column`** — same shape as `add_measure`, different
  TOM type. Asked-for in early user feedback.
- **DAX query execution** — would let an agent verify a measure it just
  wrote actually returns the expected number. Windows-only via ADOMD;
  acceptable since dev is Windows-only anyway. Would need a separate
  tool surface (`run_dax`) and careful result-shape design.

## Operational capabilities

- **Model diff / changelog tool that reads the audit log** — the audit
  pipeline records every write; surfacing "what changed in the last 24h"
  as a tool would close the loop on the audit-as-feature story.
- **Backup retention policy + `restore_from_backup` tool** — TMSL
  backups already get written before every write; nothing prunes them
  and there's no way to apply one back to the model. Both are small
  additions on top of the existing `BackupWriter`.
- **Idempotency keys (`requestId`) on writes** — would make safe client
  retries possible. Today a retry could double-apply a write if the
  client doesn't track success itself.

## Multi-user / multi-tenant

Today's HTTP transport is single-tenant: every authenticated caller
acts on Power BI as the server's UAMI. Two future capabilities lift
that constraint independently:

- **JWT bearer inbound auth** — replace `ApiKeyAuthMiddleware` with
  `Microsoft.AspNetCore.Authentication.JwtBearer` configured against
  your tenant. The `PRODUCTION SWAP:` comment in
  `Http/HttpServerHost.cs` marks the line that changes. Caller is now
  a verified user; PBI still sees the UAMI.
- **MSAL on-behalf-of (OBO)** — second `ITokenProvider` impl that
  pulls the inbound JWT off `HttpContext` and exchanges it for a PBI
  token via OBO. PBI now sees the actual end-user; audit log gains
  `principal`. The `PRODUCTION SWAP:` comment in
  `Auth/DefaultAzureCredentialTokenProvider.cs` marks the swap.

The two are independent; either can ship first.
