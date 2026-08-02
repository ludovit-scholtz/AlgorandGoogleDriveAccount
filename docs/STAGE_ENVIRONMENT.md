# Stage environment and the stage → production promotion flow

Both `BiatecMCP` and `BiatecOIDC` now deploy through two separate pipelines instead of one:

1. **`.github/workflows/deploy-stage.yml`** — runs automatically on every push to `master`. Builds
   both Docker images, pushes them to Docker Hub, and deploys them to the **stage** environment
   only (`k8s/stage/*`). Production is never touched by this workflow.
2. **`.github/workflows/promote-production.yml`** — runs only when a human triggers it (Actions tab
   → **Run workflow**, or `gh workflow run promote-production.yml -f version=... -f service=...`).
   It does not build anything — it takes an image tag that stage is already running and re-deploys
   that *exact same image* to production (`k8s/main/*`).

This is the standard "build once, promote the same artifact" pattern: nothing pushed to `master` is
ever visible to real users until someone deliberately promotes it, and what ships to production is
byte-identical to what was already tested in stage (not a fresh rebuild that could differ).

## Hosts

| Environment | BiatecMCP | BiatecOIDC |
|---|---|---|
| Production | `https://mcp.biatec.io` (+ legacy alias `google.biatec.io`) | `https://oidc.biatec.io` (+ legacy alias paths on `google.biatec.io`, see `CICD_GITHUB_ACTIONS.md`) |
| Stage | `https://stage.mcp.biatec.io` (+ legacy alias `stage.google.biatec.io`) | `https://stage.oidc.biatec.io` |

You need to create the `stage.mcp.biatec.io`, `stage.google.biatec.io`, and `stage.oidc.biatec.io` DNS
records yourself (same as you did for `oidc.biatec.io`/`mcp.biatec.io`) — this repo has no way to do that
for you. Once DNS resolves and the ingress is applied, `cert-manager` issues TLS certificates for all of
them automatically via the same `letsencrypt` `ClusterIssuer` already used in production. BiatecMCP's
`mcp.biatec.io`/`stage.mcp.biatec.io` hosts are also its canonical OAuth resource identity
(`Mcp:CanonicalResourceUri`) — BiatecOIDC's `JwtIssuer:ProtectedResources` must list the matching URI
(`https://mcp.biatec.io/mcp` / `https://stage.mcp.biatec.io/mcp`) for BiatecMCP to accept tokens BiatecOIDC
issues; see the repo root `CLAUDE.md`'s "Kubernetes / ingress routing" section.

## Kubernetes resources

Stage deliberately lives in the **same `biatec` namespace** as production, not a separate
namespace — every resource just has a `-stage` suffix so it can never collide with or be mistaken
for a production resource:

| Resource type | Production | Stage |
|---|---|---|
| Deployment | `biatec-mcp-app-deployment` / `biatec-oidc-app-deployment` | `biatec-mcp-stage-app-deployment` / `biatec-oidc-stage-app-deployment` |
| Service | `biatec-mcp-service` / `biatec-oidc-service` | `biatec-mcp-stage-service` / `biatec-oidc-stage-service` |
| Ingress | `biatec-mcp-ingress` + `biatec-mcp-domain-ingress`, `biatec-oidc-ingress` + `biatec-oidc-domain-ingress` | `biatec-mcp-stage-ingress` + `biatec-mcp-stage-domain-ingress`, `biatec-oidc-stage-ingress` |
| ConfigMap | `biatec-mcp-conf` / `biatec-oidc-conf` | `biatec-mcp-stage-conf` / `biatec-oidc-stage-conf` |
| Manifest files | `k8s/main/deployment-mcp.yaml`, `k8s/main/deployment-oidc.yaml`, `k8s/main/conf-mcp/`, `k8s/main/conf-oidc/` | `k8s/stage/deployment-mcp-stage.yaml`, `k8s/stage/deployment-oidc-stage.yaml`, `k8s/stage/conf-mcp-stage/`, `k8s/stage/conf-oidc-stage/` |

Because the existing CI `Role` (see `KUBE_CONFIG_SECURITY.md`) grants verbs on resource *types*
(`deployments`, `services`, `configmaps`, `ingresses`), not specific resource *names*, the same
`KUBE_CONFIG` secret and Role already used for production also covers every stage resource above —
**no new secret, Role, or RoleBinding was needed** to add stage. This is the main practical benefit
of the "same namespace" choice over a fully separate namespace.

`BiatecOIDC` needs a second Ingress object (`biatec-oidc-domain-ingress`/`-stage`) to carve out its
dedicated host alongside a legacy alias on the shared `google.biatec.io` host it doesn't own outright.
`BiatecMCP` needs the same split for the same reason: `biatec-mcp-ingress`/`-stage` is a legacy
catch-all on the shared `google.biatec.io`/`stage.google.biatec.io` host, and
`biatec-mcp-domain-ingress`/`-stage` is its own dedicated `mcp.biatec.io`/`stage.mcp.biatec.io` host —
its canonical OAuth resource identity. Both dedicated-host Ingress objects are each a single full
catch-all (`/(.*)` + `rewrite-target: /$1`) straight to their own service.

## What is (and isn't) isolated from production

Stage uses its own dedicated Kubernetes Secret, `biatec-stage-app-secret`, via `envFrom` — never
`google-account-main-app-secret` (production's). Generate it once with
[`k8s/stage/generate-stage-secret.sh`](../k8s/stage/generate-stage-secret.sh), which **always**
mints a fresh self-custody AES key/IV, a fresh provider-access-token-protection AES key/IV
(`ProviderTokenProtection` — see `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md`'s "Provider access token
caching" section), and a fresh RSA JWT signing key dedicated to stage — never copied from
production — and asks you for the Google/Microsoft OAuth `ClientId`/`ClientSecret` and Redis
connection string (fine to reuse production's OAuth app or Redis instance, or supply distinct
ones; the script doesn't assume either way). Here's exactly what that buys you:

- **Self-custody files ARE isolated**, two ways over. `App:StorageFolderName` is `"BiatecStage"`
  in both `k8s/stage/conf-mcp-stage/appsettings.json` and `k8s/stage/conf-oidc-stage/appsettings.json`
  (production uses `"Biatec"`), so stage and production always read/write a **different** Drive
  folder / OneDrive app-subfolder file, even if the exact same human signs in with the exact same
  real Google/Microsoft account in both environments. On top of that, stage's AES key (from
  `biatec-stage-app-secret`) is now a *different* key from production's, so even the encrypted
  blobs stage happens to write are opaque to production's key and vice versa.
- **The OIDC issuer/discovery IS isolated**, automatically, with no config at all — see
  `CICD_GITHUB_ACTIONS.md` and `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md` for `JwtIssuerService.GetIssuer`.
  `stage.oidc.biatec.io`'s `iss`/discovery `issuer` will always be `https://stage.oidc.biatec.io`,
  never anything production-related.
- **`JwtIssuer:Clients` (the OIDC client/redirect-URI allowlist) IS separate.** Stage has its own
  list in `k8s/stage/conf-oidc-stage/appsettings.json`, seeded with one example (`capitalism-stage`)
  — add real entries there for whichever "different projects" need to test against stage; this
  never touches or risks production's `JwtIssuer:Clients` in `k8s/main/conf-oidc/appsettings.json`.
- **The JWT signing key IS isolated.** `generate-stage-secret.sh` always generates a fresh RSA
  keypair for `JwtIssuer__SigningPrivateKeyPem` in `biatec-stage-app-secret` — stage never signs
  tokens with production's key (if production even has one configured; its ConfigMap leaves
  `SigningPrivateKeyPem` blank too, same ephemeral-key caveat as before, but that's now entirely
  production's own concern, unrelated to stage).
- **The provider-access-token cache IS isolated.** `BiatecOIDC` caches the caller's Google/Microsoft
  access token, AES-256-GCM encrypted, inside issued access/refresh tokens and their backing Redis
  records (`oidc:code:*`/`oidc:refresh:*`) — see `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md`'s "Provider
  access token caching" section. `generate-stage-secret.sh` always generates a fresh, separate
  `ProviderTokenProtection` key/IV for this, so even if stage and production share the same Redis
  instance (see below), a cached blob from one environment's `oidc:refresh:*` record is
  undecryptable under the other's key.
- **Redis is NOT isolated by default**, only because the script needs a real connection string
  from you and doesn't assume you want a separate Redis instance. Device-pairing session state and
  OIDC authorization codes are keyed by high-entropy random IDs, so practical collision risk is
  negligible even if you point stage at the same Redis as production; the one real (minor,
  low-severity) residual consequence if you do is that `IDistributedCache` entries keyed only by
  email (e.g. `PortfolioValuationService`'s `portfolio_value:{email}` cache, TTL 1 hour) could
  theoretically be read across environments for the same email during that TTL window. Nothing
  security-sensitive is cached this way today. To isolate it fully, give
  `generate-stage-secret.sh`'s `REDIS_CONNECTION_STRING` prompt either a separate Redis instance or
  the same instance with `,defaultDatabase=1` appended.

## One-time manual setup (outside this repo)

1. **DNS**: create `stage.google.biatec.io` and `stage.oidc.biatec.io` records pointing at the same
   ingress load balancer as the production hosts.
2. **`biatec-stage-app-secret`**: run
   [`k8s/stage/generate-stage-secret.sh`](../k8s/stage/generate-stage-secret.sh) once (with a
   kubeconfig that can write Secrets in the `biatec` namespace active) to create it. Re-run it to
   rotate/update any value later.
3. **Google Cloud Console** → OAuth client → add authorized redirect URIs:
   - `https://stage.google.biatec.io/signin-google` (BiatecMCP stage)
   - `https://stage.oidc.biatec.io/oidc/signin-google` (BiatecOIDC stage)
4. **Entra admin center** → app registration → add redirect URIs (see
   `BiatecOIDC/ENTRA_SETUP_GUIDE.md`):
   - `https://stage.google.biatec.io/signin-microsoft` (BiatecMCP stage)
   - `https://stage.oidc.biatec.io/oidc/signin-microsoft` (BiatecOIDC stage)
5. No `KUBE_CONFIG`/RBAC change is needed for CI (see above) — the existing namespace-scoped Role
   already covers stage's Deployments/Services/ConfigMaps/Ingresses; it just doesn't (and
   shouldn't) grant `get`/`list` on Secrets at all, so `generate-stage-secret.sh` must be run with
   your own admin/write-scoped kubeconfig, never CI's. `k8s/main/namespace.yaml` doesn't need
   re-applying either, since stage lives in the already-existing `biatec` namespace.

## Promoting a version to production

1. Push to `master` as usual — `deploy-stage.yml` builds, pushes, and deploys to stage
   automatically. Note the version it computed (visible in the workflow run log/summary, format
   `1.<year>.<month>.<day>-main`), or read it back from `k8s/stage/deployment-mcp-stage.yaml` /
   `deployment-oidc-stage.yaml` after the run.
2. Have humans / other projects test against `stage.google.biatec.io` / `stage.oidc.biatec.io` for
   as long as needed — there is no time limit and no automatic promotion. Multiple pushes can land
   in stage before anything is promoted.
3. When ready, go to **Actions → Promote to Production → Run workflow**, enter that `version`, and
   choose `service` (`both`, `mcp`, or `oidc` — promote independently if only one service changed).
4. The workflow updates `k8s/main/deployment-*.yaml` to that tag, commits it, and applies it to
   production the same way `deploy-stage.yml` applies to stage — just without rebuilding anything.

There is no environment-protection/required-reviewer gate on `promote-production.yml` beyond
GitHub's normal repository permissions (only people who can trigger workflows on this repo can run
it) — that was a deliberate choice over adding a GitHub Environment approval gate, to keep
promotion a single explicit action rather than a pipeline run left pending for approval. Revisit
this if the team grows and a second-approver requirement becomes desirable.
