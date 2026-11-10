# Oficina OS Service

Microsserviço responsável pela abertura, consulta e atualização das ordens de serviço.

## Stack

- ASP.NET Core 8
- PostgreSQL exclusivo do serviço
- RabbitMQ para publicação e consumo de eventos
- Swagger/OpenAPI

## Eventos

- Publica `OS_CREATED` ao abrir uma OS.
- Publica `OS_STATUS_UPDATED` ao alterar o status.
- Consome `BUDGET_CREATED` e `PAYMENT_APPROVED`.

## Executar

```powershell
dotnet run
```

Configuração padrão: PostgreSQL em `localhost:5433` e RabbitMQ em `localhost:5672`.
