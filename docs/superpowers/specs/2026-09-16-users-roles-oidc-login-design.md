# Users, roles and OIDC login

Date: 2026-09-16. Status: approved design, not yet implemented.

## Goal

Replace the single static admin token with real users. People authenticate against the company IdentityServer at
`https://identity-dev.manageamerica.com` (Duende, authorization code + PKCE). CICD keeps its own user table and
assigns roles locally, the way TeamCity does. The existing static tokens for agents and for break-glass admin access
stay.

## Decisions already made

| Question | Decision |
| --- | --- |
| Where do roles come from | Local. The IdP proves identity only. |
| Granularity | Three global roles: `viewer` < `developer` < `admin`. Per-project roles are a later feature. |
| Non-browser API access | IdP bearer JWTs, mapped to the same local user. `Security:ApiToken` stays as an admin break-glass credential. |
| Who can log in | Any IdP user. New users get `viewer`. Emails in `Security:BootstrapAdmins` get `admin`. |
| Scheme wiring | One policy scheme that forwards each request to cookie, JwtBearer or the static token handler. |
| OIDC not configured | Server behaves as today (open UI and API, static tokens work) and logs a warning at startup. |

## Authentication

### Schemes

`AddCicdSecurity` registers five schemes:

| Scheme | Handler | Used when |
| --- | --- | --- |
| `Smart` (default) | policy scheme | Always. Forwards to one of the three below. |
| `Cookies` | cookie handler | Browser sessions after OIDC login. |
| `Bearer` | JwtBearer | `Authorization: Bearer <jwt>`, or `access_token` query on `/hubs/*`, when the token has three dot-separated segments. |
| `Token` | existing `TokenAuthenticationHandler` | Any other bearer value, `X-Api-Key`, or `access_token` query on `/hubs/*`. |
| `oidc` | OpenIdConnect | Challenge only. Never authenticates a request by itself. |

Forward selector, in order: bearer or hub `access_token` present and looks like a JWT -> `Bearer`; any other bearer,
`X-Api-Key` or hub `access_token` -> `Token`; otherwise `Cookies`. When OIDC is not configured the selector never
returns `Bearer` and the `oidc` scheme is not registered.

JwtBearer validates against the authority's discovery document and JWKS. Issuer comes from discovery. Audience
validation is off unless `Oidc:ValidateAudience` is true, because the IdP has no API resource for CICD yet.
`MapInboundClaims` is false so claim types stay `sub`, `name`, `email`, `username`.

Cookie: `LoginPath=/login`, sliding expiration, 8 hours. `OnRedirectToLogin` returns 401 for `/api` and `/hubs`
and redirects everything else. `OnRedirectToAccessDenied` returns 403 for `/api` and `/hubs`, redirects to
`/access-denied` otherwise.

### Login and logout endpoints

- `GET /login?returnUrl=` challenges `oidc` with the return URL (validated as a local URL). When OIDC is not
  configured it returns 404.
- `POST /logout` signs out `Cookies` and `oidc` (end-session at the IdP, then back to `/`). Requires the antiforgery
  token; the layout renders a form.
- OIDC callback paths use the framework defaults `/signin-oidc` and `/signout-callback-oidc`.

### Local user upsert

`UserService` in Core (interface `IUserService` is not needed; the class is the seam, matching how `AgentService`
is used). `EnsureUserAsync(subject, issuer, username, email, displayName)`:

1. Find by `(issuer, subject)`. If missing, create with `Role = Viewer`, `CreatedAt = now`.
2. Update `Username`, `Email`, `DisplayName` when they changed; set `LastSeenAt = now`.
3. If `Email` matches an entry in `Security:BootstrapAdmins` (case-insensitive) and `Role != Admin`, set `Admin`.
   Bootstrap only promotes; it never demotes.
4. Save and return the user.

Called from the OIDC `OnTokenValidated` event (browser) and from the claims transformation for `Bearer` (API).
`LastSeenAt` is written at most once per 5 minutes per user to avoid a write per request.

### Claims transformation

`LocalUserClaimsTransformation : IClaimsTransformation` runs after authentication for every scheme.

- Identity from `Token` scheme (static tokens): unchanged, returned as is.
- Identity with a `sub` claim (`Cookies` or `Bearer`): load the local user by `(issuer, sub)`, creating it through
  `EnsureUserAsync` if absent. Add claims `cicd:user_id`, `cicd:role` (unless disabled) and `ClaimTypes.Role` with
  the role name. `ClaimTypes.Name` is set to display name, falling back to username, then email.
- Disabled user: no role claims are added, so every role policy fails. A claim `cicd:disabled=true` lets the UI show
  the right message.
- The transformation stores the result on `HttpContext.Items` so it runs once per request even if authorization
  evaluates the principal several times.

Role changes and disable take effect on the next request without re-login.

## Authorization

### Roles and policies

```csharp
public enum UserRole { Viewer = 0, Developer = 1, Admin = 2 }   // Cicd.Contracts
```

| Policy | Satisfied by |
| --- | --- |
| `Viewer` | any of `viewer`, `developer`, `admin` role claims |
| `Developer` | `developer` or `admin` |
| `Admin` | `admin` |
| `Agent` | `agent` (unchanged) |

The static API token yields role `admin` and therefore passes all three. `AdminRequirementHandler` is replaced by a
single `RoleRequirement(minimum)` handler that also succeeds for everything when OIDC is not configured (today's open
mode), so the open-mode shortcut lives in one place.

### Endpoint mapping

| Route group | Policy |
| --- | --- |
| `GET /api/v1/builds`, `/queue`, `/{id}`, `/{id}/steps`, `/{id}/log`, `/{id}/log.txt`, `/{id}/artifacts`, `/{id}/artifacts/{path}` | Viewer |
| `POST /api/v1/builds/{id}/cancel` | Developer |
| `POST /api/v1/builds/{id}/artifacts` | Agent |
| `GET` on projects, vcs-roots, build-configurations, pull-requests | Viewer |
| `POST`/`PUT`/`DELETE` on projects, vcs-roots, build-configurations; `/test`, `/pause`, `/queue`; `POST /pull-requests/refresh` | Developer |
| `GET /api/v1/agents`, `/{id}`, `/{id}/compatibility/{configurationId}` | Viewer |
| `POST /api/v1/agents/{id}/authorize`, `/enable`; `DELETE` | Admin |
| `GET /api/v1/plugins/*` | Admin |
| `GET /api/v1/users`, `PUT /api/v1/users/{id}/role`, `PUT /api/v1/users/{id}/disabled` | Admin |
| `/api/v1/webhooks/*`, `/health` | anonymous |
| `/hubs/agents` | Agent |
| `/hubs/builds` | Viewer |
| Blazor pages | Viewer through the fallback policy on `MapRazorComponents`; `/users` page is Admin |
| `/login`, `/logout`, `/access-denied`, `/openapi/*`, `/api/docs` | anonymous (`/logout` requires antiforgery) |

The API is reorganized so the policy is applied per endpoint rather than per group where a group mixes read and write.

### UI gating

Pages get the cascading authentication state (`AddCascadingAuthenticationState`). Action buttons render inside
`<AuthorizeView Policy="Developer">` or `Admin`. The page code also calls `IAuthorizationService.AuthorizeAsync`
before performing the action, so the hidden button is not the only guard.

Disabled users land on `/access-denied` with a "your account is disabled" message; users with an insufficient role
see "you need the X role, ask an administrator".

## Data model

New table `users`:

| Column | Type | Notes |
| --- | --- | --- |
| `id` | uuid | primary key |
| `issuer` | text | IdP issuer URL |
| `subject` | text | IdP `sub` |
| `username` | text | from `username` claim, may be null |
| `email` | text | may be null |
| `display_name` | text | from `name`, may be null |
| `role` | text | `Viewer`, `Developer`, `Admin` stored as string |
| `disabled` | boolean | default false |
| `created_at`, `last_seen_at` | timestamptz | |

Unique index on `(issuer, subject)`. Index on `email`. Entity `User` in `Cicd.Core.Entities`, added to
`CicdDbContext`; migration `AddUsers` in `Cicd.Data`.

## Configuration

```json
"Oidc": {
  "Authority": "https://identity-dev.manageamerica.com",
  "ClientId": "",
  "ClientSecret": "",
  "Scopes": "openid profile email",
  "ValidateAudience": false,
  "Audience": "",
  "RequireHttpsMetadata": true
},
"Security": {
  "ApiToken": "",
  "BootstrapAdmins": []
}
```

`OidcOptions.IsConfigured => !string.IsNullOrEmpty(Authority) && !string.IsNullOrEmpty(ClientId)`. Startup logs a
warning when it is false: "Oidc is not configured: the UI and API are open." The existing `Security:ApiToken`
warning is kept only for the not-configured case.

`Server:PublicUrl` is already present and is what the IdP redirect URIs must match.

## UI

- `MainLayout` sidebar footer: signed-in display name and a Sign out form, or a Sign in link when OIDC is configured
  and the user is anonymous.
- New page `/users` (Admin): table of users with username, email, role select, last seen, and an Enable/Disable
  button. Changing a role or disabling an admin's own account is refused with a message.
- New page `/access-denied`.
- API endpoints for the same operations so the users page and external tooling share `UserService`.

## Out of scope

Per-project roles, groups, personal access tokens, self-service profile page, audit log of role changes, IdP-driven
roles. The `Oidc` client registration at IdentityServer is an operational task outside this repo; see below.

## IdP registration (operational prerequisite)

A client in IdentityServer for CICD:

- Client id: `cicd` (or whatever is assigned; goes in `Oidc:ClientId`).
- Grant: authorization code with PKCE. Client secret optional; the code supports both.
- Redirect URIs: `http://localhost:5000/signin-oidc` for local development, plus `<Server:PublicUrl>/signin-oidc`.
- Post-logout redirect URIs: same hosts with `/signout-callback-oidc`.
- Scopes: `openid profile email`.

Until this exists the OIDC round trip cannot be exercised. Everything else is testable.

## Testing

Unit tests in `tests/Cicd.Core.Tests` (SQLite in-memory through the provider-neutral `CicdDbContext`):

- `UserService.EnsureUserAsync`: creates as viewer; updates changed profile fields; bootstrap email promotes to admin
  and never demotes; `LastSeenAt` throttling.
- `LocalUserClaimsTransformation`: adds role claims for an active user; adds no role for a disabled user; passes the
  static token identity through untouched; runs once per request.
- `RoleRequirementHandler`: each policy against each role; open mode when OIDC is not configured; static admin token.
- Scheme forward selector: JWT-shaped bearer -> Bearer, opaque bearer -> Token, `X-Api-Key` -> Token, hub query token
  both shapes, nothing -> Cookies.
- `UserService` guards: cannot demote or disable the last admin; cannot change own role.

Manual verification once the IdP client exists: browser login and logout, role change takes effect without
re-login, disabled user is blocked, curl with static token, curl with an IdP access token, `/hubs/builds` with a JWT
as the query token. Verification commands and expected results go in the README security section.

## Documentation updates

`README.md` configuration table and security paragraph, `docs/ARCHITECTURE.md` security model section,
`HANDOFF.md` gap list item 2.
