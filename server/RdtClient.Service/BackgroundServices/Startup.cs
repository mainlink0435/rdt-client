using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Services;

namespace RdtClient.Service.BackgroundServices;

public class Startup(IServiceProvider serviceProvider) : IHostedService
{
    public static Boolean Ready { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;

        using var scope = serviceProvider.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Startup>>();
        var appSettings = scope.ServiceProvider.GetRequiredService<AppSettings>();

        logger.LogWarning("Starting host on version {version}", version);

        var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();

        // Create the schema if it doesn't exist (overcome stale migration history)
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        // If PostgreSQL is configured, attempt to migrate data from SQLite
        var pgConnectionString = appSettings.Database?.ConnectionString;
        if (!String.IsNullOrWhiteSpace(pgConnectionString))
        {
            await SqliteToPostgresMigrator.MigrateIfNeeded(appSettings, logger, cancellationToken);
        }

        var settings = scope.ServiceProvider.GetRequiredService<Settings>();
        await settings.Seed();
        await settings.ResetCache();

        Ready = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Ready = false;

        return Task.CompletedTask;
    }
}
