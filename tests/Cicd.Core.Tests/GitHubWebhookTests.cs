using System.Security.Cryptography;
using System.Text;
using Cicd.Plugins.GitHub;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cicd.Core.Tests;

public class GitHubWebhookTests
{
    private const string PushBody = """{"ref":"refs/heads/main","after":"abc123","repository":{"clone_url":"https://github.com/shoff/CICD.git"}}""";

    [Fact]
    public void Parses_repository_coordinates()
    {
        Assert.Equal(new RepositoryCoordinates("shoff", "CICD"), RepositoryCoordinates.Parse("https://github.com/shoff/CICD.git"));
        Assert.Equal(new RepositoryCoordinates("shoff", "CICD"), RepositoryCoordinates.Parse("git@github.com:shoff/CICD.git"));
        Assert.Null(RepositoryCoordinates.Parse("https://gitlab.com/shoff/CICD.git"));
    }

    [Fact]
    public async Task Push_event_queues_build_for_branch()
    {
        var handler = new GitHubWebhookHandler(new ConfigurationBuilder().Build(), NullLogger<GitHubWebhookHandler>.Instance);
        var actions = await handler.HandleAsync(new WebhookRequest("github", new Dictionary<string, string> { ["X-GitHub-Event"] = "push" }, PushBody), CancellationToken.None);
        var queue = Assert.IsType<WebhookAction.QueueBuild>(Assert.Single(actions));
        Assert.Equal("main", queue.Branch);
        Assert.Equal("abc123", queue.Revision);
        Assert.Equal("https://github.com/shoff/CICD.git", queue.RepositoryUrl);
    }

    [Fact]
    public async Task Rejects_bad_signature_when_secret_configured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:WebhookSecret"] = "s3cret" }).Build();
        var handler = new GitHubWebhookHandler(configuration, NullLogger<GitHubWebhookHandler>.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            handler.HandleAsync(new WebhookRequest("github", new Dictionary<string, string> { ["X-GitHub-Event"] = "push", ["X-Hub-Signature-256"] = "sha256=00" }, PushBody), CancellationToken.None));

        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(PushBody))).ToLowerInvariant();
        var actions = await handler.HandleAsync(new WebhookRequest("github", new Dictionary<string, string> { ["X-GitHub-Event"] = "push", ["X-Hub-Signature-256"] = signature }, PushBody), CancellationToken.None);
        Assert.Single(actions);
    }
}
