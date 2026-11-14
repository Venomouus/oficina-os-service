using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<OsDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("OsDatabase")));
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddHostedService<OsEventConsumer>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { service = "os-service", status = "UP" }));

app.MapPost("/api/orders", async (CreateOrderRequest request, OsDbContext db, EventPublisher events) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerName) || string.IsNullOrWhiteSpace(request.VehiclePlate) || string.IsNullOrWhiteSpace(request.Description))
        return Results.BadRequest(new { error = "customerName, vehiclePlate e description sao obrigatorios" });

    var order = new ServiceOrder { CustomerName = request.CustomerName.Trim(), VehiclePlate = request.VehiclePlate.Trim().ToUpperInvariant(), Description = request.Description.Trim(), Status = OrderStatuses.AguardandoOrcamento, CreatedAt = DateTimeOffset.UtcNow };
    db.Orders.Add(order);
    AddHistory(order, order.Status, "OS criada");
    await db.SaveChangesAsync();
    await events.PublishAsync("OS_CREATED", new { orderId = order.Id, description = order.Description });
    return Results.Created($"/api/orders/{order.Id}", order);
});

app.MapGet("/api/orders", async (OsDbContext db) => Results.Ok(await db.Orders.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync()));
app.MapGet("/api/orders/{id:guid}", async (Guid id, OsDbContext db) => { var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id); return order is null ? Results.NotFound() : Results.Ok(order); });
app.MapGet("/api/orders/{id:guid}/history", async (Guid id, OsDbContext db) => Results.Ok(await db.OrderHistories.AsNoTracking().Where(x => x.OrderId == id).OrderBy(x => x.CreatedAt).ToListAsync()));

app.MapPatch("/api/orders/{id:guid}/status", async (Guid id, UpdateStatusRequest request, OsDbContext db, EventPublisher events) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null) return Results.NotFound();
    if (!OrderStatuses.All.Contains(request.Status)) return Results.BadRequest(new { error = "status invalido" });
    await ChangeStatusAsync(order, request.Status, "Atualizacao manual", db);
    await events.PublishAsync("OS_STATUS_UPDATED", new { orderId = id, status = order.Status });
    return Results.Ok(order);
});

app.Run();

static void AddHistory(ServiceOrder order, string status, string reason) => order.History.Add(new OrderHistory { OrderId = order.Id, Status = status, Reason = reason, CreatedAt = DateTimeOffset.UtcNow });
static async Task ChangeStatusAsync(ServiceOrder order, string status, string reason, OsDbContext db) { await OrderState.ChangeStatusAsync(order, status, reason, db); }

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
    protected override void OnModelCreating(ModelBuilder modelBuilder) { modelBuilder.Entity<ServiceOrder>().HasMany(x => x.History).WithOne().HasForeignKey(x => x.OrderId); modelBuilder.Entity<ServiceOrder>().Property(x => x.Status).HasMaxLength(50); }
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

public sealed class EventPublisher(IConfiguration configuration)
{
    public Task PublishAsync(string type, object payload)
    {
        var settings = configuration.GetSection("RabbitMq");
        var factory = new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" };
        using var connection = factory.CreateConnection(); using var channel = connection.CreateModel(); channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true); channel.BasicPublish("oficina.events", type, null, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, payload, occurredAt = DateTimeOffset.UtcNow }))); return Task.CompletedTask;
    }
}

public sealed class OsEventConsumer(IServiceScopeFactory scopes, IConfiguration configuration, EventPublisher events, ILogger<OsEventConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetSection("RabbitMq"); var factory = new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" }; var connection = factory.CreateConnection(); var channel = connection.CreateModel(); channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true); var queue = channel.QueueDeclare("os-service-events", true, false, false).QueueName;
        foreach (var type in new[] { "BUDGET_CREATED", "BUDGET_REJECTED", "PAYMENT_APPROVED", "PAYMENT_REJECTED", "EXECUTION_FINISHED", "EXECUTION_FAILED" }) channel.QueueBind(queue, "oficina.events", type);
        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.Body.ToArray()); var payload = document.RootElement.GetProperty("payload"); var orderId = payload.GetProperty("orderId").GetGuid(); var type = document.RootElement.GetProperty("type").GetString() ?? ""; using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OsDbContext>(); var order = await db.Orders.FindAsync(orderId);
                if (order is not null)
                {
                    var next = OrderSaga.NextStatusForEvent(type);
                    await OrderState.ChangeStatusAsync(order, next, $"Evento {type}", db); if (OrderSaga.StartsExecution(type)) await events.PublishAsync("OS_READY_FOR_EXECUTION", new { orderId });
                }
                channel.BasicAck(args.DeliveryTag, false);
            }
            catch (Exception ex) { logger.LogError(ex, "Falha ao processar evento de OS"); channel.BasicNack(args.DeliveryTag, false, true); }
        };
        channel.BasicConsume(queue, false, consumer); return Task.CompletedTask;
    }
}
