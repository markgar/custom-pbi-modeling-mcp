# Audit Log Schema

**Schema version:** `1`

The audit log is the source of truth for "what did the server do, and when."
Consumers (log shippers, dashboards, the future model-diff tool) version
against this document, not against `SPEC.md`. Backwards-incompatible changes
bump the schema version and are called out in a changelog at the bottom of
this file.

---

## Format

- One file per UTC date: `{AuditDir}/audit/audit-YYYY-MM-DD.log`
- Encoding: UTF-8, **JSONL** (one JSON object per line, terminated by `\n`)
- Append-only within a file
- No "current" symlink and no rename-on-rotation step — readers locate today's
  file by date
- Never contains secrets (tokens, client secrets, full connection strings)

---

## Event Fields

Each line is one event. Two events are emitted per mutating call: a `pre`
event before the operation, and a `post` event after.

| Field | Type | Required | Notes |
|---|---|---|---|
| `schema` | integer | yes | Schema version. Currently `1`. |
| `ts` | string (RFC 3339, UTC, ms precision) | yes | Event timestamp. |
| `phase` | string | yes | `"pre"` or `"post"`. |
| `action` | string | yes | Tool name, e.g. `"add_measure"`. |
| `descriptor` | object | yes | `{ "workspace": string, "dataset": string }`. Names, not GUIDs, when possible. |
| `args` | object | yes | Sanitized arguments to the tool. Secrets stripped. |
| `dryRun` | boolean | yes | `true` if the call was a preview. |
| `actor` | string | yes | OS username + hostname, or `PBI_MCP__Actor` override. |
| `requestId` | string | no | Correlates `pre` and `post` for one call. UUIDv4 recommended. |
| `outcome` | string | post only | `"applied"` \| `"preview"` \| `"error"`. |
| `durationMs` | integer | post only | Wall-clock duration of the operation. |
| `error` | object | post only, on error | `{ "type": string, "message": string }`. No stack traces. |
| `backupPath` | string | post only, when a backup was written | Path to the TMSL snapshot. |
| `transport` | string | no | `"stdio"` or `"http"`. |
| `callerIp` | string | no | Remote address for HTTP transport calls. Null for stdio. |

Unknown fields MUST be ignored by consumers — additive changes do not bump
the schema version.

---

## Examples

`pre` event for an applied write:

```json
{"schema":1,"ts":"2026-05-05T18:20:42.123Z","phase":"pre","action":"add_measure","descriptor":{"workspace":"Sales","dataset":"Sales Model"},"args":{"table":"Orders","name":"Total Revenue","dax":"SUM(Orders[Amount])"},"dryRun":false,"actor":"u@host","requestId":"3f1c..."}
```

`post` event for the same call, applied successfully:

```json
{"schema":1,"ts":"2026-05-05T18:20:42.435Z","phase":"post","action":"add_measure","descriptor":{"workspace":"Sales","dataset":"Sales Model"},"args":{"table":"Orders","name":"Total Revenue","dax":"SUM(Orders[Amount])"},"dryRun":false,"actor":"u@host","requestId":"3f1c...","outcome":"applied","durationMs":312,"backupPath":"<auditDir>/backups/Sales/Sales Model/20260505T182042189Z-add_measure.bim"}
```

`post` event for a dry run:

```json
{"schema":1,"ts":"...","phase":"post","action":"add_measure","descriptor":{...},"args":{...},"dryRun":true,"actor":"...","requestId":"...","outcome":"preview","durationMs":18}
```

`post` event for an error:

```json
{"schema":1,"ts":"...","phase":"post","action":"add_measure","descriptor":{...},"args":{...},"dryRun":false,"actor":"...","requestId":"...","outcome":"error","durationMs":204,"error":{"type":"DuplicateMeasureName","message":"Measure 'Total Revenue' already exists on table 'Orders'."}}
```

---

## Backups

Backups are TMSL snapshots written before each non-dry-run mutation:

- Path: `{AuditDir}/backups/{workspace}/{dataset}/{stamp}-{action}.bim`,
  where `{stamp}` is `yyyyMMddTHHmmssfffZ` UTC and `{action}` is the tool
  name (`add_measure`, `update_measure`, `delete_measure`, …). Workspace and
  dataset segments are slugged (non-alphanumeric → `-`).
- Format: TMSL JSON, exactly as TOM emits it
- One file per save; restoring is currently a manual TMSL-deploy operation
- Retention: not yet bounded (tracked as an open issue; consumers should not
  assume backups older than N days exist)

---

## Changelog

| Schema | Date | Change |
|---|---|---|
| 1 | 2026-05-07 | Initial version. |
