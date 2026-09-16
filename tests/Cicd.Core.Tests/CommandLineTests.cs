using Cicd.Contracts;
using Cicd.Plugins.CommandLine;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cicd.Core.Tests;

public class CommandLineTests
{
    [Fact]
    public void Argument_splitter_honors_quotes()
    {
        var args = ArgumentSplitter.Split("""build "my project.csproj" -c Release""");
        Assert.Equal(["build", "my project.csproj", "-c", "Release"], args);
    }

    [Fact]
    public void Step_type_requires_exactly_one_of_script_or_command()
    {
        var type = new CommandLineStepType();
        Assert.NotEmpty(type.Validate(new Dictionary<string, string>()));
        Assert.NotEmpty(type.Validate(new Dictionary<string, string> { ["script"] = "echo hi", ["command"] = "echo" }));
        Assert.Empty(type.Validate(new Dictionary<string, string> { ["script"] = "echo hi" }));
    }

    [Fact]
    public async Task Runner_executes_script_and_streams_output()
    {
        if (OperatingSystem.IsWindows()) return;
        var log = new CollectingLog();
        var temp = Directory.CreateTempSubdirectory("cicd-test-");
        try
        {
            var context = new BuildStepContext
            {
                Job = new BuildJob { BuildId = Guid.NewGuid(), BuildConfigurationId = Guid.NewGuid(), BuildConfigurationName = "c", ProjectName = "p", BuildNumber = "1", Steps = [], Parameters = new Dictionary<string, string>() },
                Step = new BuildStepDefinition { Index = 0, Name = "s", TypeId = "command-line", Parameters = new Dictionary<string, string> { ["script"] = "echo hello $GREETING\nexit 3" } },
                WorkingDirectory = temp.FullName,
                TempDirectory = temp.FullName,
                Log = log,
                Environment = new Dictionary<string, string> { ["GREETING"] = "world", ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "" },
                Logger = NullLogger.Instance,
            };
            var result = await new CommandLineRunner().RunAsync(context, CancellationToken.None);
            Assert.Equal(StepStatus.Failure, result.Status);
            Assert.Contains("exit", result.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(log.Lines, l => l.Text == "hello world");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private sealed class CollectingLog : IBuildLog
    {
        public List<(Cicd.Contracts.LogLevel Level, string Text)> Lines { get; } = [];
        public void Write(Cicd.Contracts.LogLevel level, string text) => Lines.Add((level, text));
    }
}
