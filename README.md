# CICD

A self-hosted build server written in ASP.NET Core, designed as a TeamCity replacement: one server, any number of
build agents in their own containers, PostgreSQL for state, a plugin model for build runners and integrations,
real-time build output, a REST API, and pull request builds.

## What is here

| Path | Purpose |
| --- | --- |
| `src/Cicd.Server` | Web app: Blazor UI, REST API (`/api/v1`), SignalR hubs, background services. One Docker image. |
| `src/Cicd.Agent` | Build agent. Separate process and Docker image. Connects out to the server, runs one build at a time. |
| `src/Cicd.Core` | Domain model, plugin host, build queue, dispatcher, triggers, pull request polling. |
| `src/Cicd.Data` | PostgreSQL wiring (Npgsql, snake_case, jsonb) and EF Core migrations. |
| `src/Cicd.Plugins.Sdk` | The interfaces plugins implement. Plugin projects reference only this and `Cicd.Contracts`. |
| `src/Cicd.Contracts` | Enums and DTOs shared by server, agent, and API clients. |
| `plugins/` | In-tree plugins: `command-line`, `dotnet`, `git`, `github`. Built to `artifacts/plugins/<id>/`. |
| `docker/` | `server.Dockerfile`, `agent.Dockerfile`, `agent-dotnet.Dockerfile`. |
| `docs/` | `ARCHITECTURE.md` (how it works), `PLUGINS.md` (how to write one). |

TeamCity concept to CICD concept:

| TeamCity | CICD |
| --- | --- |
| Project | `Project` |
| Build configuration, build steps, parameters (`%name%`) | `BuildConfiguration`, `Steps`, `Parameters` with the same `%name%` syntax |
| VCS root | `VcsRoot` serviced by an `IVcsProvider` plugin |
| VCS trigger | Built-in `vcs` trigger (poll) plus `POST /api/v1/webhooks/{provider}` |
| Agent requirements / agent parameters | `AgentRequirements` matched against agent `Capabilities` |
| Agent authorization | New agents register unauthorized; authorize in the UI or API |
| Build runner plugin (server + agent parts) | Plugin with `IBuildStepType` (server) and `IBuildRunner` (agent) |
| Pull Requests build feature | `PullRequests` feature on a configuration, `IPullRequestProvider` plugin |
| Commit Status Publisher | `PullRequestStatusNotifier` via the same provider |
| Artifact paths | `ArtifactPaths` globs, uploaded by the agent, served by the API |
| Build log | Streamed over SignalR, stored in PostgreSQL, `GET /api/v1/builds/{id}/log` |

## Run it

### Docker Compose

```bash
cp .env.example .env            # set AGENT_AUTH_TOKEN at minimum
docker compose up --build
```

Open <http://localhost:8080>. The API reference is at `/api/docs`. Two agents start (`agent-1`, `agent-dotnet-1`);
authorize them on the Agents page, or set `AGENT_AUTO_AUTHORIZE=true` in `.env` for local use.

### From source

Requirements: .NET 10 SDK, PostgreSQL, git on PATH.

```bash
createdb cicd                                   # or use ConnectionStrings__Cicd to point elsewhere
dotnet build Cicd.slnx                          # also mirrors plugins into artifacts/plugins/
ASPNETCORE_ENVIRONMENT=Development Agents__AuthToken=dev-token dotnet run --project src/Cicd.Server
Agent__AuthToken=dev-token Agent__Name=local-1 dotnet run --project src/Cicd.Agent
```

In the Development environment `appsettings.Development.json` points at identity-dev and requires a sign-in; pass
`IdentityProvider__Authority=` (empty) to run in open mode as the curl examples below assume.

Migrations run automatically on server start (`Server:MigrateOnStartup`). The Development environment auto-authorizes agents.

### First build, through the API

```bash
API=http://localhost:8080/api/v1
P=$(curl -s -X POST $API/projects -H 'content-type: application/json' -d '{"name":"Demo"}' | jq -r .id)
R=$(curl -s -X POST $API/vcs-roots -H 'content-type: application/json' \
  -d "{\"projectId\":\"$P\",\"name\":\"repo\",\"providerId\":\"git\",\"url\":\"https://github.com/shoff/CICD.git\",\"defaultBranch\":\"main\"}" | jq -r .id)
C=$(curl -s -X POST $API/build-configurations -H 'content-type: application/json' -d "{
  \"projectId\":\"$P\",\"name\":\"Build\",\"vcsRootId\":\"$R\",\"buildNumberFormat\":\"1.0.%build.counter%\",
  \"steps\":[
    {\"name\":\"Build\",\"typeId\":\"dotnet\",\"parameters\":{\"command\":\"build\",\"projects\":\"Cicd.slnx\"}},
    {\"name\":\"Test\",\"typeId\":\"dotnet\",\"parameters\":{\"command\":\"test\",\"projects\":\"tests/Cicd.Core.Tests/Cicd.Core.Tests.csproj\"}}
  ],
  \"triggers\":[{\"typeId\":\"vcs\",\"parameters\":{}}],
  \"pullRequests\":{\"enabled\":true,\"targetBranchFilter\":\"main\",\"reportStatus\":true}
}" | jq -r .id)
curl -s -X POST $API/build-configurations/$C/queue -H 'content-type: application/json' -d '{}'
```

Watch it on `/builds`, or follow the log with `GET $API/builds/{id}/log?after=-1`.

## Configuration

All settings are `appsettings.json` keys and can be set as environment variables with `__` separators.

| Key | Meaning |
| --- | --- |
| `ConnectionStrings:Cicd` | PostgreSQL connection string. |
| `Agents:AuthToken` | Shared secret agents must present. Required. |
| `Agents:AutoAuthorize` | Authorize agents on first registration. Development only. |
| `Security:ApiToken` | Static bearer token that acts as an admin. Break-glass and automation. Empty disables it. |
| `Security:BootstrapAdmins` | Emails promoted to admin on every sign-in. Set at least one before the first login. |
| `IdentityProvider:Authority` | Issuer base URL of the company identity provider. Empty runs the server in open mode (no login; UI and API open unless `Security:ApiToken` is set). |
| `IdentityProvider:LoginUrl` | The username/password endpoint. Empty derives `{Authority}/api/v21/accountv21/login`. |
| `IdentityProvider:ReturnUrl` | Sent to the login endpoint as `ReturnUrl`; it requires a value but CICD never follows it. |
| `IdentityProvider:ValidateAudience` / `IdentityProvider:Audience` | Audience validation for bearer JWTs. Off until the IdP has an API resource for CICD. |
| `IdentityProvider:RequireHttpsMetadata` | Default `true`. Requires https for the login endpoint and the discovery document. Turning it off also allows credentials to be posted over plain http. |
| `IdentityProvider:TimeoutSeconds` | Default `15`. Timeout for the call to the login endpoint. |
| `Server:PublicUrl` | Used in commit status links. |
| `Server:DataDirectory` | Where artifacts are stored. |
| `Server:*IntervalSeconds` | Dispatch, trigger poll, and pull request poll cadence. |
| `GitHub:Token` | Default token for pull request discovery and status publishing. A VCS root property `github.token` overrides it. |
| `GitHub:WebhookSecret` | If set, webhooks must carry a valid `X-Hub-Signature-256`. |
| `Plugins:Directory` / `Plugins:Disabled` | Plugin root and ids to skip. |

The former `Oidc` section is gone. Set `IdentityProvider:Authority`; with it empty the server runs in open mode (or
token-only mode when `Security:ApiToken` is set).

Agent: `Agent:ServerUrl`, `Agent:Name`, `Agent:AuthToken`, `Agent:WorkDirectory`, `Agent:Capabilities` (extra key/values).

## Users and roles

Sign-in is a username and password form at `/login` that posts the credentials to the identity provider's v21 login
endpoint (`IdentityProvider:LoginUrl`, by default `{Authority}/api/v21/accountv21/login`), so CICD needs no client
registration at the IdP. The access token the endpoint returns is read for `sub`, `name`, `unique_name`/`username` and
`email` and then discarded; the browser session is CICD's own 8-hour sliding cookie. On first sign-in a user row is
created with the `viewer` role; emails listed in `Security:BootstrapAdmins` become `admin`. Roles are global:

| Role | Can |
| --- | --- |
| viewer | read everything: builds, logs, artifacts, projects, agents |
| developer | viewer plus queue and cancel builds, create and edit projects, VCS roots and configurations |
| admin | developer plus authorize and delete agents, manage users, list plugins |

Admins change roles on `/users` or with `PUT /api/v1/users/{id}/role`. Changes apply on the next request.
API clients send an IdP access token as `Authorization: Bearer <jwt>`; the same local user and role apply.
`Security:ApiToken` is a static admin credential for automation. Agents use `Agents:AuthToken` only.
Until an API resource for CICD exists at the IdP and `IdentityProvider:ValidateAudience` is on, any access token identity-dev
issued to any application authenticates to CICD as that user. Enable audience validation as soon as the resource
is registered.

Behind a TLS-terminating proxy set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` on the server so cookies use the
public `https` scheme. Outside the Development environment the session cookie is always marked `Secure`.

## Pull requests

Enable `pullRequests` on a configuration whose VCS root points at GitHub. The server polls open pull requests
(`Server:PullRequestPollIntervalSeconds`), stores them, and queues a build each time a PR head moves. The agent
fetches `refs/pull/N/head`. Build status is published back as a commit status named `cicd/<configuration>`.
Configure a GitHub webhook to `POST /api/v1/webhooks/github` for `push` and `pull_request` events to react
immediately instead of waiting for the poll.

## What is deliberately not here yet

This is the scaffold, built so features can be added one at a time. Known gaps, in rough priority order:

- **Per-project roles, groups, personal access tokens.** Roles are global for now.
- **Secrets.** VCS root properties (tokens, passwords) are stored in plain jsonb. They are redacted in API responses but not encrypted at rest.
- **Build log storage.** Lines go into PostgreSQL. Fine for a team, wrong for very large logs; TeamCity uses files.
- **Agent pools, build chains and snapshot dependencies, artifact dependencies, scheduled triggers, build history cleanup, notifications (email, Slack), test result parsing, code coverage, Windows agents (untested), Kubernetes agent autoscaling.**
- **UI editing.** Projects can be created in the UI; VCS roots and build configurations are created through the API.
- **Docker images are unverified** in this environment (no Docker daemon was available). The Dockerfiles follow the standard multi-stage pattern but have not been built.

## Development

```bash
dotnet build Cicd.slnx
dotnet test Cicd.slnx
cd src/Cicd.Data && dotnet ef migrations add <Name> --context PostgresCicdDbContext --output-dir Migrations
```
