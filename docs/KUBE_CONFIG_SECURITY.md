# Keeping the CI kubeconfig safe

The GitHub Actions workflow needs a kubeconfig to run `kubectl apply` against the cluster. That
credential lives in the `KUBE_CONFIG` repository secret. GitHub secrets are reasonably well
protected, but they're still a bigger blast radius than a laptop: they're readable by any workflow
run on the repo (including from a compromised dependency in a build step), and a secret, once
leaked in a log or artifact, can't be un-leaked. For that reason **never put your admin/cluster-admin
kubeconfig in a GitHub secret.** If it ever leaks, the entire cluster is compromised, not just this
one namespace.

Instead, `KUBE_CONFIG` should hold a credential that is deliberately weak in every dimension that
doesn't matter for this pipeline:

- **Namespace-scoped**: a Kubernetes `Role` + `RoleBinding` (not `ClusterRole`/`ClusterRoleBinding`)
  restricted to the `biatec` namespace, so the credential physically cannot read or modify
  anything outside it — other namespaces, nodes, cluster-scoped resources, etc.
- **Verb-scoped**: the `Role` only grants the verbs the pipeline actually calls
  (`get`/`list`/`watch`/`create`/`update`/`patch`/`delete` on `deployments`, `replicasets`, `pods`,
  `services`, `configmaps`, `ingresses` — see the Role definition in
  `k8s/main/generate-ci-kubeconfig.sh`). It cannot touch `secrets`, `nodes`, RBAC objects, or
  anything cluster-scoped like `Namespace`.
- **Time-limited**: the token is a Kubernetes 1.24+ [bound service account
  token](https://kubernetes.io/docs/reference/access-authn-authz/service-accounts-admission-control/#bound-service-account-tokens)
  minted with `kubectl create token ... --duration=720h` (30 days). It stops working on its own
  even if nobody rotates it or notices a leak.

## Why the `Namespace` object isn't managed by CI

`Namespace` is a cluster-scoped resource. RBAC `Role`/`RoleBinding` can only grant access to
resources *inside* a namespace — there's no way to give a namespaced credential permission to
create or edit a `Namespace` object without also making it (or an equivalent ClusterRole) cluster-
scoped, which would defeat the purpose. So namespace creation is a one-time, manual, admin-only
step (`kubectl apply -f k8s/main/namespace.yaml`) — see
[`CICD_GITHUB_ACTIONS.md`](CICD_GITHUB_ACTIONS.md#one-time-setup-the-biatec-namespace`). CI only
ever touches resources that already live inside `biatec`.

## Generating the credential

Run [`k8s/main/generate-ci-kubeconfig.sh`](../k8s/main/generate-ci-kubeconfig.sh) with your admin
kubeconfig active. It:

1. Creates (or updates) a `github-actions-ci` ServiceAccount in the `biatec` namespace.
2. Creates the least-privilege `Role` described above and binds it to that ServiceAccount via a
   `RoleBinding`, both scoped to `biatec`.
3. Mints a 30-day bound token for that ServiceAccount.
4. Assembles a minimal kubeconfig (cluster server + CA + the token, nothing else — no other
   contexts, no other users, no cluster-admin credentials) pointing its default context at the
   `biatec` namespace.
5. Base64-encodes it, ready to paste into the `KUBE_CONFIG` GitHub secret.

Your admin kubeconfig is only ever used locally, to create these scoped objects — it is never
written to a file that leaves your machine, and the script never uploads anything itself.

## Rotation

Because the token expires after 30 days, the pipeline will start failing kubectl auth once it
lapses. Re-run the generation script and update the `KUBE_CONFIG` secret before then — put a
recurring reminder on your calendar, or invoke the script from a separate, human-triggered
maintenance job. There is intentionally no automatic renewal: a credential that silently renews
itself forever is exactly the kind of long-lived secret this setup is trying to avoid.

## If the secret leaks anyway

Because the credential is namespace- and verb-scoped, the worst case is limited to the resources
listed in the `Role` within `biatec` — not the whole cluster. To revoke immediately (faster than
waiting for expiry):

```bash
kubectl delete rolebinding github-actions-ci-rolebinding -n biatec
```

Then re-run `generate-ci-kubeconfig.sh` to issue a fresh token and update the GitHub secret.
