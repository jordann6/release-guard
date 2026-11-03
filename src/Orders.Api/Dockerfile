# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f src/Orders.Api/Dockerfile -t orders-api .
# Base images are pinned by multi-arch index digest (ADR-0007). Dependabot updates them.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:c89a6a61f494f20a5ae302990e814627a8d91b332fd4a8e7c1707c2d4da9555d
WORKDIR /app
COPY --from=build /app ./

# Numeric UID so Kubernetes runAsNonRoot can verify it without a passwd lookup.
USER 1654
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Orders.Api.dll"]
