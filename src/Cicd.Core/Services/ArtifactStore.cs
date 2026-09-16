using Cicd.Core.Builds;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Microsoft.Extensions.Options;

namespace Cicd.Core.Services;

/// <summary>Stores build artifacts on the server's local disk under &lt;DataDirectory&gt;/artifacts/&lt;buildId&gt;/.</summary>
public sealed class ArtifactStore(CicdDbContext db, IOptions<CicdServerOptions> options)
{
    public string RootDirectory => Path.GetFullPath(Path.Combine(options.Value.DataDirectory, "artifacts"));

    public async Task<BuildArtifact> SaveAsync(Guid buildId, string relativePath, Stream content, CancellationToken cancellationToken)
    {
        var safeRelative = Sanitize(relativePath);
        var buildDirectory = Path.Combine(RootDirectory, buildId.ToString("N"));
        var fullPath = Path.GetFullPath(Path.Combine(buildDirectory, safeRelative));
        if (!fullPath.StartsWith(buildDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Artifact path '{relativePath}' escapes the build directory.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using (var file = File.Create(fullPath))
        {
            await content.CopyToAsync(file, cancellationToken);
        }
        var artifact = new BuildArtifact
        {
            BuildId = buildId,
            Path = safeRelative.Replace('\\', '/'),
            SizeBytes = new FileInfo(fullPath).Length,
            StoragePath = fullPath,
        };
        db.BuildArtifacts.Add(artifact);
        await db.SaveChangesAsync(cancellationToken);
        return artifact;
    }

    public Stream? Open(BuildArtifact artifact) => File.Exists(artifact.StoragePath) ? File.OpenRead(artifact.StoragePath) : null;

    private static string Sanitize(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s is not "." and not "..")
            .ToArray();
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("Artifact path is empty.");
        }
        return Path.Combine(segments);
    }
}
