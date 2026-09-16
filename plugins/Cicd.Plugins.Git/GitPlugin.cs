using Cicd.Contracts;
using Cicd.Plugins.Sdk;

namespace Cicd.Plugins.Git;

public sealed class GitPlugin : IPlugin
{
    public void Configure(IPluginRegistrar registrar) => registrar
        .AddVcsProvider<GitVcsProvider>()
        .AddVcsCheckout<GitCheckout>()
        .AddCapabilityProvider<GitCapabilityProvider>();
}

public static class GitProperties
{
    public const string ProviderId = "git";
    public const string Username = "username";
    public const string Password = "password";
    public const string Depth = "depth";
    public const string Submodules = "submodules";
}

internal static class GitCommand
{
    /// <summary>Environment that disables every interactive prompt and injects HTTPS credentials without touching disk.</summary>
    public static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        var environment = baseEnvironment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(baseEnvironment);
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "never";
        var username = properties.Get(GitProperties.Username);
        var password = properties.Get(GitProperties.Password);
        if (!string.IsNullOrEmpty(password))
        {
            environment["CICD_GIT_USERNAME"] = string.IsNullOrEmpty(username) ? "x-access-token" : username;
            environment["CICD_GIT_PASSWORD"] = password;
            environment["GIT_ASKPASS"] = AskPassScript.Ensure();
        }
        return environment;
    }
}

/// <summary>Tiny askpass helper so credentials never appear on the command line or in the remote URL.</summary>
internal static class AskPassScript
{
    private static readonly Lazy<string> Path = new(Create);

    public static string Ensure() => Path.Value;

    private static string Create()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cicd-git");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var cmd = System.IO.Path.Combine(directory, "askpass.cmd");
            File.WriteAllText(cmd, "@echo off\r\necho %1 | findstr /i username >nul && (echo %CICD_GIT_USERNAME%) || (echo %CICD_GIT_PASSWORD%)\r\n");
            return cmd;
        }
        var sh = System.IO.Path.Combine(directory, "askpass.sh");
        File.WriteAllText(sh, "#!/bin/sh\ncase \"$1\" in *sername*) echo \"$CICD_GIT_USERNAME\" ;; *) echo \"$CICD_GIT_PASSWORD\" ;; esac\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return sh;
    }
}

public sealed class GitVcsProvider : IVcsProvider
{
    public string Id => GitProperties.ProviderId;
    public string DisplayName => "Git";
    public IReadOnlyList<ParameterDefinition> Properties { get; } =
    [
        new(GitProperties.Username, "Username", "For HTTPS remotes. Use 'x-access-token' with a GitHub token."),
        new(GitProperties.Password, "Password / token", Kind: ParameterKind.Password),
        new(GitProperties.Depth, "Clone depth", "0 for a full clone.", DefaultValue: "50"),
        new(GitProperties.Submodules, "Checkout submodules", Kind: ParameterKind.Boolean, DefaultValue: "false"),
    ];

    public async Task<VcsRevision?> GetCurrentRevisionAsync(VcsRootInfo root, string branch, CancellationToken cancellationToken)
    {
        var refs = await LsRemoteAsync(root, cancellationToken);
        var candidates = new[] { branch, $"refs/heads/{branch}", $"refs/tags/{branch}", $"refs/pull/{branch}/head" };
        foreach (var candidate in candidates)
        {
            if (refs.TryGetValue(candidate, out var sha))
            {
                return new VcsRevision(branch, sha);
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(VcsRootInfo root, CancellationToken cancellationToken)
    {
        var refs = await LsRemoteAsync(root, cancellationToken);
        return refs.Keys.Where(r => r.StartsWith("refs/heads/")).Select(r => r["refs/heads/".Length..]).Order().ToList();
    }

    public async Task<string?> TestConnectionAsync(VcsRootInfo root, CancellationToken cancellationToken)
    {
        try
        {
            var refs = await LsRemoteAsync(root, cancellationToken);
            return refs.ContainsKey($"refs/heads/{root.DefaultBranch}") ? null : $"Connected, but branch '{root.DefaultBranch}' was not found.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static async Task<Dictionary<string, string>> LsRemoteAsync(VcsRootInfo root, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await ProcessRunner.CaptureAsync("git", ["ls-remote", root.Url], null, GitCommand.Environment(root.Properties), cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git ls-remote failed ({exitCode}): {stderr.Trim()}");
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2)
            {
                result[parts[1].Trim()] = parts[0].Trim();
            }
        }
        return result;
    }
}

public sealed class GitCheckout : IVcsCheckout
{
    public string ProviderId => GitProperties.ProviderId;

    public async Task<string> CheckoutAsync(VcsCheckoutSpec spec, string directory, IBuildLog log, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var environment = GitCommand.Environment(spec.Properties);
        var depth = spec.Properties.Get(GitProperties.Depth, "50");

        if (!Directory.Exists(Path.Combine(directory, ".git")))
        {
            await RunAsync(["init"], directory, environment, log, cancellationToken);
            await RunAsync(["remote", "add", "origin", spec.Url], directory, environment, log, cancellationToken);
        }
        else
        {
            await RunAsync(["remote", "set-url", "origin", spec.Url], directory, environment, log, cancellationToken);
        }

        var refToFetch = spec.Branch.StartsWith("refs/") ? spec.Branch : $"refs/heads/{spec.Branch}";
        var fetch = new List<string> { "fetch", "--force", "--prune" };
        if (depth != "0")
        {
            fetch.AddRange(["--depth", depth]);
        }
        fetch.AddRange(["origin", $"+{refToFetch}:refs/remotes/cicd/target"]);
        await RunAsync(fetch, directory, environment, log, cancellationToken);

        var target = spec.Revision ?? "refs/remotes/cicd/target";
        try
        {
            await RunAsync(["checkout", "--force", "--detach", target], directory, environment, log, cancellationToken);
        }
        catch (InvalidOperationException) when (spec.Revision is not null && depth != "0")
        {
            // Revision is older than the shallow window; deepen and retry once.
            log.Warning($"Revision {spec.Revision} not in shallow history, fetching full history");
            await RunAsync(["fetch", "--unshallow", "origin", $"+{refToFetch}:refs/remotes/cicd/target"], directory, environment, log, cancellationToken);
            await RunAsync(["checkout", "--force", "--detach", target], directory, environment, log, cancellationToken);
        }
        await RunAsync(["clean", "-fdx"], directory, environment, log, cancellationToken);
        if (spec.Properties.GetBool(GitProperties.Submodules))
        {
            await RunAsync(["submodule", "update", "--init", "--recursive"], directory, environment, log, cancellationToken);
        }

        var (_, sha, _) = await ProcessRunner.CaptureAsync("git", ["rev-parse", "HEAD"], directory, environment, cancellationToken);
        return sha.Trim();
    }

    private static async Task RunAsync(IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string> environment, IBuildLog log, CancellationToken cancellationToken)
    {
        log.Info($"$ git {string.Join(' ', arguments)}");
        var exitCode = await ProcessRunner.RunAsync("git", arguments, directory, environment, log, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments[0]} failed with exit code {exitCode}");
        }
    }
}

public sealed class GitCapabilityProvider : IAgentCapabilityProvider
{
    public async Task<IReadOnlyDictionary<string, string>> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, stdout, _) = await ProcessRunner.CaptureAsync("git", ["--version"], null, null, cancellationToken);
            if (exitCode == 0)
            {
                return new Dictionary<string, string> { ["git.version"] = stdout.Trim().Replace("git version ", "") };
            }
        }
        catch
        {
            // git missing: the agent will not advertise vcs.git and never receives git builds.
        }
        return new Dictionary<string, string>();
    }
}
