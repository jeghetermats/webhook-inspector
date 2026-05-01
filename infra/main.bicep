// Webhook Inspector: everything in one resource group (PLAN.md section 10).
// Deployed by .github/workflows/infra.yml. Secrets arrive as @secure() parameters from
// GitHub environment secrets and end up as Container Apps secrets.
targetScope = 'resourceGroup'

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Image for the app, e.g. ghcr.io/owner/repo:<sha>.')
param appImage string

@description('Image for the migrate job. Normally the same as appImage.')
param migrateImage string = appImage

@description('Postgres administrator login.')
param postgresAdminLogin string = 'webhookadmin'

@secure()
@description('Postgres administrator password.')
param postgresAdminPassword string

@description('Postgres major version.')
param postgresVersion string = '17'

@description('Client id of the production GitHub OAuth app. Empty until that app exists; sign-in then returns 503.')
param githubClientId string = ''

@secure()
@description('Client secret of the production GitHub OAuth app.')
param githubClientSecret string = ''

@description('Capture rate limit per token per minute. Raise only temporarily, for a load test.')
param capturePermitsPerTokenPerMinute int = 120

@description('Capture rate limit per source IP per minute. Raise only temporarily, for a load test.')
param capturePermitsPerIpPerMinute int = 300

@description('Email for budget alerts. Empty skips the budget.')
param budgetContactEmail string = ''

@description('First day of the month the budget starts. Must stay fixed after the first deploy.')
param budgetStartDate string = '2026-03-01'

var name = 'webhook-inspector'
var suffix = uniqueString(resourceGroup().id)

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    name: name
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    location: location
    serverName: 'psql-${name}-${suffix}'
    adminLogin: postgresAdminLogin
    adminPassword: postgresAdminPassword
    version: postgresVersion
  }
}

module apps 'modules/containerapps.bicep' = {
  name: 'containerapps'
  params: {
    location: location
    name: name
    workspaceName: monitoring.outputs.workspaceName
    appInsightsName: monitoring.outputs.appInsightsName
    postgresHost: postgres.outputs.host
    databaseName: postgres.outputs.databaseName
    postgresUser: postgresAdminLogin
    postgresPassword: postgresAdminPassword
    appImage: appImage
    migrateImage: migrateImage
    githubClientId: githubClientId
    githubClientSecret: githubClientSecret
    capturePermitsPerTokenPerMinute: capturePermitsPerTokenPerMinute
    capturePermitsPerIpPerMinute: capturePermitsPerIpPerMinute
  }
}

module budget 'modules/budget.bicep' = if (!empty(budgetContactEmail)) {
  name: 'budget'
  params: {
    name: name
    contactEmail: budgetContactEmail
    startDate: budgetStartDate
  }
}

output appUrl string = 'https://${apps.outputs.appFqdn}'
output appName string = apps.outputs.appName
output migrateJobName string = apps.outputs.migrateJobName
output cleanupJobName string = apps.outputs.cleanupJobName
output logAnalyticsWorkspaceId string = monitoring.outputs.workspaceCustomerId
