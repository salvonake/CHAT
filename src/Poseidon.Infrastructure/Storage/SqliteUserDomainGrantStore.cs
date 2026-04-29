using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Poseidon.Infrastructure.Storage;

public sealed class SqliteUserDomainGrantStore : IUserDomainGrantStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteUserDomainGrantStore> _logger;

    public SqliteUserDomainGrantStore(string databasePath, ILogger<SqliteUserDomainGrantStore> logger)
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
            CREATE TABLE IF NOT EXISTS user_domain_grants (
                user_id TEXT NOT NULL,
                domain_id TEXT NOT NULL,
                dataset_scope TEXT NOT NULL DEFAULT '',
                granted_at TEXT NOT NULL,
                granted_by TEXT,
                PRIMARY KEY(user_id, domain_id, dataset_scope)
            );

            CREATE INDEX IF NOT EXISTS idx_user_domain_grants_user
                ON user_domain_grants(user_id);

            CREATE INDEX IF NOT EXISTS idx_user_domain_grants_domain
                ON user_domain_grants(domain_id);
            """;
        cmd.ExecuteNonQuery();

        _logger.LogDebug("SQLite user-domain-grant store schema initialized");
    }

    public async Task UpsertAsync(UserDomainGrant grant, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO user_domain_grants
            (user_id, domain_id, dataset_scope, granted_at, granted_by)
            VALUES
            (@user_id, @domain_id, @dataset_scope, @granted_at, @granted_by)
            """;

        cmd.Parameters.AddWithValue("@user_id", grant.UserId);
        cmd.Parameters.AddWithValue("@domain_id", grant.DomainId);
        cmd.Parameters.AddWithValue("@dataset_scope", NormalizeScope(grant.DatasetScope));
        cmd.Parameters.AddWithValue("@granted_at", grant.GrantedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@granted_by", grant.GrantedBy ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(
        string userId,
        string domainId,
        string? datasetScope = null,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM user_domain_grants
            WHERE user_id = @user_id
              AND domain_id = @domain_id
              AND dataset_scope = @dataset_scope
            """;

        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@domain_id", domainId);
        cmd.Parameters.AddWithValue("@dataset_scope", NormalizeScope(datasetScope));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<UserDomainGrant>> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, domain_id, dataset_scope, granted_at, granted_by
            FROM user_domain_grants
            WHERE user_id = @user_id
            ORDER BY domain_id ASC, dataset_scope ASC
            """;
        cmd.Parameters.AddWithValue("@user_id", userId);

        var grants = new List<UserDomainGrant>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            grants.Add(new UserDomainGrant
            {
                UserId = reader.GetString(0),
                DomainId = reader.GetString(1),
                DatasetScope = ToNullableScope(reader.GetString(2)),
                GrantedAt = DateTimeOffset.Parse(reader.GetString(3)),
                GrantedBy = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return grants;
    }

    public async Task<bool> HasAccessAsync(
        string userId,
        string domainId,
        string? datasetScope = null,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM user_domain_grants
            WHERE user_id = @user_id
              AND domain_id = @domain_id
              AND (
                    dataset_scope = ''
                    OR dataset_scope = @dataset_scope
                  )
            """;

        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@domain_id", domainId);
        cmd.Parameters.AddWithValue("@dataset_scope", NormalizeScope(datasetScope));

        var scalar = await cmd.ExecuteScalarAsync(ct);
        var count = Convert.ToInt32(scalar);
        return count > 0;
    }

    private static string NormalizeScope(string? datasetScope)
    {
        return string.IsNullOrWhiteSpace(datasetScope)
            ? string.Empty
            : datasetScope.Trim().ToLowerInvariant();
    }

    private static string? ToNullableScope(string scope)
    {
        return string.IsNullOrEmpty(scope) ? null : scope;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}

