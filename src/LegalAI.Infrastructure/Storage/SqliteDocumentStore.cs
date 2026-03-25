using System.Text.Json;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LegalAI.Infrastructure.Storage;

/// <summary>
/// SQLite-based document store for tracking indexed documents,
/// their metadata, status, and quarantine records.
/// </summary>
public sealed class SqliteDocumentStore : IDocumentStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteDocumentStore> _logger;

    public SqliteDocumentStore(string databasePath, ILogger<SqliteDocumentStore> logger)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS documents (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                file_size_bytes INTEGER NOT NULL,
                page_count INTEGER DEFAULT 0,
                indexed_at TEXT NOT NULL,
                last_modified TEXT,
                status INTEGER DEFAULT 0,
                error_message TEXT,
                failure_count INTEGER DEFAULT 0,
                case_namespace TEXT,
                chunk_count INTEGER DEFAULT 0,
                metadata_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_documents_file_path ON documents(file_path);
            CREATE INDEX IF NOT EXISTS idx_documents_status ON documents(status);
            CREATE INDEX IF NOT EXISTS idx_documents_content_hash ON documents(content_hash);

            CREATE TABLE IF NOT EXISTS quarantine (
                document_id TEXT NOT NULL,
                file_path TEXT NOT NULL,
                reason TEXT NOT NULL,
                failure_count INTEGER NOT NULL,
                quarantined_at TEXT NOT NULL,
                content_hash TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_quarantine_document_id ON quarantine(document_id);
            """;
        cmd.ExecuteNonQuery();

        _logger.LogDebug("SQLite document store schema initialized");
    }

    public async Task<LegalDocument?> GetByFilePathAsync(string filePath, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM documents WHERE file_path = @path LIMIT 1";
        cmd.Parameters.AddWithValue("@path", filePath);
        return await ReadSingleDocumentAsync(cmd, ct);
    }

    public async Task<LegalDocument?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM documents WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", id);
        return await ReadSingleDocumentAsync(cmd, ct);
    }

    public async Task UpsertAsync(LegalDocument document, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO documents 
            (id, file_path, file_name, content_hash, file_size_bytes, page_count, 
             indexed_at, last_modified, status, error_message, failure_count, 
             case_namespace, chunk_count, metadata_json)
            VALUES 
            (@id, @file_path, @file_name, @content_hash, @file_size_bytes, @page_count,
             @indexed_at, @last_modified, @status, @error_message, @failure_count,
             @case_namespace, @chunk_count, @metadata_json)
            """;

        cmd.Parameters.AddWithValue("@id", document.Id);
        cmd.Parameters.AddWithValue("@file_path", document.FilePath);
        cmd.Parameters.AddWithValue("@file_name", document.FileName);
        cmd.Parameters.AddWithValue("@content_hash", document.ContentHash);
        cmd.Parameters.AddWithValue("@file_size_bytes", document.FileSizeBytes);
        cmd.Parameters.AddWithValue("@page_count", document.PageCount);
        cmd.Parameters.AddWithValue("@indexed_at", document.IndexedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@last_modified",
            document.LastModified?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (int)document.Status);
        cmd.Parameters.AddWithValue("@error_message",
            document.ErrorMessage ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@failure_count", document.FailureCount);
        cmd.Parameters.AddWithValue("@case_namespace",
            document.CaseNamespace ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@chunk_count", document.ChunkCount);
        cmd.Parameters.AddWithValue("@metadata_json",
            JsonSerializer.Serialize(document.Metadata));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<LegalDocument>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM documents ORDER BY indexed_at DESC";
        return await ReadDocumentsAsync(cmd, ct);
    }

    public async Task<List<LegalDocument>> GetByStatusAsync(DocumentStatus status, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM documents WHERE status = @status ORDER BY indexed_at DESC";
        cmd.Parameters.AddWithValue("@status", (int)status);
        return await ReadDocumentsAsync(cmd, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> GetDocumentCountAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents WHERE status = @status";
        cmd.Parameters.AddWithValue("@status", (int)DocumentStatus.Indexed);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task<List<QuarantineRecord>> GetQuarantineRecordsAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM quarantine ORDER BY quarantined_at DESC";

        var records = new List<QuarantineRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new QuarantineRecord
            {
                DocumentId = reader.GetString(0),
                FilePath = reader.GetString(1),
                Reason = reader.GetString(2),
                FailureCount = reader.GetInt32(3),
                QuarantinedAt = DateTimeOffset.Parse(reader.GetString(4)),
                ContentHash = reader.GetString(5)
            });
        }

        return records;
    }

    public async Task AddQuarantineRecordAsync(QuarantineRecord record, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO quarantine (document_id, file_path, reason, failure_count, quarantined_at, content_hash)
            VALUES (@document_id, @file_path, @reason, @failure_count, @quarantined_at, @content_hash)
            """;
        cmd.Parameters.AddWithValue("@document_id", record.DocumentId);
        cmd.Parameters.AddWithValue("@file_path", record.FilePath);
        cmd.Parameters.AddWithValue("@reason", record.Reason);
        cmd.Parameters.AddWithValue("@failure_count", record.FailureCount);
        cmd.Parameters.AddWithValue("@quarantined_at", record.QuarantinedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@content_hash", record.ContentHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<LegalDocument?> ReadSingleDocumentAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapDocument(reader);
    }

    private static async Task<List<LegalDocument>> ReadDocumentsAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var docs = new List<LegalDocument>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            docs.Add(MapDocument(reader));
        }
        return docs;
    }

    private static LegalDocument MapDocument(SqliteDataReader reader)
    {
        var metadataJson = reader.IsDBNull(13) ? null : reader.GetString(13);
        var metadata = metadataJson is not null
            ? JsonSerializer.Deserialize<LegalDocumentMetadata>(metadataJson) ?? new()
            : new LegalDocumentMetadata();

        return new LegalDocument
        {
            Id = reader.GetString(0),
            FilePath = reader.GetString(1),
            FileName = reader.GetString(2),
            ContentHash = reader.GetString(3),
            FileSizeBytes = reader.GetInt64(4),
            PageCount = reader.GetInt32(5),
            IndexedAt = DateTimeOffset.Parse(reader.GetString(6)),
            LastModified = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            Status = (DocumentStatus)reader.GetInt32(8),
            ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
            FailureCount = reader.GetInt32(10),
            CaseNamespace = reader.IsDBNull(11) ? null : reader.GetString(11),
            ChunkCount = reader.GetInt32(12),
            Metadata = metadata
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
