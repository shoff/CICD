# Handoff

State of the repository as of commit 8d52bf8 on branch `claude/peanut-core-build-server-v48now`. Read this first, then
`README.md` (how to run), `docs/ARCHITECTURE.md` (how it works), `docs/PLUGINS.md` (how to extend).

## Goal

Replace TeamCity with a self-hosted ASP.NET Core build server: server in one Docker image, agents in separate images,
TeamCity-style plugins, PostgreSQL for state, real-time build reporting, a REST API, and pull request builds. The
scaffold is in place; the intended workflow from here is "TeamCity does X, add X."

## What is done and verified

| Area | Status | How it was verified |
| --- | --- | --- |
| Solution builds (`dotnet build Cicd.slnx`) | done | clean build, no warnings, .NET 10 SDK 10.0.401 |
| Unit tests (`dotnet test Cicd.slnx`) | 28 passing | resolver, matcher, globs, plugin host, command line runner, GitHub webhook |
| Server + agent end to end | done | ran against local PostgreSQL 16: register, dispatch, git checkout, 4 steps, `%param%` substitution, log streaming, artifact upload and download, webhook rebuild, cancel, UI pages 200, agent 401 without token |
| EF Core migration `InitialCreate` | done, committed | applied at server startup; 10 jsonb columns; snake_case names |
| Plugin loading from `artifacts/plugins/` | done | both sides load `command-line`, `dotnet`, `git`; server loads `github` |
| Docker images and compose | **written, never built** | no Docker daemon in the authoring sandbox |
| GitHub pull request polling and status publishing | **written, never exercised against GitHub** | no token; webhook signature verification is unit tested |
| Users, roles, v21 login | code, unit tests and curl smoke done; **first real sign-in pending** | run in Development and sign in at /login |

## Layout

```
src/Cicd.Contracts      enums + DTOs shared by server, agent, API clients
src/Cicd.Plugins.Sdk    plugin interfaces (the only thing plugins reference)
src/Cicd.Core           entities, CicdDbContext (provider-neutral), plugin host, queue, dispatcher, triggers, PR service
src/Cicd.Data           Npgsql wiring, PostgresCicdDbContext (jsonb), Migrations/
src/Cicd.Server         Program.cs, Api/ApiEndpoints.cs, Hubs/, Realtime/, Security/, Components/ (Blazor)
src/Cicd.Agent          AgentWorker (hub connection), BuildExecutor, HubBuildLog, ArtifactUploader
plugins/*               command-line, dotnet, git, github (each: csproj with <PluginId>, plugin.json, one file)
tests/Cicd.Core.Tests   xunit
docker/                 server.Dockerfile, agent.Dockerfile, agent-dotnet.Dockerfile
```

## Key design decisions and why

- **Agents connect out to the server over SignalR** (`/hubs/agents`). Nothing listens on an agent, so agents can run
  anywhere. The server pushes a `BuildJob` with a client invoke and expects a `bool` back; a busy agent throws and the
  dispatcher returns the build to the queue.
- **Hub JSON protocol serializes enums as strings on both sides.** The server sets `JsonStringEnumConverter` in
  `Program.cs` and the agent sets it in `AgentWorker.cs`. They must stay in sync. This was the one runtime bug found
  in the end-to-end run: without the agent-side converter every job failed to parse and builds sat in Queued.
- **Step types become implicit agent requirements.** `AgentMatcher.EffectiveRequirements` adds `runner.<typeId>` and
  `vcs.<providerId>` so a build never lands on an agent lacking the plugin. Agents advertise those from their loaded
  runners and checkouts.
- **One plugin package, two halves.** `IPluginRegistrar` silently drops contributions that do not apply to the current
  side (server or agent), so a plugin registers both `IBuildStepType` and `IBuildRunner` in one `Configure`.
- **Plugin isolation.** `PluginLoadContext` shares any assembly already loaded in the default context (SDK, Contracts,
  Microsoft.Extensions.*) and resolves everything else from the plugin folder. Plugin manifests must never use the
  generic enum converter for `PluginSide`; `PluginManifestReader` has its own converter that accepts arrays.
- **DbContext split.** `CicdDbContext` (Core) has no provider; `PostgresCicdDbContext` (Data) applies `jsonb` to every
  property annotated `Cicd:Json`. Constructors take `DbContextOptions` non-generic in Core and
  `DbContextOptions<PostgresCicdDbContext>` in Data. `AddCicdPostgres` registers a factory plus a scoped
  `CicdDbContext` plus an `IDbContextFactory<CicdDbContext>` adapter. Blazor pages use the factory; hubs and
  endpoints use the scoped context.
- **Blazor pages get live updates in-process** through `BuildEventStream`, not through a second SignalR connection.
  External clients use `/hubs/builds`.
- **Build logs live in PostgreSQL** (`build_log_lines`, sequenced per build). Simple and queryable; not right for very
  large logs. TeamCity uses files. Revisit when log volume is real.
- **Build numbers** use `BuildCounter` as an EF concurrency token with a retry loop in `BuildQueueService`, so two
  simultaneous queue requests cannot produce the same number.
- **Naming rule from the owner:** no leading underscores on C# fields. Primary constructors are used throughout to
  avoid the question entirely.

## How to run locally

```bash
export PATH=$HOME/.dotnet:$PATH          # if the SDK was installed with dotnet-install.sh
createdb cicd                             # default connection string is Host=localhost;Database=cicd;Username=cicd;Password=cicd
dotnet build Cicd.slnx                    # plugins are mirrored into artifacts/plugins/ by plugins/Directory.Build.targets
ASPNETCORE_ENVIRONMENT=Development Agents__AuthToken=dev dotnet run --project src/Cicd.Server
Agent__AuthToken=dev Agent__Name=local-1 dotnet run --project src/Cicd.Agent
```

Development environment auto-authorizes agents. `README.md` has a full curl sequence for project, VCS root,
configuration, and queue.

Migrations: `dotnet-ef` targets net8.0; with only the .NET 10 runtime installed run it with
`DOTNET_ROLL_FORWARD=LatestMajor`. Command in README under Development.

## Gaps, in the order I would tackle them

1. **Build the Docker images and run `docker compose up`.** Nobody has. Expect small path or apt issues, not design issues.
2. **First real sign-in.** `dotnet run` in Development (appsettings.Development.json points at identity-dev and bootstraps shoff@manageamerica.com as admin), sign in at /login, confirm /users shows the admin, then verify an IdP access token against /api/v1/builds and /hubs/builds, and register an API resource + enable `IdentityProvider:ValidateAudience`.
3. **Secrets at rest.** VCS root `Properties` (tokens, passwords) are plain jsonb. Redacted in API output only.
   Add a data protection based encrypter in `Mapping`/`VcsRoot` persistence, or a dedicated `secrets` table.
4. **UI editing** of VCS roots and build configurations. The API does it; the UI only creates projects and queues builds.
5. **Scheduled trigger** (`IBuildTrigger` with cron; consider the Cronos package).
6. **Agent pools**, then **build chains / snapshot dependencies**, then **artifact dependencies**.
7. **Notifications** (`INotifier` plugins: email, Slack) and **test result parsing** (TRX from the dotnet runner).
8. **Agent loss handling** is blunt: a dropped agent connection marks its build `Error` immediately.
   TeamCity waits a grace period for reconnection.
9. **Log storage** to files or object storage once volumes justify it.
10. Windows agents are untested. `ProcessRunner.ShellCommand` and the git askpass helper have Windows branches that were never run.

## Things that look odd but are intentional

- `plugins/Directory.Build.props` references the SDK with `Private=false` yet `Cicd.Contracts.dll` still lands in each
  plugin folder (transitive reference). Harmless: the load context prefers the host's copy by name.
- `Agents:AuthToken` default is `change-me` in `appsettings.json`; the server logs a warning at startup if unchanged.
- `PullRequestService.RefreshAsync` marks pull requests it no longer sees as `closed` locally. It never deletes rows.
- The `github` plugin declares `"sides": ["server"]` only. There is no agent half; checkout of `refs/pull/N/head`
  is done by the `git` plugin.

## Session notes

- Authoring environment had no Docker daemon; PostgreSQL 16 was available locally and used for the end-to-end run.
- Package versions are pinned in `Directory.Packages.props` (central package management). EF Core and
  Microsoft.Extensions 10.0.12, Npgsql provider 10.0.3, EFCore.NamingConventions 10.0.1, Scalar 2.17.4.
- 2026-09-16: users, roles and OIDC login implemented on branch `feature/users-roles-oidc` via
  `docs/superpowers/plans/2026-09-16-users-roles-oidc-login.md`. Smoke-tested against identity-dev discovery with a
  placeholder client; no real login yet. Local PostgreSQL runs in Docker as `cicd-postgres` (user/password/db `cicd`).
  Replaced the OIDC redirect flow with the IdP's v21 username/password login (same mechanism MAI uses) so no client
  registration is needed.
