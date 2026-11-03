# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f src/Release.Verifier/Dockerfile -t release-verifier .

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Release.Core/Release.Core.csproj src/Release.Core/packages.lock.json src/Release.Core/
COPY src/Release.Verifier/Release.Verifier.csproj src/Release.Verifier/packages.lock.json src/Release.Verifier/
RUN dotnet restore src/Release.Verifier/Release.Verifier.csproj --locked-mode

COPY src/Release.Core/ src/Release.Core/
COPY src/Release.Verifier/ src/Release.Verifier/
RUN dotnet publish src/Release.Verifier/Release.Verifier.csproj \
      --configuration Release --no-restore --output /app \
      -p:UseAppHost=false -p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:c89a6a61f494f20a5ae302990e814627a8d91b332fd4a8e7c1707c2d4da9555d
WORKDIR /app
COPY --from=build /app ./
USER 1654
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Release.Verifier.dll"]
