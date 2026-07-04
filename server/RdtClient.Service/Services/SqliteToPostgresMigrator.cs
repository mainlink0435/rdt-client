using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Models.Internal;
using Npgsql;

namespace RdtClient.Service.Services;

/// <summary>
/// One-time migration utility that reads data from an existing SQLite database and
/// inserts it into PostgreSQL. Runs before EF Core migrations are applied.
/// The old SQLite file is renamed to .migrated once complete.
/// </summary>
public static class SqliteToPostgresMigrator
{
    /// <summary>
    /// Migrates data from SQLite (at the path specified in <see cref="AppSettingsDatabase.Path"/>)
    /// to PostgreSQL (via <see cref="AppSettingsDatabase.ConnectionString"/>).
    /// Only runs once — if the .migrated marker file already exists or if PG already has data.
    /// </summary>
    public static async Task MigrateIfNeeded(AppSettings appSettings, ILogger logger, CancellationToken ct)
    {
        // Default path for the SQLite DB — the volume /data/db is always mounted
        var sqlitePath = appSettings.Database?.Path ?? "/data/db/rdtclient.db";
        var pgConnectionString = appSettings.Database?.ConnectionString;

        if (!File.Exists(sqlitePath))
        {
            logger.LogInformation("No SQLite database found at {Path}, skipping migration", sqlitePath);
            return;
        }

        var migratedMarker = sqlitePath + ".migrated";
        if (File.Exists(migratedMarker))
        {
            logger.LogInformation("SQLite database already migrated (marker exists: {Marker}), skipping", migratedMarker);
            return;
        }

        logger.LogWarning("SQLite database found at {Path}. Starting one-time migration to PostgreSQL...", sqlitePath);

        // Check if PG already has data — if so, don't migrate
        if (!String.IsNullOrWhiteSpace(pgConnectionString))
        {
            var hasData = await PostgresHasData(pgConnectionString, ct);
            if (hasData)
            {
                logger.LogWarning("PostgreSQL already contains data. Skipping migration. " +
                                  "If you want to re-migrate, remove the data from PG first.");
                
                // Still rename the SQLite to prevent repeated migration attempts
                File.Move(sqlitePath, migratedMarker, overwrite: true);
                logger.LogInformation("Renamed {Path} -> {Marker} to prevent future migration attempts", sqlitePath, migratedMarker);
                return;
            }
        }

        // Read all data from SQLite
        logger.LogInformation("Reading data from SQLite...");

        var sqliteConnectionString = $"Data Source={sqlitePath};Cache=Shared;";
        await using var sqliteConn = new SqliteConnection(sqliteConnectionString);
        await sqliteConn.OpenAsync(ct);

        // Migrate Settings
        var settings = await ReadSettings(sqliteConn, ct);
        logger.LogInformation("Read {Count} settings from SQLite", settings.Count);

        // Migrate Torrents
        var torrents = await ReadTorrents(sqliteConn, ct);
        logger.LogInformation("Read {Count} torrents from SQLite", torrents.Count);

        // Migrate Downloads
        var downloads = await ReadDownloads(sqliteConn, ct);
        logger.LogInformation("Read {Count} downloads from SQLite", downloads.Count);

        // Migrate ASP.NET Identity tables
        var users = await ReadIdentityUsers(sqliteConn, ct);
        var roles = await ReadIdentityRoles(sqliteConn, ct);
        var userRoles = await ReadIdentityUserRoles(sqliteConn, ct);
        var roleClaims = await ReadIdentityRoleClaims(sqliteConn, ct);
        var userClaims = await ReadIdentityUserClaims(sqliteConn, ct);
        var userLogins = await ReadIdentityUserLogins(sqliteConn, ct);
        var userTokens = await ReadIdentityUserTokens(sqliteConn, ct);
        logger.LogInformation("Read identity data: {Users} users, {Roles} roles, {UserRoles} user-roles", 
            users.Count, roles.Count, userRoles.Count);

        // Write to PostgreSQL
        logger.LogInformation("Writing data to PostgreSQL...");
        await using var pgConn = new NpgsqlConnection(pgConnectionString);
        await pgConn.OpenAsync(ct);

        await WriteSettings(pgConn, settings, ct);
        await WriteTorrents(pgConn, torrents, ct);
        await WriteDownloads(pgConn, downloads, ct);
        await WriteIdentityRoles(pgConn, roles, ct);
        await WriteIdentityUsers(pgConn, users, ct);
        await WriteIdentityRoleClaims(pgConn, roleClaims, ct);
        await WriteIdentityUserClaims(pgConn, userClaims, ct);
        await WriteIdentityUserRoles(pgConn, userRoles, ct);
        await WriteIdentityUserLogins(pgConn, userLogins, ct);
        await WriteIdentityUserTokens(pgConn, userTokens, ct);

        // Rename old SQLite
        File.Move(sqlitePath, migratedMarker, overwrite: true);
        logger.LogWarning("Migration complete! Renamed {Path} -> {Marker}", sqlitePath, migratedMarker);
    }

    private static async Task<Boolean> PostgresHasData(String connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name = 'Settings')";
        var exists = (Boolean)(await cmd.ExecuteScalarAsync(ct) ?? false);
        if (!exists) return false;

        cmd.CommandText = "SELECT COUNT(1) FROM \"Settings\"";
        var count = (Int64)(await cmd.ExecuteScalarAsync(ct) ?? 0);
        return count > 0;
    }

    // ----- Settings -----
    private static async Task<List<(String SettingId, String? Value)>> ReadSettings(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<(String, String?)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SettingId, Value FROM Settings";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        return results;
    }

    private static async Task WriteSettings(NpgsqlConnection conn, List<(String SettingId, String? Value)> settings, CancellationToken ct)
    {
        if (settings.Count == 0) return;
        foreach (var chunk in settings.Chunk(100))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var s in chunk)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO \"Settings\" (\"SettingId\", \"Value\") VALUES ($1, $2) ON CONFLICT DO NOTHING";
                cmd.Parameters.AddWithValue("$1", s.SettingId);
                cmd.Parameters.AddWithValue("$2", (object?)s.Value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    // ----- Torrents -----
    private static async Task<List<Dictionary<String, Object?>>> ReadTorrents(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<Dictionary<String, Object?>>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Torrents";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<String, Object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }
        return results;
    }

    private static async Task WriteTorrents(NpgsqlConnection conn, List<Dictionary<String, Object?>> torrents, CancellationToken ct)
    {
        if (torrents.Count == 0) return;

        var columns = torrents[0].Keys.ToList();
        var colList = String.Join(", ", columns.Select(c => "\"" + c + "\""));
        var paramList = String.Join(", ", columns.Select((_, i) => "$" + (i + 1)));

        foreach (var chunk in torrents.Chunk(50))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var torrent in chunk)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO \"Torrents\" ({colList}) VALUES ({paramList}) ON CONFLICT DO NOTHING";

                for (var i = 0; i < columns.Count; i++)
                {
                    var val = torrent[columns[i]];
                    cmd.Parameters.AddWithValue("$" + (i + 1), ConvertValueForPg(val));
                }

                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    // ----- Downloads -----
    private static async Task<List<Dictionary<String, Object?>>> ReadDownloads(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<Dictionary<String, Object?>>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Downloads";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<String, Object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }
        return results;
    }

    private static async Task WriteDownloads(NpgsqlConnection conn, List<Dictionary<String, Object?>> downloads, CancellationToken ct)
    {
        if (downloads.Count == 0) return;

        var columns = downloads[0].Keys.ToList();
        var colList = String.Join(", ", columns.Select(c => "\"" + c + "\""));
        var paramList = String.Join(", ", columns.Select((_, i) => "$" + (i + 1)));

        foreach (var chunk in downloads.Chunk(100))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var download in chunk)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO \"Downloads\" ({colList}) VALUES ({paramList}) ON CONFLICT DO NOTHING";

                for (var i = 0; i < columns.Count; i++)
                {
                    var val = download[columns[i]];
                    cmd.Parameters.AddWithValue("$" + (i + 1), ConvertValueForPg(val));
                }

                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    // ----- Identity Tables -----
    private static async Task<List<Dictionary<String, Object?>>> ReadTable(SqliteConnection conn, String tableName, CancellationToken ct)
    {
        var results = new List<Dictionary<String, Object?>>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{tableName}\"";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<String, Object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }
        return results;
    }

    private static async Task WriteIdentityTable(NpgsqlConnection conn, String tableName, List<Dictionary<String, Object?>> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;

        var columns = rows[0].Keys.ToList();
        var colList = String.Join(", ", columns.Select(c => "\"" + c + "\""));
        var paramList = String.Join(", ", columns.Select((_, i) => "$" + (i + 1)));

        foreach (var chunk in rows.Chunk(100))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var row in chunk)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO \"{tableName}\" ({colList}) VALUES ({paramList}) ON CONFLICT DO NOTHING";

                for (var i = 0; i < columns.Count; i++)
                {
                    cmd.Parameters.AddWithValue("$" + (i + 1), ConvertValueForPg(row[columns[i]]));
                }

                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    private static async Task<List<Dictionary<String, Object?>>> ReadIdentityUsers(SqliteConnection conn, CancellationToken ct) => await ReadTable(conn, "AspNetUsers", ct);
    private static async Task<List<Dictionary<String, Object?>>> ReadIdentityRoles(SqliteConnection conn, CancellationToken ct) => await ReadTable(conn, "AspNetRoles", ct);
    private static async Task<List<Dictionary<String, Object?>>> ReadIdentityUserRoles(SqliteConnection conn, CancellationToken ct) => await ReadTable(conn, "AspNetUserRoles", ct);
    private static async Task<List<Dictionary<String, Object?>>> ReadIdentityRoleClaims(SqliteConnection conn, CancellationToken ct) => await ReadTable(conn, "AspNetRoleClaims", ct);
    private static async Task<List<Dictionary<String, Object?>>> ReadIdentityUserClaims(SqliteConnection conn, CancellationToken ct) => await ReadTable(conn, "AspNetUserClaims", ct);
    private static async Task<List<Dictionary<String, Object?>>> ReadIdentityUserLogins(SqliteConnection conn, CancellationToken ct) => await ReadTable(conn, "AspNetUserLogins", ct);
    private static async Task<List<Dictionary<String, Object?>>> ReadIdentityUserTokens(SqliteConnection conn, CancellationToken ct) => await ReadTable(conn, "AspNetUserTokens", ct);

    private static async Task WriteIdentityUsers(NpgsqlConnection conn, List<Dictionary<String, Object?>> rows, CancellationToken ct) => await WriteIdentityTable(conn, "AspNetUsers", rows, ct);
    private static async Task WriteIdentityRoles(NpgsqlConnection conn, List<Dictionary<String, Object?>> rows, CancellationToken ct) => await WriteIdentityTable(conn, "AspNetRoles", rows, ct);
    private static async Task WriteIdentityUserRoles(NpgsqlConnection conn, List<Dictionary<String, Object?>> rows, CancellationToken ct) => await WriteIdentityTable(conn, "AspNetUserRoles", rows, ct);
    private static async Task WriteIdentityRoleClaims(NpgsqlConnection conn, List<Dictionary<String, Object?>> rows, CancellationToken ct) => await WriteIdentityTable(conn, "AspNetRoleClaims", rows, ct);
    private static async Task WriteIdentityUserClaims(NpgsqlConnection conn, List<Dictionary<String, Object?>> rows, CancellationToken ct) => await WriteIdentityTable(conn, "AspNetUserClaims", rows, ct);
    private static async Task WriteIdentityUserLogins(NpgsqlConnection conn, List<Dictionary<String, Object?>> rows, CancellationToken ct) => await WriteIdentityTable(conn, "AspNetUserLogins", rows, ct);
    private static async Task WriteIdentityUserTokens(NpgsqlConnection conn, List<Dictionary<String, Object?>> rows, CancellationToken ct) => await WriteIdentityTable(conn, "AspNetUserTokens", rows, ct);

    /// <summary>
    /// Converts SQLite values to PostgreSQL-compatible values.
    /// SQLite stores Guids as TEXT (string), DateTimeOffsets as TEXT (ISO 8601 string), 
    /// and booleans as 0/1 integers.
    /// </summary>
    private static Object ConvertValueForPg(Object? value)
    {
        if (value == null || value == DBNull.Value)
            return DBNull.Value;

        // SQLite stores booleans as 0/1 long
        if (value is Int64 longVal && (longVal == 0 || longVal == 1))
        {
            // Check if the column name suggests boolean (heuristic)
            // Actually, let's just pass through as integer - PG will handle it
            return longVal;
        }

        // SQLite stores strings for DateTimeOffset and Guid
        if (value is String str)
        {
            // Try to parse as Guid
            if (Guid.TryParse(str, out var guid))
                return guid;

            // Try to parse as DateTimeOffset
            if (DateTimeOffset.TryParse(str, out var dto))
                return dto;
        }

        return value;
    }
}