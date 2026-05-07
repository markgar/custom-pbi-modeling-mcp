# Deploy this MCP server to Azure

The repo ships with an
[`azd`](https://learn.microsoft.com/azure/developer/azure-developer-cli/)
template (`azure.yaml`, `infra/main.bicep`, `infra/resources.bicep`,
`infra/main.parameters.json`) that puts the server on Azure App Service
Linux with a User-Assigned Managed Identity. Four resources, ~$0/mo on
the F1 Free tier, ~2-minute first deploy.

The story splits in two: tenant-admin **prerequisites** that the
deployer almost certainly can't do themselves, and the **deployment**
flow that's a single command.

---

## Prerequisites (tenant-admin, one-time)

If you're not a Power BI tenant admin, send this list to whoever is.
Once it's done, every deploy in this tenant just works.

1. **Allow service principals (and managed identities) to use Power BI APIs**
   — Power BI Admin Portal → *Tenant settings* → *Developer settings*.
   Apply to entire org or to a security group containing the UAMIs you'll
   deploy.
2. **Allow XMLA endpoints — Read Write** — same admin portal,
   *Integration settings* (or *Premium settings* depending on portal
   version). Required for write tools; reads work without it.
3. **Premium / Fabric capacity** assigned to the workspaces you'll
   target. XMLA write does not exist on shared capacity.

If your tenant already runs other SPN/MI Power BI work (Fabric pipelines,
deployment pipelines, …) items 1 and 2 are usually already on.

There is no API surface that lets you flip these toggles without an
admin. Don't try to automate them.

---

## Deployment

```powershell
# one-time on your laptop
winget install Microsoft.Azd
azd auth login                    # browser flow

# from the repo root
azd up                            # prompts: env name (e.g. dev), subscription, region
                                  # ~2 minutes; prints the App Service URL
```

What gets deployed:

- Resource group `rg-pbi-mcp-server-<env>` (overridden from `azd`'s
  default `rg-<env>` so the RG's role is obvious in a busy subscription)
- App Service Plan F1 Linux + App Service running the .NET 8 binary
- A User-Assigned Managed Identity attached to the App Service
- An auto-generated `X-Api-Key` value, persisted across re-deploys

The server is reachable but **the deployed UAMI has no Power BI access
yet**. `list_workspaces` will return an empty array until step 3 below.
This is expected, not a bug.

---

## Grant the UAMI access to a Power BI workspace

Without this step, the deployed server runs but Power BI returns no
workspaces. The UAMI's clientId is exposed as an `azd` output:

```powershell
Install-Module -Name MicrosoftPowerBIMgmt -Scope CurrentUser   # one-time
Connect-PowerBIServiceAccount

$uami = azd env get-value PBI_MCP_UAMI_CLIENT_ID
Add-PowerBIWorkspaceUser -Workspace '<your workspace name>' `
  -PrincipalType App -Identifier $uami -AccessRight Member
```

Member or Admin — Contributor is not enough for write. There is no
Azure provider for this; it's a Power BI REST call that requires a
workspace admin to issue.

---

## Wire up an MCP client

The endpoint URL and key are both `azd env` outputs:

```powershell
azd env get-value SERVICE_PBI_MODELING_ENDPOINT_URL
azd env get-value HTTP_AUTH_TOKEN
```

[`.vscode/mcp.json.sample`](../.vscode/mcp.json.sample) ships with a
`pbi-modeling-remote` server entry pointed at a placeholder URL.
After `azd up`, copy the sample to `.vscode/mcp.json` (gitignored),
replace `<your-app-name>` in the URL with what `azd env get-value
SERVICE_…` printed, reload VS Code, and paste the key when prompted
(VS Code caches it in the OS keychain thereafter).

---

## Verify end-to-end

```powershell
$url = azd env get-value SERVICE_PBI_MODELING_ENDPOINT_URL
$tok = azd env get-value HTTP_AUTH_TOKEN

# Liveness — exempt from auth, should be 200
curl.exe -s -o NUL -w "healthz : %{http_code}`n" "$url/healthz"

# Auth gate active — should be 401 with no body
curl.exe -s -o NUL -w "no auth : %{http_code}`n" -X POST "$url/"

# Past the gate — should NOT be 401 (likely 4xx from empty body, that's fine)
curl.exe -s -o NUL -w "with key: %{http_code}`n" -X POST "$url/" -H "X-Api-Key: $tok"
```

When an MCP client connects to the deployed URL and `list_workspaces`
returns the workspace you granted, the deploy is fully wired.

---

## Re-deploy and rotate

```powershell
azd up                       # idempotent; key stays the same across re-runs
azd deploy                   # code-only; skip provisioning
azd down --force --purge     # delete everything and clear local state
```

To rotate the API key:

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$b   = New-Object byte[] 32
$rng.GetBytes($b)
azd env set HTTP_AUTH_TOKEN ([Convert]::ToBase64String($b))
azd provision
```

App Service restarts; redistribute the new value to clients.

---

## Production hardening

The shipped template is intentionally minimal: F1 (free, sleeps when
idle), no Application Insights, no Key Vault, no VNet. Each is a
deliberate "keep the demo cheap" choice. For real production:

- **F1 → B1.** Edit `infra/resources.bicep`'s `sku.name` to `B1` and
  set `alwaysOn: true`. ~$13/mo, no cold start.
- **Add Application Insights.** Adds a Log Analytics workspace and an
  App Insights resource; wire them in via `appSettings` block.
- **Replace the API-key middleware** with `JwtBearer` against your
  tenant or turn on App Service Easy Auth and set
  `PBI_MCP__HttpDisableAuth=true`. Production-swap is one Bicep edit
  and one DI line.

None of those changes touch the C# code.

---

## Common gotchas

- **`azd auth login` browser popup may be blocked.** If no browser tab
  opens, switch to `azd auth login --use-device-code` — it prints a
  code to paste at https://microsoft.com/devicelogin.
- **Default plan SKU is F1 (Free).** Most subscriptions have zero Basic
  VM quota; F1 sidesteps that. F1 has no Always On, so the first request
  after ~20 min idle pays a cold-start. To switch to always-on, change
  `infra/resources.bicep`'s `sku.name` to `B1` and set `alwaysOn: true`
  (and be ready for a quota request).
- **Region matters for quota.** If F1 isn't enough and B1 fails on
  `InternalSubscriptionIsOverQuotaForSku`, try a different region
  (`azd env set AZURE_LOCATION westus3`) before requesting a quota
  increase.
- **`HTTP_AUTH_TOKEN` is sticky by design.** Bicep generates the value
  on first deploy via `newGuid()`; the `postprovision` hook in
  `azure.yaml` copies the resolved App Setting back into `azd env`,
  so subsequent `azd up` runs reuse the same value rather than rotating
  the key under the client.
- **Resource group naming is opinionated.** `azure.yaml` overrides
  `azd`'s default `rg-<env>` to `rg-pbi-mcp-server-<env>` so the RG's
  role is obvious in a busy subscription.
- **The deployed UAMI starts with no Power BI access.**
  `list_workspaces` will return an empty array until
  `Add-PowerBIWorkspaceUser` runs. This is expected.
- **No Key Vault by design.** App Settings holding the API key has the
  same RBAC story as a KV reference at this scale. Production deployments
  that need audited secret reads can add Key Vault later — it's a Bicep
  edit, not a code change.
