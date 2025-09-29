# Docker

Esta pasta reúne todos os artefatos necessários para construir e executar a solução com Docker.

## Build das imagens

Execute os comandos abaixo a partir da raiz do repositório:

```bash
docker build -f deploy/docker/Dockerfile.api -t techchallengegrupo22/api:latest .
docker build -f deploy/docker/Dockerfile.worker -t techchallengegrupo22/worker:latest .
```

Ajuste as tags das imagens conforme a sua convenção interna.

## Execução local com Docker Compose

Dentro da pasta `deploy/docker` execute:

```bash
docker compose up
```

O arquivo `docker-compose.yml` usa as imagens construídas no passo anterior automaticamente ao detectar mudanças de código.
