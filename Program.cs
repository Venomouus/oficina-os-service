using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Oficina.Os;

var builder = WebApplication.CreateBuilder(args);
Observability.Configure(builder);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
builder.Services.AddDbContext<OsDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("OsDatabase")));
builder.Services.AddScoped<OsSagaHandler>();
builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var settings = sp.GetRequiredService<IConfiguration>().GetSection("RabbitMq");
    return new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" };
});
builder.Services.AddSingleton<IEventPublisher, EventPublisher>();
builder.Services.AddHostedService<OsEventConsumer>();
builder.Services.AddHostedService<OsOutboxWorker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OsDbContext>();
    await db.Database.EnsureCreatedAsync();
    // EnsureCreated nao altera bancos ja existentes.
    if (db.Database.IsRelational())
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS saga_outbox (\"Id\" uuid PRIMARY KEY, \"Type\" text NOT NULL, \"Body\" text NOT NULL, \"CreatedAt\" timestamptz NOT NULL, \"PublishedAt\" timestamptz NULL)");
}

app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { service = "os-service", status = "UP" }));

app.MapPost("/api/orders", async (CreateOrderRequest request, OsDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerName) || string.IsNullOrWhiteSpace(request.VehiclePlate) || string.IsNullOrWhiteSpace(request.Description))
        return Results.BadRequest(new { error = "customerName, vehiclePlate e description sao obrigatorios" });

    var order = new ServiceOrder { CustomerName = request.CustomerName.Trim(), VehiclePlate = request.VehiclePlate.Trim().ToUpperInvariant(), Description = request.Description.Trim(), Status = OrderStatuses.AguardandoOrcamento, CreatedAt = DateTimeOffset.UtcNow };
    db.Orders.Add(order);
    AddHistory(order, order.Status, "OS criada");
    db.Outbox.Add(SagaOutboxEvent.Create("OS_CREATED", new { orderId = order.Id, description = order.Description }));
    await db.SaveChangesAsync();
    return Results.Created($"/api/orders/{order.Id}", order);
});

app.MapGet("/api/orders", async (OsDbContext db) => Results.Ok(await db.Orders.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync()));
app.MapGet("/api/orders/{id:guid}", async (Guid id, OsDbContext db) => { var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id); return order is null ? Results.NotFound() : Results.Ok(order); });
app.MapGet("/api/orders/{id:guid}/history", async (Guid id, OsDbContext db) => Results.Ok(await db.OrderHistories.AsNoTracking().Where(x => x.OrderId == id).OrderBy(x => x.CreatedAt).ToListAsync()));

app.MapPatch("/api/orders/{id:guid}/status", async (Guid id, UpdateStatusRequest request, OsDbContext db) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null) return Results.NotFound();
    if (!OrderStatuses.All.Contains(request.Status)) return Results.BadRequest(new { error = "status invalido" });
    var status = OrderStatuses.All.First(x => string.Equals(x, request.Status, StringComparison.OrdinalIgnoreCase));
    if (order.Status == status) return Results.Ok(order);
    if (OrderSaga.IsTerminal(order.Status)) return Results.Conflict(new { error = "OS em estado terminal" });
    db.Outbox.Add(SagaOutboxEvent.Create("OS_STATUS_UPDATED", new { orderId = id, status }));
    await ChangeStatusAsync(order, status, "Atualizacao manual", db);
    return Results.Ok(order);
});

app.Run();

static void AddHistory(ServiceOrder order, string status, string reason) => order.History.Add(new OrderHistory { OrderId = order.Id, Status = status, Reason = reason, CreatedAt = DateTimeOffset.UtcNow });
static async Task ChangeStatusAsync(ServiceOrder order, string status, string reason, OsDbContext db) { await OrderState.ChangeStatusAsync(order, status, reason, db); }

public partial class Program { }

namespace Oficina.Os
{
public record CreateOrderRequest(string CustomerName, string VehiclePlate, string Description);
public record UpdateStatusRequest(string Status);

public static class OrderStatuses
{
    public const string AguardandoOrcamento = "AGUARDANDO_ORCAMENTO";
    public const string AguardandoPagamento = "AGUARDANDO_PAGAMENTO";
    public const string PagamentoConfirmado = "PAGAMENTO_CONFIRMADO";
    public const string AguardandoExecucao = "AGUARDANDO_EXECUCAO";
    public const string EmDiagnostico = "EM_DIAGNOSTICO";
    public const string EmReparo = "EM_REPARO";
    public const string Finalizada = "FINALIZADA";
    public const string OrcamentoRejeitado = "ORCAMENTO_REJEITADO";
    public const string PagamentoRecusado = "PAGAMENTO_RECUSADO";
    public const string ExecucaoComErro = "EXECUCAO_COM_ERRO";
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase) { AguardandoOrcamento, AguardandoPagamento, PagamentoConfirmado, AguardandoExecucao, EmDiagnostico, EmReparo, Finalizada, OrcamentoRejeitado, PagamentoRecusado, ExecucaoComErro };
}

public class ServiceOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CustomerName { get; set; } = "";
    public string VehiclePlate { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<OrderHistory> History { get; set; } = [];
}

public class OrderHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class OsDbContext(DbContextOptions<OsDbContext> options) : DbContext(options)
{
    public DbSet<ServiceOrder> Orders => Set<ServiceOrder>();
    public DbSet<OrderHistory> OrderHistories => Set<OrderHistory>();
    public DbSet<SagaOutboxEvent> Outbox => Set<SagaOutboxEvent>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) { modelBuilder.Entity<ServiceOrder>().HasMany(x => x.History).WithOne().HasForeignKey(x => x.OrderId); modelBuilder.Entity<ServiceOrder>().Property(x => x.Status).HasMaxLength(50).IsConcurrencyToken(); modelBuilder.Entity<SagaOutboxEvent>().ToTable("saga_outbox"); }
}

public class SagaOutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public static SagaOutboxEvent Create(string type, object payload) => new() { Type = type, Body = JsonSerializer.Serialize(new { type, payload, occurredAt = DateTimeOffset.UtcNow }) };
}

public static class OrderState
{
    public static async Task ChangeStatusAsync(ServiceOrder order, string status, string reason, OsDbContext db)
    {
        order.Status = status;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        db.OrderHistories.Add(new OrderHistory { OrderId = order.Id, Status = status, Reason = reason, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
}

public sealed class OsSagaHandler(OsDbContext db)
{
    public async Task<bool> HandleAsync(Guid orderId, string type)
    {
        var order = await db.Orders.FindAsync(orderId);
        if (order is null) return false;
        var next = OrderSaga.NextStatus(order.Status, type);
        if (next is null) return false;
        if (OrderSaga.StartsExecution(type)) db.Outbox.Add(SagaOutboxEvent.Create("OS_READY_FOR_EXECUTION", new { orderId }));
        await OrderState.ChangeStatusAsync(order, next, $"Evento {type}", db);
        return true;
    }
}

public interface IEventPublisher { Task PublishAsync(string type, string body); }

public sealed class EventPublisher(IConnectionFactory factory) : IEventPublisher
{
    public Task PublishAsync(string type, string body)
    {
        using var activity = Observability.Activities.StartActivity("saga.publish", ActivityKind.Producer);
        activity?.SetTag("messaging.destination.name", "oficina.events");
        activity?.SetTag("messaging.operation.name", "publish");
        activity?.SetTag("saga.event.type", type);
        using var connection = factory.CreateConnection(); using var channel = connection.CreateModel(); channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true);
        channel.QueueDeclare("billing-service-events", true, false, false);
        channel.QueueBind("billing-service-events", "oficina.events", "OS_CREATED");
        channel.QueueDeclare("execution-service-events", true, false, false);
        channel.QueueBind("execution-service-events", "oficina.events", "OS_READY_FOR_EXECUTION");
        channel.ConfirmSelect();
        var properties = channel.CreateBasicProperties(); properties.Persistent = true;
        channel.BasicPublish("oficina.events", type, properties, Encoding.UTF8.GetBytes(body));
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        Observability.Published.Add(1, new KeyValuePair<string, object?>("event.type", type));
        return Task.CompletedTask;
    }
}

public sealed class OsOutboxWorker(IServiceScopeFactory scopes, IEventPublisher events, ILogger<OsOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OsDbContext>();
                var pending = await db.Outbox.Where(x => x.PublishedAt == null).OrderBy(x => x.CreatedAt).Take(50).ToListAsync(stoppingToken);
                foreach (var item in pending)
                {
                    await events.PublishAsync(item.Type, item.Body);
                    item.PublishedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Falha ao publicar outbox de OS; sera tentado novamente"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

public sealed class OsEventConsumer(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<OsEventConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => ReliableRabbitConsumer.RunAsync(
        configuration, "os-service-events",
        ["BUDGET_CREATED", "BUDGET_REJECTED", "PAYMENT_APPROVED", "PAYMENT_REJECTED", "EXECUTION_FINISHED", "EXECUTION_FAILED"],
        async body =>
    {
        using var document = JsonDocument.Parse(body); var payload = document.RootElement.GetProperty("payload"); var orderId = payload.GetProperty("orderId").GetGuid(); var type = document.RootElement.GetProperty("type").GetString() ?? ""; using var scope = scopes.CreateScope(); var handler = scope.ServiceProvider.GetRequiredService<OsSagaHandler>();
        await handler.HandleAsync(orderId, type);
    }, logger, stoppingToken);
}
}
