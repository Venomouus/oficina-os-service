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
