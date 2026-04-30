// Log Analytics (container logs and Application Insights data) with a daily ingestion cap.
param location string
param name string

@description('Daily ingestion cap in GB. When reached, ingestion stops until the next day, so logs can be lost but cost cannot run away.')
param dailyQuotaGb string = '0.15'

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json(dailyQuotaGb)
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    // Keep IP masking on: the sender of a webhook did not agree to have their IP stored in telemetry.
    DisableIpMasking: false
  }
}

output workspaceName string = workspace.name
output workspaceCustomerId string = workspace.properties.customerId
output appInsightsName string = appInsights.name

// Operations dashboard: capture traffic and latency, errors, cleanup runs and failures.
resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, 'webhook-inspector-operations')
  location: location
  kind: 'shared'
  properties: {
    displayName: 'Webhook Inspector operations'
    category: 'workbook'
    sourceId: appInsights.id
    serializedData: string(loadJsonContent('workbook.json'))
  }
}

