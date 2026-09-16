using Cicd.Plugins.Sdk;

namespace Cicd.Core.Triggers;

/// <summary>Built-in equivalent of TeamCity's VCS Trigger: queue a build when the watched branch moves.</summary>
public sealed class VcsTrigger : IBuildTrigger
{
    public const string Type = "vcs";
    private const string LastRevisionKey = "lastRevision";

    public string TypeId => Type;
    public string DisplayName => "VCS Trigger";
    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("branch", "Branch", "Branch to watch. Defaults to the VCS root's default branch."),
        new("buildOnFirstPoll", "Build on first poll", "Queue a build the first time the trigger sees the branch.", Kind: ParameterKind.Boolean, DefaultValue: "false"),
    ];

    public async Task<IReadOnlyList<TriggerRequest>> EvaluateAsync(TriggerContext context, CancellationToken cancellationToken)
    {
        var root = context.Configuration.VcsRoot;
        if (root is null || context.VcsProvider is null)
        {
            return [];
        }
        var branch = context.Trigger.Parameters.Get("branch", root.DefaultBranch);
        var current = await context.VcsProvider.GetCurrentRevisionAsync(root, branch, cancellationToken);
        if (current is null)
        {
            return [];
        }

        var previous = await context.State.GetAsync(LastRevisionKey, cancellationToken);
        if (previous == current.Revision)
        {
            return [];
        }
        await context.State.SetAsync(LastRevisionKey, current.Revision, cancellationToken);

        if (previous is null && !context.Trigger.Parameters.GetBool("buildOnFirstPoll"))
        {
            return [];
        }
        return [new TriggerRequest(branch, current.Revision, $"vcs: {branch}@{Shorten(current.Revision)}")];
    }

    private static string Shorten(string revision) => revision.Length > 10 ? revision[..10] : revision;
}
