// Load-test staging store (lnicoara/cmms#2917, per epic #2731).
//
// Holds everything the pre-prod load test needs and nothing else: the generated artifact to read
// (#2876) and the loader's per-chunk checkpoints to write (#2908). Two containers on ONE account
// because they share a lifetime: the checkpoint set is keyed to an artifact's content hash, they are
// meaningless apart, and deleting this account is the entire cleanup when the load test is over.
//
// Deliberately NOT the work-order photo account. That one holds real uploaded product data, and a bulk
// load job running with a managed identity has no business holding write access to it. The loader
// reaches this account through its own Storage:LoadCheckpointBlobUrl key rather than by overriding
// Storage:BlobUrl, following the Storage:ComplianceBlobUrl precedent (#2208).
//
// WHY BLOB AND NOT AN AZURE FILES SHARE. The first design mounted the artifact as a read-only SMB
// share, which fits the loader's one-chunk-at-a-time streaming perfectly. Mounting SMB into a Container
// Apps environment REQUIRES a storage account key, and every other storage account in this repo sets
// allowSharedKeyAccess: false so there is no long-lived key to leak. Rather than make this the first
// shared-key exception in a product carrying a Part 11 posture, the artifact is downloaded by managed
// identity before the load. A downloader is bounded, owned, testable work; a documented security
// exception is permanent and gets copied by whoever reads it next. lnicoara/cmms#2917 grill round 3.

@description('Region for the storage account itself. The resource group\'s region is fine; the account does not have to sit near the VNet.')
param location string = resourceGroup().location

@description('Region for the blob private endpoint, which MUST be the standing VNet\'s, not the resource group\'s. In pre-prod those differ (resource group westus3, VNet centralus), and a private endpoint has to sit in the same region as the subnet it joins. Deploying it at resourceGroup().location produced InvalidResourceReference: a westus3 endpoint cannot join a centralus subnet. Private Link is happy for the endpoint and the account it targets to be in different regions, so only THIS has to follow the VNet.')
param privateEndpointLocation string = location

@description('Globally-unique storage account name (3 to 24 lowercase alphanumerics). load-job.bicep recomputes the same name to derive the endpoints.')
param storageAccountName string = 'stloadtest${uniqueString(resourceGroup().id)}'

@description('Principal id of the managed identity the load job runs as (id-cmms-api), granted Storage Blob Data Contributor.')
param jobPrincipalId string

@description('Environment name, used to reach the standing VNet vnet-cmms-<environment> when privateLink is set.')
param environment string

@description('Object id of the human operator who uploads the artifact. The account is RBAC-only, so without this grant there is no way to write to it. Empty skips the grant.')
param operatorPrincipalId string = ''

@description('Reach the account over a blob private endpoint in snet-pe (the HIPAA environments), and deny every public address that is not explicitly allowed. Requires vnet-cmms-<environment> to already exist.')
param privateLink bool = false

@description('The one public IP allowed to reach the account, which is the operator machine that uploads the artifact. Empty denies all public access, which is the right posture once the artifact is staged.')
param operatorIpAddress string = ''

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // RBAC only, so there is no long-lived account key to leak. Uniform with every other account here,
    // and the reason the Azure Files mount was rejected (see the header).
    allowSharedKeyAccess: false
    // NOT 'Disabled', and the difference from the photo store is deliberate rather than an oversight.
    // That account is only ever written from INSIDE the VNet by the API, so it can refuse the public
    // internet outright. This one has to be WRITTEN from an operator laptop (3.7 GB of artifact) and READ
    // from inside the VNet, and only the second half survives 'Disabled'. Copying the photo store's
    // posture made the upload fail the same way sqlcmd fails against pre-prod SQL.
    //
    // Deny-by-default with one allowed address is tighter than a bare 'Enabled'. IP rules are IGNORED
    // when publicNetworkAccess is 'Disabled', so an exception is only expressible this way. The private
    // endpoint bypasses networkAcls entirely, so the job is unaffected by any of it.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Deny'
      // 'None', not 'AzureServices'. Nothing in this chain reaches the account through the trusted-service
      // path: the operator uploads over the allowed IP, and the job reads through the private endpoint,
      // which bypasses networkAcls on its own regardless of this setting. Leaving the bypass at
      // 'AzureServices' would have made the posture quietly broader than "one operator IP plus the VNet".
      bypass: 'None'
      ipRules: empty(operatorIpAddress) ? [] : [
        { value: operatorIpAddress, action: 'Allow' }
      ]
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// The generated artifact: manifest.json plus <table>/<table>-NNNNN.jsonl.gz. Written once by the
// operator from a laptop, read by the job. The job never writes here.
resource artifactContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'artifact'
  properties: {
    publicAccess: 'None'
  }
}

// One small blob per loaded chunk, keyed by artifact content hash and target database. This is the
// loader's resume record, so losing it costs a re-probe rather than a reload.
resource checkpointContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  // MUST match LoaderOptions.CheckpointContainer. They disagreed once (checkpoints vs load-checkpoints)
  // and the job would have written its resume record into a container nobody provisioned.
  name: 'load-checkpoints'
  properties: {
    publicAccess: 'None'
  }
}

// Storage Blob Data Contributor at ACCOUNT scope: the job reads the artifact container and writes the
// checkpoint container, and BlobCheckpointStore creates the checkpoint container's blobs on demand.
// Role id ba92f5b4-2d11-453d-a403-e96b0029c9fe.
var blobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
resource jobGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, jobPrincipalId, blobDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: blobDataContributorRoleId
    principalId: jobPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The operator uploads the 3.7 GB artifact from a laptop. allowSharedKeyAccess is false, so there is no
// account key to hand azcopy and this grant is the only way in. Scoped to the same account, granted at
// deploy time so the script never has to tell a human to go and add a role assignment by hand.
resource operatorGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorPrincipalId)) {
  name: guid(storage.id, operatorPrincipalId, blobDataContributorRoleId, 'operator')
  scope: storage
  properties: {
    roleDefinitionId: blobDataContributorRoleId
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}

// Locked environments reach the account only over the VNet, mirroring the photo store and the SQL and
// Key Vault private endpoints in main.bicep.
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' existing = if (privateLink) {
  name: 'vnet-cmms-${environment}'
}

resource blobDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' existing = if (privateLink) {
  name: 'privatelink.blob.${az.environment().suffixes.storage}'
}

resource blobPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (privateLink) {
  name: 'pe-load-test-${environment}'
  location: privateEndpointLocation
  properties: {
    subnet: { id: '${vnet.id}/subnets/snet-pe' }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storage.id
          groupIds: [ 'blob' ]
        }
      }
    ]
  }
}

// The zone and its VNet link already exist from the photo store, so this only adds the A record for
// this account rather than re-creating shared DNS.
resource blobPeDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (privateLink) {
  parent: blobPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      { name: 'blob', properties: { privateDnsZoneId: blobDnsZone.id } }
    ]
  }
}

@description('Base blob endpoint. load-job.bicep derives the same value from the account name for Storage:LoadCheckpointBlobUrl.')
output blobUrl string = storage.properties.primaryEndpoints.blob

@description('Account name, so the operator script can target azcopy at the artifact container.')
output accountName string = storage.name
