using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LegalAI.Infrastructure.Storage;

public sealed class SqliteIngestionJobStore : IIngestionJobStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteIngestionJobStore> _logger;

    public SqliteIngestionJobStore(string databasePath, ILogger<SqliteIngestionJobStore> logger)
    {
        _logger = logger;

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ingestion_jobs (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                status INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL,
                max_attempts INTEGER NOT NULL,
                last_error TEXT,
                quarantine_path TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_attempt_at TEXT,
                next_attempt_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_ingestion_jobs_file_hash ON ingestion_jobs(file_path, content_hash);
            CREATE INDEX IF NOT EXISTS idx_ingestion_jobs_status ON ingestion_jobs(status);
            CREATE INDEX IF NOT EXISTS idx_ingestion_jobs_updated ON ingestion_jobs(updated_at);
            """;
        cmd.ExecuteNonQuery();

        _logger.LogDebug("SQLite ingestion job schema initialized");
    }

    public async Task<IngestionJob?> GetByFileAndHashAsync(string filePath, string contentHash, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, content_hash, status, attempt_count, max_attempts, last_error,
                   quarantine_path, created_at, updated_at, last_attempt_at, next_attempt_at
            FROM ingestion_jobs
            WHERE file_path = @file_path AND content_hash = @content_hash
            ORDER BY updated_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.Parameters.AddWithValue("@content_hash", contentHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<IngestionJob> CreateAsync(IngestionJob job, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ingestion_jobs
            (id, file_path, content_hash, status, attempt_count, max_attempts, last_error,
             quarantine_path, created_at, updated_at, last_attempt_at, next_attempt_at)
            VALUES
            (@id, @file_path, @content_hash, @status, @attempt_count, @max_attempts, @last_error,
             @quarantine_path, @created_at, @updated_at, @last_attempt_at, @next_attempt_at)
            """;

        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@file_path", job.FilePath);
        cmd.Parameters.AddWithValue("@content_hash", job.ContentHash);
        cmd.Parameters.AddWithValue("@status", (int)job.Status);
        cmd.Parameters.AddWithValue("@attempt_count", job.AttemptCount);
        cmd.Parameters.AddWithValue("@max_attempts", job.MaxAttempts);
        cmd.Parameters.AddWithValue("@last_error", job.LastError ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@quarantine_path", job.QuarantinePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", job.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", job.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@last_attempt_at", job.LastAttemptAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@next_attempt_at", job.NextAttemptAt?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
        return job;
    }

    public async Task MarkRunningAsync(string id, int attemptCount, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ingestion_jobs
            SET status = @status,
                attempt_count = @attempt_count,
                updated_at = @updated_at,
                last_attempt_at = @last_attempt_at,
                next_attempt_at = NULL,
                last_error = NULL
            WHERE id = @id
            """;

        cmd.Parameters.AddWithValue("@status", (int)IngestionJobStatus.Running);
        cmd.Parameters.AddWithValue("@attempt_count", attemptCount);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@last_attempt_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkSucceededAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ingestion_jobs
            SET status = @status,
                updated_at = @updated_at,
                next_attempt_at = NULL,
                last_error = NULL
            WHERE id = @id
            """;

        cmd.Parameters.AddWithValue("@status", (int)IngestionJobStatus.Succeeded);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(string id, string error, DateTimeOffset? nextAttemptAt, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ingestion_jobs
            SET status = @status,
                updated_at = @updated_at,
                last_error = @last_error,
                next_attempt_at = @next_attempt_at
            WHERE id = @id
            """;

        cmd.Parameters.AddWithValue("@status", (int)IngestionJobStatus.Failed);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@last_error", error);
        cmd.Parameters.AddWithValue("@next_attempt_at", nextAttemptAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkQuarantinedAsync(string id, string quarantinePath, string error, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ingestion_jobs
            SET status = @status,
                updated_at = @updated_at,
                last_error = @last_error,
                quarantine_path = @quarantine_path,
                next_attempt_at = NULL
            WHERE id = @id
            """;

        cmd.Parameters.AddWithValue("@status", (int)IngestionJobStatus.Quarantined);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@last_error", error);
        cmd.Parameters.AddWithValue("@quarantine_path", quarantinePath);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<IngestionJob>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, content_hash, status, attempt_count, max_attempts, last_error,
                   quarantine_path, created_at, updated_at, last_attempt_at, next_attempt_at
            FROM ingestion_jobs
            ORDER BY updated_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var list = new List<IngestionJob>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Map(reader));
        }

        return list;
    }

    private static IngestionJob Map(SqliteDataReader reader)
    {
        return new IngestionJob
        {
            Id = reader.GetString(0),
            FilePath = reader.GetString(1),
            ContentHash = reader.GetString(2),
            Status = (IngestionJobStatus)reader.GetInt32(3),
            AttemptCount = reader.GetInt32(4),
            MaxAttempts = reader.GetInt32(5),
            LastError = reader.IsDBNull(6) ? null : reader.GetString(6),
            QuarantinePath = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(8)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9)),
            LastAttemptAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
            NextAttemptAt = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11))
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
