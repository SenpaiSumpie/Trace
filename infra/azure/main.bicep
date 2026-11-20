// TRACE on Azure: Container Apps (web, API, worker, a migration job), Azure Database for
// PostgreSQL with pgvector, a container registry and Log Analytics.
//
// Deploy in two passes (see docs/deployment.md): first with deployApps=false to create the
// registry and database, then, after pushing the images, with deployApps=true.
//
// NOTE: this template has been compiled in CI but never deployed.

targetScope = 'resourceGroup'

@description('Short lowercase prefix for resource names, 3-12 letters or digits.')
@minLength(3)
@maxLength(12)
param namePrefix string

param location string = resourceGroup().location

@description('Create the container apps. Set to false for the first pass, before images exist in the registry.')
param deployApps bool = true

@description('Tag of the trace-api, trace-worker and trace-web images in the registry.')
param imageTag string = 'latest'

@description('Public address the web app is served from, e.g. https://trace.example.com. Leave empty to use the generated address.')
param publicUrl string = ''

param postgresAdminLogin string = 'traceadmin'

@secure()
param postgresAdminPassword string

@allowed(['anthropic'])
param chatProvider string = 'anthropic'

@allowed(['voyage'])
param embeddingsProvider string = 'voyage'

@secure()
param anthropicApiKey string

@secure()
param voyageApiKey string

param gitHubClientId string

@secure()
param gitHubClientSecret string

@secure()
@description('The GitHub App private key in PEM format; line breaks may be written as \\n.')
param gitHubPrivateKey string

@secure()
param gitHubWebhookSecret string

var suffix = uniqueString(resourceGroup().id)
var registryName = toLower('${namePrefix}${suffix}')
var databaseName = 'trace'
var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: { name: 'Basic' }
  properties: {
    // Images are pulled with a managed identity; there is no registry password.
    adminUserEnabled: false
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-apps'
  location: location
}

resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${namePrefix}-pg-${suffix}'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '17'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
  }
}

// pgvector must be allow-listed before the application's migration can CREATE EXTENSION.
resource postgresExtensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: {
    value: 'VECTOR'
    source: 'user-override'
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// Lets other Azure services (the container apps) reach the server. Connections still need
// TLS and the password. Hardening step: put the server and apps in a virtual network instead.
resource postgresAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'allow-azure-services'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var connectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=${databaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require'

// Secrets live in the Container Apps secret store and reach the containers only as
// environment variables; none of them is baked into an image or written to source control.
var secrets = [
  { name: 'connection-string', value: connectionString }
  { name: 'anthropic-api-key', value: anthropicApiKey }
  { name: 'voyage-api-key', value: voyageApiKey }
  { name: 'github-client-secret', value: gitHubClientSecret }
  { name: 'github-private-key', value: gitHubPrivateKey }
  { name: 'github-webhook-secret', value: gitHubWebhookSecret }
]

// Shared by the API, the worker and the migration job.
var appEnvironment = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
  { name: 'ConnectionStrings__Trace', secretRef: 'connection-string' }
  { name: 'Chat__Provider', value: chatProvider }
  { name: 'Chat__ApiKey', secretRef: 'anthropic-api-key' }
  { name: 'Embeddings__Provider', value: embeddingsProvider }
  { name: 'Embeddings__ApiKey', secretRef: 'voyage-api-key' }
  { name: 'GitHub__ClientId', value: gitHubClientId }
  { name: 'GitHub__ClientSecret', secretRef: 'github-client-secret' }
  { name: 'GitHub__PrivateKey', secretRef: 'github-private-key' }
  { name: 'GitHub__WebhookSecret', secretRef: 'github-webhook-secret' }
]

var registryConfiguration = [
  {
    server: registry.properties.loginServer
    identity: identity.id
  }
]

var appIdentity = {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${identity.id}': {}
  }
}

resource migrateJob 'Microsoft.App/jobs@2024-03-01' = if (deployApps) {
  name: '${namePrefix}-migrate'
  location: location
  identity: appIdentity
  dependsOn: [registryPull, database, postgresExtensions, postgresAllowAzure]
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 1
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: registryConfiguration
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: '${registry.properties.loginServer}/trace-api:${imageTag}'
          args: ['--migrate']
          env: appEnvironment
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: '${namePrefix}-api'
  location: location
  identity: appIdentity
  dependsOn: [registryPull, database, postgresExtensions, postgresAllowAzure]
  properties: {
    environmentId: environment.id
    configuration: {
      // Reachable only from inside the environment: the web app is the single public entry.
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
      }
      registries: registryConfiguration
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${registry.properties.loginServer}/trace-api:${imageTag}'
          env: concat(appEnvironment, [
            // TLS ends at the public ingress; the API learns the real scheme and client from it.
            { name: 'Proxy__TrustForwardedHeaders', value: 'true' }
          ])
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 15
            }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 2 }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: '${namePrefix}-worker'
  location: location
  identity: appIdentity
  dependsOn: [registryPull, database, postgresExtensions, postgresAllowAzure]
  properties: {
    environmentId: environment.id
    configuration: {
      // No ingress: the worker only talks to the database, GitHub and the model providers.
      registries: registryConfiguration
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${registry.properties.loginServer}/trace-worker:${imageTag}'
          env: appEnvironment
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource web 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: '${namePrefix}-web'
  location: location
  identity: appIdentity
  dependsOn: [registryPull]
  properties: {
    environmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: registryConfiguration
    }
    template: {
      containers: [
        {
          name: 'web'
          image: '${registry.properties.loginServer}/trace-web:${imageTag}'
          env: [
            { name: 'API_UPSTREAM', value: 'http://${namePrefix}-api' }
          ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 2 }
    }
  }
}

output registryLoginServer string = registry.properties.loginServer
output registryName string = registry.name
output migrateJobName string = '${namePrefix}-migrate'
output webUrl string = deployApps ? (empty(publicUrl) ? 'https://${web!.properties.configuration.ingress.fqdn}' : publicUrl) : ''
