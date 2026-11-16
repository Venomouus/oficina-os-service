using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class OsApiTests
{
    [Fact]
    public async Task Create_Rejects_Incomplete_Order()
    {
        using var factory = new OsApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new CreateOrderRequest("", "ABC1234", "Freios"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_Creates_Queries_And_Protects_Terminal_Order()
    {
        using var factory = new OsApiFactory();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/orders", new CreateOrderRequest(" Cliente ", " abc1d23 ", " Revisao "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<ServiceOrder>())!;
        Assert.Equal("Cliente", order.CustomerName);
        Assert.Equal("ABC1D23", order.VehiclePlate);
        Assert.Equal("Revisao", order.Description);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<List<ServiceOrder>>("/api/orders"))!, x => x.Id == order.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/orders/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/orders/{Guid.NewGuid()}/status", new UpdateStatusRequest(OrderStatuses.Finalizada))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest("INVALIDO"))).StatusCode);

        var changed = await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest("em_diagnostico"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(OrderStatuses.EmDiagnostico, (await changed.Content.ReadFromJsonAsync<ServiceOrder>())!.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest(OrderStatuses.EmDiagnostico))).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<OrderHistory>>($"/api/orders/{order.Id}/history"))!.Count);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest(OrderStatuses.Finalizada))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest(OrderStatuses.AguardandoPagamento))).StatusCode);
        Assert.Equal(OrderStatuses.Finalizada, (await client.GetFromJsonAsync<ServiceOrder>($"/api/orders/{order.Id}"))!.Status);

        using var scope = factory.Services.CreateScope();
        var outbox = await scope.ServiceProvider.GetRequiredService<OsDbContext>().Outbox.ToListAsync();
        Assert.Contains(outbox, x => x.Type == "OS_CREATED");
        Assert.Contains(outbox, x => x.Type == "OS_STATUS_UPDATED");
    }

    [Fact]
    public async Task Handler_Ignores_Unknown_And_Duplicate_Events_Without_Extra_Outbox()
    {
        using var factory = new OsApiFactory();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/orders", new CreateOrderRequest("Cliente", "ABC1D23", "Freios"));
        var order = (await created.Content.ReadFromJsonAsync<ServiceOrder>())!;
        using var scope = factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<OsSagaHandler>();
        Assert.False(await handler.HandleAsync(Guid.NewGuid(), "BUDGET_CREATED"));
        Assert.False(await handler.HandleAsync(order.Id, "EXECUTION_FINISHED"));
        Assert.True(await handler.HandleAsync(order.Id, "BUDGET_CREATED"));
        Assert.False(await handler.HandleAsync(order.Id, "BUDGET_CREATED"));
        Assert.True(await handler.HandleAsync(order.Id, "PAYMENT_APPROVED"));
        Assert.False(await handler.HandleAsync(order.Id, "PAYMENT_APPROVED"));
        var db = scope.ServiceProvider.GetRequiredService<OsDbContext>();
        Assert.Single(await db.Outbox.Where(x => x.Type == "OS_READY_FOR_EXECUTION").ToListAsync());
    }
}
