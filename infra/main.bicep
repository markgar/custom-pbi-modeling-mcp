// Subscription-scoped entry point for the Power BI Modeling MCP server.
//
// `azd up` invokes this with the standard inputs (`environmentName`,
// `location`, `principalId`). We create one resource group and delegate
// the actual resources to `resources.bicep` so the RG-scoped module can
// stay simple and re-entrant.
//
// Outputs become `azd env get-value <NAME>` once provisioning completes.

targetScope = 'subscription'

@minLength(2)
@maxLength(32)
@description('Name of the azd environment. Used to stamp resource names.')
param environmentName string

@description('Azure region for all resources.')
param location string

@secure()
@description('Shared-secret API key the App Service exposes via the X-Api-Key header. Empty on first deploy (when the azd environment has not cached an HTTP_AUTH_TOKEN yet); the fallback param below generates one. The azure.yaml `postprovision` hook then writes the resolved value back to the azd environment so subsequent `azd up` runs reuse it rather than rotating the key.')
param httpAuthToken string = ''

@secure()
@description('Fallback shared-secret used when httpAuthToken is empty. Always defaulted, so newGuid() evaluates lazily per deploy when needed.')
param httpAuthTokenFallback string = '${newGuid()}-${newGuid()}'

// First-deploy generation. main.parameters.json substitutes empty string
// when ${HTTP_AUTH_TOKEN} is unset; that beats the parameter default, so we
// resolve here against the always-defaulted fallback instead.
var resolvedHttpAuthToken = empty(httpAuthToken) ? httpAuthTokenFallback : httpAuthToken

var resourceGroupName = 'rg-pbi-mcp-server-${environmentName}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: {
    'azd-env-name': environmentName
  }
}

module resources './resources.bicep' = {
  name: 'pbi-mcp-resources'
  scope: rg
  params: {
    environmentName: environmentName
    location: location
    httpAuthToken: resolvedHttpAuthToken
  }
}

// `azd` reads SERVICE_<service>_ENDPOINT_URL by convention; the suffix is the
// service name from azure.yaml uppercased with hyphens turned into underscores.
output SERVICE_PBI_MODELING_ENDPOINT_URL string = resources.outputs.appServiceUri

// PBI workspace grant — `Add-PowerBIWorkspaceUser ... -Identifier <clientId>`
// wants the MI's Application (client) ID, not its principal/object ID.
output PBI_MCP_UAMI_CLIENT_ID string = resources.outputs.uamiClientId

// Useful for portal-based workspace grants where Power BI asks for a
// service principal's object id.
output PBI_MCP_UAMI_OBJECT_ID string = resources.outputs.uamiPrincipalId

// Read by the azure.yaml `postprovision` hook so it can pull the resolved
// `PBI_MCP__HttpAuthToken` App Setting back into `azd env`. azd auto-exports
// every output as an env var to hooks.
output PBI_MCP_APP_SERVICE_NAME string = resources.outputs.appServiceName
output AZURE_RESOURCE_GROUP string = resourceGroupName

// Persisted by `azd` so a `git clone` + `azd up` round-trip survives without
// the user re-entering anything. Marked secure so `azd env get-values` does
// not splatter it across stdout by default.
@secure()
output PBI_MCP_AUTH_TOKEN string = resolvedHttpAuthToken
