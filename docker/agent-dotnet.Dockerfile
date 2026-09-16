# syntax=docker/dockerfile:1
# Agent image with the .NET SDK for building .NET projects. Layers the published agent on the SDK image.
# docker build -f docker/agent-dotnet.Dockerfile -t cicd-agent-dotnet .
FROM cicd-agent:latest AS agent

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates curl unzip && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=agent /app ./
ENV Agent__WorkDirectory=/work \
    Plugins__Directory=/app/plugins \
    Agent__Capabilities__dotnet=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_PACKAGES=/work/.nuget
VOLUME ["/work"]
ENTRYPOINT ["dotnet", "Cicd.Agent.dll"]
