using Cicd.Plugins.Sdk;

namespace Cicd.Plugins.CommandLine;

public sealed class CommandLinePlugin : IPlugin
{
    public void Configure(IPluginRegistrar registrar) => registrar
        .AddStepType<CommandLineStepType>()
        .AddRunner<CommandLineRunner>();
}

public sealed class CommandLineStepType : IBuildStepType
{
    public const string TypeId = "command-line";

    public string Id => TypeId;
    public string DisplayName => "Command Line";
    public string Description => "Run a script with /bin/sh (or cmd.exe on Windows), or an executable with arguments.";
    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("script", "Script", "Script body. Mutually exclusive with 'command'.", Kind: ParameterKind.MultilineText),
        new("command", "Executable", "Executable to run instead of a script."),
        new("arguments", "Arguments", "Whitespace separated arguments for 'command'. Quote with double quotes."),
        new("workingDirectory", "Working directory", "Relative to the checkout directory.", Kind: ParameterKind.Path),
        new("failOnStderr", "Fail if anything is written to stderr", Kind: ParameterKind.Boolean, DefaultValue: "false"),
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> parameters)
    {
        var hasScript = !string.IsNullOrWhiteSpace(parameters.Get("script"));
        var hasCommand = !string.IsNullOrWhiteSpace(parameters.Get("command"));
        if (hasScript == hasCommand)
        {
            return ["Specify exactly one of 'script' or 'command'."];
        }
        return [];
    }
}

public sealed class CommandLineRunner : IBuildRunner
{
    public string TypeId => CommandLineStepType.TypeId;

    public async Task<StepResult> RunAsync(BuildStepContext context, CancellationToken cancellationToken)
    {
        var parameters = context.Step.Parameters;
        var workingDirectory = Path.GetFullPath(Path.Combine(context.WorkingDirectory, parameters.Get("workingDirectory", ".")));
        Directory.CreateDirectory(workingDirectory);

        string fileName;
        IReadOnlyList<string> arguments;
        var script = parameters.Get("script");
        if (!string.IsNullOrWhiteSpace(script))
        {
            var scriptPath = Path.Combine(context.TempDirectory, $"step-{context.Step.Index}{(OperatingSystem.IsWindows() ? ".cmd" : ".sh")}");
            await File.WriteAllTextAsync(scriptPath, script.Replace("\r\n", "\n"), cancellationToken);
            if (OperatingSystem.IsWindows())
            {
                fileName = "cmd.exe";
                arguments = ["/c", scriptPath];
            }
            else
            {
                fileName = "/bin/sh";
                arguments = ["-e", scriptPath];
            }
        }
        else
        {
            fileName = parameters.Require("command");
            arguments = ArgumentSplitter.Split(parameters.Get("arguments"));
        }

        context.Log.Info($"$ {fileName} {string.Join(' ', arguments)}");
        var stderrSeen = false;
        var log = parameters.GetBool("failOnStderr") ? new StderrTrackingLog(context.Log, () => stderrSeen = true) : context.Log;
        var exitCode = await ProcessRunner.RunAsync(fileName, arguments, workingDirectory, context.Environment, log, cancellationToken);
        if (exitCode != 0)
        {
            return StepResult.Failure($"Process exited with code {exitCode}");
        }
        if (stderrSeen)
        {
            return StepResult.Failure("Process wrote to stderr and 'failOnStderr' is set");
        }
        return StepResult.Success();
    }

    private sealed class StderrTrackingLog(IBuildLog inner, Action onStderr) : IBuildLog
    {
        public void Write(Cicd.Contracts.LogLevel level, string text)
        {
            if (level == Cicd.Contracts.LogLevel.Warning)
            {
                onStderr();
            }
            inner.Write(level, text);
        }
    }
}

public static class ArgumentSplitter
{
    /// <summary>Splits on whitespace, honoring double quotes.</summary>
    public static IReadOnlyList<string> Split(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }
}
