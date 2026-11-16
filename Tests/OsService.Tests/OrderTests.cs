using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
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
        var state = OrderStatuses.AguardandoOrcamento;
        foreach (var (type, expected) in new[] {
            ("BUDGET_CREATED", OrderStatuses.AguardandoPagamento),
            ("PAYMENT_APPROVED", OrderStatuses.AguardandoExecucao),
            ("EXECUTION_FINISHED", OrderStatuses.Finalizada) })
        {
            state = OrderSaga.NextStatus(state, type)!;
            Assert.Equal(expected, state);
        }
        Assert.Null(OrderSaga.NextStatus(state, "PAYMENT_REJECTED"));
        Assert.Null(OrderSaga.NextStatus(state, "EXECUTION_FINISHED"));
        Assert.Equal(OrderStatuses.OrcamentoRejeitado, OrderSaga.NextStatus(OrderStatuses.AguardandoPagamento, "BUDGET_REJECTED"));
        Assert.Equal(OrderStatuses.PagamentoRecusado, OrderSaga.NextStatus(OrderStatuses.AguardandoPagamento, "PAYMENT_REJECTED"));
        Assert.Equal(OrderStatuses.ExecucaoComErro, OrderSaga.NextStatus(OrderStatuses.AguardandoExecucao, "EXECUTION_FAILED"));
        Assert.True(OrderSaga.StartsExecution("PAYMENT_APPROVED"));
    }

    [Fact]
    public void Duplicate_And_OutOfOrder_Events_Do_Not_Reopen_Or_Finalize_Order()
    {
        Assert.Null(OrderSaga.NextStatus(OrderStatuses.AguardandoOrcamento, "EXECUTION_FINISHED"));
        Assert.Null(OrderSaga.NextStatus(OrderStatuses.AguardandoPagamento, "BUDGET_CREATED"));
        Assert.Null(OrderSaga.NextStatus(OrderStatuses.OrcamentoRejeitado, "PAYMENT_APPROVED"));
        Assert.Null(OrderSaga.NextStatus(OrderStatuses.ExecucaoComErro, "EXECUTION_FINISHED"));
        Assert.Equal(OrderStatuses.AguardandoExecucao, OrderSaga.NextStatus(OrderStatuses.PagamentoRecusado, "PAYMENT_APPROVED"));
    }

    [Theory]
    [InlineData(OrderStatuses.AguardandoOrcamento, "BUDGET_REJECTED", OrderStatuses.OrcamentoRejeitado)]
    [InlineData(OrderStatuses.EmDiagnostico, "EXECUTION_FINISHED", OrderStatuses.Finalizada)]
    [InlineData(OrderStatuses.EmReparo, "EXECUTION_FINISHED", OrderStatuses.Finalizada)]
    [InlineData(OrderStatuses.EmDiagnostico, "EXECUTION_FAILED", OrderStatuses.ExecucaoComErro)]
    [InlineData(OrderStatuses.EmReparo, "EXECUTION_FAILED", OrderStatuses.ExecucaoComErro)]
    public void Saga_Handles_Compensation_And_Execution_From_Intermediate_States(string current, string eventType, string expected)
    {
        Assert.Equal(expected, OrderSaga.NextStatus(current, eventType));
        Assert.False(OrderSaga.StartsExecution(eventType));
        Assert.False(OrderSaga.IsTerminal(current));
        Assert.True(OrderSaga.IsTerminal(expected));
    }

    [Fact]
    public async Task Outbox_Retries_After_Broker_Failure_Without_Losing_Event()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<OsDbContext>(x => x.UseInMemoryDatabase(databaseName));
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OsDbContext>();
            db.Outbox.Add(SagaOutboxEvent.Create("OS_CREATED", new { orderId = Guid.NewGuid() }));
            await db.SaveChangesAsync();
        }
        var publisher = new FailsOncePublisher();
        using var worker = new OsOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), publisher, NullLogger<OsOutboxWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            SagaOutboxEvent? item = null;
            while (item?.PublishedAt is null && !timeout.IsCancellationRequested)
            {
                await using var scope = provider.CreateAsyncScope();
                item = await scope.ServiceProvider.GetRequiredService<OsDbContext>().Outbox.AsNoTracking().SingleAsync();
                if (item.PublishedAt is null) await Task.Delay(100, timeout.Token);
            }
            Assert.Equal(2, publisher.Attempts);
            Assert.NotNull(item?.PublishedAt);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class FailsOncePublisher : IEventPublisher
    {
        public int Attempts;
        public Task PublishAsync(string type, string body)
        {
            if (Interlocked.Increment(ref Attempts) == 1) throw new InvalidOperationException("RabbitMQ indisponivel");
            return Task.CompletedTask;
        }
    }
}
