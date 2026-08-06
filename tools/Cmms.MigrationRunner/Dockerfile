# Container image for the EF migration fan-out runner (lnicoara/cmms#33, per #37).
#
# It runs as a Container Apps Job INSIDE Azure, so it carries the user-assigned managed
# identity and sits inside the SQL firewall. A GitHub Actions runner has neither (wrong
# identity, blocked by the firewall), which is why the fan-out runs here rather than in CI.
# Build context is the repo root: the runner references src/Cmms.Infrastructure.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Directory.Build.props is copied to the same relative position so MSBuild applies it walking
# up from the project (TreatWarningsAsErrors), matching the repo build.
COPY Directory.Build.props ./
# The SDK pin governs the image build too, or the deploy image could silently compile under a different SDK policy. lnicoara/cmms#1437
COPY global.json ./
COPY src/ src/
COPY tools/ tools/
RUN dotnet restore tools/Cmms.MigrationRunner/Cmms.MigrationRunner.csproj
RUN dotnet publish tools/Cmms.MigrationRunner/Cmms.MigrationRunner.csproj -c Release -o /app --no-restore

# Console runtime (no ASP.NET). The built-in non-root 'app' user ships in the .NET 8 image.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Generic Host reads DOTNET_ENVIRONMENT; Production selects the Key Vault secret store.
ENV DOTNET_ENVIRONMENT=Production
USER app
ENTRYPOINT ["dotnet", "Cmms.MigrationRunner.dll"]
