# 0011: GitHub OAuth app (not a GitHub App) for sign-in

- Status: accepted
- Date: 2026-03-15

## Context

Users sign in with GitHub. The app only needs to know who the user is: their numeric GitHub id and login, read once during the sign-in callback. It never calls the GitHub API on the user's behalf afterwards and does not store the GitHub access token (see 0002).

GitHub offers two kinds of integration that can do this: OAuth apps and GitHub Apps. GitHub's documentation generally prefers GitHub Apps.

## Decision

Use an OAuth app with no scopes. One registration for local development (callback `http://localhost:5173/signin-github`) and one for production (callback on the Container App's URL).

## Alternatives considered

- **GitHub App with no permissions**: GitHub's main arguments for GitHub Apps are fine-grained permissions and short-lived tokens. Neither applies here, because the app requests no access and throws the token away after sign-in. The GitHub App consent screen also lists "Act on your behalf", which is more alarming than an OAuth app's "limited access to public information" for an app that only wants a username. It also adds setup (permissions, installation settings, private key) that sign-in does not use.

## Consequences

- Simple setup: a client id and secret per environment.
- No scopes means the token can only read public profile data, so a leaked token in transit would expose nothing private.
- Both kinds of integration use the same `login/oauth/authorize` web flow, and the sign-in package (`AspNet.Security.OAuth.GitHub`) works with either. Switching to a GitHub App later means new configuration values, not new code.
- Revisit this if a later feature calls the GitHub API for the user, for example creating repository webhooks automatically. Fine-grained GitHub App permissions would then be the better fit.
