#!/bin/bash
set -euo pipefail

# Generates a namespace-scoped, time-limited kubeconfig for the GitHub Actions CI/CD pipeline.
#
# Run this ONCE (and again whenever the token needs to be rotated) using your own admin
# kubeconfig (KUBECONFIG env var or --kubeconfig pointing at the cluster-admin file). It never
# uses or exposes the admin kubeconfig itself - it only uses it to create a restricted
# ServiceAccount/Role/RoleBinding in the "biatec" namespace and to mint a short-lived bound
# token for that ServiceAccount.
#
# Output: a base64 blob to paste into the GitHub repository secret KUBE_CONFIG.
#
# Requirements: kubectl >= 1.24 (for `kubectl create token`), access to a cluster-admin
# kubeconfig, and the "biatec" namespace must already exist (apply k8s/main/namespace.yaml
# once with an admin kubeconfig if it does not).

NAMESPACE="biatec"
SERVICE_ACCOUNT="github-actions-ci"
ROLE_NAME="github-actions-ci-role"
ROLE_BINDING_NAME="github-actions-ci-rolebinding"
TOKEN_DURATION="720h" # 30 days
OUTPUT_FILE="ci-kubeconfig.yaml"
OUTPUT_FILE_B64="ci-kubeconfig.base64"

echo "==> Verifying namespace '$NAMESPACE' exists"
if ! kubectl get namespace "$NAMESPACE" >/dev/null 2>&1; then
  echo "Namespace '$NAMESPACE' does not exist. Apply k8s/main/namespace.yaml with an admin"
  echo "kubeconfig first: kubectl apply -f k8s/main/namespace.yaml"
  exit 1
fi

echo "==> Creating/updating ServiceAccount '$SERVICE_ACCOUNT' in namespace '$NAMESPACE'"
kubectl create serviceaccount "$SERVICE_ACCOUNT" -n "$NAMESPACE" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "==> Creating/updating Role '$ROLE_NAME' scoped to namespace '$NAMESPACE'"
cat <<EOF | kubectl apply -f -
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: $ROLE_NAME
  namespace: $NAMESPACE
rules:
  - apiGroups: ["apps"]
    resources: ["deployments", "replicasets"]
    verbs: ["get", "list", "watch", "create", "update", "patch"]
  - apiGroups: [""]
    resources: ["pods", "pods/log"]
    verbs: ["get", "list", "watch"]
  - apiGroups: [""]
    resources: ["services"]
    verbs: ["get", "list", "watch", "create", "update", "patch"]
  - apiGroups: [""]
    resources: ["configmaps"]
    verbs: ["get", "list", "watch", "create", "update", "patch", "delete"]
  - apiGroups: ["networking.k8s.io"]
    resources: ["ingresses"]
    verbs: ["get", "list", "watch", "create", "update", "patch"]
EOF

echo "==> Binding role to service account"
kubectl create rolebinding "$ROLE_BINDING_NAME" -n "$NAMESPACE" \
  --role="$ROLE_NAME" \
  --serviceaccount="$NAMESPACE:$SERVICE_ACCOUNT" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "==> Minting a bound token for '$SERVICE_ACCOUNT' valid for $TOKEN_DURATION"
TOKEN=$(kubectl create token "$SERVICE_ACCOUNT" -n "$NAMESPACE" --duration="$TOKEN_DURATION")

echo "==> Reading cluster connection details from your current context"
CLUSTER_NAME=$(kubectl config view --minify -o jsonpath='{.clusters[0].name}')
CLUSTER_SERVER=$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')
CLUSTER_CA=$(kubectl config view --minify --raw -o jsonpath='{.clusters[0].cluster.certificate-authority-data}')

echo "==> Writing restricted kubeconfig to $OUTPUT_FILE"
cat <<EOF > "$OUTPUT_FILE"
apiVersion: v1
kind: Config
current-context: $SERVICE_ACCOUNT@$NAMESPACE
clusters:
  - name: $CLUSTER_NAME
    cluster:
      server: $CLUSTER_SERVER
      certificate-authority-data: $CLUSTER_CA
contexts:
  - name: $SERVICE_ACCOUNT@$NAMESPACE
    context:
      cluster: $CLUSTER_NAME
      namespace: $NAMESPACE
      user: $SERVICE_ACCOUNT
users:
  - name: $SERVICE_ACCOUNT
    user:
      token: $TOKEN
EOF

base64 -w0 "$OUTPUT_FILE" > "$OUTPUT_FILE_B64" 2>/dev/null || base64 "$OUTPUT_FILE" > "$OUTPUT_FILE_B64"

echo
echo "Done."
echo "  - Restricted kubeconfig written to: $OUTPUT_FILE"
echo "  - Base64-encoded (paste this into the GitHub secret KUBE_CONFIG): $OUTPUT_FILE_B64"
echo
echo "This token expires in $TOKEN_DURATION (~30 days). Re-run this script before it expires"
echo "and update the KUBE_CONFIG GitHub secret with the new value."
echo
echo "IMPORTANT: delete both output files locally once the secret is uploaded - they contain"
echo "a live (if short-lived) credential."
