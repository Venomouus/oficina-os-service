using System.Net.Http.Json;
using System.Text.Json;
using Reqnroll;
using Xunit;

namespace Saga.E2E;

[Binding]
public sealed class DistributedSagaSteps
{
    private string _orderId = "";
    private string _budgetId = "";

    [Given("uma ordem de servico nova com orcamento criado")]
    public async Task CreateOrderAsync()
    {
        using var response = await SagaEnvironment.Os.PostAsJsonAsync("/api/orders", new
        {
            customerName = "Cliente BDD",
            vehiclePlate = "BDD1234",
            description = "Teste da saga distribuida"
        });
        response.EnsureSuccessStatusCode();
        using var order = await ReadAsync(response);
        _orderId = order.RootElement.GetProperty("id").GetString()!;
        using var budget = await WaitForAsync(SagaEnvironment.Billing, "/api/budgets", body =>
            body.ValueKind == JsonValueKind.Array && body.EnumerateArray().Any(x => x.GetProperty("orderId").GetString() == _orderId));
        _budgetId = budget.RootElement.EnumerateArray().First(x => x.GetProperty("orderId").GetString() == _orderId).GetProperty("id").GetString()!;
    }

    [When("o orcamento e aprovado e o pagamento sandbox e confirmado")]
    public async Task PayAsync()
    {
        using (var amount = await SagaEnvironment.Billing.PatchAsJsonAsync($"/api/budgets/{_budgetId}/amount", new { amount = 154 })) amount.EnsureSuccessStatusCode();
        using (var approval = await SagaEnvironment.Billing.PostAsync($"/api/budgets/{_budgetId}/approve", null)) approval.EnsureSuccessStatusCode();
        using var awaiting = await WaitForAsync(SagaEnvironment.Os, $"/api/orders/{_orderId}", body => body.GetProperty("status").GetString() == "AGUARDANDO_PAGAMENTO");
        using var payment = await SagaEnvironment.Billing.PostAsync($"/api/budgets/{_budgetId}/payment", null);
        payment.EnsureSuccessStatusCode();
    }

    [Then("a ordem deve chegar a aguardando execucao")]
    public async Task AssertQueuedAsync()
    {
        using var order = await WaitForAsync(SagaEnvironment.Os, $"/api/orders/{_orderId}", body => body.GetProperty("status").GetString() == "AGUARDANDO_EXECUCAO");
        using var execution = await WaitForAsync(SagaEnvironment.Execution, "/api/execution", body =>
            body.ValueKind == JsonValueKind.Array && body.EnumerateArray().Any(x => x.GetProperty("orderId").GetString() == _orderId));
    }

    [When("a execucao e finalizada")]
    public async Task FinishAsync()
    {
        await UpdateExecutionAsync("EM_DIAGNOSTICO");
        await UpdateExecutionAsync("EM_REPARO");
        await UpdateExecutionAsync("FINALIZADA");
    }

    [When("a execucao falha")]
    public Task FailAsync() => UpdateExecutionAsync("EXECUCAO_COM_ERRO");

    [Then("a ordem deve chegar a finalizada")]
    public async Task AssertFinishedAsync()
    {
        using var order = await WaitForAsync(SagaEnvironment.Os, $"/api/orders/{_orderId}", body => body.GetProperty("status").GetString() == "FINALIZADA");
    }

    [Then("a ordem deve chegar a erro de execucao")]
    public async Task AssertFailedAsync()
    {
        using var order = await WaitForAsync(SagaEnvironment.Os, $"/api/orders/{_orderId}", body => body.GetProperty("status").GetString() == "EXECUCAO_COM_ERRO");
    }

    [Then("o orcamento deve continuar pago")]
    public async Task AssertPaidAsync()
    {
        using var budget = await WaitForAsync(SagaEnvironment.Billing, $"/api/budgets/{_budgetId}", body => body.GetProperty("status").GetString() == "PAID");
    }

    [Then("o historico da ordem deve registrar quatro transicoes")]
    public async Task AssertHistoryAsync()
    {
        using var history = await WaitForAsync(SagaEnvironment.Os, $"/api/orders/{_orderId}/history", body => body.ValueKind == JsonValueKind.Array && body.GetArrayLength() == 4);
        Assert.Equal(4, history.RootElement.GetArrayLength());
    }

    private async Task UpdateExecutionAsync(string status)
    {
        using var response = await SagaEnvironment.Execution.PatchAsJsonAsync($"/api/execution/{_orderId}/status", new { status, reason = status == "EXECUCAO_COM_ERRO" ? "Falha simulada" : null });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonDocument> WaitForAsync(HttpClient client, string url, Func<JsonElement, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var body = await ReadAsync(response);
            last = body.RootElement.GetRawText();
            if (predicate(body.RootElement)) return body;
            body.Dispose();
            await Task.Delay(300);
        }
        throw new TimeoutException($"Estado esperado nao chegou em {url}. Ultima resposta: {last}");
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
