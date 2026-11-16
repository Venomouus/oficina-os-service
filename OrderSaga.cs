public static class OrderSaga
{
    // null significa entrega duplicada ou fora de ordem; nao desfaz estado terminal.
    public static string? NextStatus(string current, string eventType) => (current, eventType) switch
    {
        (OrderStatuses.AguardandoOrcamento, "BUDGET_CREATED") => OrderStatuses.AguardandoPagamento,
        (OrderStatuses.AguardandoOrcamento, "BUDGET_REJECTED") => OrderStatuses.OrcamentoRejeitado,
        (OrderStatuses.AguardandoPagamento, "BUDGET_REJECTED") => OrderStatuses.OrcamentoRejeitado,
        (OrderStatuses.AguardandoPagamento, "PAYMENT_APPROVED") => OrderStatuses.AguardandoExecucao,
        (OrderStatuses.PagamentoRecusado, "PAYMENT_APPROVED") => OrderStatuses.AguardandoExecucao,
        (OrderStatuses.AguardandoPagamento, "PAYMENT_REJECTED") => OrderStatuses.PagamentoRecusado,
        (OrderStatuses.AguardandoExecucao, "EXECUTION_FINISHED") => OrderStatuses.Finalizada,
        (OrderStatuses.EmDiagnostico, "EXECUTION_FINISHED") => OrderStatuses.Finalizada,
        (OrderStatuses.EmReparo, "EXECUTION_FINISHED") => OrderStatuses.Finalizada,
        (OrderStatuses.AguardandoExecucao, "EXECUTION_FAILED") => OrderStatuses.ExecucaoComErro,
        (OrderStatuses.EmDiagnostico, "EXECUTION_FAILED") => OrderStatuses.ExecucaoComErro,
        (OrderStatuses.EmReparo, "EXECUTION_FAILED") => OrderStatuses.ExecucaoComErro,
        _ => null
    };

    public static bool StartsExecution(string eventType) => eventType == "PAYMENT_APPROVED";
    public static bool IsTerminal(string status) => status is OrderStatuses.Finalizada or OrderStatuses.OrcamentoRejeitado or OrderStatuses.ExecucaoComErro;
}
