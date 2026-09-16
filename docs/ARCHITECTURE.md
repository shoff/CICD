# Architecture

## Processes

```
 browser / API client                          build agents (N containers)
        |  HTTP + SignalR (/hubs/builds)               |  SignalR (/hubs/agents) + HTTP artifact upload
        v                                              v
 +--------------------------------------------------------------------+
 |  Cicd.Server (ASP.NET Core)                                        |
 |  Blazor UI  |  REST /api/v1  |  AgentHub  |  BuildHub              |
 |  BuildDispatcher | TriggerService | PullRequestService (hosted)    |
 |  PluginHost (server-side plugins)                                  |
 +--------------------------------------------------------------------+
        |  EF Core / Npgsql
        v
   PostgreSQL
```

Agents only make outbound connections. Nothing on the agent listens, so agents can run anywhere that can reach the server.

## Build lifecycle

1. **Queue.** `BuildQueueService.QueueAsync` creates a `Build` in `Queued`, bumps the configuration's `BuildCounter`
   (an EF concurrency token, retried on conflict) and formats the build number.
   Sources: UI button, `POST /build-configurations/{id}/queue`, a trigger, the pull request poller, a webhook.
2. **Dispatch.** `BuildDispatcher` runs every `Server:DispatchIntervalSeconds`. It loads queued builds oldest first and the
   set of connected, authorized, enabled, idle agents. `AgentMatcher` compares each configuration's effective requirements
   (explicit `AgentRequirements` plus an implicit `runner.<stepType>` and `vcs.<provider>` for every step type and VCS
   provider used) against agent capabilities. `BuildJobFactory` resolves `%parameters%`, asks the VCS provider for the
   current revision if none was pinned, and produces a `BuildJob`. The job is pushed to the agent with a hub invoke; if
   the agent throws (busy), the build goes back to `Queued`.
3. **Execute.** `BuildExecutor` on the agent: `BuildStarted`, checkout via `IVcsCheckout`, then each step through the
   `IBuildRunner` whose `TypeId` matches, honoring `StepExecutionPolicy`. Output goes through `HubBuildLog`, which batches
   lines and sends them with a monotonically increasing sequence number. Step transitions are reported as they happen.
   Matching artifact globs are uploaded with `POST /api/v1/builds/{id}/artifacts`.
4. **Record.** `BuildProgressService` persists log lines and status and hands every change to `BuildEventBroadcaster`,
   which pushes to `BuildHub` clients, to the in-process `BuildEventStream` the Blazor pages listen on, and to every
   `INotifier` plugin (the pull request status publisher is one).
5. **Finish.** `BuildFinished` sets the final status and frees the agent. If the agent connection drops mid-build,
   `AgentService.DisconnectedAsync` marks the build `Error`.

## Plugins

See `PLUGINS.md`. In short: a directory under the plugin root with a `plugin.json` and an assembly containing one
`IPlugin`. `PluginHost` loads each plugin into its own `AssemblyLoadContext`, sharing the SDK, Contracts and
Microsoft.Extensions assemblies with the host so interface types unify. The plugin's `Configure` registers contributions;
the registrar ignores contributions that do not apply to the current side (server or agent), so one plugin package can
ship both halves exactly like a TeamCity plugin zip.

Contribution kinds:

| Interface | Side | Role |
| --- | --- | --- |
| `IBuildStepType` | server | Describes a step type and validates its parameters. |
| `IBuildRunner` | agent | Executes a step type. |
| `IVcsProvider` | server | Current revision, branch list, connection test. |
| `IVcsCheckout` | agent | Materializes a revision on disk. |
| `IPullRequestProvider` | server | Lists open pull requests, publishes commit status. |
| `IBuildTrigger` | server | Polled; returns builds to queue. Persistent per-trigger state is provided. |
| `INotifier` | server | Receives every build state change. |
| `IWebhookHandler` | server | Turns an inbound HTTP webhook into actions. |
| `IAgentCapabilityProvider` | agent | Adds capabilities (e.g. installed SDK versions). |

## Data model

PostgreSQL, snake_case, jsonb for the flexible parts. Tables: `projects`, `vcs_roots`, `build_configurations`
(`steps`, `parameters`, `triggers`, `agent_requirements`, `artifact_paths`, `pull_requests` are jsonb), `builds`
(`step_runs` jsonb), `build_log_lines`, `build_artifacts`, `agents` (`capabilities` jsonb), `pull_requests`,
`trigger_state`. `CicdDbContext` in Core is provider-neutral; `PostgresCicdDbContext` in Data adds the jsonb column
types and owns the migrations.

## Real-time

Two hubs. `/hubs/agents` is for agents and requires the agent token. `/hubs/builds` is for browsers and tools:
`BuildUpdated` and `AgentUpdated` are broadcast to everyone, `BuildLog` goes to the group of a build after
`SubscribeToBuild(buildId)`. Log lines carry sequence numbers so a client can catch up with
`GET /api/v1/builds/{id}/log?after=<seq>` and then continue live without gaps or duplicates.

## Security model

- **Schemes.** A policy scheme (`SchemeSelector`) forwards each request: a JWT-shaped bearer or hub `access_token` goes
  to JwtBearer (validated against the IdP), any other bearer or `X-Api-Key` goes to `TokenAuthenticationHandler`
  (agent token -> role `agent`, `Security:ApiToken` -> role `admin`), everything else is the session cookie issued
  by the v21 login (`GET /login` renders the form, `POST /login` posts the credentials to the IdP's
  `api/v21/accountv21/login`, reads the identity claims out of the access token it returns and signs the cookie in;
  `/logout` clears the cookie).
- **Local users.** `LocalUserClaimsTransformation` runs on every cookie or JWT request, upserts the `users` row by
  issuer and subject through `UserService`, and adds `cicd:user_id` plus a role claim. Disabled users get no role.
- **Policies.** `Viewer` < `Developer` < `Admin` through `RoleRequirement`; `Agent` requires the agent role. In open
  mode (no `IdentityProvider:Authority` and no `Security:ApiToken`) every role policy succeeds, so a local run with
  `IdentityProvider:Authority` empty needs no login.
- Webhooks stay anonymous at the HTTP layer; handlers verify provider signatures.
