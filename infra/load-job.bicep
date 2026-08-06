// Container Apps Job: the load-data runner (lnicoara/cmms#2917, per #2908 and epic #2731).
//
// Bulk-loads a generated synthetic artifact into ONE provisioned tenant database. It runs INSIDE Azure so
// it carries the user-assigned managed identity (Key Vault, SQL, and the load-test blob account) and sits
// inside the VNet, which is the only way to reach a pre-prod SQL server that sets Deny Public Network
// Access. Measured, from a laptop:
//   mssql: login error: Connection was denied because Deny Public Network Access is set to Yes.
//
// It mirrors infra/seed-job.bicep and references the standing infrastructure by deterministic name, so it
// has no output plumbing. Two things differ from the seed job and both are deliberate:
//
//   replicaTimeout   The seed job allows 1800s. This load moves 42.7M rows into a General Purpose
//                    database whose log throughput caps around 4.5 MB/s, so it is measured in hours.
//   replicaRetryLimit The seed job allows 0 retries because a half-seed is awkward to reason about. A
//                    retried LOAD is safe by construction: every chunk is one transaction and preflight
//                    reconciles against the checkpoint set, so a retry resumes rather than duplicates.
//
// The job is PLAN-ONLY unless loadExecute is set true, matching the runner's own default.

@description('Container Apps region. Must match main.bicep computeLocation (where the environment lives).')
param computeLocation string = 'centralus'

@description('Deployment environment. Must match the standing infrastructure this job references. lnicoara/cmms#34, #509.')
@allowed([ 'dev', 'staging', 'preprod', 'prod' ])
param environment string = 'preprod'

@description('Data-tier region, used to recompute the SQL server name. Must match main.bicep.')
param location string = 'centralus'

@description('Runner image to run. The operator script passes the real acr.azurecr.io/cmms-load:<tag>.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart-jobs:latest'

@description('ACR name (defaults to the main.bicep-computed value).')
param acrName string = 'acrcmms${uniqueString(resourceGroup().id)}'

@description('SQL server name (defaults to the main.bicep-computed value).')
param sqlServerName string = 'sql-cmms-${environment}-${uniqueString(resourceGroup().id, location)}'

@description('Key Vault name (defaults to the main.bicep-computed value).')
param keyVaultName string = 'kv-cmms-${uniqueString(resourceGroup().id)}'

@description('Load-test storage account name (defaults to the load-test-store.bicep-computed value).')
param loadStorageName string = 'stloadtest${uniqueString(resourceGroup().id)}'

@description('Slug of the tenant to load, for example loadtest (its database is cmms-tenant-<slug>). This tenant must be UNSEEDED: the loader refuses a target whose tables already hold rows.')
param tenantSlug string

@description('FALSE plans and writes nothing. TRUE actually loads. Defaults to false so a mistaken deploy-and-start cannot move 42.7 million rows.')
param loadExecute bool = false

@description('Slug of the tenant whose write set may be emptied before loading. DESTRUCTIVE. Empty means never clear. It carries the SLUG rather than a boolean so the runner can refuse unless it matches tenantSlug: a deploy that changes the target but forgets this cannot empty the new tenant. Needs loadExecute too.')
param clearTargetSlug string = ''

@description('Hours the job may run before Container Apps kills the replica. The loader is resumable, so a timeout costs a restart rather than the load.')
@minValue(1)
@maxValue(24)
param timeoutHours int = 12

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

resource loadStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: loadStorageName
}

// Passwordless catalog connection (managed identity); the runner resolves the tenant's own connection from
// Key Vault. User Id MUST be the identity's client id.
var catalogConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=cmms-catalog;Authentication=Active Directory Managed Identity;User Id=${uami.properties.clientId};Encrypt=True;TrustServerCertificate=False'
// az.environment() is qualified because the `environment` parameter (lnicoara/cmms#34) shadows the
// unqualified built-in of the same name.
var keyVaultUri = 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/'
var loadBlobUrl = loadStorage.properties.primaryEndpoints.blob

resource loadJob 'Microsoft.App/jobs@2025-01-01' = {
  name: 'caj-cmms-load'
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
      replicaTimeout: timeoutHours * 3600
      // Safe to retry: chunks are atomic and preflight reconciles against the checkpoint set, so a retry
      // resumes where the last replica stopped instead of reloading or duplicating.
      replicaRetryLimit: 1
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
          name: 'load'
          image: containerImage
          // Ephemeral disk on Container Apps scales with the CPU and memory request, and the replica has
          // to hold the staged 3.7 GB artifact. 2 CPU / 4 GiB is sized for that headroom, not for the
          // loader's own appetite: it streams one chunk at a time and the plan path measured 30 MB.
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
          env: [
            { name: 'ConnectionStrings__Catalog', value: catalogConnectionString }
            { name: 'KeyVault__Uri', value: keyVaultUri }
            // Tells DefaultAzureCredential (and azcopy) which user-assigned identity to use.
            { name: 'AZURE_CLIENT_ID', value: uami.properties.clientId }
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            // Checkpoints go to the LOAD-TEST account under their own key, never to Storage__BlobUrl,
            // which is the work-order photo store holding real product data. lnicoara/cmms#2917 round 2.
            { name: 'Storage__LoadCheckpointBlobUrl', value: loadBlobUrl }
            { name: 'Storage__ManagedIdentityClientId', value: uami.properties.clientId }
            // Where the entrypoint stages the artifact from, and to.
            { name: 'ARTIFACT_BLOB_URL', value: '${loadBlobUrl}artifact' }
            { name: 'ARTIFACT_DIR', value: '/artifact' }
            { name: 'LOAD_TENANT_SLUG', value: tenantSlug }
            { name: 'LOAD_EXECUTE', value: string(loadExecute) }
            { name: 'LOAD_CLEAR_TARGET_SLUG', value: clearTargetSlug }
          ]
        }
      ]
    }
  }
}

output jobName string = loadJob.name
