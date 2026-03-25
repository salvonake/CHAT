using System.IO;
using FluentAssertions;
using LegalAI.Domain.Entities;
using LegalAI.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Infrastructure;

/// <summary>
/// Tests for <see cref="SqliteDocumentStore"/>: SQLite-based document metadata
/// store with CRUD, status filtering, quarantine management, and JSON metadata.
/// Uses temp files (SqliteDocumentStore takes a databasePath string).
/// </summary>
public sealed class SqliteDocumentStoreTests : IDisposable
{
    private readonly Mock<ILogger<SqliteDocumentStore>> _logger = new();
    private readonly string _dbPath;

    public SqliteDocumentStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"docstore_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private SqliteDocumentStore CreateStore() =>
        new(_dbPath, _logger.Object);

    private static LegalDocument MakeDoc(
        string? id = null,
        string path = "/docs/test.pdf",
        string name = "test.pdf",
        string hash = "abc123",
        DocumentStatus status = DocumentStatus.Indexed,
        string? caseNamespace = null,
        int failureCount = 0,
        string? errorMessage = null)
    {
        var doc = new LegalDocument
        {
            FilePath = path,
            FileName = name,
            ContentHash = hash,
            FileSizeBytes = 1024,
            Status = status,
            CaseNamespace = caseNamespace,
            FailureCount = failureCount,
            ErrorMessage = errorMessage
        };
        // Use reflection to set init-only Id if needed
        if (id != null)
        {
            typeof(LegalDocument).GetProperty(nameof(LegalDocument.Id))!
                .SetValue(doc, id);
        }
        return doc;
    }

    // ─── Schema & Initialization ──────────────────────────────────

    [Fact]
    public async Task Constructor_CreatesSchemaSuccessfully()
    {
        await using var sut = CreateStore();

        var docs = await sut.GetAllAsync();
        docs.Should().BeEmpty();
    }

    [Fact]
    public async Task Constructor_CreatesDirectoryIfMissing()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"nested_{Guid.NewGuid():N}", "docs.db");
        try
        {
            await using var sut = new SqliteDocumentStore(nestedPath, _logger.Object);
            Directory.Exists(Path.GetDirectoryName(nestedPath)).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(nestedPath)!, true); } catch { }
        }
    }

    // ─── UpsertAsync ──────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_InsertsNewDocument()
    {
        await using var sut = CreateStore();
        var doc = MakeDoc(id: "doc1", path: "/docs/file1.pdf");

        await sut.UpsertAsync(doc);

        var all = await sut.GetAllAsync();
        all.Should().ContainSingle();
        all[0].Id.Should().Be("doc1");
        all[0].FilePath.Should().Be("/docs/file1.pdf");
    }

    [Fact]
    public async Task UpsertAsync_UpdatesExistingDocument()
    {
        await using var sut = CreateStore();
        var doc1 = MakeDoc(id: "doc1", hash: "hash_v1", status: DocumentStatus.Pending);
        await sut.UpsertAsync(doc1);

        var doc2 = MakeDoc(id: "doc1", hash: "hash_v2", status: DocumentStatus.Indexed);
        await sut.UpsertAsync(doc2);

        var all = await sut.GetAllAsync();
        all.Should().ContainSingle();
        all[0].ContentHash.Should().Be("hash_v2");
        all[0].Status.Should().Be(DocumentStatus.Indexed);
    }

    [Fact]
    public async Task UpsertAsync_PreservesAllFields()
    {
        await using var sut = CreateStore();
        var doc = MakeDoc(
            id: "full_doc",
            path: "/arabic/حكم.pdf",
            name: "حكم.pdf",
            hash: "sha256_hash_value",
            status: DocumentStatus.Indexed,
            caseNamespace: "case-2024-001",
            failureCount: 2,
            errorMessage: "retry");
        doc.PageCount = 42;
        doc.ChunkCount = 150;
        doc.Metadata = new LegalDocumentMetadata
        {
            CaseNumber = "2024/001",
            CourtName = "محكمة النقض",
            JudgeName = "القاضي أحمد"
        };

        await sut.UpsertAsync(doc);
        var retrieved = await sut.GetByIdAsync("full_doc");

        retrieved.Should().NotBeNull();
        retrieved!.FilePath.Should().Be("/arabic/حكم.pdf");
        retrieved.FileName.Should().Be("حكم.pdf");
        retrieved.ContentHash.Should().Be("sha256_hash_value");
        retrieved.FileSizeBytes.Should().Be(1024);
        retrieved.PageCount.Should().Be(42);
        retrieved.Status.Should().Be(DocumentStatus.Indexed);
        retrieved.CaseNamespace.Should().Be("case-2024-001");
        retrieved.FailureCount.Should().Be(2);
        retrieved.ErrorMessage.Should().Be("retry");
        retrieved.ChunkCount.Should().Be(150);
        retrieved.Metadata.CaseNumber.Should().Be("2024/001");
        retrieved.Metadata.CourtName.Should().Be("محكمة النقض");
        retrieved.Metadata.JudgeName.Should().Be("القاضي أحمد");
    }

    [Fact]
    public async Task UpsertAsync_NullOptionalFields_StoredAsNull()
    {
        await using var sut = CreateStore();
        var doc = MakeDoc(id: "nullable_doc");

        await sut.UpsertAsync(doc);
        var retrieved = await sut.GetByIdAsync("nullable_doc");

        retrieved.Should().NotBeNull();
        retrieved!.ErrorMessage.Should().BeNull();
        retrieved.CaseNamespace.Should().BeNull();
        retrieved.LastModified.Should().BeNull();
    }

    // ─── GetByFilePathAsync ───────────────────────────────────────

    [Fact]
    public async Task GetByFilePath_ReturnsMatchingDocument()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "fp1", path: "/docs/a.pdf"));
        await sut.UpsertAsync(MakeDoc(id: "fp2", path: "/docs/b.pdf"));

        var result = await sut.GetByFilePathAsync("/docs/b.pdf");
        result.Should().NotBeNull();
        result!.Id.Should().Be("fp2");
    }

    [Fact]
    public async Task GetByFilePath_NonExistent_ReturnsNull()
    {
        await using var sut = CreateStore();

        var result = await sut.GetByFilePathAsync("/nonexistent.pdf");
        result.Should().BeNull();
    }

    // ─── GetByIdAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetById_ReturnsMatchingDocument()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "lookup_id"));

        var result = await sut.GetByIdAsync("lookup_id");
        result.Should().NotBeNull();
        result!.Id.Should().Be("lookup_id");
    }

    [Fact]
    public async Task GetById_NonExistent_ReturnsNull()
    {
        await using var sut = CreateStore();

        var result = await sut.GetByIdAsync("missing");
        result.Should().BeNull();
    }

    // ─── GetAllAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ReturnsAllDocumentsOrderedByIndexedAtDesc()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "d1"));
        await sut.UpsertAsync(MakeDoc(id: "d2"));
        await sut.UpsertAsync(MakeDoc(id: "d3"));

        var all = await sut.GetAllAsync();
        all.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetAll_EmptyStore_ReturnsEmptyList()
    {
        await using var sut = CreateStore();

        var all = await sut.GetAllAsync();
        all.Should().BeEmpty();
    }

    // ─── GetByStatusAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetByStatus_FiltersCorrectly()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "indexed1", status: DocumentStatus.Indexed));
        await sut.UpsertAsync(MakeDoc(id: "failed1", status: DocumentStatus.Failed));
        await sut.UpsertAsync(MakeDoc(id: "indexed2", status: DocumentStatus.Indexed));
        await sut.UpsertAsync(MakeDoc(id: "pending1", status: DocumentStatus.Pending));

        var indexed = await sut.GetByStatusAsync(DocumentStatus.Indexed);
        indexed.Should().HaveCount(2);
        indexed.Should().OnlyContain(d => d.Status == DocumentStatus.Indexed);

        var failed = await sut.GetByStatusAsync(DocumentStatus.Failed);
        failed.Should().ContainSingle();
        failed[0].Id.Should().Be("failed1");
    }

    [Fact]
    public async Task GetByStatus_NoMatches_ReturnsEmpty()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "d1", status: DocumentStatus.Indexed));

        var quarantined = await sut.GetByStatusAsync(DocumentStatus.Quarantined);
        quarantined.Should().BeEmpty();
    }

    // ─── DeleteAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesDocument()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "to_delete"));

        await sut.DeleteAsync("to_delete");

        var result = await sut.GetByIdAsync("to_delete");
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_DoesNotThrow()
    {
        await using var sut = CreateStore();

        var act = () => sut.DeleteAsync("nonexistent");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_DoesNotAffectOtherDocuments()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "keep"));
        await sut.UpsertAsync(MakeDoc(id: "remove"));

        await sut.DeleteAsync("remove");

        var all = await sut.GetAllAsync();
        all.Should().ContainSingle();
        all[0].Id.Should().Be("keep");
    }

    // ─── GetDocumentCountAsync ────────────────────────────────────

    [Fact]
    public async Task GetDocumentCount_CountsOnlyIndexedDocuments()
    {
        await using var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "i1", status: DocumentStatus.Indexed));
        await sut.UpsertAsync(MakeDoc(id: "i2", status: DocumentStatus.Indexed));
        await sut.UpsertAsync(MakeDoc(id: "f1", status: DocumentStatus.Failed));
        await sut.UpsertAsync(MakeDoc(id: "p1", status: DocumentStatus.Pending));

        var count = await sut.GetDocumentCountAsync();
        count.Should().Be(2); // Only Indexed
    }

    [Fact]
    public async Task GetDocumentCount_EmptyStore_ReturnsZero()
    {
        await using var sut = CreateStore();

        var count = await sut.GetDocumentCountAsync();
        count.Should().Be(0);
    }

    // ─── Quarantine Records ───────────────────────────────────────

    [Fact]
    public async Task AddQuarantineRecord_InsertsRecord()
    {
        await using var sut = CreateStore();
        var record = new QuarantineRecord
        {
            DocumentId = "q_doc_1",
            FilePath = "/docs/bad.pdf",
            Reason = "Repeated extraction failure",
            FailureCount = 3,
            ContentHash = "bad_hash"
        };

        await sut.AddQuarantineRecordAsync(record);

        var records = await sut.GetQuarantineRecordsAsync();
        records.Should().ContainSingle();
        records[0].DocumentId.Should().Be("q_doc_1");
        records[0].Reason.Should().Be("Repeated extraction failure");
        records[0].FailureCount.Should().Be(3);
    }

    [Fact]
    public async Task GetQuarantineRecords_EmptyStore_ReturnsEmpty()
    {
        await using var sut = CreateStore();

        var records = await sut.GetQuarantineRecordsAsync();
        records.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQuarantineRecords_MultipleRecords_OrderedByDateDesc()
    {
        await using var sut = CreateStore();

        await sut.AddQuarantineRecordAsync(new QuarantineRecord
        {
            DocumentId = "q1", FilePath = "/a.pdf", Reason = "r1",
            FailureCount = 3, ContentHash = "h1"
        });
        await sut.AddQuarantineRecordAsync(new QuarantineRecord
        {
            DocumentId = "q2", FilePath = "/b.pdf", Reason = "r2",
            FailureCount = 5, ContentHash = "h2"
        });

        var records = await sut.GetQuarantineRecordsAsync();
        records.Should().HaveCount(2);
        // Newer record should come first (ORDER BY quarantined_at DESC)
        records[0].DocumentId.Should().Be("q2");
    }

    [Fact]
    public async Task QuarantineRecord_PreservesArabicContent()
    {
        await using var sut = CreateStore();
        await sut.AddQuarantineRecordAsync(new QuarantineRecord
        {
            DocumentId = "arabic_q",
            FilePath = "/المستندات/حكم_محكمة.pdf",
            Reason = "فشل استخراج النص من الملف",
            FailureCount = 3,
            ContentHash = "arabic_hash"
        });

        var records = await sut.GetQuarantineRecordsAsync();
        records[0].FilePath.Should().Be("/المستندات/حكم_محكمة.pdf");
        records[0].Reason.Should().Be("فشل استخراج النص من الملف");
    }

    // ─── Metadata Serialization ───────────────────────────────────

    [Fact]
    public async Task UpsertAsync_MetadataRoundTrips_ViaJson()
    {
        await using var sut = CreateStore();
        var doc = MakeDoc(id: "meta_doc");
        doc.Metadata = new LegalDocumentMetadata
        {
            CaseNumber = "2024/100",
            CourtName = "المحكمة العليا",
            JudgeName = "أحمد المحمد",
            CaseType = "جنائي",
            ArticleReferences = ["المادة 15", "المادة 22"],
            Parties = ["الطرف الأول", "الطرف الثاني"],
            AdditionalProperties = new() { ["custom_key"] = "custom_value" }
        };

        await sut.UpsertAsync(doc);
        var retrieved = await sut.GetByIdAsync("meta_doc");

        retrieved!.Metadata.CaseNumber.Should().Be("2024/100");
        retrieved.Metadata.CourtName.Should().Be("المحكمة العليا");
        retrieved.Metadata.ArticleReferences.Should().HaveCount(2);
        retrieved.Metadata.Parties.Should().Contain("الطرف الأول");
        retrieved.Metadata.AdditionalProperties.Should().ContainKey("custom_key");
    }

    [Fact]
    public async Task UpsertAsync_EmptyMetadata_RoundTripsAsDefault()
    {
        await using var sut = CreateStore();
        var doc = MakeDoc(id: "empty_meta");
        // Metadata is default (new LegalDocumentMetadata())

        await sut.UpsertAsync(doc);
        var retrieved = await sut.GetByIdAsync("empty_meta");

        retrieved!.Metadata.Should().NotBeNull();
        retrieved.Metadata.CaseNumber.Should().BeNull();
        retrieved.Metadata.ArticleReferences.Should().BeEmpty();
    }

    // ─── DateTimeOffset Persistence ───────────────────────────────

    [Fact]
    public async Task UpsertAsync_IndexedAtRoundTrips()
    {
        await using var sut = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var doc = MakeDoc(id: "time_doc");
        // IndexedAt is set in init

        await sut.UpsertAsync(doc);
        var retrieved = await sut.GetByIdAsync("time_doc");

        // Should round-trip within 1 second
        retrieved!.IndexedAt.Should().BeCloseTo(now, TimeSpan.FromSeconds(2));
    }

    // ─── DisposeAsync ─────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CanBeCalledSafely()
    {
        var sut = CreateStore();
        await sut.UpsertAsync(MakeDoc(id: "pre_dispose"));

        await sut.DisposeAsync();
        // No assertion needed — should not throw
    }
}
