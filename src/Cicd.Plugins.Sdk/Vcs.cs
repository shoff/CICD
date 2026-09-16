using Cicd.Contracts;

namespace Cicd.Plugins.Sdk;

public sealed record VcsRootInfo(
    Guid Id,
    string ProviderId,
    string Url,
    string DefaultBranch,
    IReadOnlyDictionary<string, string> Properties);

public sealed record VcsRevision(string Branch, string Revision, string? Author = null, string? Message = null, DateTimeOffset? Timestamp = null);

/// <summary>
/// Server-side VCS integration: answer questions about a repository without checking it out.
/// </summary>
public interface IVcsProvider
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<ParameterDefinition> Properties { get; }

    Task<VcsRevision?> GetCurrentRevisionAsync(VcsRootInfo root, string branch, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListBranchesAsync(VcsRootInfo root, CancellationToken cancellationToken);
    Task<string?> TestConnectionAsync(VcsRootInfo root, CancellationToken cancellationToken);
}

/// <summary>Agent-side VCS integration: materialize a revision on disk.</summary>
public interface IVcsCheckout
{
    string ProviderId { get; }

    /// <summary>Checkout <paramref name="spec"/> into <paramref name="directory"/>. Returns the revision actually checked out.</summary>
    Task<string> CheckoutAsync(VcsCheckoutSpec spec, string directory, IBuildLog log, CancellationToken cancellationToken);
}

public sealed record PullRequestInfo(
    long Number,
    string Title,
    string SourceBranch,
    string TargetBranch,
    string HeadSha,
    string Author,
    string State,
    string? Url,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The ref an agent should fetch to build this pull request. GitHub: refs/pull/N/head.</summary>
    public string? CheckoutRef { get; init; }
}

public sealed record CommitStatusReport(string Sha, BuildStatus Status, string Context, string Description, string? TargetUrl);

/// <summary>
/// Pull request discovery and status reporting for a hosting service. Equivalent to TeamCity's
/// "Pull Requests" and "Commit Status Publisher" build features.
/// </summary>
public interface IPullRequestProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>True if this provider can service the given root (typically by inspecting the URL host).</summary>
    bool Supports(VcsRootInfo root);

    Task<IReadOnlyList<PullRequestInfo>> ListOpenPullRequestsAsync(VcsRootInfo root, CancellationToken cancellationToken);
    Task ReportStatusAsync(VcsRootInfo root, CommitStatusReport report, CancellationToken cancellationToken);
}
