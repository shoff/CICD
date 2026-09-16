# syntax=docker/dockerfile:1
# Build context: repository root.  docker build -f docker/server.Dockerfile -t cicd-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Cicd.slnx ./
COPY src/ src/
COPY plugins/ plugins/
RUN dotnet restore src/Cicd.Server/Cicd.Server.csproj \
 && for p in plugins/*/*.csproj; do dotnet restore "$p"; done
# Plugins first: their AfterBuild target mirrors output into /src/artifacts/plugins/<id>/
RUN for p in plugins/*/*.csproj; do dotnet build "$p" -c Release --no-restore; done
RUN dotnet publish src/Cicd.Server/Cicd.Server.csproj -c Release --no-restore -o /app/server

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# git is required for the git VCS provider's server-side operations (ls-remote).
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/server ./
COPY --from=build /src/artifacts/plugins ./plugins
ENV ASPNETCORE_URLS=http://+:8080 \
    Server__DataDirectory=/data \
    Plugins__Directory=/app/plugins
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Cicd.Server.dll"]
