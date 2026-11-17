# Oficina OS Service

Microsservico responsavel pela abertura, consulta e atualizacao das ordens de servico.

## Stack

- ASP.NET Core 8
- PostgreSQL exclusivo do servico
- RabbitMQ para publicacao e consumo de eventos
- Swagger/OpenAPI

## Eventos

- Publica `OS_CREATED` ao abrir uma OS.
- Publica `OS_STATUS_UPDATED` ao alterar o status.
- Publica `OS_READY_FOR_EXECUTION` apos pagamento aprovado.

## Saga coreografada

Os tres servicos reagem a eventos RabbitMQ sem acessar o banco uns dos outros.
`OS_CREATED -> BUDGET_CREATED -> PAYMENT_APPROVED -> OS_READY_FOR_EXECUTION ->
EXECUTION_FINISHED` e o caminho feliz. Recusa de orcamento, recusa de pagamento
e falha de execucao compensam a OS com `ORCAMENTO_REJEITADO`,
`PAGAMENTO_RECUSADO` e `EXECUCAO_COM_ERRO`, respectivamente. A compensacao
interrompe o avanco indevido; pagamento ja capturado nao e estornado
automaticamente. Um eventual estorno exige politica propria e API do provedor.

Cada mudanca grava o evento de saida no mesmo registro/transacao local (outbox).
Um worker publica mensagens persistentes com confirmacao do RabbitMQ e tenta
novamente a cada 2 segundos se falhar. Entregas duplicadas sao esperadas: as
transicoes de `OrderSaga` ignoram repeticoes e eventos fora de ordem. O OS
Service cria a tabela `saga_outbox` no PostgreSQL existente sem apagar dados.
O status da OS e um token de concorrencia: dois consumidores nao podem gravar
transicoes conflitantes ao mesmo tempo. O consumidor tenta reconectar quando o
RabbitMQ nao esta disponivel. Falhas temporarias aguardam 30 segundos na fila
`os-service-events.retry` e recebem ate 5 novas tentativas; mensagens malformadas ou
que excederam esse limite ficam em `os-service-events.dead` para inspecao e
reenvio manual apos corrigir a causa. Nao apague a fila dead sem investigar.

Os testes incluem o fluxo feliz, as compensacoes, eventos duplicados/fora de
ordem e falha temporaria de publicacao. O arquivo Gherkin
`Tests/OsService.Tests/Features/order-lifecycle.feature` e executado pelo
Reqnroll/xUnit: os passos chamam a API da OS e o handler de eventos usado pelo
consumidor RabbitMQ. Isso verifica a Saga no OS Service, mas nao substitui um
teste externo com o Mercado Pago real ou com os tres processos em rede.

O BDD distribuido em `Tests/Saga.E2E/Features/distributed-saga.feature`
inicia PostgreSQL, MongoDB e RabbitMQ descartaveis com Testcontainers e sobe
OS, Billing e Execution como processos HTTP separados. Testa pagamento
sandbox simulado, finalizacao e compensacao por falha de execucao. Nao usa
credenciais nem API real do Mercado Pago. Requer Docker e os tres repositorios
lado a lado em `services/`. Execute, depois de compilar os tres servicos em
`Release`:

```powershell
dotnet test Tests/Saga.E2E/Saga.E2E.csproj -c Release
```

O job `distributed-bdd` do CI repete o teste com a branch `main` de Billing
e Execution tanto em PRs quanto em pushes na `main` do OS Service.

Para executar BDD, testes de API e exigir pelo menos 80% de linhas do assembly:

```powershell
./scripts/verify-coverage.ps1
```

O script falha se a cobertura ficar abaixo de 80%. O CI publica o XML Cobertura
como artefato `os-service-coverage` em cada execucao.
O [painel SonarCloud do PR](https://sonarcloud.io/dashboard?id=Venomouus_oficina-os-service&pullRequest=1)
mostra o quality gate e a cobertura de codigo novo; o verificador acima mede
separadamente todas as linhas da assembly de producao.

## Endpoints principais

- `POST /api/orders`
- `GET /api/orders`
- `GET /api/orders/{id}`
- `GET /api/orders/{id}/history`
- `PATCH /api/orders/{id}/status`

## Executar

```powershell
dotnet run
```

Configuracao padrao: PostgreSQL em `localhost:5433` e RabbitMQ em `localhost:5672`.

## CI/CD e observabilidade

O PR executa testes/cobertura, BDD distribuido, SonarCloud com Quality Gate
bloqueante e build Docker. Em `main`, o job `publish-deploy` assume a role AWS
via OIDC, publica a imagem `oficina-fase4-lab/os-service:<SHA>` no ECR privado
e aguarda o rollout no EKS. O namespace `oficina`, os Secrets
`messaging-secrets` e `os-service-secrets` e a role de deploy precisam existir;
o manifesto nao contem credenciais reais. O lab e temporario e usa `emptyDir`
para os bancos: os dados se perdem se o pod for recriado.

Logs JSON vao para stdout. Com `OTEL_EXPORTER_OTLP_ENDPOINT` definido, traces,
metricas HTTP/Saga e logs vao por OTLP gRPC ao coletor; o ConfigMap opcional
`observability-config` fornece o endereco no Kubernetes. O painel fica no
Grafana conectado a esse coletor, nao no Swagger.
