# syntax=docker/dockerfile:1
# Base build agent: .NET runtime + git + the in-tree agent-side plugins (command-line, dotnet runner, git checkout).
# Build context: repository root.  docker build -f docker/agent.Dockerfile -t cicd-agent .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Cicd.slnx ./
COPY src/ src/
COPY plugins/ plugins/
RUN dotnet restore src/Cicd.Agent/Cicd.Agent.csproj \
 && for p in plugins/*/*.csproj; do dotnet restore "$p"; done
RUN for p in plugins/*/*.csproj; do dotnet build "$p" -c Release --no-restore; done
RUN dotnet publish src/Cicd.Agent/Cicd.Agent.csproj -c Release --no-restore -o /app/agent

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates curl unzip && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/agent ./
COPY --from=build /src/artifacts/plugins ./plugins
ENV Agent__WorkDirectory=/work \
    Plugins__Directory=/app/plugins \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME ["/work"]
ENTRYPOINT ["dotnet", "Cicd.Agent.dll"]
