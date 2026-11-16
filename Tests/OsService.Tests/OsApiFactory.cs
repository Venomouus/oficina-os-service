using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class OsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"os-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService)
                && (x.ImplementationType == typeof(OsEventConsumer) || x.ImplementationType == typeof(OsOutboxWorker))).ToArray())
                services.Remove(descriptor);
            foreach (var descriptor in services.Where(x => x.ServiceType == typeof(DbContextOptions<OsDbContext>)
                || x.ServiceType == typeof(OsDbContext)).ToArray())
                services.Remove(descriptor);
            services.AddDbContext<OsDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
