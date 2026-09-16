using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cicd.Contracts;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cicd.Plugins.GitHub;

public sealed class GitHubPlugin : IPlugin
{
    public void Configure(IPluginRegistrar registrar)
    {
        registrar.Services.AddHttpClient(GitHubClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(registrar.Configuration["GitHub:ApiBaseUrl"] ?? "https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("cicd-server/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        registrar.Services.AddSingleton<GitHubClient>();
        registrar
            .AddPullRequestProvider<GitHubPullRequestProvider>()
            .AddWebhookHandler<GitHubWebhookHandler>();
    }
}

public static class GitHubProperties
{
    /// <summary>VCS root property holding the API token. Falls back to the GitHub:Token configuration value.</summary>
    public const string Token = "github.token";
    public const string WebhookSecret = "GitHub:WebhookSecret";
}

public sealed record RepositoryCoordinates(string Owner, string Name)
{
    public static RepositoryCoordinates? Parse(string url)
    {
        var value = url.Trim();
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        string path;
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = value["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            return null;
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? new RepositoryCoordinates(parts[0], parts[1]) : null;
    }
}

public sealed class GitHubClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    public const string HttpClientName = "github";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public HttpClient Create(VcsRootInfo root)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var token = root.Properties.Get(GitHubProperties.Token, configuration["GitHub:Token"] ?? "");
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    public async Task<List<PullRequestPayload>> ListOpenPullRequestsAsync(VcsRootInfo root, RepositoryCoordinates repo, CancellationToken cancellationToken)
    {
        using var client = Create(root);
        var all = new List<PullRequestPayload>();
        for (var page = 1; page <= 10; page++)
        {
            var batch = await client.GetFromJsonAsync<List<PullRequestPayload>>($"repos/{repo.Owner}/{repo.Name}/pulls?state=open&per_page=100&page={page}", Json, cancellationToken) ?? [];
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return all;
    }

    public async Task SetCommitStatusAsync(VcsRootInfo root, RepositoryCoordinates repo, CommitStatusReport report, CancellationToken cancellationToken)
    {
        using var client = Create(root);
        var state = report.Status switch
        {
            BuildStatus.Success => "success",
            BuildStatus.Failure => "failure",
            BuildStatus.Error or BuildStatus.Canceled => "error",
            _ => "pending",
        };
        var body = new { state, target_url = report.TargetUrl, description = Truncate(report.Description, 140), context = report.Context };
        using var response = await client.PostAsJsonAsync($"repos/{repo.Owner}/{repo.Name}/statuses/{report.Sha}", body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public sealed class PullRequestPayload
{
    public long Number { get; set; }
    public string Title { get; set; } = "";
    public string State { get; set; } = "open";
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    public GitHubUser? User { get; set; }
    public GitRef Head { get; set; } = new();
    public GitRef Base { get; set; } = new();
}

public sealed class GitHubUser { public string Login { get; set; } = ""; }

public sealed class GitRef
{
    public string Ref { get; set; } = "";
    public string Sha { get; set; } = "";
    public GitHubRepository? Repo { get; set; }
}

public sealed class GitHubRepository
{
    [JsonPropertyName("clone_url")] public string? CloneUrl { get; set; }
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
}

public sealed class GitHubPullRequestProvider(GitHubClient client, ILogger<GitHubPullRequestProvider> logger) : IPullRequestProvider
{
    public string Id => "github";
    public string DisplayName => "GitHub";

    public bool Supports(VcsRootInfo root) => RepositoryCoordinates.Parse(root.Url) is not null;

    public async Task<IReadOnlyList<PullRequestInfo>> ListOpenPullRequestsAsync(VcsRootInfo root, CancellationToken cancellationToken)
    {
        var repo = RepositoryCoordinates.Parse(root.Url) ?? throw new InvalidOperationException($"'{root.Url}' is not a GitHub repository URL.");
        var payloads = await client.ListOpenPullRequestsAsync(root, repo, cancellationToken);
        logger.LogDebug("GitHub: {Count} open pull request(s) in {Owner}/{Name}", payloads.Count, repo.Owner, repo.Name);
        return payloads.Select(p => new PullRequestInfo(
            p.Number, p.Title, p.Head.Ref, p.Base.Ref, p.Head.Sha, p.User?.Login ?? "", p.State, p.HtmlUrl, p.UpdatedAt)
        {
            CheckoutRef = $"refs/pull/{p.Number}/head",
        }).ToList();
    }

    public Task ReportStatusAsync(VcsRootInfo root, CommitStatusReport report, CancellationToken cancellationToken)
    {
        var repo = RepositoryCoordinates.Parse(root.Url) ?? throw new InvalidOperationException($"'{root.Url}' is not a GitHub repository URL.");
        return client.SetCommitStatusAsync(root, repo, report, cancellationToken);
    }
}

/// <summary>Handles GitHub "push" and "pull_request" events posted to /api/v1/webhooks/github.</summary>
public sealed class GitHubWebhookHandler(IConfiguration configuration, ILogger<GitHubWebhookHandler> logger) : IWebhookHandler
{
    public string ProviderId => "github";

    public Task<IReadOnlyList<WebhookAction>> HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var secret = configuration[GitHubProperties.WebhookSecret];
        if (!string.IsNullOrEmpty(secret) && !SignatureValid(secret, request))
        {
            throw new UnauthorizedAccessException("GitHub webhook signature is missing or invalid.");
        }

        var eventName = request.Headers.TryGetValue("X-GitHub-Event", out var value) ? value : "";
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        var repositoryUrl = root.TryGetProperty("repository", out var repository) && repository.TryGetProperty("clone_url", out var cloneUrl)
            ? cloneUrl.GetString() ?? ""
            : "";

        IReadOnlyList<WebhookAction> actions = eventName switch
        {
            "push" when root.TryGetProperty("ref", out var refElement) && root.TryGetProperty("after", out var after) =>
                after.GetString() is { } sha && sha != new string('0', 40)
                    ? [new WebhookAction.QueueBuild(repositoryUrl, StripHeads(refElement.GetString() ?? ""), sha, "github push")]
                    : [],
            "pull_request" => [new WebhookAction.RefreshPullRequests(repositoryUrl)],
            "ping" => [],
            _ => [],
        };
        logger.LogInformation("GitHub webhook {Event} for {Repository}: {Count} action(s)", eventName, repositoryUrl, actions.Count);
        return Task.FromResult(actions);
    }

    private static string StripHeads(string gitRef) => gitRef.StartsWith("refs/heads/") ? gitRef["refs/heads/".Length..] : gitRef;

    private static bool SignatureValid(string secret, WebhookRequest request)
    {
        if (!request.Headers.TryGetValue("X-Hub-Signature-256", out var header) || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Body));
        var actual = Convert.FromHexString(header["sha256=".Length..]);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
