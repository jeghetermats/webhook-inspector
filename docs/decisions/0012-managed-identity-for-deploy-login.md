# 0012: User-assigned managed identity for the deploy login

- Status: accepted
- Date: 2026-03-22

## Context

GitHub Actions has to log in to Azure to deploy. The plan uses OIDC (federated credentials), so no long-lived Azure secret is stored in GitHub. The usual way is an Entra ID app registration with a federated credential. The Azure for Students subscription lives in NTNU's tenant, where students are not allowed to create app registrations ("Insufficient privileges").

## Decision

Use a user-assigned managed identity, `id-github-deploy`, created in the project's resource group:

- Federated credential with subject `repo:<owner>/<repo>:environment:production`
- Role: Contributor on `rg-webhook-inspector` only
- GitHub stores only its client id, tenant id and subscription id, as environment variables (not secrets)

## Why the subject names the environment, not the branch

The deploy jobs run in the GitHub environment `production`. For such jobs GitHub puts the environment, not the branch, in the token's subject, so a credential for `ref:refs/heads/main` would be rejected. The environment's deployment branch rule allows only `main`, which keeps the "only main deploys" guarantee.

## Alternatives considered

- **App registration**: not allowed in the NTNU tenant.
- **Service principal secret stored in GitHub**: long-lived credential that can leak and must be rotated; also needs an app registration.

## Consequences

- No Azure secret exists in GitHub. A token is only issued to workflow runs in this repository's `production` environment.
- The identity can manage resources in the resource group but nothing else in the subscription. Resource providers had to be registered once by the subscription owner.
- The identity lives in the same resource group it manages. Deleting the resource group deletes the deploy login too, which is acceptable for this project.
- Renaming or moving the repository requires updating the federated credential's subject.
