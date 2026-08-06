// Container Apps Job: the EF migration fan-out runner (lnicoara/cmms#33, per #37).
//
// Applies pending migrations to the catalog and every active tenant database. It runs INSIDE
// Azure so it has the user-assigned managed identity (to read Key Vault and authenticate to
// SQL) and sits inside the SQL firewall, which a GitHub Actions runner does not. Trigger it on
// demand:  az containerapp job start -n caj-cmms-migrate -g rg-cmms-dev
// Dry run (pre-flight only, writes nothing): deploy with jobArgs=['--dry-run'] and start the job. The
// old comment said to set args "on the start command", which az containerapp job start does not support,
// so the documented dry run was unreachable. It is a template parameter now. lnicoara/cmms#2917
//
// It references the standing infrastructure by deterministic name, like infra/app.bicep, so it
// has no output plumbing. The runner image is built and pushed separately (see infra/README.md).

@description('Container Apps region. Must match main.bicep computeLocation (where the environment lives).')
param computeLocation string = 'eastus2'

@description('Deployment environment. Must match the standing infrastructure this job references. Defaults to dev. lnicoara/cmms#34.')
// preprod was missing, so this job could not be deployed there at ALL and pre-prod tenant databases had no migration path. demo-health drifted 2 migrations behind main and the load runner refused it. lnicoara/cmms#2917
@allowed([ 'dev', 'staging', 'preprod', 'prod' ])
param environment string = 'dev'

@description('Data-tier region, used to recompute the SQL server name. Must match main.bicep.')
param location string = 'westus3'

@description('Runner image to run. CI/operator passes the real acr.azurecr.io/cmms-migrate:<tag>.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart-jobs:latest'

@description('ACR name (defaults to the main.bicep-computed value).')
param acrName string = 'acrcmms${uniqueString(resourceGroup().id)}'

@description('SQL server name (defaults to the main.bicep-computed value).')
param sqlServerName string = 'sql-cmms-${environment}-${uniqueString(resourceGroup().id, location)}'

@description('Arguments appended to the runner. Pass a --dry-run element to preflight every database and write nothing, which is the safe way to see what is pending before applying it.')
param jobArgs array = []

@description('Key Vault name (defaults to the main.bicep-computed value).')
param keyVaultName string = 'kv-cmms-${uniqueString(resourceGroup().id)}'

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'id-cmms-api'
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource containerEnv 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: 'cae-cmms-${environment}'
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: sqlServerName
}

// Passwordless catalog connection (managed identity); the runner resolves each tenant's own
// connection from Key Vault. User Id MUST be the identity's client id.
var catalogConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=cmms-catalog;Authentication=Active Directory Managed Identity;User Id=${uami.properties.clientId};Encrypt=True;TrustServerCertificate=False'
// az.environment() is qualified because the `environment` parameter (lnicoara/cmms#34) shadows the
// unqualified built-in of the same name.
var keyVaultUri = 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/'

resource migrateJob 'Microsoft.App/jobs@2025-01-01' = {
  name: 'caj-cmms-migrate'
  location: computeLocation
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uami.id}': {}
    }
  }
  properties: {
    environmentId: containerEnv.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: acr.properties.loginServer
          identity: uami.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: containerImage
          args: jobArgs
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ConnectionStrings__Catalog', value: catalogConnectionString }
            { name: 'KeyVault__Uri', value: keyVaultUri }
            // Tells DefaultAzureCredential which user-assigned identity to use.
            { name: 'AZURE_CLIENT_ID', value: uami.properties.clientId }
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
          ]
        }
      ]
    }
  }
}

output jobName string = migrateJob.name
