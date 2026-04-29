using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Poseidon.Infrastructure.Storage;

public sealed class SqliteUserStore : IUserStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteUserStore> _logger;

    public SqliteUserStore(string databasePath, ILogger<SqliteUserStore> logger)
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
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                role INTEGER NOT NULL,
                is_disabled INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_login_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_users_username ON users(username);
            CREATE INDEX IF NOT EXISTS idx_users_role ON users(role);
            """;
        cmd.ExecuteNonQuery();

        _logger.LogDebug("SQLite user store schema initialized");
    }

    public async Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE username = @username LIMIT 1";
        cmd.Parameters.AddWithValue("@username", username.Trim().ToLowerInvariant());
        return await ReadSingleAsync(cmd, ct);
    }

    public async Task<UserAccount?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", id);
        return await ReadSingleAsync(cmd, ct);
    }

    public async Task<List<UserAccount>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM users ORDER BY created_at ASC";

        var users = new List<UserAccount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            users.Add(Map(reader));
        }

        return users;
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(scalar);
    }

    public async Task UpsertAsync(UserAccount user, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO users
            (id, username, password_hash, role, is_disabled, created_at, last_login_at)
            VALUES
            (@id, @username, @password_hash, @role, @is_disabled, @created_at, @last_login_at)
            """;

        cmd.Parameters.AddWithValue("@id", user.Id);
        cmd.Parameters.AddWithValue("@username", user.Username.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@password_hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("@role", (int)user.Role);
        cmd.Parameters.AddWithValue("@is_disabled", user.IsDisabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@created_at", user.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@last_login_at", user.LastLoginAt?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetDisabledAsync(string id, bool isDisabled, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE users SET is_disabled = @is_disabled WHERE id = @id";
        cmd.Parameters.AddWithValue("@is_disabled", isDisabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetLastLoginAsync(string id, DateTimeOffset loggedInAt, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE users SET last_login_at = @last_login_at WHERE id = @id";
        cmd.Parameters.AddWithValue("@last_login_at", loggedInAt.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<UserAccount?> ReadSingleAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return Map(reader);
    }

    private static UserAccount Map(SqliteDataReader reader)
    {
        return new UserAccount
        {
            Id = reader.GetString(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2),
            Role = (UserRole)reader.GetInt32(3),
            IsDisabled = reader.GetInt32(4) == 1,
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            LastLoginAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}

