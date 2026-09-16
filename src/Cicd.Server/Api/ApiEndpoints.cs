using Cicd.Contracts;
using Cicd.Contracts.Api;
using Cicd.Core.Agents;
using Cicd.Core.Builds;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Cicd.Core.Plugins;
using Cicd.Core.PullRequests;
using Cicd.Core.Services;
using Cicd.Plugins.Sdk;
using Cicd.Server.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cicd.Server.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapCicdApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        MapProjects(api.MapGroup("/projects").WithTags("Projects").RequireAuthorization(Policies.Admin));
        MapVcsRoots(api.MapGroup("/vcs-roots").WithTags("VCS roots").RequireAuthorization(Policies.Admin));
        MapBuildConfigurations(api.MapGroup("/build-configurations").WithTags("Build configurations").RequireAuthorization(Policies.Admin));
        MapBuilds(api.MapGroup("/builds").WithTags("Builds"));
        MapAgents(api.MapGroup("/agents").WithTags("Agents").RequireAuthorization(Policies.Admin));
        MapPullRequests(api.MapGroup("/pull-requests").WithTags("Pull requests").RequireAuthorization(Policies.Admin));
        MapPlugins(api.MapGroup("/plugins").WithTags("Plugins").RequireAuthorization(Policies.Admin));
        MapWebhooks(api.MapGroup("/webhooks").WithTags("Webhooks"));
        return app;
    }

    private static void MapProjects(RouteGroupBuilder group)
    {
        group.MapGet("/", async (CicdDbContext db, CancellationToken ct) =>
            await db.Projects.OrderBy(p => p.Name).Select(p => p.ToDto()).ToListAsync(ct));

        group.MapGet("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.Projects.FindAsync([id], ct) is { } project ? Results.Ok(project.ToDto()) : Results.NotFound());

        group.MapPost("/", async Task<IResult> (CreateProjectRequest request, CicdDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required."] });
            }

            var project = new Project { Name = request.Name.Trim(), Description = request.Description, ParentId = request.ParentId };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/projects/{project.Id}", project.ToDto());
        });

        group.MapDelete("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());
    }

    private static void MapVcsRoots(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? projectId, CicdDbContext db, CancellationToken ct) =>
            await db.VcsRoots.Where(v => projectId == null || v.ProjectId == projectId).OrderBy(v => v.Name).Select(v => v.ToDto()).ToListAsync(ct));

        group.MapGet("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.VcsRoots.FindAsync([id], ct) is { } root ? Results.Ok(root.ToDto()) : Results.NotFound());

        group.MapPost("/", async Task<IResult> (CreateVcsRootRequest request, CicdDbContext db, IEnumerable<IVcsProvider> providers, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Name is required."];
            }

            if (string.IsNullOrWhiteSpace(request.Url))
            {
                errors["url"] = ["Url is required."];
            }

            if (!providers.Any(p => string.Equals(p.Id, request.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                errors["providerId"] = [$"No VCS provider '{request.ProviderId}' is installed. Available: {string.Join(", ", providers.Select(p => p.Id))}"];
            }

            if (!await db.Projects.AnyAsync(p => p.Id == request.ProjectId, ct))
            {
                errors["projectId"] = ["Project does not exist."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var root = new VcsRoot
            {
                ProjectId = request.ProjectId,
                Name = request.Name.Trim(),
                ProviderId = request.ProviderId,
                Url = request.Url.Trim(),
                DefaultBranch = string.IsNullOrWhiteSpace(request.DefaultBranch) ? "main" : request.DefaultBranch,
                Properties = new Dictionary<string, string>(request.Properties ?? new Dictionary<string, string>()),
            };
            db.VcsRoots.Add(root);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/vcs-roots/{root.Id}", root.ToDto());
        });

        group.MapPost("/{id:guid}/test", async Task<IResult> (Guid id, CicdDbContext db, IEnumerable<IVcsProvider> providers, CancellationToken ct) =>
        {
            var root = await db.VcsRoots.FindAsync([id], ct);
            if (root is null)
            {
                return Results.NotFound();
            }

            var provider = providers.FirstOrDefault(p => string.Equals(p.Id, root.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return Results.Problem($"No VCS provider '{root.ProviderId}' is installed.");
            }

            var error = await provider.TestConnectionAsync(root.ToInfo(), ct);
            return Results.Ok(new { ok = error is null, message = error ?? "Connection successful." });
        });

        group.MapDelete("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.VcsRoots.Where(v => v.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());
    }

    private static void MapBuildConfigurations(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? projectId, CicdDbContext db, CancellationToken ct) =>
            await db.BuildConfigurations.Where(c => projectId == null || c.ProjectId == projectId).OrderBy(c => c.Name).Select(c => c.ToDto()).ToListAsync(ct));

        group.MapGet("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.BuildConfigurations.FindAsync([id], ct) is { } configuration ? Results.Ok(configuration.ToDto()) : Results.NotFound());

        group.MapPost("/", async Task<IResult> (UpsertBuildConfigurationRequest request, CicdDbContext db, IEnumerable<IBuildStepType> stepTypes, IEnumerable<IBuildTrigger> triggers, CancellationToken ct) =>
        {
            var errors = await ValidateAsync(request, db, stepTypes, triggers, ct);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var configuration = new BuildConfiguration { ProjectId = request.ProjectId, Name = request.Name.Trim() };
            Apply(configuration, request);
            db.BuildConfigurations.Add(configuration);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/build-configurations/{configuration.Id}", configuration.ToDto());
        });

        group.MapPut("/{id:guid}", async Task<IResult> (Guid id, UpsertBuildConfigurationRequest request, CicdDbContext db, IEnumerable<IBuildStepType> stepTypes, IEnumerable<IBuildTrigger> triggers, CancellationToken ct) =>
        {
            var configuration = await db.BuildConfigurations.FindAsync([id], ct);
            if (configuration is null)
            {
                return Results.NotFound();
            }

            var errors = await ValidateAsync(request, db, stepTypes, triggers, ct);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            configuration.ProjectId = request.ProjectId;
            configuration.Name = request.Name.Trim();
            Apply(configuration, request);
            configuration.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(configuration.ToDto());
        });

        group.MapPost("/{id:guid}/pause", async Task<IResult> (Guid id, bool paused, CicdDbContext db, CancellationToken ct) =>
        {
            var configuration = await db.BuildConfigurations.FindAsync([id], ct);
            if (configuration is null)
            {
                return Results.NotFound();
            }

            configuration.Paused = paused;
            await db.SaveChangesAsync(ct);
            return Results.Ok(configuration.ToDto());
        });

        group.MapPost("/{id:guid}/queue", async Task<IResult> (Guid id, QueueBuildRequest? request, BuildQueueService queue, CancellationToken ct) =>
        {
            try
            {
                var triggeredBy = string.IsNullOrWhiteSpace(request?.Comment) ? "manual (api)" : $"manual (api): {request!.Comment}";
                var build = await queue.QueueAsync(new QueueBuildCommand(id, request?.Branch, request?.Revision, TriggeredBy: triggeredBy), ct);
                return Results.Created($"/api/v1/builds/{build.Id}", build.ToDto());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapDelete("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.BuildConfigurations.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());
    }

    private static async Task<Dictionary<string, string[]>> ValidateAsync(UpsertBuildConfigurationRequest request, CicdDbContext db, IEnumerable<IBuildStepType> stepTypes, IEnumerable<IBuildTrigger> triggers, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Name is required."];
        }

        if (!await db.Projects.AnyAsync(p => p.Id == request.ProjectId, ct))
        {
            errors["projectId"] = ["Project does not exist."];
        }

        if (request.VcsRootId is { } rootId && !await db.VcsRoots.AnyAsync(v => v.Id == rootId, ct))
        {
            errors["vcsRootId"] = ["VCS root does not exist."];
        }

        var types = stepTypes.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var stepErrors = new List<string>();
        foreach (var (step, index) in (request.Steps ?? []).Select((s, i) => (s, i)))
        {
            if (!types.TryGetValue(step.TypeId, out var type))
            {
                stepErrors.Add($"steps[{index}]: unknown step type '{step.TypeId}'. Installed: {string.Join(", ", types.Keys)}");
                continue;
            }
            foreach (var parameter in type.Parameters.Where(p => p.Required))
            {
                if (string.IsNullOrWhiteSpace(step.Parameters.Get(parameter.Name)))
                {
                    stepErrors.Add($"steps[{index}]: parameter '{parameter.Name}' is required for '{type.Id}'.");
                }
            }
            stepErrors.AddRange(type.Validate(step.Parameters).Select(e => $"steps[{index}]: {e}"));
        }
        if (stepErrors.Count > 0)
        {
            errors["steps"] = [.. stepErrors];
        }

        var triggerTypes = triggers.Select(t => t.TypeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var triggerErrors = (request.Triggers ?? []).Where(t => !triggerTypes.Contains(t.TypeId)).Select(t => $"unknown trigger type '{t.TypeId}'. Installed: {string.Join(", ", triggerTypes)}").ToArray();
        if (triggerErrors.Length > 0)
        {
            errors["triggers"] = triggerErrors;
        }

        return errors;
    }

    private static void Apply(BuildConfiguration configuration, UpsertBuildConfigurationRequest request)
    {
        configuration.Description = request.Description;
        configuration.VcsRootId = request.VcsRootId;
        configuration.BuildNumberFormat = string.IsNullOrWhiteSpace(request.BuildNumberFormat) ? "%build.counter%" : request.BuildNumberFormat;
        configuration.Steps = (request.Steps ?? []).Select(s => new BuildStep { Name = s.Name, TypeId = s.TypeId, Parameters = new Dictionary<string, string>(s.Parameters), ExecutionPolicy = s.ExecutionPolicy }).ToList();
        configuration.Parameters = new Dictionary<string, string>(request.Parameters ?? new Dictionary<string, string>());
        configuration.Triggers = (request.Triggers ?? []).Select(t => new TriggerSetting { TypeId = t.TypeId, Parameters = new Dictionary<string, string>(t.Parameters) }).ToList();
        configuration.AgentRequirements = new Dictionary<string, string>(request.AgentRequirements ?? new Dictionary<string, string>());
        configuration.ArtifactPaths = [.. request.ArtifactPaths ?? []];
        if (request.PullRequests is { } pr)
        {
            configuration.PullRequests = new PullRequestFeature { Enabled = pr.Enabled, TargetBranchFilter = pr.TargetBranchFilter, ReportStatus = pr.ReportStatus };
        }
    }

    private static void MapBuilds(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? configurationId, Guid? projectId, BuildStatus? status, int? take, CicdDbContext db, CancellationToken ct) =>
            await BuildQuery(db)
                .Where(b => configurationId == null || b.BuildConfigurationId == configurationId)
                .Where(b => projectId == null || b.BuildConfiguration!.ProjectId == projectId)
                .Where(b => status == null || b.Status == status)
                .OrderByDescending(b => b.QueuedAt)
                .Take(Math.Clamp(take ?? 50, 1, 500))
                .Select(b => b.ToDto())
                .ToListAsync(ct)).RequireAuthorization(Policies.Admin);

        group.MapGet("/queue", async (CicdDbContext db, CancellationToken ct) =>
            await BuildQuery(db).Where(b => b.Status == BuildStatus.Queued).OrderBy(b => b.QueuedAt).Select(b => b.ToDto()).ToListAsync(ct)).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await BuildQuery(db).FirstOrDefaultAsync(b => b.Id == id, ct) is { } build ? Results.Ok(build.ToDto()) : Results.NotFound()).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:guid}/steps", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.Builds.Where(b => b.Id == id).Select(b => b.StepRuns).FirstOrDefaultAsync(ct) is { } steps ? Results.Ok(steps) : Results.NotFound()).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:guid}/log", async Task<IResult> (Guid id, long? after, int? take, CicdDbContext db, CancellationToken ct) =>
        {
            var build = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
            if (build is null)
            {
                return Results.NotFound();
            }

            var lines = await db.BuildLogLines.AsNoTracking()
                .Where(l => l.BuildId == id && l.Sequence > (after ?? -1))
                .OrderBy(l => l.Sequence)
                .Take(Math.Clamp(take ?? 2000, 1, 10000))
                .Select(l => new LogLine { Sequence = l.Sequence, Timestamp = l.Timestamp, Level = l.Level, StepIndex = l.StepIndex, Text = l.Text })
                .ToListAsync(ct);
            return Results.Ok(new BuildLogDto(id, lines, lines.Count > 0 ? lines[^1].Sequence : after ?? -1, build.Status.IsFinished()));
        }).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:guid}/log.txt", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
        {
            if (!await db.Builds.AnyAsync(b => b.Id == id, ct))
            {
                return Results.NotFound();
            }

            var lines = await db.BuildLogLines.AsNoTracking().Where(l => l.BuildId == id).OrderBy(l => l.Sequence).Select(l => l.Text).ToListAsync(ct);
            return Results.Text(string.Join('\n', lines), "text/plain");
        }).RequireAuthorization(Policies.Admin);

        group.MapPost("/{id:guid}/cancel", async Task<IResult> (Guid id, BuildQueueService queue, CancellationToken ct) =>
            await queue.CancelAsync(id, "api", ct) is { } build ? Results.Ok(build.ToDto()) : Results.NotFound()).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:guid}/artifacts", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
        {
            if (!await db.Builds.AnyAsync(b => b.Id == id, ct))
            {
                return Results.NotFound();
            }

            return Results.Ok(await db.BuildArtifacts.Where(a => a.BuildId == id).OrderBy(a => a.Path).Select(a => a.ToDto()).ToListAsync(ct));
        }).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:guid}/artifacts/{**path}", async Task<IResult> (Guid id, string path, CicdDbContext db, ArtifactStore store, CancellationToken ct) =>
        {
            var artifact = await db.BuildArtifacts.FirstOrDefaultAsync(a => a.BuildId == id && a.Path == path, ct);
            if (artifact is null)
            {
                return Results.NotFound();
            }

            var stream = store.Open(artifact);
            return stream is null ? Results.NotFound() : Results.File(stream, "application/octet-stream", Path.GetFileName(artifact.Path));
        }).RequireAuthorization(Policies.Admin);

        // Agents publish artifacts here with the agent token.
        group.MapPost("/{id:guid}/artifacts", async Task<IResult> (Guid id, [FromQuery] string path, HttpRequest request, CicdDbContext db, ArtifactStore store, CancellationToken ct) =>
        {
            if (!await db.Builds.AnyAsync(b => b.Id == id, ct))
            {
                return Results.NotFound();
            }

            var artifact = await store.SaveAsync(id, path, request.Body, ct);
            return Results.Created($"/api/v1/builds/{id}/artifacts/{artifact.Path}", artifact.ToDto());
        }).RequireAuthorization(Policies.Agent).DisableAntiforgery();
    }

    private static IQueryable<Build> BuildQuery(CicdDbContext db) =>
        db.Builds.AsNoTracking().Include(b => b.BuildConfiguration!).ThenInclude(c => c.Project).Include(b => b.Agent);

    private static void MapAgents(RouteGroupBuilder group)
    {
        group.MapGet("/", async (CicdDbContext db, AgentConnectionRegistry connections, CancellationToken ct) =>
            (await db.Agents.OrderBy(a => a.Name).ToListAsync(ct)).Select(a => a.ToDto(connections.IsConnected(a.Id))).ToList());

        group.MapGet("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, AgentConnectionRegistry connections, CancellationToken ct) =>
            await db.Agents.FindAsync([id], ct) is { } agent ? Results.Ok(agent.ToDto(connections.IsConnected(agent.Id))) : Results.NotFound());

        group.MapPost("/{id:guid}/authorize", async Task<IResult> (Guid id, bool? authorized, AgentService agents, AgentConnectionRegistry connections, CancellationToken ct) =>
            await agents.SetAuthorizedAsync(id, authorized ?? true, ct) is { } agent ? Results.Ok(agent.ToDto(connections.IsConnected(agent.Id))) : Results.NotFound());

        group.MapPost("/{id:guid}/enable", async Task<IResult> (Guid id, bool? enabled, AgentService agents, AgentConnectionRegistry connections, CancellationToken ct) =>
            await agents.SetEnabledAsync(id, enabled ?? true, ct) is { } agent ? Results.Ok(agent.ToDto(connections.IsConnected(agent.Id))) : Results.NotFound());

        group.MapGet("/{id:guid}/compatibility/{configurationId:guid}", async Task<IResult> (Guid id, Guid configurationId, CicdDbContext db, CancellationToken ct) =>
        {
            var agent = await db.Agents.FindAsync([id], ct);
            var configuration = await db.BuildConfigurations.Include(c => c.VcsRoot).FirstOrDefaultAsync(c => c.Id == configurationId, ct);
            if (agent is null || configuration is null)
            {
                return Results.NotFound();
            }

            var unmet = AgentMatcher.Explain(AgentMatcher.EffectiveRequirements(configuration), agent.Capabilities);
            return Results.Ok(new { compatible = unmet.Count == 0, unmet });
        });

        group.MapDelete("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.Agents.Where(a => a.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());
    }

    private static void MapPullRequests(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? vcsRootId, string? state, CicdDbContext db, CancellationToken ct) =>
            await db.PullRequests
                .Where(p => vcsRootId == null || p.VcsRootId == vcsRootId)
                .Where(p => state == null || p.State == state)
                .OrderByDescending(p => p.UpdatedAt)
                .Select(p => p.ToDto())
                .ToListAsync(ct));

        group.MapPost("/refresh", async (string? repositoryUrl, PullRequestService service, CancellationToken ct) =>
            new { queued = await service.RefreshAsync(repositoryUrl, ct) });
    }

    private static void MapPlugins(RouteGroupBuilder group)
    {
        group.MapGet("/", (PluginCatalog catalog) => new { catalog.Side, plugins = catalog.Plugins.Select(p => p.ToDto()), failures = catalog.Failures });
        group.MapGet("/step-types", (IEnumerable<IBuildStepType> types) => types.Select(t => t.ToDto()).OrderBy(t => t.Id));
        group.MapGet("/triggers", (IEnumerable<IBuildTrigger> triggers) => triggers.Select(t => new { t.TypeId, t.DisplayName, parameters = t.Parameters }));
        group.MapGet("/vcs-providers", (IEnumerable<IVcsProvider> providers) => providers.Select(p => new { p.Id, p.DisplayName, properties = p.Properties }));
        group.MapGet("/pull-request-providers", (IEnumerable<IPullRequestProvider> providers) => providers.Select(p => new { p.Id, p.DisplayName }));
    }

    private static void MapWebhooks(RouteGroupBuilder group)
    {
        // Authentication is delegated to the handler (e.g. GitHub HMAC signature).
        group.MapPost("/{providerId}", async Task<IResult> (string providerId, HttpRequest request, WebhookService webhooks, CancellationToken ct) =>
        {
            if (!webhooks.HasHandler(providerId))
            {
                return Results.NotFound(new { error = $"No webhook handler for '{providerId}'." });
            }

            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            try
            {
                var outcomes = await webhooks.HandleAsync(new WebhookRequest(providerId, headers, body), ct);
                return Results.Ok(new { outcomes });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
            }
        }).AllowAnonymous().DisableAntiforgery();
    }
}
