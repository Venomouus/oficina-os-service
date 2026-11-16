using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using Xunit;

[Binding]
public sealed class OrderLifecycleSteps : IDisposable
{
    private readonly OsApiFactory _factory = new();
    private readonly HttpClient _client;
    private Guid _orderId;

    public OrderLifecycleSteps() => _client = _factory.CreateClient();

    [Given("uma OS criada pela API")]
    public async Task GivenOrderCreated()
    {
        var response = await _client.PostAsJsonAsync("/api/orders", new CreateOrderRequest("Cliente BDD", "BDD1A23", "Revisao"));
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<ServiceOrder>();
        _orderId = Assert.IsType<ServiceOrder>(order).Id;
    }

    [When("chega o evento {string}")]
    public async Task WhenEventArrives(string eventType)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OsSagaHandler>().HandleAsync(_orderId, eventType);
    }

    [Then("a OS deve estar com status {string}")]
    public async Task ThenOrderHasStatus(string expected)
    {
        var order = await _client.GetFromJsonAsync<ServiceOrder>($"/api/orders/{_orderId}");
        Assert.Equal(expected, Assert.IsType<ServiceOrder>(order).Status);
    }

    [Then("o historico deve ter {int} transicoes")]
    public async Task ThenHistoryHasTransitions(int expected)
    {
        var history = await _client.GetFromJsonAsync<List<OrderHistory>>($"/api/orders/{_orderId}/history");
        Assert.Equal(expected, Assert.IsType<List<OrderHistory>>(history).Count);
    }

    public void Dispose() { _client.Dispose(); _factory.Dispose(); }
}
