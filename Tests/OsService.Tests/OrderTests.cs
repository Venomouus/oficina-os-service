using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class OrderTests
{
    [Fact]
    public void Statuses_Contain_Expected_Workflow()
    {
        Assert.Contains(OrderStatuses.AguardandoOrcamento, OrderStatuses.All);
        Assert.Contains(OrderStatuses.AguardandoExecucao, OrderStatuses.All);
        Assert.Contains(OrderStatuses.Finalizada, OrderStatuses.All);
    }

    [Fact]
    public async Task ChangeStatus_Updates_Order_And_History()
    {
        var options = new DbContextOptionsBuilder<OsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new OsDbContext(options);
        var order = new ServiceOrder { Status = OrderStatuses.AguardandoOrcamento };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await OrderState.ChangeStatusAsync(order, OrderStatuses.AguardandoPagamento, "teste", db);

        Assert.Equal(OrderStatuses.AguardandoPagamento, order.Status);
        Assert.NotNull(order.UpdatedAt);
        Assert.Single(await db.OrderHistories.ToListAsync());
    }

    [Fact]
    public void Bdd_Saga_Completes_Order_Flow_And_Compensates_Failures()
    {
        // Given uma OS criada, When os eventos do fluxo chegam, Then cada etapa avanca corretamente.
        var events = new[] { "BUDGET_CREATED", "PAYMENT_APPROVED", "EXECUTION_FINISHED" };
        var statuses = events.Select(OrderSaga.NextStatusForEvent).ToArray();

        Assert.Equal(new[]
        {
            OrderStatuses.AguardandoPagamento,
            OrderStatuses.AguardandoExecucao,
            OrderStatuses.Finalizada
        }, statuses);

        // Caminhos de compensacao encerram a etapa com um estado explicito.
        Assert.Equal(OrderStatuses.OrcamentoRejeitado, OrderSaga.NextStatusForEvent("BUDGET_REJECTED"));
        Assert.Equal(OrderStatuses.PagamentoRecusado, OrderSaga.NextStatusForEvent("PAYMENT_REJECTED"));
        Assert.Equal(OrderStatuses.ExecucaoComErro, OrderSaga.NextStatusForEvent("EXECUTION_FAILED"));
        Assert.True(OrderSaga.StartsExecution("PAYMENT_APPROVED"));
    }
}
