// Container Apps environment, the app and the migrate job.
param location string
param name string
param workspaceName string
param appInsightsName string
param postgresHost string
param databaseName string
param postgresUser string

@secure()
param postgresPassword string

param appImage string
param migrateImage string
param githubClientId string
param capturePermitsPerTokenPerMinute int
param capturePermitsPerIpPerMinute int

@secure()
param githubClientSecret string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

// VerifyFull: encrypted and the server certificate is checked against the image's CA store.
// GSS Encryption Mode=Disable: Azure uses TLS, and without it Npgsql first tries Kerberos
// and logs an error because the image has no libgssapi.
// Maximum Pool Size=20: B1MS allows 50 connections (some reserved by Azure). Npgsql's default
// of 100 let an overloaded replica open more than the server accepts (phase 6 load test).
var postgresConnection = 'Host=${postgresHost};Database=${databaseName};Username=${postgresUser};Password=${postgresPassword};SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Maximum Pool Size=20'

var hasGitHub = !empty(githubClientId) && !empty(githubClientSecret)

var appSecrets = concat(
  [
    { name: 'postgres-connection', value: postgresConnection }
    { name: 'appinsights-connection', value: appInsights.properties.ConnectionString }
  ],
  hasGitHub ? [{ name: 'github-client-secret', value: githubClientSecret }] : []
)

var appEnv = concat(
  [
    { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
    { name: 'ForwardedHeaders__Enabled', value: 'true' }
    { name: 'ConnectionStrings__Postgres', secretRef: 'postgres-connection' }
    { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection' }
    { name: 'Capture__PermitsPerTokenPerMinute', value: string(capturePermitsPerTokenPerMinute) }
    { name: 'Capture__PermitsPerIpPerMinute', value: string(capturePermitsPerIpPerMinute) }
  ],
  hasGitHub
    ? [
        { name: 'Authentication__GitHub__ClientId', value: githubClientId }
        { name: 'Authentication__GitHub__ClientSecret', secretRef: 'github-client-secret' }
      ]
    : []
)

// environmentMode must be set explicitly: without it Azure creates an "express"
// environment, which does not support jobs (ExpressEnvironmentResourceNotSupported).
// The Consumption workload profile has the same pricing and free grant as before.
resource environment 'Microsoft.App/managedEnvironments@2026-03-02-preview' = {
  name: 'cae-${name}'
  location: location
  properties: {
    environmentMode: 'WorkloadProfiles'
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: 'ca-${name}'
  location: location
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      secrets: appSecrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: appImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: appEnv
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 2
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 15
              failureThreshold: 3
            }
          ]
        }
      ]
      // Scale to zero to stay inside the free grant (ADR 0007). One replica, because
      // SignalR runs in-process without a backplane (ADR 0004).
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

// Applies EF Core migrations from the new image before the app is updated (ADR 0008).
resource migrateJob 'Microsoft.App/jobs@2026-03-02-preview' = {
  name: 'caj-${name}-migrate'
  location: location
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [
        // postgresConnection is built from the @secure() password; the linter cannot see that through a variable.
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'postgres-connection', value: postgresConnection }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: migrateImage
          command: ['/app/efbundle']
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ConnectionStrings__Postgres', secretRef: 'postgres-connection' }
          ]
        }
      ]
    }
  }
}

// Deletes requests older than 7 days, every hour at :17 (ADR 0009). Runs the app image
// with the 'cleanup' argument and works while the app itself has scaled to zero.
resource cleanupJob 'Microsoft.App/jobs@2026-03-02-preview' = {
  name: 'caj-${name}-cleanup'
  location: location
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 600
      replicaRetryLimit: 1
      scheduleTriggerConfig: {
        cronExpression: '17 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [
        // Built from the @secure() password; the linter cannot see that through a variable.
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'postgres-connection', value: postgresConnection }
        { name: 'appinsights-connection', value: appInsights.properties.ConnectionString }
      ]
    }
    template: {
      containers: [
        {
          name: 'cleanup'
          image: appImage
          args: ['cleanup']
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__Postgres', secretRef: 'postgres-connection' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection' }
          ]
        }
      ]
    }
  }
}

output appName string = app.name
output appFqdn string = app.properties.configuration.ingress.fqdn
output migrateJobName string = migrateJob.name
output cleanupJobName string = cleanupJob.name
