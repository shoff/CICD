# Database-backed settings and credentials at rest

Date: 2026-09-16. Status: approved design.

## Goal

All server configuration lives in PostgreSQL and is edited from an Admin → Settings page, with secrets encrypted at
rest. `appsettings` keeps only what is needed to reach the database and boot. VCS root credentials (git password,
GitHub token) are encrypted at rest with the same mechanism. This closes handoff gap 3 and is the foundation for the
"create project from repo" wizard, which stores a personal access token per project.

## Decisions

| Question | Decision |
| --- | --- |
| Scope | Everything except `ConnectionStrings:Cicd`, `Server:DataDirectory`, `Server:MigrateOnStartup`, listen URLs (`ASPNETCORE_URLS`/Kestrel), `Logging`, `AllowedHosts`, `Plugins:Directory`. |
| Precedence | After first-run seeding the database is the only source for managed keys. `appsettings` and environment values for those keys are ignored. |
| Mechanism | A custom `IConfigurationProvider` reads the `settings` table and is layered last, so every existing `IOptions<T>` / `IConfiguration` consumer, including plugins, keeps working. |
| Live vs restart | Consumers that read per operation switch to `IOptionsMonitor<T>` and pick changes up on the next use. Keys consumed once at startup are flagged restart-required and the page shows a banner while the running value differs from the stored one. |
| Encryption | ASP.NET Core Data Protection. Keys persisted under `<Server:DataDirectory>/keys` with application name `cicd`, shared by every instance through the data volume. |
| VCS root credentials | The whole `Properties` JSON of a VCS root is encrypted through the EF value converter. Readers see plaintext. |

## Data model

Table `settings`:

| Column | Type | Notes |
| --- | --- | --- |
| `key` | text, primary key | the configuration path, e.g. `GitHub:Token` |
| `value` | text | plaintext, or `enc:v1:<ciphertext>` when `is_secret` |
| `is_secret` | boolean | |
| `updated_at` | timestamptz | |
| `updated_by` | text, nullable | display name or `api-token` |

`vcs_roots.properties` changes from `jsonb` to `text`; content becomes `enc:v1:<ciphertext of the JSON>`. Rows that
still hold plain JSON (no prefix) load as legacy plaintext and are encrypted on their next save.

## Catalog

`SettingsCatalog` in Core is the single list of managed keys. Each `SettingDefinition` has `Key`, `Section`,
`DisplayName`, `Description`, `Kind` (`Text`, `Number`, `Boolean`, `Secret`, `List`), `RestartRequired`. Only
cataloged keys can be read or written through the settings service, API and page.

| Section | Keys | Kind | Restart |
| --- | --- | --- | --- |
| Server | `Server:PublicUrl` | Text | no |
| Server | `Server:DispatchIntervalSeconds`, `Server:TriggerPollIntervalSeconds`, `Server:PullRequestPollIntervalSeconds` | Number | no |
| Agents | `Agents:AuthToken` | Secret | no |
| Agents | `Agents:AutoAuthorize` | Boolean | no |
| Security | `Security:ApiToken` | Secret | no |
| Security | `Security:BootstrapAdmins` | List | no |
| IdentityProvider | `IdentityProvider:Authority` | Text | yes |
| IdentityProvider | `IdentityProvider:LoginUrl`, `IdentityProvider:ReturnUrl` | Text | no |
| IdentityProvider | `IdentityProvider:ValidateAudience`, `IdentityProvider:RequireHttpsMetadata` | Boolean | yes |
| IdentityProvider | `IdentityProvider:Audience` | Text | yes |
| IdentityProvider | `IdentityProvider:TimeoutSeconds` | Number | no |
| GitHub | `GitHub:Token`, `GitHub:WebhookSecret` | Secret | no |
| GitHub | `GitHub:ApiBaseUrl` | Text | no |
| Plugins | `Plugins:Disabled` | List | yes |

List values are stored comma-separated in one row and expanded by the provider into `Key:0`, `Key:1`, ... so the
configuration binder sees a normal array.

## Components

- **Core / `Cicd.Core.Settings`**: `ISecretProtector` (`Protect`, `Unprotect`), `NullSecretProtector`, `SecretCodec`
  (adds/strips the `enc:v1:` prefix and passes legacy plaintext through), `Setting` entity, `SettingsCatalog`,
  `SettingsConfigurationMapper` (rows → flat configuration dictionary, decrypting secrets, expanding lists),
  `SettingsSeeder` (computes the rows to insert for cataloged keys missing from the table, from the current
  `IConfiguration`), `ISettingsReloader`, `SettingsService` (read all with masking; validate and write; restart-pending
  detection by comparing stored values with the values the running host loaded at startup).
- **Core / persistence**: `CicdDbContext(DbContextOptions, ISecretProtector)`; `Settings` DbSet; `Properties`
  converter uses `SecretCodec`; an `IModelCacheKeyFactory` keyed on the protector instance so tests with different
  protectors do not share a cached model.
- **Data**: `DatabaseSettingsConfigurationSource`/`Provider` (EF query through `PostgresCicdDbContext`, `Reload()`
  re-reads and raises change), `DatabaseStartup.MigrateAsync(connectionString)` for pre-host migration, migration
  `AddSettingsAndProtectVcsRootProperties` (hand-written `ALTER COLUMN ... TYPE text USING properties::text`).
- **Server**: `DataProtectionSecretProtector` over `IDataProtectionProvider` with purpose `Cicd.Secrets`;
  `Program.cs` startup order: read connection string and data directory → create the standalone Data Protection
  provider over `<data>/keys` → migrate → seed → add the database configuration source → register services →
  build. `AddDataProtection().PersistKeysToFileSystem(<data>/keys).SetApplicationName("cicd")` so antiforgery and
  cookies share the same key ring across instances. Settings API `GET /api/v1/settings` and
  `PUT /api/v1/settings` (Admin; secrets masked on read, empty means unchanged on write). Consumers switched to
  `IOptionsMonitor<T>`: `TokenAuthenticationHandler`, `RoleRequirementHandler`, `UserService`, `V21LoginClient`,
  `AuthEndpoints`, `MainLayout`, `Login.razor`, `PullRequestStatusNotifier`; `BuildDispatcher`, `TriggerService`,
  `PullRequestService` read their interval on every loop iteration.
- **UI**: sidebar gains an "Admin" group (Admin policy) with Settings, Users, Agents, Plugins; those three links leave
  the main navigation, their routes are unchanged. `/admin/settings`: one card per section generated from the
  catalog, inputs by kind (secrets masked with a replace field, lists as comma-separated text, booleans as checkboxes),
  save per section with validation messages, a banner listing restart-required keys whose stored value differs from
  the running value.

## Error handling

Validation happens in `SettingsService` before any write: numbers must parse and be ≥ 0, booleans must be true/false,
unknown keys are rejected, restart-required keys are accepted with the banner. Rejected values return the message to
the page and to the API as 400 for validation, 409 when the rows were saved but the running configuration could not be
reloaded. A missing key ring directory is created on startup; a lost key ring makes
stored secrets unreadable (logged as an error naming the key) and the page shows them as "unreadable, re-enter".

## Testing

Core (SQLite): `SecretCodec` prefix handling and legacy pass-through with a fake reversible protector;
`SettingsCatalog` sanity (unique keys, every key has a section); `SettingsConfigurationMapper` list expansion and
secret decryption; `SettingsSeeder` computes only missing keys and joins configuration arrays; `SettingsService` read
masking, validation failures, write + reload call, restart-pending detection; `CicdDbContext` VCS root round trip
proving the stored column carries the prefix and a legacy plaintext row loads. Server: `DataProtectionSecretProtector`
round trip with `EphemeralDataProtectionProvider`. Smoke: boot against PostgreSQL, table seeded from `appsettings`,
`PUT /api/v1/settings` changes `Agents:AuthToken` and the agent token path honors it without restart.

## Documentation

README "Configuration" rewritten: what stays in `appsettings`, first-run seeding, the settings page and API, key
storage and backup. ARCHITECTURE gains a "Settings and secrets" section. HANDOFF gap 3 closes; a note explains the
`vcs_roots.properties` column change.

## Out of scope

Settings history/audit log, per-environment overrides, importing/exporting settings, rotating Data Protection keys.
