kubectl apply -f deployment-mcp.yaml -n biatec
kubectl apply -f deployment-oidc.yaml -n biatec
kubectl delete configmap biatec-mcp-conf -n biatec
kubectl create configmap biatec-mcp-conf --from-file=conf-mcp -n biatec
kubectl delete configmap biatec-oidc-conf -n biatec
kubectl create configmap biatec-oidc-conf --from-file=conf-oidc -n biatec
kubectl rollout restart deployment/biatec-mcp-app-deployment -n biatec
kubectl rollout status deployment/biatec-mcp-app-deployment -n biatec
kubectl rollout restart deployment/biatec-oidc-app-deployment -n biatec
kubectl rollout status deployment/biatec-oidc-app-deployment -n biatec
