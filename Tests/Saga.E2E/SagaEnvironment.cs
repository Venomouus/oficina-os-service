using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Reqnroll;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Saga.E2E;

[Binding]
public static class SagaEnvironment
{
    private static readonly PostgreSqlContainer Postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private static readonly MongoDbContainer Mongo = new MongoDbBuilder().WithImage("mongo:7").Build();
    private static readonly RabbitMqContainer Rabbit = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").WithUsername("test").WithPassword("test").Build();
    private static readonly List<Process> Processes = [];
    private static readonly StringBuilder Logs = new();
    public static HttpClient Os { get; private set; } = null!;
    public static HttpClient Billing { get; private set; } = null!;
    public static HttpClient Execution { get; private set; } = null!;

    [BeforeTestRun]
    public static async Task StartAsync()
    {
        try
        {
            await Task.WhenAll(Postgres.StartAsync(), Mongo.StartAsync(), Rabbit.StartAsync());
            var osRoot = FindOsRoot();
            var servicesRoot = Path.GetDirectoryName(osRoot)!;
            var billingRoot = Environment.GetEnvironmentVariable("SAGA_BILLING_DIR") ?? Path.Combine(servicesRoot, "billing-service");
            var executionRoot = Environment.GetEnvironmentVariable("SAGA_EXECUTION_DIR") ?? Path.Combine(servicesRoot, "execution-service");
            var shared = new Dictionary<string, string>
            {
                ["RabbitMq__Host"] = Rabbit.Hostname,
                ["RabbitMq__Port"] = Rabbit.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq__User"] = "test",
                ["RabbitMq__Password"] = "test",
                ["RabbitMq__RetryDelayMs"] = "100",
                ["Mongo__ConnectionString"] = Mongo.GetConnectionString()
            };
            Os = await LaunchAsync(osRoot, "OsService", shared, new Dictionary<string, string> { ["ConnectionStrings__OsDatabase"] = Postgres.GetConnectionString() });
            Billing = await LaunchAsync(billingRoot, "BillingService", shared, new Dictionary<string, string> { ["Mongo__Database"] = "saga_billing" });
            Execution = await LaunchAsync(executionRoot, "ExecutionService", shared, new Dictionary<string, string> { ["Mongo__Database"] = "saga_execution" });
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    [AfterTestRun]
    public static async Task StopAsync()
    {
        foreach (var process in Processes)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
        Processes.Clear();
        Os?.Dispose(); Billing?.Dispose(); Execution?.Dispose();
        await Task.WhenAll(Postgres.DisposeAsync().AsTask(), Mongo.DisposeAsync().AsTask(), Rabbit.DisposeAsync().AsTask());
    }

    private static async Task<HttpClient> LaunchAsync(string root, string assembly, Dictionary<string, string> shared, Dictionary<string, string> specific)
    {
        root = Path.GetFullPath(root);
        var configuration = Environment.GetEnvironmentVariable("SAGA_BUILD_CONFIGURATION") ?? "Release";
        var dll = Path.Combine(root, "bin", configuration, "net8.0", assembly + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"Compile {assembly} em {configuration} antes do BDD: {dll}");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        foreach (var (key, value) in shared) start.Environment[key] = value;
        foreach (var (key, value) in specific) start.Environment[key] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Nao iniciou {assembly}");
        Processes.Add(process);
        _ = DrainAsync(process.StandardOutput, assembly);
        _ = DrainAsync(process.StandardError, assembly);
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"{assembly} encerrou com codigo {process.ExitCode}. Logs:\n{Logs}");
            try { if ((await client.GetAsync("/health")).IsSuccessStatusCode) return client; }
            catch (HttpRequestException) { }
            await Task.Delay(300);
        }
        throw new TimeoutException($"{assembly} nao respondeu /health. Logs:\n{Logs}");
    }

    private static async Task DrainAsync(StreamReader reader, string service)
    {
        while (await reader.ReadLineAsync() is { } line)
            lock (Logs) { Logs.AppendLine($"[{service}] {line}"); }
    }

    private static string FindOsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OsService.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("OsService.csproj nao encontrado");
    }
}
