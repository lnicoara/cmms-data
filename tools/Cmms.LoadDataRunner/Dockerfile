# Container image for the load-data runner (lnicoara/cmms#2917, per #2908).
#
# It runs as a Container Apps Job INSIDE Azure, so it carries the user-assigned managed identity and sits
# inside the VNet. That is the whole reason it exists: pre-prod SQL sets Deny Public Network Access, so a
# load cannot be driven from a laptop at all. Build context is the repo root: the runner references
# src/Cmms.Infrastructure and tools/Cmms.LoadDataGenerator.
#
# The image also carries azcopy, because the 3.7 GB artifact is staged from blob into the replica before
# the load rather than mounted. An SMB Azure Files mount would have suited the loader's one-chunk-at-a-time
# reads better, but Container Apps requires a storage ACCOUNT KEY to mount one, and every storage account
# in this repo sets allowSharedKeyAccess: false. lnicoara/cmms#2917 grill round 3.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Directory.Build.props is copied to the same relative position so MSBuild applies it walking up from the
# project (TreatWarningsAsErrors), matching the repo build.
COPY Directory.Build.props ./
# The SDK pin governs the image build too, or the deploy image could silently compile under a different SDK policy. lnicoara/cmms#1437
COPY global.json ./
COPY src/ src/
COPY tools/ tools/
RUN dotnet restore tools/Cmms.LoadDataRunner/Cmms.LoadDataRunner.csproj
RUN dotnet publish tools/Cmms.LoadDataRunner/Cmms.LoadDataRunner.csproj -c Release -o /app --no-restore

# Console runtime (no ASP.NET). The built-in non-root 'app' user ships in the .NET 8 image.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# azcopy stages the artifact by managed identity. Pinned rather than "latest" so an image rebuild cannot
# silently change how the artifact is fetched.
# From the GitHub release, NOT azcopyvnext.azureedge.net. That host stopped resolving when the Azure CDN
# behind it was retired, so the original URL would have failed every image build with a DNS error rather
# than anything that named the real cause. Pinned by tag: an image rebuild must not silently change how
# the artifact is fetched.
# Pinned by version AND by content. A tag pins availability; only the hash pins what actually ran, which
# is what a regulated build needs from an executable it pulls off the internet at image-build time. The
# download is verified BEFORE it is unpacked, so a substituted tarball fails the build rather than
# shipping.
ARG AZCOPY_VERSION=10.27.1
ARG AZCOPY_SHA256=b66b35c564b88c2e89f0c6b179ea52c42f1dd35924e2f0ebbbc5ff39c94f1d2c
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && curl -sSL -o /tmp/azcopy.tar.gz "https://github.com/Azure/azure-storage-azcopy/releases/download/v${AZCOPY_VERSION}/azcopy_linux_amd64_${AZCOPY_VERSION}.tar.gz" \
 && echo "${AZCOPY_SHA256}  /tmp/azcopy.tar.gz" | sha256sum -c - \
 && tar -xz --strip-components=1 -C /usr/local/bin --wildcards '*/azcopy' -f /tmp/azcopy.tar.gz \
 && rm /tmp/azcopy.tar.gz \
 && chmod +x /usr/local/bin/azcopy \
 && apt-get purge -y curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./
COPY tools/Cmms.LoadDataRunner/docker-entrypoint.sh /usr/local/bin/load-entrypoint
RUN chmod +x /usr/local/bin/load-entrypoint

# Generic Host reads DOTNET_ENVIRONMENT; Production selects the Key Vault secret store.
ENV DOTNET_ENVIRONMENT=Production
# Staging target. The replica's writable scratch space, not a mount.
ENV ARTIFACT_DIR=/artifact
RUN mkdir -p /artifact && chown app:app /artifact

USER app
ENTRYPOINT ["/usr/local/bin/load-entrypoint"]
