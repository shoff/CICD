using System.Net.Http.Headers;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Cicd.Agent;

public sealed class ArtifactUploader(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "cicd-server";

    public async Task<int> UploadAsync(Guid buildId, string workingDirectory, IReadOnlyList<string> patterns, IBuildLog log, CancellationToken cancellationToken)
    {
        if (patterns.Count == 0)
        {
            return 0;
        }
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                matcher.AddExclude(pattern[1..]);
            }
            else
            {
                matcher.AddInclude(pattern);
            }
        }
        var matches = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(workingDirectory)));
        if (!matches.HasMatches)
        {
            log.Warning("Artifact patterns matched no files: " + string.Join(", ", patterns));
            return 0;
        }

        using var client = httpClientFactory.CreateClient(HttpClientName);
        var count = 0;
        foreach (var file in matches.Files)
        {
            var fullPath = Path.Combine(workingDirectory, file.Path);
            await using var stream = File.OpenRead(fullPath);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var url = $"api/v1/builds/{buildId}/artifacts?path={Uri.EscapeDataString(file.Path)}";
            using var response = await client.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                log.Error($"Failed to publish artifact {file.Path}: HTTP {(int)response.StatusCode}");
                continue;
            }
            log.Info($"Published artifact {file.Path} ({stream.Length:N0} bytes)");
            count++;
        }
        return count;
    }
}
