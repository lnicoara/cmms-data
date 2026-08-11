// Container Apps Job: the table census.
//
// Reports how many rows every table in ONE tenant database holds, and nothing else. It exists because
// pre-prod SQL sets Deny Public Network Access, so the obvious way to answer that question is refused at
// login from anywhere outside the VNet:
//   mssql: login error: Connection was denied because Deny Public Network Access is set to Yes.
// A count therefore has to run in a container that is already inside, which is the same reason the load
// job exists and the reason this is IaC rather than a SELECT in a shell script.
//
// A SEPARATE JOB FROM caj-cmms-load, and that is the whole design decision here.
//
// Reusing the load job would have been less Bicep. It also would have made every count a second concurrent
// EXECUTION of the job that is running the load, and two executions against one tenant is not hypothetical:
// on 2026-08-10 caj-cmms-load-w13fd38 and caj-cmms-load-ylrqm1s overlapped for 56 minutes and spent it
// blocking each other into 600-second command timeouts and duplicate-key skips on AuditEvents. A load
// measured in DAYS (replicaTimeout is up to 96 hours) means the whole point of a count is to run WHILE one
// is in flight, so a design that cannot do that answers the question only when nobody needs it.
//
// Same image as the load job, deliberately. The runner already authenticates to Key Vault and SQL with the
// managed identity, resolves the tenant through the catalog, and refuses a secret that names another
// database or another environment's server. A second image with a SQL client in it would be a second
// implementation of all of that, and the failure it would eventually have is connecting to the wrong
// database while reporting a number that looks fine.
//
// It writes nothing. There is no --execute, no clear slug, no artifact.

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

@description('Slug of the tenant to count, for example demo-health (its database is cmms-tenant-<slug>).')
param tenantSlug string

// Seconds. The census is a single query over sys.partitions, which the engine answers from metadata in
// constant time however large the tables are, so this bounds a connection and one round trip rather than
// any scan. It is NOT sized for COUNT(*): counting 34 million AuditEvents rows for real would need minutes
// and would be the wrong thing to do to a database that has a load running against it.
@description('SQL command timeout, in seconds, for the census query.')
@minValue(30)
@maxValue(600)
param countTimeoutSeconds int = 120

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

resource countJob 'Microsoft.App/jobs@2025-01-01' = {
  name: 'caj-cmms-count'
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
      // Five minutes, against the load job's hours. A census that has not answered in five minutes is not
      // a slow census, it is a connection or permission problem, and waiting longer only delays the
      // sentence that says so.
      replicaTimeout: 300
      // No retry. A read that failed produces no partial state to reconcile, so the honest response is to
      // report the failure and let the operator run it again having read it, rather than to hide the first
      // error behind a second attempt.
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
          name: 'count'
          image: containerImage
          // The smallest profile Container Apps offers. The load job asks for 2 CPU / 4 GiB because its
          // ephemeral disk has to hold a staged 3.8 GB artifact; this one stages nothing and holds one
          // result set of 158 rows.
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ConnectionStrings__Catalog', value: catalogConnectionString }
            { name: 'KeyVault__Uri', value: keyVaultUri }
            { name: 'AZURE_CLIENT_ID', value: uami.properties.clientId }
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            { name: 'LOAD_TENANT_SLUG', value: tenantSlug }
            // The entrypoint branches on this BEFORE it requires an artifact URL, so a count job needs no
            // ARTIFACT_BLOB_URL and stages no blob.
            { name: 'LOAD_COUNT_ONLY', value: 'true' }
            // Binds onto LoaderOptions.TimeoutSeconds through config.GetSection("Load").
            { name: 'Load__TimeoutSeconds', value: string(countTimeoutSeconds) }
            // NOT SET, and their absence is the safety property rather than an omission: LOAD_EXECUTE and
            // LOAD_CLEAR_TARGET_SLUG are what let the runner write or delete. The count path returns
            // before either is read, and leaving them off this job means a portal edit to one variable
            // cannot turn the census into a load.
          ]
        }
      ]
    }
  }
}

output jobName string = countJob.name
