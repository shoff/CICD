# Writing a plugin

A plugin is a class library that references `Cicd.Plugins.Sdk`, ships a `plugin.json`, and contains exactly one
public class implementing `IPlugin`.

## 1. Project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
    <ProjectReference Include="path/to/Cicd.Plugins.Sdk.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

Inside this repository, put the project under `plugins/` and set `<PluginId>` in the csproj; `plugins/Directory.Build.props`
and `Directory.Build.targets` add the SDK reference and copy the output to `artifacts/plugins/<PluginId>/` after every build.

## 2. Manifest

```json
{
  "id": "my-runner",
  "name": "My Runner",
  "version": "1.0.0",
  "description": "Runs the thing.",
  "assembly": "My.Runner.dll",
  "sides": ["server", "agent"]
}
```

`sides` controls which processes load the plugin at all. Within the plugin, `IPluginRegistrar` further ignores
contributions that do not apply to the current side, so registering both a step type and a runner from one `Configure`
method is the normal pattern.

## 3. Entry point

```csharp
public sealed class MyPlugin : IPlugin
{
    public void Configure(IPluginRegistrar registrar) => registrar
        .AddStepType<MyStepType>()      // server: metadata + validation
        .AddRunner<MyRunner>();         // agent: execution
}
```

Anything else the plugin needs can be registered directly on `registrar.Services` (an `IServiceCollection`).
`registrar.Configuration` gives access to host configuration for tokens and URLs.

## 4. Step type and runner

```csharp
public sealed class MyStepType : IBuildStepType
{
    public string Id => "my-runner";
    public string DisplayName => "My Runner";
    public string Description => "Runs the thing.";
    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("target", "Target", Required: true),
        new("verbose", "Verbose", Kind: ParameterKind.Boolean, DefaultValue: "false"),
    ];
}

public sealed class MyRunner : IBuildRunner
{
    public string TypeId => "my-runner";

    public async Task<StepResult> RunAsync(BuildStepContext context, CancellationToken cancellationToken)
    {
        var target = context.Step.Parameters.Require("target");
        context.Log.Info($"Running {target}");
        var exitCode = await ProcessRunner.RunAsync("thing", [target], context.WorkingDirectory, context.Environment, context.Log, cancellationToken);
        return exitCode == 0 ? StepResult.Success() : StepResult.Failure($"thing exited with {exitCode}");
    }
}
```

Parameters arrive with `%references%` already resolved. `context.Environment` is the environment for child processes
(agent environment plus build parameters as `CICD_*` / `env.*`). Honor the cancellation token: it fires when a user
cancels the build.

## 5. Install

Copy the build output directory (assembly, `plugin.json`, private dependencies) to `<plugin root>/<id>/` on the server
and/or agents and restart. The plugin root is `Plugins:Directory`, or `plugins/` next to the executable, or the
repository's `artifacts/plugins/` in development. `GET /api/v1/plugins` lists what loaded and why anything failed.

## Other contribution types

- `IVcsProvider` + `IVcsCheckout`: see `plugins/Cicd.Plugins.Git`.
- `IPullRequestProvider` + `IWebhookHandler`: see `plugins/Cicd.Plugins.GitHub`.
- `IBuildTrigger`: see `VcsTrigger` in `src/Cicd.Core/Triggers`. Use `context.State` for persisted values.
- `INotifier`: receives a `BuildEvent` for every state change; return quickly or offload work.
- `IAgentCapabilityProvider`: return key/values that become agent capabilities (see `DotNetCapabilityProvider`).

## Isolation rules

Each plugin loads in its own `AssemblyLoadContext`. Assemblies the host already has (SDK, Contracts,
Microsoft.Extensions.*) are shared; everything else resolves from the plugin's directory first. Do not ship your own
copy of the SDK; do ship your third-party dependencies.
