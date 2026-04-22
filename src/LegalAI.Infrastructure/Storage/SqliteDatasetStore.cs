using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LegalAI.Infrastructure.Storage;

public sealed class SqliteDatasetStore : IDatasetStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteDatasetStore> _logger;

    public SqliteDatasetStore(string databasePath, ILogger<SqliteDatasetStore> logger)
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
            CREATE TABLE IF NOT EXISTS datasets (
                id TEXT PRIMARY KEY,
                domain_id TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                lifecycle INTEGER NOT NULL,
                sensitivity INTEGER NOT NULL,
                owner_user_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_datasets_domain_name
                ON datasets(domain_id, name COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_datasets_domain_lifecycle
                ON datasets(domain_id, lifecycle);
            """;
        cmd.ExecuteNonQuery();

        _logger.LogDebug("SQLite dataset store schema initialized");
    }

    public async Task CreateAsync(Dataset dataset, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO datasets
            (id, domain_id, name, description, lifecycle, sensitivity, owner_user_id, created_at, updated_at)
            VALUES
            (@id, @domain_id, @name, @description, @lifecycle, @sensitivity, @owner_user_id, @created_at, @updated_at)
            """;

        BindDataset(cmd, dataset);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dataset?> GetByIdAsync(string datasetId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, domain_id, name, description, lifecycle, sensitivity, owner_user_id, created_at, updated_at
            FROM datasets
            WHERE id = @id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", datasetId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<List<Dataset>> GetByDomainAsync(
        string domainId,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = includeArchived
            ? """
                SELECT id, domain_id, name, description, lifecycle, sensitivity, owner_user_id, created_at, updated_at
                FROM datasets
                WHERE domain_id = @domain_id
                ORDER BY created_at DESC
                """
            : """
                SELECT id, domain_id, name, description, lifecycle, sensitivity, owner_user_id, created_at, updated_at
                FROM datasets
                WHERE domain_id = @domain_id AND lifecycle <> @archived
                ORDER BY created_at DESC
                """;

        cmd.Parameters.AddWithValue("@domain_id", domainId);
        if (!includeArchived)
        {
            cmd.Parameters.AddWithValue("@archived", (int)DatasetLifecycle.Archived);
        }

        return await ReadListAsync(cmd, ct);
    }

    public async Task<List<Dataset>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = includeArchived
            ? """
                SELECT id, domain_id, name, description, lifecycle, sensitivity, owner_user_id, created_at, updated_at
                FROM datasets
                ORDER BY created_at DESC
                """
            : """
                SELECT id, domain_id, name, description, lifecycle, sensitivity, owner_user_id, created_at, updated_at
                FROM datasets
                WHERE lifecycle <> @archived
                ORDER BY created_at DESC
                """;

        if (!includeArchived)
        {
            cmd.Parameters.AddWithValue("@archived", (int)DatasetLifecycle.Archived);
        }

        return await ReadListAsync(cmd, ct);
    }

    public async Task UpdateAsync(Dataset dataset, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE datasets
            SET domain_id = @domain_id,
                name = @name,
                description = @description,
                lifecycle = @lifecycle,
                sensitivity = @sensitivity,
                owner_user_id = @owner_user_id,
                updated_at = @updated_at
            WHERE id = @id
            """;

        cmd.Parameters.AddWithValue("@id", dataset.Id);
        cmd.Parameters.AddWithValue("@domain_id", dataset.DomainId);
        cmd.Parameters.AddWithValue("@name", dataset.Name);
        cmd.Parameters.AddWithValue("@description", dataset.Description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@lifecycle", (int)dataset.Lifecycle);
        cmd.Parameters.AddWithValue("@sensitivity", (int)dataset.Sensitivity);
        cmd.Parameters.AddWithValue("@owner_user_id", dataset.OwnerUserId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updated_at", dataset.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetLifecycleAsync(string datasetId, DatasetLifecycle lifecycle, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE datasets
            SET lifecycle = @lifecycle,
                updated_at = @updated_at
            WHERE id = @id
            """;

        cmd.Parameters.AddWithValue("@id", datasetId);
        cmd.Parameters.AddWithValue("@lifecycle", (int)lifecycle);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsByNameAsync(string domainId, string datasetName, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM datasets
            WHERE domain_id = @domain_id AND name = @name COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("@domain_id", domainId);
        cmd.Parameters.AddWithValue("@name", datasetName);

        var result = await cmd.ExecuteScalarAsync(ct);
        var count = Convert.ToInt32(result);
        return count > 0;
    }

    private static void BindDataset(SqliteCommand cmd, Dataset dataset)
    {
        cmd.Parameters.AddWithValue("@id", dataset.Id);
        cmd.Parameters.AddWithValue("@domain_id", dataset.DomainId);
        cmd.Parameters.AddWithValue("@name", dataset.Name);
        cmd.Parameters.AddWithValue("@description", dataset.Description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@lifecycle", (int)dataset.Lifecycle);
        cmd.Parameters.AddWithValue("@sensitivity", (int)dataset.Sensitivity);
        cmd.Parameters.AddWithValue("@owner_user_id", dataset.OwnerUserId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", dataset.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", dataset.UpdatedAt.ToString("O"));
    }

    private static async Task<List<Dataset>> ReadListAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var datasets = new List<Dataset>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            datasets.Add(Map(reader));
        }

        return datasets;
    }

    private static Dataset Map(SqliteDataReader reader)
    {
        return new Dataset
        {
            Id = reader.GetString(0),
            DomainId = reader.GetString(1),
            Name = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            Lifecycle = (DatasetLifecycle)reader.GetInt32(4),
            Sensitivity = (DatasetSensitivity)reader.GetInt32(5),
            OwnerUserId = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(8))
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
