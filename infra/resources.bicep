// Resource-group-scoped resources for the Power BI Modeling MCP server.
// Called by main.bicep after the RG is created. Four resources:
//
//   - User-Assigned Managed Identity (the principal that needs Member
//     access on the target Power BI workspace; granted out of band by
//     `Add-PowerBIWorkspaceUser`)
//   - App Service Plan (Linux, F1 Free)
//   - App Service (.NET 8, attached to the UAMI)
//   - Diagnostic settings — none by default; logs land in App
//     Service's built-in stream + the audit dir on persistent storage.
//
// No Key Vault: the API key lives in App Settings. App Service reads
// (`Microsoft.Web/sites/config/list`) require the same RBAC a KV
// reference would; KV would be ceremony, not a security uplift, at this
// scale. Production deployments that need audited secret reads can add
// KV later — it's a Bicep edit, not a code change.

@minLength(2)
@maxLength(32)
param environmentName string

param location string

@secure()
@description('Shared-secret API key. main.bicep is responsible for generating a default; resources.bicep just receives whatever value the caller passes through.')
param httpAuthToken string

// Deterministic per (sub, env, location). Stable across `azd up` re-runs
// so resource names don't churn, but globally unique enough to clear the
// App Service hostname namespace.
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

var planName = 'pbi-mcp-plan-${resourceToken}'
var appName = 'pbi-mcp-app-${resourceToken}'
var uamiName = 'pbi-mcp-mi-${resourceToken}'

// `azd-service-name` lets `azd deploy` find the right host for the
// `pbi-modeling` service declared in azure.yaml.
var serviceTag = {
  'azd-env-name': environmentName
  'azd-service-name': 'pbi-modeling'
}

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
  tags: {
    'azd-env-name': environmentName
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: {
    'azd-env-name': environmentName
  }
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  kind: 'linux'
  properties: {
    reserved: true // required for Linux plans
  }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  tags: serviceTag
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uami.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: uami.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      // F1 (Free) does not support alwaysOn; first request after ~20 min
      // idle pays a cold-start penalty. Bump to B1 (and re-enable
      // alwaysOn) when you outgrow the demo tier.
      alwaysOn: false
      http20Enabled: true
      // App Settings are intentionally inline, not in a child config
      // resource, so the whole server contract lands in one place. The
      // server reads these via `IConfiguration` -> `ServerOptions`; see
      // src/PbiModelingMcp/Configuration/ServerOptions.cs.
      appSettings: [
        {
          name: 'PBI_MCP__Transport'
          value: 'http'
        }
        {
          // Bind on *:{PORT}, skip the loopback / cleartext-secret guards.
          // App Service injects PORT and terminates TLS upstream.
          name: 'PBI_MCP__HttpListenAllInterfaces'
          value: 'true'
        }
        {
          // The shared secret. ANY caller with this string acts as the
          // UAMI on Power BI — single-tenant only. Rotate by changing
          // this value (`azd env set HTTP_AUTH_TOKEN <new>` then
          // `azd provision`) and updating clients to match.
          name: 'PBI_MCP__HttpAuthToken'
          value: httpAuthToken
        }
        {
          // App Service mounts /home as persistent storage shared across
          // restarts within a plan. Audit log + TMSL backups live here.
          name: 'PBI_MCP__AuditDir'
          value: '/home/pbi-modeling-mcp'
        }
        {
          // Disambiguate to the UAMI when DefaultAzureCredential's
          // ManagedIdentityCredential step runs. Required when more than
          // one MI is reachable; harmless when only this UAMI is attached.
          name: 'AZURE_CLIENT_ID'
          value: uami.properties.clientId
        }
        {
          // Build server output is published into /site/wwwroot via
          // azd's zip-deploy; setting this to 1 lets App Service mount
          // the package read-only for faster cold start.
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
      ]
    }
  }
}

output appServiceUri string = 'https://${app.properties.defaultHostName}'
output appServiceName string = app.name
output uamiClientId string = uami.properties.clientId
output uamiPrincipalId string = uami.properties.principalId
