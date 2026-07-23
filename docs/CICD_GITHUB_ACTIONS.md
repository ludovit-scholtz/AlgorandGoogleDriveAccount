# CI/CD via GitHub Actions

The `build-api.yml` workflow (`.github/workflows/build-api.yml`) builds the Docker image, pushes
it to Docker Hub, updates the Kubernetes manifest with the new image tag, and applies it directly
to the cluster from the GitHub Actions runner. It no longer SSHes into a staging server to run
`deploy.sh` — everything is managed by the pipeline itself.

## What the pipeline does, on every push to `master`

1. Computes a version tag `1.<year>.<month>.<day>-main`, matching the scheme the old `deploy.sh`
   used (e.g. `1.2026.07.23-main`).
2. Builds `AlgorandGoogleDriveAccount/Dockerfile` and pushes
   `scholtz2/algorand-google-account:<version>` to Docker Hub.
3. Updates the image tag in `k8s/main/deployment-main.yaml` and commits that change back to
   `master` with `[skip ci]` (so it doesn't retrigger the workflow) — this keeps the manifest in
   git as the source of truth, same as before.
4. Applies `k8s/main/deployment-main.yaml` to the `biatec` namespace using a namespace-scoped
   kubeconfig (see below — the cluster's `Namespace` object itself is **not** managed by CI, see
   "One-time setup").
5. Recreates the `google-account-main-conf` ConfigMap from `k8s/main/conf`.
6. Restarts the deployment and waits for the rollout to complete.

## Required GitHub repository secrets

Configure these under **Settings → Secrets and variables → Actions → Repository secrets**:

| Secret               | Purpose                                                                 |
|----------------------|--------------------------------------------------------------------------|
| `DOCKERHUB_USERNAME` | Docker Hub account/organization that owns `scholtz2/algorand-google-account`. |
| `DOCKERHUB_TOKEN`    | A Docker Hub [access token](https://hub.docker.com/settings/security) (not your password) scoped to read/write for that repo. |
| `KUBE_CONFIG`        | Base64-encoded, namespace-scoped, time-limited kubeconfig for the `biatec` namespace. Generate it with `k8s/main/generate-ci-kubeconfig.sh` — see [below](#generating-the-scoped-kube_config-secret) and never paste an admin kubeconfig here. |

The old `SSH_USER`, `SSH_KEY`, and `SSH_HOST` secrets are no longer used by this workflow and can
be removed once you've confirmed the new pipeline is working.

### Setting up `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`

1. Log in to [hub.docker.com](https://hub.docker.com) as the account that owns
   `scholtz2/algorand-google-account`.
2. Go to **Account Settings → Security → New Access Token**, give it a description (e.g.
   `github-actions-algorand-google-drive-account`), and grant it **Read & Write** scope.
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
least-privilege, 30-day-expiring one instead.

Quick version:

```bash
# Using your admin kubeconfig (KUBECONFIG env var or default ~/.kube/config)
./k8s/main/generate-ci-kubeconfig.sh
```

This prints a `ci-kubeconfig.base64` file. Paste its contents as the `KUBE_CONFIG` GitHub
secret, then delete the local `ci-kubeconfig.yaml` / `ci-kubeconfig.base64` files.

The resulting token is valid for 30 days. Re-run the script and update the secret before it
expires — there is no automatic rotation.
