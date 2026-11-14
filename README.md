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

## Saga e BDD

O OS Service concentra a decisao de transicao do Saga em `OrderSaga`. O fluxo
principal e `BUDGET_CREATED -> PAYMENT_APPROVED -> EXECUTION_FINISHED`, com
compensacoes explicitas para `BUDGET_REJECTED`, `PAYMENT_REJECTED` e
`EXECUTION_FAILED`. O cenario BDD esta em
`Tests/OsService.Tests/Features/order-lifecycle.feature` e e coberto pelo teste
`Bdd_Saga_Completes_Order_Flow_And_Compensates_Failures`.
- Consome eventos de orcamento, pagamento e execucao.

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
