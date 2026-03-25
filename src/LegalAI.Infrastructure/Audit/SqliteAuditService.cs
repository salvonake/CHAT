using System.Security.Cryptography;
using System.Text;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LegalAI.Infrastructure.Audit;

/// <summary>
/// Append-only HMAC-signed audit log stored in SQLite.
/// Each entry is chained to the previous via HMAC, forming a tamper-evident chain.
/// </summary>
public sealed class SqliteAuditService : IAuditService, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<SqliteAuditService> _logger;
    private string _lastHash = "GENESIS";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteAuditService(
        string databasePath,
        IEncryptionService encryption,
        ILogger<SqliteAuditService> logger)
    {
        _encryption = encryption;
        _logger = logger;

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        InitializeSchema();
        LoadLastHash();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                action TEXT NOT NULL,
                user_id TEXT,
                details TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                previous_hash TEXT NOT NULL,
                hmac TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit_log(timestamp);
            CREATE INDEX IF NOT EXISTS idx_audit_action ON audit_log(action);
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadLastHash()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT hmac FROM audit_log ORDER BY id DESC LIMIT 1";
        var result = cmd.ExecuteScalar();
        if (result is not null and not DBNull)
        {
            _lastHash = (string)result;
        }
    }

    public async Task LogAsync(string action, string details, string? userId = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var previousHash = _lastHash;

            // Compute HMAC over the entry data
            var entryData = $"{action}|{userId ?? "system"}|{details}|{timestamp:O}|{previousHash}";
            var hmac = ComputeHmac(entryData);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_log (action, user_id, details, timestamp, previous_hash, hmac)
                VALUES (@action, @user_id, @details, @timestamp, @previous_hash, @hmac)
                """;

            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@user_id", userId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@details", details);
            cmd.Parameters.AddWithValue("@timestamp", timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@previous_hash", previousHash);
            cmd.Parameters.AddWithValue("@hmac", hmac);

            await cmd.ExecuteNonQueryAsync(ct);

            _lastHash = hmac;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<AuditEntry>> GetEntriesAsync(int limit = 100, int offset = 0,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, action, user_id, details, timestamp, previous_hash, hmac
            FROM audit_log ORDER BY id DESC LIMIT @limit OFFSET @offset
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var entries = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new AuditEntry
            {
                Id = reader.GetInt64(0),
                Action = reader.GetString(1),
                UserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Details = reader.GetString(3),
                Timestamp = DateTimeOffset.Parse(reader.GetString(4)),
                PreviousHash = reader.GetString(5),
                Hmac = reader.GetString(6)
            });
        }

        return entries;
    }

    public async Task<bool> VerifyChainIntegrityAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT action, user_id, details, timestamp, previous_hash, hmac
            FROM audit_log ORDER BY id ASC
            """;

        var previousHash = "GENESIS";
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var action = reader.GetString(0);
            var userId = reader.IsDBNull(1) ? "system" : reader.GetString(1);
            var details = reader.GetString(2);
            var timestamp = reader.GetString(3);
            var storedPreviousHash = reader.GetString(4);
            var storedHmac = reader.GetString(5);

            // Verify chain linkage
            if (storedPreviousHash != previousHash)
            {
                _logger.LogError("Audit chain broken: expected previous hash {Expected}, got {Actual}",
                    previousHash, storedPreviousHash);
                return false;
            }

            // Verify HMAC
            var entryData = $"{action}|{userId}|{details}|{timestamp}|{storedPreviousHash}";
            var computedHmac = ComputeHmac(entryData);

            if (computedHmac != storedHmac)
            {
                _logger.LogError("Audit HMAC mismatch for entry with action: {Action}", action);
                return false;
            }

            previousHash = storedHmac;
        }

        _logger.LogInformation("Audit chain integrity verified successfully");
        return true;
    }

    private string ComputeHmac(string data)
    {
        if (_encryption.IsEnabled)
        {
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var hmacBytes = _encryption.ComputeHmac(dataBytes);
            return Convert.ToHexString(hmacBytes).ToLowerInvariant();
        }

        // Fallback: SHA256 hash when encryption is disabled
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        _lock.Dispose();
        await _connection.DisposeAsync();
    }
}
