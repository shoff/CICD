using Cicd.Plugins.Sdk;

namespace Cicd.Plugins.DotNet;

public sealed class DotNetPlugin : IPlugin
{
    public void Configure(IPluginRegistrar registrar) => registrar
        .AddStepType<DotNetStepType>()
        .AddRunner<DotNetRunner>()
        .AddCapabilityProvider<DotNetCapabilityProvider>();
}

public sealed class DotNetStepType : IBuildStepType
{
    public const string TypeId = "dotnet";
    public static readonly string[] Commands = ["restore", "build", "test", "publish", "pack", "run", "clean", "custom"];

    public string Id => TypeId;
    public string DisplayName => ".NET CLI";
    public string Description => "Run a dotnet CLI command against a project or solution.";
    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("command", "Command", Required: true, DefaultValue: "build", Kind: ParameterKind.Select, Options: Commands),
        new("projects", "Projects", "Project or solution paths, whitespace separated. Empty runs in the working directory."),
        new("configuration", "Configuration", DefaultValue: "Release"),
        new("arguments", "Additional arguments"),
        new("workingDirectory", "Working directory", Kind: ParameterKind.Path),
        new("customCommand", "Custom command", "Used when command is 'custom', e.g. 'tool run something'."),
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> parameters)
    {
        var command = parameters.Get("command");
        if (!Commands.Contains(command))
        {
            return [$"Unknown dotnet command '{command}'. Expected one of: {string.Join(", ", Commands)}."];
        }
        if (command == "custom" && string.IsNullOrWhiteSpace(parameters.Get("customCommand")))
        {
            return ["'customCommand' is required when command is 'custom'."];
        }
        return [];
    }
}

public sealed class DotNetRunner : IBuildRunner
{
    public string TypeId => DotNetStepType.TypeId;

    public async Task<StepResult> RunAsync(BuildStepContext context, CancellationToken cancellationToken)
    {
        var parameters = context.Step.Parameters;
        var workingDirectory = Path.GetFullPath(Path.Combine(context.WorkingDirectory, parameters.Get("workingDirectory", ".")));
        var command = parameters.Get("command", "build");
        var projects = parameters.Get("projects").Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extra = parameters.Get("arguments").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var configuration = parameters.Get("configuration");

        var environment = new Dictionary<string, string>(context.Environment)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };

        var targets = projects.Length == 0 ? [""] : projects;
        foreach (var target in targets)
        {
            var arguments = new List<string>();
            if (command == "custom")
            {
                arguments.AddRange(parameters.Require("customCommand").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            else
            {
                arguments.Add(command);
            }
            if (!string.IsNullOrEmpty(target))
            {
                arguments.Add(target);
            }
            if (!string.IsNullOrEmpty(configuration) && command is "build" or "test" or "publish" or "pack" or "run")
            {
                arguments.AddRange(["--configuration", configuration]);
            }
            arguments.AddRange(extra);

            context.Log.Info($"$ dotnet {string.Join(' ', arguments)}");
            var exitCode = await ProcessRunner.RunAsync("dotnet", arguments, workingDirectory, environment, context.Log, cancellationToken);
            if (exitCode != 0)
            {
                return StepResult.Failure($"dotnet {command} exited with code {exitCode}");
            }
        }
        return StepResult.Success();
    }
}

public sealed class DotNetCapabilityProvider : IAgentCapabilityProvider
{
    public async Task<IReadOnlyDictionary<string, string>> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, stdout, _) = await ProcessRunner.CaptureAsync("dotnet", ["--version"], null, null, cancellationToken);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var version = stdout.Trim();
                return new Dictionary<string, string>
                {
                    ["dotnet.sdk"] = version,
                    ["dotnet.sdk.major"] = version.Split('.')[0],
                };
            }
        }
        catch
        {
            // dotnet is not on PATH; the agent simply does not advertise it.
        }
        return new Dictionary<string, string>();
    }
}
