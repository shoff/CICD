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
`trigger_state`, `users`, `settings`. `vcs_roots.properties` is `text`, not `jsonb`: it holds encrypted JSON. `CicdDbContext` in Core is provider-neutral; `PostgresCicdDbContext` in Data adds the jsonb column
types and owns the migrations.

## Real-time

Two hubs. `/hubs/agents` is for agents and requires the agent token. `/hubs/builds` is for browsers and tools:
`BuildUpdated` and `AgentUpdated` are broadcast to everyone, `BuildLog` goes to the group of a build after
`SubscribeToBuild(buildId)`. Log lines carry sequence numbers so a client can catch up with
`GET /api/v1/builds/{id}/log?after=<seq>` and then continue live without gaps or duplicates.

## Settings and secrets

Configuration comes from two places. The connection string, `Server:DataDirectory`, `Server:MigrateOnStartup`,
`Plugins:Directory`, logging and the listen URLs must be readable before the database is, so they stay in
`appsettings`/environment. Everything in `SettingsCatalog.All` (19 keys) is managed in the `settings` table
(`key`, `value`, `is_secret`, `updated_at`, `updated_by`).

- **Provider.** `DatabaseSettingsConfigurationSource` (Cicd.Data) is appended to `builder.Configuration.Sources` in
  `Program.cs` *after* the JSON files and the environment, so stored rows win over both. Its provider reads the table
  with its own short-lived `PostgresCicdDbContext`, before the service container exists.
  `SettingsConfigurationMapper` does the shaping: secrets are decrypted, and list settings become the indexed children
  the options binder expects (`Plugins:Disabled` stored as `a,b` becomes `Plugins:Disabled:0` and `:1`). A row that
  cannot be decrypted is logged once and emitted as an empty string: the key is treated as unset (fail closed) until it
  is re-entered, rather than falling back to its `appsettings` value or default - otherwise losing the key ring would
  silently reinstate a placeholder like `Agents:AuthToken = change-me`.
- **Shadowing a shorter list.** `ConfigurationRoot` unions the children of every provider, so a stored list that is
  shorter than the one in `appsettings` would still show the extra entries. `Program.cs` counts the children each list
  key already has in the lower layers and hands the counts to the source; the mapper then emits an explicit `null` for
  every index the stored row does not fill, which hides the lower value from the binder (a provider returning true from
  `TryGet` with a null value wins).
- **Seeding.** `DatabaseStartup.SeedSettingsAsync` runs before the source is added and inserts one row per cataloged
  key that has none, taking the value from the configuration built so far. It is a first-run operation only: once a
  row exists, `appsettings` and environment values for that key are dead weight.
- **Reload.** `SettingsService.UpdateAsync` validates every value first (nothing is written on any error), writes the
  rows in one `SaveChangesAsync`, then calls `ISettingsReloader.Reload()` - the same provider object, registered as a
  singleton - which re-reads the table and raises the configuration change token. Consumers that take
  `IOptionsMonitor<T>` and read `CurrentValue` per request or per tick therefore see the new value immediately;
  nothing is cached in a constructor. The reload is local: only the instance that handled the save re-reads the table,
  and other instances keep their startup values until they are restarted (there is no cross-instance signal yet).
- **Validation.** Per-key checks (`SettingsService.Validate`) and the cross-field `IdentityProvider` rules
  (`ValidateAudience` needs an `Audience`; `RequireHttpsMetadata` needs https `Authority` and `LoginUrl`) both run
  before any write and throw `SettingsValidationException`, which the API answers with 400. The cross-field rules merge
  the submitted values over the stored ones, because a section can be saved a field at a time. A reload failure after a
  successful write is the plain `InvalidOperationException` and answers 409: the value is stored but not yet live.
- **Restart required.** Four `IdentityProvider` keys (`Authority`, `ValidateAudience`, `Audience`,
  `RequireHttpsMetadata`) and `Plugins:Disabled` are read once while the authentication handlers and the plugin host
  are built, so they are flagged `RestartRequired`. `SettingView.RestartPending` compares the stored value with the
  value the provider loaded at startup, and the settings page shows a banner listing what is waiting.
- **Secrets.** `DataProtectionSecretProtector` wraps a Data Protection provider rooted at
  `<Server:DataDirectory>/keys`; `SecretCodec` gives stored ciphertext the prefix `enc:v1:` so unprefixed legacy
  plaintext still loads. Secret settings are encrypted on write, and the API and UI never echo one back - `SettingView`
  reports a mask plus `IsSet`/`Unreadable`. In an update an empty value for a secret means "leave unchanged" and null
  means "clear it" (the row is kept with an empty value); the settings page sends null when the field's Clear box is
  ticked.
- **VCS root converter.** `HasProtectedJsonConversion` (Core, `CicdDbContext`) is an EF value converter that serializes
  `VcsRoot.Properties` to JSON and then protects it, which is why the column is `text`. `PostgresCicdDbContext` takes
  the `ISecretProtector` in its constructor for that reason. Decoding goes through `ProtectedJson.DecodeProperties`, a
  static method (value converters must be expression trees) that never throws: a row whose ciphertext cannot be read
  loads as an empty dictionary, and `BuildJobFactory` logs `VCS root {Name} has no readable credentials` when it puts
  such a root into a job. `DatabaseStartup.EncryptLegacyVcsRootsAsync` runs on every
  boot and re-saves any row whose value is still plaintext; it is a no-op once they are all encrypted. The migration's
  `Down` is not reversible once rows are encrypted, because ciphertext is not valid `jsonb`.
- **Loss of the key ring** makes every secret unreadable: startup logs one error per row and the settings page shows
  `unreadable, re-enter`. The key files are not themselves encrypted, so they belong in the same backup as the data
  directory.

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
