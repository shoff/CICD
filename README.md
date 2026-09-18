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

The first start against an empty database seeds the managed settings from configuration (see
[Configuration](#configuration)), so `Agents__AuthToken` above only has an effect on that first run. After that the
token lives in the `settings` table; change it on Admin -> Settings (or via the API).

In the Development environment `appsettings.Development.json` points at identity-dev and requires a sign-in. To run in
open mode as the curl examples below assume, clear the authority on Admin -> Settings (or with
`PUT /api/v1/settings`) and restart the server; setting `IdentityProvider__Authority=` in the environment only has an
effect on the very first start.

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

Configuration lives in two places.

**Startup keys** are needed before the database can be read, so they stay in `appsettings.json` or the environment
(`__` separators, e.g. `ConnectionStrings__Cicd`):

| Key | Meaning |
| --- | --- |
| `ConnectionStrings:Cicd` | PostgreSQL connection string. Required. |
| `Server:DataDirectory` | Artifacts, and the Data Protection key ring in `<Server:DataDirectory>/keys`. |
| `Server:MigrateOnStartup` | Apply EF Core migrations on start. Default `true`. |
| `Plugins:Directory` | Plugin root scanned at startup. |
| `Logging:*` | Log levels. |
| `ASPNETCORE_URLS` (or `--urls`) | Listen addresses. |

**Managed settings** live in the `settings` table. On the first start every managed key that has no row yet is seeded
from the current configuration (`appsettings.json`, `appsettings.<Environment>.json`, environment variables). From then
on the database wins: **changing a managed key through the environment or `appsettings` has no effect** - the database
configuration source is added last and overrides both. Edit them at Admin -> Settings (admins only) or through
`GET /api/v1/settings` and `PUT /api/v1/settings`. A change takes effect on the instance that saved it as soon as it is
saved, unless the Restart column says otherwise; the settings page shows a banner while a restart is pending.

**Multiple instances.** A change reloads only the instance that handled the save. Other instances keep the values they
read at startup until they are restarted - there is no cross-instance signal yet. Restart the rest of the pool after a
change that matters, or run one server.

Because seeding only happens once, `appsettings.Development.json` (identity-dev authority, the bootstrap admin,
auto-authorize) only matters on the very first start against an empty database.

### Managed settings

| Key | Meaning | Restart |
| --- | --- | --- |
| `Server:PublicUrl` | Used in commit status links. | no |
| `Server:DispatchIntervalSeconds` | How often queued builds are assigned to agents. | no |
| `Server:TriggerPollIntervalSeconds` | How often VCS and other triggers are polled. | no |
| `Server:PullRequestPollIntervalSeconds` | How often open pull requests are refreshed. | no |
| `Agents:AuthToken` | Shared secret agents must present. Secret. | no |
| `Agents:AutoAuthorize` | Authorize agents on first registration. Development only. | no |
| `Security:ApiToken` | Static bearer token that acts as an admin. Break-glass and automation. Empty disables it. Secret. | no |
| `Security:BootstrapAdmins` | Emails promoted to admin on every sign-in. Set at least one before the first login. | no |
| `IdentityProvider:Authority` | Issuer base URL of the company identity provider. Empty runs the server in open mode (no login; UI and API open unless `Security:ApiToken` is set). | **yes** |
| `IdentityProvider:LoginUrl` | The username/password endpoint. Empty derives `{Authority}/api/v21/accountv21/login`. | no |
| `IdentityProvider:ReturnUrl` | Sent to the login endpoint as `ReturnUrl`; it requires a value but CICD never follows it. | no |
| `IdentityProvider:ValidateAudience` | Validate the audience of API bearer tokens. Off until the IdP has an API resource for CICD. | **yes** |
| `IdentityProvider:Audience` | Expected audience when validation is on. | **yes** |
| `IdentityProvider:RequireHttpsMetadata` | Default `true`. Requires https for the login endpoint and the discovery document. Turning it off also allows credentials to be posted over plain http. | **yes** |
| `IdentityProvider:TimeoutSeconds` | Default `15`. Timeout for the call to the login endpoint. | no |
| `GitHub:Token` | Default token for pull request discovery and status publishing. A VCS root property `github.token` overrides it. Secret. | no |
| `GitHub:WebhookSecret` | If set, webhooks must carry a valid `X-Hub-Signature-256`. Secret. | no |
| `GitHub:ApiBaseUrl` | GitHub REST API base. | no |
| `Plugins:Disabled` | Plugin ids to skip even if present on disk. | **yes** |

The keys marked **yes** are read once while the authentication handlers and the plugin host are built; everything else
is read through `IOptionsMonitor` and picks up a change on the next request or the next background tick.

### Secrets at rest

Settings marked secret above, and VCS root properties (`vcs_roots.properties`), are encrypted with ASP.NET Core Data
Protection before they are written. The API and the settings page never return a stored secret: they report only
whether one is set. Because a stored secret is never echoed back, leave a secret blank to keep it as it is; to remove
one, tick **Clear** next to the field on the settings page, or send `null` for that key to `PUT /api/v1/settings`.

The key ring is a set of XML files in `<Server:DataDirectory>/keys`, **unencrypted on disk** (there is no DPAPI on
Linux and no certificate is configured), so protect that directory with filesystem permissions and **back it up with
the data directory**. It is load-bearing: without the matching keys every stored secret and every VCS root credential
becomes unreadable and has to be re-entered. The server logs one error per unreadable key at startup
(`Setting {Key} cannot be decrypted; nothing will match it until it is re-entered`) and the settings page shows
`unreadable, re-enter` in the field. An unreadable secret neither falls back to whatever `appsettings` or the
environment holds nor reads as empty - an empty `Security:ApiToken` would mean open mode and an empty
`GitHub:WebhookSecret` would skip signature checks. It is replaced by a random value nobody can present, so the API
token, agent token and webhook signature all fail closed until the value is re-entered. If the static API token was
the only way in, sign in through the identity provider, or restore the key ring, to re-enter it. An unreadable
VCS root credential set loads as no properties at all. The server log names each such root at startup
(`VCS root {Name} has credentials that cannot be decrypted`). Nothing rewrites the stored ciphertext, so restoring the
key ring brings the credentials back; otherwise recreate the root, since there is no edit for an existing one yet.

The former `Oidc` section is gone. Set `IdentityProvider:Authority`; with it empty the server runs in open mode (or
token-only mode when `Security:ApiToken` is set).

Agent: `Agent:ServerUrl`, `Agent:Name`, `Agent:AuthToken`, `Agent:WorkDirectory`, `Agent:Capabilities` (extra key/values).
The agent has no database, so all of its configuration is `appsettings`/environment.

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
`Security:ApiToken` is a static admin credential for automation; set it on Admin -> Settings (or via the API),
because an environment value only seeds the very first start. Agents use `Agents:AuthToken` only.
Until an API resource for CICD exists at the IdP and `IdentityProvider:ValidateAudience` is on, any access token identity-dev
issued to any application authenticates to CICD as that user. Enable audience validation as soon as the resource
is registered; the audience keys need a server restart to take effect.

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
- **Secret rotation.** Stored secrets and VCS root credentials are encrypted at rest with Data Protection, but the
  key ring is unencrypted on disk and there is no re-encryption command if it is lost or rotated by hand.
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
