# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f src/Orders.Api/Dockerfile -t orders-api .
# Base images are pinned by multi-arch index digest (ADR-0007). Dependabot updates them.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src

# Restore first so the dependency layer is cached independently of source changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Orders.Api/Orders.Api.csproj src/Orders.Api/packages.lock.json src/Orders.Api/
RUN dotnet restore src/Orders.Api/Orders.Api.csproj --locked-mode

COPY src/Orders.Api/ src/Orders.Api/
RUN dotnet publish src/Orders.Api/Orders.Api.csproj \
      --configuration Release --no-restore --output /app \
      -p:UseAppHost=false -p:DebugType=none

# Chiseled runtime: no shell, no package manager, non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.0-noble-chiseled@sha256:5730fa91fab5ec91f69661b86897249ad6a01c8da6da557695e9da4c6bc83621
WORKDIR /app
COPY --from=build /app ./

# Numeric UID so Kubernetes runAsNonRoot can verify it without a passwd lookup.
USER 1654
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Orders.Api.dll"]
