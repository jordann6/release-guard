# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f src/Release.Verifier/Dockerfile -t release-verifier .

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:84def277556c2843a44fdeefbd3145d0a2e32d8f736d0c1a7b0a96b2880b0187
WORKDIR /app
COPY --from=build /app ./
USER 1654
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Release.Verifier.dll"]
