using System;
using System.Threading.Tasks;
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
}
