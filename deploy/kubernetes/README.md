# Kubernetes

Os manifestos desta pasta permitem orquestrar a aplicação no Kubernetes. Todos os arquivos assumem o uso do namespace `visionary-analytics` e imagens previamente publicadas em um registry acessível pelo cluster.

## Preparação

1. Crie (ou ajuste) as imagens Docker e publique-as em um registry:
   ```bash
   docker build -f deploy/docker/Dockerfile.api -t <seu-registry>/visionary-api:latest .
   docker push <seu-registry>/visionary-api:latest

   docker build -f deploy/docker/Dockerfile.worker -t <seu-registry>/visionary-worker:latest .
   docker push <seu-registry>/visionary-worker:latest
   ```
2. Atualize os campos `image` dos manifestos `api-deployment.yaml` e `worker-deployment.yaml` para apontarem para as imagens enviadas.

## Aplicando os manifestos

```bash
kubectl apply -f deploy/kubernetes/namespace.yaml
kubectl apply -f deploy/kubernetes/videos-pvc.yaml
kubectl apply -f deploy/kubernetes/rabbitmq-deployment.yaml
kubectl apply -f deploy/kubernetes/rabbitmq-service.yaml
kubectl apply -f deploy/kubernetes/redis-deployment.yaml
kubectl apply -f deploy/kubernetes/redis-service.yaml
kubectl apply -f deploy/kubernetes/api-deployment.yaml
kubectl apply -f deploy/kubernetes/api-service.yaml
kubectl apply -f deploy/kubernetes/worker-deployment.yaml
```

Após a publicação, use `kubectl port-forward service/visionary-api 8080:8080 -n visionary-analytics` para acessar a API localmente.
