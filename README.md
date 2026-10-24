# ChatApp API

ASP.NET Core (.NET 10) Web API for a chat application: local registration/login, OpenID Connect login
with external providers, OAuth 2.0 token endpoint issuing JWTs, and friend requests.

Stack: ASP.NET Core Identity, EF Core (SQL Server), JWT bearer auth, OpenID Connect handler.

## Run

```bash
dotnet tool restore
dotnet run --project src/ChatApp.Api --launch-profile https
```

Requires SQL Server. The default connection string (`ConnectionStrings:Default`) points at the local default
instance with Windows authentication and database `ChatApp`; override it with user-secrets or the
`ConnectionStrings__Default` environment variable for other servers (e.g. LocalDB:
`Server=(localdb)\\MSSQLLocalDB;Database=ChatApp;Trusted_Connection=True`).

In Development the database is created/migrated on startup and the OpenAPI document is at
`/openapi/v1.json`. Sample requests are in [ChatApp.Api.http](src/ChatApp.Api/ChatApp.Api.http).

Swagger UI is at `https://localhost:7129/swagger`. To call the protected endpoints, click **Authorize** and either
paste an `access_token` into **Bearer**, or enter a user name and password under **OAuth2Password** (Swagger then
calls `/connect/token` for you). The login is kept across page reloads.

## Configuration

| Key | Purpose |
| --- | --- |
| `Jwt:SigningKey` | HMAC key, ≥ 32 chars. A dev key is in `appsettings.Development.json`; use user-secrets or a vault elsewhere. |
| `Jwt:Issuer`, `Jwt:Audience`, `Jwt:AccessTokenMinutes`, `Jwt:RefreshTokenDays` | Token settings. |
| `ExternalAuth:Providers:<Name>` | OIDC providers (`Authority`, `ClientId`, `ClientSecret`, optional `Scopes`). Providers without a `ClientId` are skipped. |
| `ExternalAuth:AllowedRedirectUris` | Client URIs allowed to receive the authorization code after external login. |
| `Cors:AllowedOrigins` | Browser origins allowed to call the API. |

Set provider secrets with user-secrets, e.g.:

```bash
dotnet user-secrets --project src/ChatApp.Api set "ExternalAuth:Providers:Google:ClientId" "<id>"
```

Register `https://localhost:7129/signin-<provider-lowercase>` (e.g. `/signin-google`) as the redirect URI
in the provider's console.

## Endpoints

### Auth

| Method | Route | Notes |
| --- | --- | --- |
| POST | `/api/auth/register` | `{ email, userName, password, displayName? }` → 201 + tokens |
| POST | `/api/auth/login` | `{ userNameOrEmail, password }` → tokens. 5 failures lock the account for 15 min. |
| POST | `/connect/token` | OAuth 2.0 token endpoint (form-encoded): `password`, `refresh_token`, `authorization_code` grants |
| POST | `/api/auth/logout` | Revokes the given refresh token, or all of the user's tokens if none given |
| GET | `/api/auth/me` | Current user |
| GET | `/api/auth/providers` | Configured OIDC providers |
| GET | `/api/auth/external/{provider}` | Starts OIDC login (`redirect_uri`, `state`, `code_challenge`, `code_challenge_method=S256`) |

Token responses follow RFC 6749: `access_token`, `token_type`, `expires_in`, `refresh_token`.
Refresh tokens are opaque, stored hashed, single-use (rotated on every refresh); replaying a used one revokes
all of that user's sessions.

### External (OIDC) login flow

1. Client opens `/api/auth/external/google?redirect_uri=<registered uri>&state=<random>&code_challenge=<S256>&code_challenge_method=S256`.
2. The API runs the authorization code + PKCE flow with the provider, then finds the local user by external
   login, or links/creates one. An existing account is linked by email only when both the provider and the
   local account have verified the email.
3. The API redirects to `redirect_uri?code=...&state=...` (or `?error=access_denied&...`).
4. Client posts `grant_type=authorization_code&code=...&redirect_uri=...&code_verifier=...` to `/connect/token`.

In Development, omitting `redirect_uri` returns tokens directly from the callback, so the flow can be tried
in a browser.

### Friends (requires `Authorization: Bearer <token>`)

| Method | Route | Notes |
| --- | --- | --- |
| POST | `/api/friends/requests` | `{ userName }` → 201. 409 if already friends or a request is pending either way. |
| GET | `/api/friends/requests?direction=incoming\|outgoing` | Pending requests |
| GET | `/api/friends/requests/{id}` | A request you sent or received |
| POST | `/api/friends/requests/{id}/accept` | Receiver only |
| POST | `/api/friends/requests/{id}/decline` | Receiver only |
| POST | `/api/friends/requests/{id}/cancel` | Sender only |
| GET | `/api/friends` | Your friends |
| DELETE | `/api/friends/{friendId}` | Unfriend |

## Production notes

- Authorization codes are kept in `IMemoryCache`; use a distributed cache when running more than one instance.
- Apply migrations as a deployment step (`dotnet ef database update`) instead of on startup.
