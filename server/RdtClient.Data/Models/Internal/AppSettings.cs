namespace RdtClient.Data.Models.Internal;

public class AppSettings
{
    public AppSettingsLogging? Logging { get; set; }
    public AppSettingsDatabase? Database { get; set; }

    public Int32 Port { get; set; }
    public String? BasePath { get; set; }
}

public class AppSettingsLogging
{
    public AppSettingsLoggingFile? File { get; set; }
}

public class AppSettingsLoggingFile
{
    public String? Path { get; set; }
    public Int64 FileSizeLimitBytes { get; set; }
    public Int32 MaxRollingFiles { get; set; }
}

public class AppSettingsDatabase
{
    public String? Path { get; set; }

    /// <summary>
    /// PostgreSQL connection string. If set, overrides <see cref="Path"/>.
    /// Format: Host=host;Port=5432;Database=rdtclient;Username=user;Password=pass
    /// </summary>
    public String? ConnectionString { get; set; }
}
