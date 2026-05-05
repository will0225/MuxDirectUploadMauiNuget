using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mux.DirectUpload.Maui;

namespace Mux.DirectUpload.Demo;

/// <summary>
/// Persists <see cref="MuxResumableUploadSession"/> in SQLite (alternative to a flat JSON file).
/// </summary>
public sealed class MuxUploadSqliteSessionStore
{
    private static readonly string LegacyJsonPath =
        Path.Combine(FileSystem.AppDataDirectory, "mux_resumable_session.json");

    private readonly string _dbPath =
        Path.Combine(FileSystem.AppDataDirectory, "mux_resumable_session.db");

    public async Task<MuxResumableUploadSession?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await MigrateLegacyJsonIfNeededAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM mux_upload_session WHERE id = 1;";
        var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MuxResumableUploadSession>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(MuxResumableUploadSession session, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(session);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO mux_upload_session (id, payload, updated_utc)
            VALUES (1, @payload, @updated);
            """;
        cmd.Parameters.AddWithValue("@payload", json);
        cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_dbPath))
            return;

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM mux_upload_session WHERE id = 1;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS mux_upload_session (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              payload TEXT NOT NULL,
              updated_utc TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateLegacyJsonIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LegacyJsonPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(LegacyJsonPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                File.Delete(LegacyJsonPath);
                return;
            }

            var existing = await TryLoadRawAsync(cancellationToken).ConfigureAwait(false);
            if (existing is null)
                await SaveRawPayloadAsync(json, cancellationToken).ConfigureAwait(false);

            File.Delete(LegacyJsonPath);
        }
        catch
        {
            /* keep legacy file if migration fails */
        }
    }

    private async Task<string?> TryLoadRawAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM mux_upload_session WHERE id = 1;";
        return (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveRawPayloadAsync(string json, CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO mux_upload_session (id, payload, updated_utc)
            VALUES (1, @payload, @updated);
            """;
        cmd.Parameters.AddWithValue("@payload", json);
        cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
