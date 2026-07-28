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
| Production | `https://google.biatec.io` | `https://oidc.biatec.io` (+ legacy alias paths on `google.biatec.io`, see `CICD_GITHUB_ACTIONS.md`) |
| Stage | `https://stage.google.biatec.io` | `https://stage.oidc.biatec.io` |

You need to create the `stage.google.biatec.io` and `stage.oidc.biatec.io` DNS records yourself
(same as you did for `oidc.biatec.io`) — this repo has no way to do that for you. Once DNS resolves
and the ingress is applied, `cert-manager` issues TLS certificates for both automatically via the
same `letsencrypt` `ClusterIssuer` already used in production.

## Kubernetes resources

Stage deliberately lives in the **same `biatec` namespace** as production, not a separate
namespace — every resource just has a `-stage` suffix so it can never collide with or be mistaken
for a production resource:

| Resource type | Production | Stage |
|---|---|---|
| Deployment | `biatec-mcp-app-deployment` / `biatec-oidc-app-deployment` | `biatec-mcp-stage-app-deployment` / `biatec-oidc-stage-app-deployment` |
| Service | `biatec-mcp-service` / `biatec-oidc-service` | `biatec-mcp-stage-service` / `biatec-oidc-stage-service` |
| Ingress | `biatec-mcp-ingress`, `biatec-oidc-ingress` + `biatec-oidc-domain-ingress` | `biatec-mcp-stage-ingress`, `biatec-oidc-stage-ingress` |
| ConfigMap | `biatec-mcp-conf` / `biatec-oidc-conf` | `biatec-mcp-stage-conf` / `biatec-oidc-stage-conf` |
| Manifest files | `k8s/main/deployment-mcp.yaml`, `k8s/main/deployment-oidc.yaml`, `k8s/main/conf-mcp/`, `k8s/main/conf-oidc/` | `k8s/stage/deployment-mcp-stage.yaml`, `k8s/stage/deployment-oidc-stage.yaml`, `k8s/stage/conf-mcp-stage/`, `k8s/stage/conf-oidc-stage/` |

Because the existing CI `Role` (see `KUBE_CONFIG_SECURITY.md`) grants verbs on resource *types*
(`deployments`, `services`, `configmaps`, `ingresses`), not specific resource *names*, the same
`KUBE_CONFIG` secret and Role already used for production also covers every stage resource above —
**no new secret, Role, or RoleBinding was needed** to add stage. This is the main practical benefit
of the "same namespace" choice over a fully separate namespace.

Stage's Ingress objects are each a single full catch-all (`/(.*)` + `rewrite-target: /$1`) straight
to their own service — unlike production's `BiatecOIDC`, which needs a second Ingress object to
carve out a legacy alias host. Stage has no legacy host to preserve, so it doesn't need that split.

## What is (and isn't) isolated from production

Stage reuses the same `google-account-main-app-secret` Kubernetes Secret as production via
`envFrom` (same Google/Entra OAuth `ClientId`/`ClientSecret`, same AES key, same Redis connection
string). That is a deliberate simplicity trade-off, not an oversight — here's exactly what it means:

- **Self-custody files ARE isolated.** `App:StorageFolderName` is set to `"BiatecStage"` in both
  `k8s/stage/conf-mcp-stage/appsettings.json` and `k8s/stage/conf-oidc-stage/appsettings.json`
  (production uses `"Biatec"`). Since this is a config value read from the mounted ConfigMap, not
  the shared secret, stage and production always read/write a **different** Drive folder /
  OneDrive app-subfolder file — even if the exact same human signs in with the exact same real
  Google/Microsoft account in both environments. A tester's stage account is a distinct Algorand
  account from whatever (if anything) exists for that email in production; testing in stage can
  never touch a production self-custody account.
- **The OIDC issuer/discovery IS isolated**, automatically, with no config at all — see
  `CICD_GITHUB_ACTIONS.md` and `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md` for `JwtIssuerService.GetIssuer`.
  `stage.oidc.biatec.io`'s `iss`/discovery `issuer` will always be `https://stage.oidc.biatec.io`,
  never anything production-related.
- **`JwtIssuer:Clients` (the OIDC client/redirect-URI allowlist) IS separate.** Stage has its own
  list in `k8s/stage/conf-oidc-stage/appsettings.json`, seeded with one example (`capitalism-stage`)
  — add real entries there for whichever "different projects" need to test against stage; this
  never touches or risks production's `JwtIssuer:Clients` in `k8s/main/conf-oidc/appsettings.json`.
- **The JWT signing key is NOT isolated by default.** `JwtIssuer:SigningPrivateKeyPem` is blank in
  stage's ConfigMap, same as production's — meaning both currently fall back to an ephemeral
  per-pod RSA key if neither the ConfigMap nor a `JwtIssuer__SigningPrivateKeyPem` env var (from the
  shared secret) supplies one. If production's secret *does* set that env var, stage inherits the
  **same signing key** via the same `envFrom`. This is acceptable for now (a stage-issued token
  bearing production's key doesn't grant access to anything, since it's still validated against
  `stage.oidc.biatec.io`'s own `iss`/audience by any correctly-implemented relying party) but isn't
  ideal defense-in-depth. To give stage a genuinely distinct signing key later: generate a fresh
  RSA keypair (`openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096`), create a small new
  Secret holding it, and add an explicit `env: JwtIssuer__SigningPrivateKeyPem` entry (`secretKeyRef`)
  to `k8s/stage/deployment-oidc-stage.yaml` — the same pattern already used there for
  `csharp-cert-password`. Not done by default because it's an extra manual step this repo can't
  perform for you (a Secret's contents aren't something CI or a checked-in file can safely hold).
- **Redis is NOT isolated.** Stage and production share the same Redis instance and logical
  database (index 0), via the same connection string. Device-pairing session state and OIDC
  authorization codes are keyed by high-entropy random IDs, so practical collision risk is
  negligible; the one real (minor, low-severity) consequence is that `IDistributedCache` entries
  keyed only by email (e.g. `PortfolioValuationService`'s `portfolio_value:{email}` cache, TTL 1
  hour) could theoretically be read across environments for the same email during that TTL window.
  Nothing security-sensitive is cached this way today. If this ever needs tightening, the fix is
  either a dedicated Redis instance for stage or appending `,defaultDatabase=1` to a
  stage-specific `Redis:ConnectionString` override (same secret-override pattern as the signing key
  above) — not done now because it needs a real connection-string value this repo doesn't have.

## One-time manual setup (outside this repo)

1. **DNS**: create `stage.google.biatec.io` and `stage.oidc.biatec.io` records pointing at the same
   ingress load balancer as the production hosts.
2. **Google Cloud Console** → OAuth client → add authorized redirect URIs:
   - `https://stage.google.biatec.io/signin-google` (BiatecMCP stage)
   - `https://stage.oidc.biatec.io/oidc/signin-google` (BiatecOIDC stage)
3. **Entra admin center** → app registration → add redirect URIs (see
   `BiatecOIDC/ENTRA_SETUP_GUIDE.md`):
   - `https://stage.google.biatec.io/signin-microsoft` (BiatecMCP stage)
   - `https://stage.oidc.biatec.io/oidc/signin-microsoft` (BiatecOIDC stage)
4. Nothing else is required — no new Kubernetes Secret, no new `KUBE_CONFIG`, no RBAC change (see
   above). `k8s/main/namespace.yaml` doesn't need re-applying either, since stage lives in the
   already-existing `biatec` namespace.

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
