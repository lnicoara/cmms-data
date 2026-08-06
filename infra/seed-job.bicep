// Container Apps Job: the tenant demo-seed runner (lnicoara/cmms#2442, per #2435).
//
// Seeds ONE already-provisioned tenant database with demo data. It runs INSIDE Azure so it has the
// user-assigned managed identity (to read Key Vault and authenticate to SQL) and sits inside the VNet,
// which is what lets it seed a pre-prod tenant whose SQL server has public network access disabled.
// Provision the tenant first (POST /api/admin/organizations), then trigger this:
//   az containerapp job start -n caj-cmms-seed -g rg-cmms-preprod
// The target tenant is the tenantSlug parameter, surfaced to the runner as Seed__TenantSlug.
//
// It references the standing infrastructure by deterministic name, like infra/migrate-job.bicep, so it has
// no output plumbing. The runner image is built and pushed separately (see infra/README.md).

@description('Container Apps region. Must match main.bicep computeLocation (where the environment lives).')
param computeLocation string = 'centralus'

@description('Deployment environment. Must match the standing infrastructure this job references. lnicoara/cmms#34, #509.')
@allowed([ 'dev', 'staging', 'preprod', 'prod' ])
param environment string = 'preprod'

@description('Data-tier region, used to recompute the SQL server name. Must match main.bicep.')
param location string = 'centralus'

@description('Runner image to run. CI/operator passes the real acr.azurecr.io/cmms-seed:<tag>.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart-jobs:latest'

@description('ACR name (defaults to the main.bicep-computed value).')
param acrName string = 'acrcmms${uniqueString(resourceGroup().id)}'

@description('SQL server name (defaults to the main.bicep-computed value).')
param sqlServerName string = 'sql-cmms-${environment}-${uniqueString(resourceGroup().id, location)}'

@description('Key Vault name (defaults to the main.bicep-computed value).')
param keyVaultName string = 'kv-cmms-${uniqueString(resourceGroup().id)}'

@description('Slug of the tenant to seed, for example demo-health (its database is cmms-tenant-<slug>).')
param tenantSlug string

@description('Restore access instead of seeding demo data: create a System Admin user, its group, and the MAIN service line, but ONLY in a tenant that has no users at all. Recovers an operator locked out after a clear.')
param ensureAdmin bool = false

@description('Password for the restored System Admin. Required when ensureAdmin is true. Secure so it is not echoed in deployment output.')
@secure()
param adminPassword string = ''

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

// Passwordless catalog connection (managed identity); the runner resolves the tenant's own connection from
// Key Vault. User Id MUST be the identity's client id.
var catalogConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=cmms-catalog;Authentication=Active Directory Managed Identity;User Id=${uami.properties.clientId};Encrypt=True;TrustServerCertificate=False'
// az.environment() is qualified because the `environment` parameter (lnicoara/cmms#34) shadows the
// unqualified built-in of the same name.
var keyVaultUri = 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/'

resource seedJob 'Microsoft.App/jobs@2025-01-01' = {
  name: 'caj-cmms-seed'
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
          name: 'seed'
          image: containerImage
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
            // The tenant this job seeds.
            { name: 'Seed__TenantSlug', value: tenantSlug }
            // Lower-cased because Bicep's string(true) emits 'True', and the runner compares
            // case-insensitively, but every other consumer of these flags has been bitten by the capital T.
            { name: 'Seed__EnsureAdmin', value: toLower(string(ensureAdmin)) }
            { name: 'Seed__AdminPassword', value: adminPassword }
          ]
        }
      ]
    }
  }
}

output jobName string = seedJob.name
