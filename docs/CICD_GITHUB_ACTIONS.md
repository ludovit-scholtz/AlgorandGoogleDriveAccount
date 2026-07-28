# CI/CD via GitHub Actions

The `build-api.yml` workflow (`.github/workflows/build-api.yml`) builds two Docker images — one for
the `BiatecMCP` service (MCP server + Google Drive self-custody backend) and one for the
`BiatecOIDC` service (OIDC/JWT identity provider) — pushes them to Docker Hub, updates the
Kubernetes manifests with the new image tags, and applies both directly to the cluster from the
GitHub Actions runner. Everything is managed by the pipeline itself; there is no staging server or
SSH involved.

## What the pipeline does, on every push to `master`

1. Computes a version tag `1.<year>.<month>.<day>-main`, matching the scheme the old `deploy.sh`
   used (e.g. `1.2026.07.23-main`). Both images share the same version tag.
2. Builds `BiatecMCP/Dockerfile` and pushes `scholtz2/biatec-mcp:<version>`, and builds
   `BiatecOIDC/Dockerfile` and pushes `scholtz2/biatec-oidc:<version>`, to Docker Hub.
3. Updates the image tags in `k8s/main/deployment-mcp.yaml` and `k8s/main/deployment-oidc.yaml`
   and commits that change back to `master` with `[skip ci]` (so it doesn't retrigger the
   workflow) — this keeps the manifests in git as the source of truth, same as before.
4. Applies both manifests to the `biatec` namespace using a namespace-scoped kubeconfig (see
   below — the cluster's `Namespace` object itself is **not** managed by CI, see "One-time
   setup").
5. Recreates the `biatec-mcp-conf` ConfigMap from `k8s/main/conf-mcp`, and the `biatec-oidc-conf`
   ConfigMap from `k8s/main/conf-oidc`.
6. Restarts both deployments and waits for each rollout to complete.

## Required GitHub repository secrets

Configure these under **Settings → Secrets and variables → Actions → Repository secrets**:

| Secret               | Purpose                                                                 |
|----------------------|--------------------------------------------------------------------------|
| `DOCKERHUB_USERNAME` | Docker Hub account/organization that owns `scholtz2/biatec-mcp` and `scholtz2/biatec-oidc`. |
| `DOCKERHUB_TOKEN`    | A Docker Hub [access token](https://hub.docker.com/settings/security) (not your password) scoped to read/write for those repos. |
| `KUBE_CONFIG`        | Base64-encoded, namespace-scoped, time-limited kubeconfig for the `biatec` namespace. Generate it with `k8s/main/generate-ci-kubeconfig.sh` — see [below](#generating-the-scoped-kube_config-secret) and never paste an admin kubeconfig here. |

The old `SSH_USER`, `SSH_KEY`, and `SSH_HOST` secrets are no longer used by this workflow and can
be removed once you've confirmed the new pipeline is working. Both new Docker Hub repos
(`scholtz2/biatec-mcp`, `scholtz2/biatec-oidc`) must exist (or be auto-creatable) under the same
Docker Hub account as the old `scholtz2/algorand-google-account` repo — no new
`DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` is needed.

### Setting up `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`

1. Log in to [hub.docker.com](https://hub.docker.com) as the account that owns the
   `scholtz2/*` repositories.
2. Go to **Account Settings → Security → New Access Token**, give it a description (e.g.
   `github-actions-biatec`), and grant it **Read & Write** scope.
3. Copy the token immediately — it is only shown once.
4. In the GitHub repo, add `DOCKERHUB_USERNAME` (your Docker Hub username/org) and
   `DOCKERHUB_TOKEN` (the token you just created) as repository secrets.

### One-time setup: the `biatec` namespace

Because the CI credential is deliberately namespace-scoped (see next section), it cannot create
or modify cluster-scoped objects such as a `Namespace`. Apply `k8s/main/namespace.yaml` once,
manually, with an admin kubeconfig, before the first CI run (and again only if the namespace is
ever deleted):

```bash
kubectl apply -f k8s/main/namespace.yaml
```

After that, CI only ever touches namespaced resources (`Deployment`, `Service`, `Ingress`,
`ConfigMap`) inside `biatec`.

## Generating the scoped `KUBE_CONFIG` secret

See [`k8s/main/generate-ci-kubeconfig.sh`](../k8s/main/generate-ci-kubeconfig.sh) and
[`KUBE_CONFIG_SECURITY.md`](KUBE_CONFIG_SECURITY.md) for the full explanation of why the CI
credential must never be a copy of your admin kubeconfig, and how the script builds a
least-privilege, 30-day-expiring one instead. The Role it creates grants verbs on resource
*types* (`deployments`, `services`, `configmaps`, `ingresses`), not specific resource names, so no
RBAC change was needed for the `biatec-mcp-*`/`biatec-oidc-*` resource names introduced by the
MCP/OIDC split.

Quick version:

```bash
# Using your admin kubeconfig (KUBECONFIG env var or default ~/.kube/config)
./k8s/main/generate-ci-kubeconfig.sh
```

This prints a `ci-kubeconfig.base64` file. Paste its contents as the `KUBE_CONFIG` GitHub
secret, then delete the local `ci-kubeconfig.yaml` / `ci-kubeconfig.base64` files.

The resulting token is valid for 30 days. Re-run the script and update the secret before it
expires — there is no automatic rotation.

## One-time migration: cutting over from the old single-service deployment

Before the MCP/OIDC split, everything ran as one service named `google-account-main-*`
(`google-account-main-app-deployment`, `google-account-service-main`,
`google-account-ingress-main`, `google-account-main-conf`), image
`scholtz2/algorand-google-account`. The split introduces new resources
(`biatec-mcp-app-deployment` / `biatec-mcp-service` / `biatec-mcp-ingress` /
`biatec-mcp-conf`, and `biatec-oidc-app-deployment` / `biatec-oidc-service` /
`biatec-oidc-ingress` / `biatec-oidc-conf`) rather than renaming the old ones in place, so the
first CI run after this change lands is purely additive — it does not touch the old resources.

Once the new `biatec-mcp-*` resources are confirmed healthy (check
`https://google.biatec.io/mcp/` and the site's static pages) and `biatec-oidc-*` is confirmed
healthy (check `https://google.biatec.io/.well-known/openid-configuration`), remove the old
resources manually — **this is a destructive, one-time step and is deliberately not automated in
CI**:

```bash
kubectl delete deployment google-account-main-app-deployment -n biatec
kubectl delete service google-account-service-main -n biatec
kubectl delete ingress google-account-ingress-main -n biatec
kubectl delete configmap google-account-main-conf -n biatec
```

The `google-account-main-app-secret` and `csharp-cert`/`csharp-cert-password` secrets are reused
unchanged by both new deployments, so they should **not** be deleted.
