using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LegalAI.Infrastructure.Storage;

public sealed class SqliteChatStore : IChatStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteChatStore> _logger;

    public SqliteChatStore(string databasePath, ILogger<SqliteChatStore> logger)
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
            CREATE TABLE IF NOT EXISTS chat_sessions (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                title TEXT NOT NULL,
                case_namespace TEXT,
                domain_id TEXT,
                dataset_scope TEXT,
                strict_mode INTEGER NOT NULL DEFAULT 1,
                top_k INTEGER NOT NULL DEFAULT 10,
                is_archived INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_chat_sessions_user ON chat_sessions(user_id);
            CREATE INDEX IF NOT EXISTS idx_chat_sessions_updated ON chat_sessions(updated_at);
            CREATE INDEX IF NOT EXISTS idx_chat_sessions_domain ON chat_sessions(domain_id);

            CREATE TABLE IF NOT EXISTS chat_messages (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                metadata_json TEXT,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_chat_messages_session ON chat_messages(session_id, created_at);
            """;
        cmd.ExecuteNonQuery();

        EnsureColumnExists("chat_sessions", "domain_id TEXT");
        EnsureColumnExists("chat_sessions", "dataset_scope TEXT");

        _logger.LogDebug("SQLite chat store schema initialized");
    }

    private void EnsureColumnExists(string tableName, string columnDefinition)
    {
        var columnName = columnDefinition.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        using var pragma = _connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            var existingColumn = reader.GetString(1);
            if (string.Equals(existingColumn, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    public async Task<ChatSession> CreateSessionAsync(ChatSession session, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_sessions
            (id, user_id, title, case_namespace, domain_id, dataset_scope, strict_mode, top_k, is_archived, created_at, updated_at)
            VALUES
            (@id, @user_id, @title, @case_namespace, @domain_id, @dataset_scope, @strict_mode, @top_k, @is_archived, @created_at, @updated_at)
            """;

        cmd.Parameters.AddWithValue("@id", session.Id);
        cmd.Parameters.AddWithValue("@user_id", session.UserId);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@case_namespace", session.CaseNamespace ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@domain_id", session.DomainId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@dataset_scope", session.DatasetScope ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@strict_mode", session.StrictMode ? 1 : 0);
        cmd.Parameters.AddWithValue("@top_k", session.TopK);
        cmd.Parameters.AddWithValue("@is_archived", session.IsArchived ? 1 : 0);
        cmd.Parameters.AddWithValue("@created_at", session.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", session.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        return session;
    }

    public async Task<ChatSession?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, title, case_namespace, domain_id, dataset_scope,
                   strict_mode, top_k, is_archived, created_at, updated_at
            FROM chat_sessions
            WHERE id = @id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MapSession(reader);
    }

    public async Task<List<ChatSession>> GetSessionsForUserAsync(string userId, bool includeArchived, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = includeArchived
            ? """
                SELECT id, user_id, title, case_namespace, domain_id, dataset_scope,
                       strict_mode, top_k, is_archived, created_at, updated_at
                FROM chat_sessions
                WHERE user_id = @user_id
                ORDER BY updated_at DESC
                """
            : """
                SELECT id, user_id, title, case_namespace, domain_id, dataset_scope,
                       strict_mode, top_k, is_archived, created_at, updated_at
                FROM chat_sessions
                WHERE user_id = @user_id AND is_archived = 0
                ORDER BY updated_at DESC
                """;
        cmd.Parameters.AddWithValue("@user_id", userId);

        var list = new List<ChatSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapSession(reader));
        }

        return list;
    }

    public async Task RenameSessionAsync(string sessionId, string title, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET title = @title, updated_at = @updated_at WHERE id = @id";
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ArchiveSessionAsync(string sessionId, bool archived, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET is_archived = @is_archived, updated_at = @updated_at WHERE id = @id";
        cmd.Parameters.AddWithValue("@is_archived", archived ? 1 : 0);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_messages
            (id, session_id, user_id, role, content, metadata_json, created_at)
            VALUES
            (@id, @session_id, @user_id, @role, @content, @metadata_json, @created_at)
            """;

        cmd.Parameters.AddWithValue("@id", message.Id);
        cmd.Parameters.AddWithValue("@session_id", message.SessionId);
        cmd.Parameters.AddWithValue("@user_id", message.UserId);
        cmd.Parameters.AddWithValue("@role", message.Role);
        cmd.Parameters.AddWithValue("@content", message.Content);
        cmd.Parameters.AddWithValue("@metadata_json", message.MetadataJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", message.CreatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);

        await using var touch = _connection.CreateCommand();
        touch.CommandText = "UPDATE chat_sessions SET updated_at = @updated_at WHERE id = @id";
        touch.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        touch.Parameters.AddWithValue("@id", message.SessionId);
        await touch.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(string sessionId, int limit = 200, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, user_id, role, content, metadata_json, created_at
            FROM chat_messages
            WHERE session_id = @session_id
            ORDER BY created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var list = new List<ChatMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ChatMessage
            {
                Id = reader.GetString(0),
                SessionId = reader.GetString(1),
                UserId = reader.GetString(2),
                Role = reader.GetString(3),
                Content = reader.GetString(4),
                MetadataJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(6))
            });
        }

        list.Reverse();
        return list;
    }

    private static ChatSession MapSession(SqliteDataReader reader)
    {
        return new ChatSession
        {
            Id = reader.GetString(0),
            UserId = reader.GetString(1),
            Title = reader.GetString(2),
            CaseNamespace = reader.IsDBNull(3) ? null : reader.GetString(3),
            DomainId = reader.IsDBNull(4) ? null : reader.GetString(4),
            DatasetScope = reader.IsDBNull(5) ? null : reader.GetString(5),
            StrictMode = reader.GetInt32(6) == 1,
            TopK = reader.GetInt32(7),
            IsArchived = reader.GetInt32(8) == 1,
            CreatedAt = DateTimeOffset.Parse(reader.GetString(9)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
