# 0002: Cookie authentication instead of JWT in the browser

- Status: accepted
- Date: 2026-03-14

## Context

Users sign in with GitHub. After that, the React SPA calls the API and opens a SignalR connection. The SPA and API are served from the same origin (one container). Captured requests can contain other people's secrets, so a stolen session is a real risk.

## Decision

After the GitHub OAuth callback the server issues its own encrypted session cookie:

- `__Host-wi-auth`, `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, 7 days sliding
- The cookie carries the app's user id, login and GitHub id. The GitHub access token is not stored.
- The cookie is encrypted with ASP.NET Core Data Protection keys stored in Postgres, so it survives restarts and deploys.
- Every state-changing request under `/api` and `/auth` (including logout) must carry an antiforgery token in `X-XSRF-TOKEN`. The SPA gets it from `GET /api/antiforgery`, which sets a JavaScript-readable `XSRF-TOKEN` cookie. Validation is a middleware, so a new endpoint cannot forget it.
- API calls without a session get `401`, not a redirect.

## Alternatives considered

- **JWT in localStorage, sent as a bearer token**: any XSS can read and exfiltrate the token, and a JWT cannot be revoked before it expires. It would also need a refresh-token flow.
- **JWT in an HttpOnly cookie**: same CSRF exposure as a session cookie, with none of the benefits of JWT, since there is only one server reading it.
- **Keeping the GitHub access token**: not needed; the app never calls GitHub on the user's behalf after sign-in.

## Consequences

- JavaScript cannot read the session, which limits what an XSS bug could take.
- Cookies bring CSRF risk, covered by `SameSite=Lax` plus the antiforgery token. Both are tested on every state-changing route.
- SignalR uses the same cookie automatically because it runs on the same origin.
- The capture route (`/h/*`) runs in a pipeline branch without authentication, so it never reads or refreshes the cookie, even if a signed-in browser sends to it.
- A signed-in browser that opens its own capture URL sends the session cookie along (`SameSite=Lax` allows top-level GETs, and a `__Host-` cookie must have `Path=/`, so it cannot be scoped away from `/h/`). The capture route therefore removes the app's own cookies from the stored `Cookie` header; otherwise a working session token would end up in the request history. Found during the phase 4 design review.
- Data Protection keys are stored without application-level encryption in v1 (only the database's storage encryption). Anyone with database access could forge cookies. v2 can wrap the keys with Key Vault.
