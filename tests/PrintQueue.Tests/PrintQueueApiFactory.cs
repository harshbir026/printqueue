using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintQueue.Api.Data;

namespace PrintQueue.Tests;

public sealed class PrintQueueApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"printqueue-http-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(IsPrintQueueContextRegistration).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<PrintQueueContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
        });
    }

    private static bool IsPrintQueueContextRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(PrintQueueContext)
        || descriptor.ServiceType == typeof(DbContextOptions<PrintQueueContext>)
        || (descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>)
            && descriptor.ServiceType.GenericTypeArguments[0] == typeof(PrintQueueContext));
}
