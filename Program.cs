using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<OsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("OsDatabase")));
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
    var order = new ServiceOrder
    {
        CustomerName = request.CustomerName,
        VehiclePlate = request.VehiclePlate,
        Description = request.Description,
        Status = "AGUARDANDO_ORCAMENTO",
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    await events.PublishAsync("OS_CREATED", new { orderId = order.Id, description = order.Description });
    return Results.Created($"/api/orders/{order.Id}", order);
});

app.MapGet("/api/orders", async (OsDbContext db) => Results.Ok(await db.Orders.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync()));

app.MapGet("/api/orders/{id:guid}", async (Guid id, OsDbContext db) =>
{
    var order = await db.Orders.FindAsync(id);
    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.MapPatch("/api/orders/{id:guid}/status", async (Guid id, UpdateStatusRequest request, OsDbContext db, EventPublisher events) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null) return Results.NotFound();
    order.Status = request.Status;
    order.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    await events.PublishAsync("OS_STATUS_UPDATED", new { orderId = id, order.Status });
    return Results.Ok(order);
});

app.Run();

public record CreateOrderRequest(string CustomerName, string VehiclePlate, string Description);
public record UpdateStatusRequest(string Status);

public class ServiceOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CustomerName { get; set; } = "";
    public string VehiclePlate { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class OsDbContext(DbContextOptions<OsDbContext> options) : DbContext(options)
{
    public DbSet<ServiceOrder> Orders => Set<ServiceOrder>();
}

public sealed class EventPublisher(IConfiguration configuration)
{
    public Task PublishAsync(string type, object payload)
    {
        var settings = configuration.GetSection("RabbitMq");
        var factory = new ConnectionFactory
        {
            HostName = settings["Host"] ?? "localhost",
            Port = settings.GetValue("Port", 5672),
            UserName = settings["User"] ?? "oficina",
            Password = settings["Password"] ?? "oficina"
        };
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, payload, occurredAt = DateTimeOffset.UtcNow }));
        channel.BasicPublish("oficina.events", type, null, body);
        return Task.CompletedTask;
    }
}

public sealed class OsEventConsumer(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<OsEventConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetSection("RabbitMq");
        var factory = new ConnectionFactory { HostName = settings["Host"] ?? "localhost", Port = settings.GetValue("Port", 5672), UserName = settings["User"] ?? "oficina", Password = settings["Password"] ?? "oficina" };
        var connection = factory.CreateConnection();
        var channel = connection.CreateModel();
        channel.ExchangeDeclare("oficina.events", ExchangeType.Topic, durable: true);
        var queue = channel.QueueDeclare("os-service-events", durable: true, exclusive: false, autoDelete: false).QueueName;
        channel.QueueBind(queue, "oficina.events", "BUDGET_CREATED");
        channel.QueueBind(queue, "oficina.events", "PAYMENT_APPROVED");
        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.Body.ToArray());
                var payload = document.RootElement.GetProperty("payload");
                var orderId = payload.GetProperty("orderId").GetGuid();
                var type = document.RootElement.GetProperty("type").GetString();
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OsDbContext>();
                var order = await db.Orders.FindAsync(orderId);
                if (order is not null)
                {
                    order.Status = type == "PAYMENT_APPROVED" ? "AGUARDANDO_EXECUCAO" : "AGUARDANDO_PAGAMENTO";
                    order.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();
                }
                channel.BasicAck(args.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao processar evento de OS");
                channel.BasicNack(args.DeliveryTag, false, true);
            }
        };
        channel.BasicConsume(queue, autoAck: false, consumer);
        return Task.CompletedTask;
    }
}
