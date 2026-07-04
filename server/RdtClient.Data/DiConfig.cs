using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RdtClient.Data.Data;
using RdtClient.Data.Models.Internal;

namespace RdtClient.Data;

public static class DiConfig
{
    public static void Config(IServiceCollection services, AppSettings appSettings)
    {
        var connectionString = appSettings.Database?.ConnectionString;

        if (String.IsNullOrWhiteSpace(connectionString))
        {
            throw new("No PostgreSQL connection string found. Set Database:ConnectionString in appsettings.");
        }

        services.AddDbContext<DataContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<DownloadData>();
        services.AddScoped<SettingData>();
        services.AddScoped<ITorrentData, TorrentData>();
        services.AddScoped<UserData>();
    }
}
