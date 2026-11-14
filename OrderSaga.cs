public static class OrderSaga
{
    public static string NextStatusForEvent(string eventType) => eventType switch
    {
        "BUDGET_CREATED" => OrderStatuses.AguardandoPagamento,
        "BUDGET_REJECTED" => OrderStatuses.OrcamentoRejeitado,
        "PAYMENT_APPROVED" => OrderStatuses.AguardandoExecucao,
        "PAYMENT_REJECTED" => OrderStatuses.PagamentoRecusado,
        "EXECUTION_FINISHED" => OrderStatuses.Finalizada,
        "EXECUTION_FAILED" => OrderStatuses.ExecucaoComErro,
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Evento de Saga desconhecido")
    };

    public static bool StartsExecution(string eventType) => eventType == "PAYMENT_APPROVED";
}
