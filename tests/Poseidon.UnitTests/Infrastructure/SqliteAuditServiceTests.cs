using System.IO;
using System.Text;
using FluentAssertions;
using Poseidon.Domain.Interfaces;
using Poseidon.Infrastructure.Audit;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Infrastructure;

/// <summary>
/// Tests for <see cref="SqliteAuditService"/>: append-only, HMAC-signed,
/// tamper-evident audit chain stored in SQLite.
/// Uses temp files because SqliteAuditService takes a databasePath string,
/// not a pre-opened connection.
/// </summary>
public sealed class SqliteAuditServiceTests : IDisposable
{
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly Mock<ILogger<SqliteAuditService>> _logger = new();
    private readonly string _dbPath;

    public SqliteAuditServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}.db");

        // Default: encryption disabled â†’ SHA256 fallback
        _encryption.Setup(e => e.IsEnabled).Returns(false);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }

    private SqliteAuditService CreateService() =>
        new(_dbPath, _encryption.Object, _logger.Object);

    // â”€â”€â”€ Schema & Initialization â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Constructor_CreatesSchemaAndOpensConnection()
    {
        await using var sut = CreateService();

        // Should be able to log immediately after construction
        await sut.LogAsync("test_action", "test_details");
        var entries = await sut.GetEntriesAsync();
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Constructor_CreatesDirectoryIfMissing()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"nested_{Guid.NewGuid():N}", "audit.db");
        try
        {
            await using var sut = new SqliteAuditService(nestedPath, _encryption.Object, _logger.Object);
            Directory.Exists(Path.GetDirectoryName(nestedPath)).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(nestedPath)!, true); } catch { }
        }
    }

    // â”€â”€â”€ LogAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task LogAsync_StoresActionAndDetails()
    {
        await using var sut = CreateService();

        await sut.LogAsync("QUERY", "User asked a question", "user-42");

        var entries = await sut.GetEntriesAsync();
        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.Action.Should().Be("QUERY");
        entry.Details.Should().Be("User asked a question");
        entry.UserId.Should().Be("user-42");
    }

    [Fact]
    public async Task LogAsync_NullUserId_StoredAsNull()
    {
        await using var sut = CreateService();

        await sut.LogAsync("SYSTEM_EVENT", "Boot complete");

        var entries = await sut.GetEntriesAsync();
        entries[0].UserId.Should().BeNull();
    }

    [Fact]
    public async Task LogAsync_FirstEntry_PreviousHashIsGenesis()
    {
        await using var sut = CreateService();

        await sut.LogAsync("FIRST", "first entry");

        var entries = await sut.GetEntriesAsync();
        entries[0].PreviousHash.Should().Be("GENESIS");
    }

    [Fact]
    public async Task LogAsync_SecondEntry_ChainsToPreviousHmac()
    {
        await using var sut = CreateService();

        await sut.LogAsync("ONE", "first");
        await sut.LogAsync("TWO", "second");

        var entries = await sut.GetEntriesAsync(); // DESC order
        var second = entries[0]; // newest
        var first = entries[1];  // oldest

        second.PreviousHash.Should().Be(first.Hmac);
    }

    [Fact]
    public async Task LogAsync_HmacNotEmpty()
    {
        await using var sut = CreateService();

        await sut.LogAsync("ACTION", "details");

        var entries = await sut.GetEntriesAsync();
        entries[0].Hmac.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LogAsync_TimestampIsReasonable()
    {
        var before = DateTimeOffset.UtcNow;

        await using var sut = CreateService();
        await sut.LogAsync("TIME_TEST", "check timestamp");

        var after = DateTimeOffset.UtcNow;
        var entries = await sut.GetEntriesAsync();
        entries[0].Timestamp.Should().BeOnOrAfter(before);
        entries[0].Timestamp.Should().BeOnOrBefore(after);
    }

    // â”€â”€â”€ Encryption Integration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task LogAsync_EncryptionEnabled_UsesHmacFromService()
    {
        _encryption.Setup(e => e.IsEnabled).Returns(true);
        _encryption.Setup(e => e.ComputeHmac(It.IsAny<byte[]>()))
            .Returns((byte[] data) =>
            {
                // Return a deterministic "mock HMAC" based on length
                return Encoding.UTF8.GetBytes("MOCK_HMAC_RESULT");
            });

        await using var sut = CreateService();
        await sut.LogAsync("ENC_TEST", "encrypted");

        var entries = await sut.GetEntriesAsync();
        entries[0].Hmac.Should().NotBeNullOrWhiteSpace();
        _encryption.Verify(e => e.ComputeHmac(It.IsAny<byte[]>()), Times.Once);
    }

    [Fact]
    public async Task LogAsync_EncryptionDisabled_UsesSha256Fallback()
    {
        _encryption.Setup(e => e.IsEnabled).Returns(false);

        await using var sut = CreateService();
        await sut.LogAsync("SHA_TEST", "sha256");

        var entries = await sut.GetEntriesAsync();
        // SHA256 hex string is 64 chars
        entries[0].Hmac.Should().HaveLength(64);
        _encryption.Verify(e => e.ComputeHmac(It.IsAny<byte[]>()), Times.Never);
    }

    // â”€â”€â”€ GetEntriesAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GetEntriesAsync_ReturnsEmptyOnFreshDatabase()
    {
        await using var sut = CreateService();

        var entries = await sut.GetEntriesAsync();
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntriesAsync_ReturnsInDescendingOrder()
    {
        await using var sut = CreateService();
        await sut.LogAsync("A", "first");
        await sut.LogAsync("B", "second");
        await sut.LogAsync("C", "third");

        var entries = await sut.GetEntriesAsync();
        entries.Should().HaveCount(3);
        entries[0].Action.Should().Be("C");
        entries[1].Action.Should().Be("B");
        entries[2].Action.Should().Be("A");
    }

    [Fact]
    public async Task GetEntriesAsync_LimitRespectsParameter()
    {
        await using var sut = CreateService();
        for (int i = 0; i < 10; i++)
            await sut.LogAsync($"ACTION_{i}", $"details_{i}");

        var entries = await sut.GetEntriesAsync(limit: 3);
        entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetEntriesAsync_OffsetSkipsEntries()
    {
        await using var sut = CreateService();
        for (int i = 0; i < 5; i++)
            await sut.LogAsync($"ACTION_{i}", $"details_{i}");

        var all = await sut.GetEntriesAsync(limit: 100);
        var offset = await sut.GetEntriesAsync(limit: 100, offset: 2);

        offset.Should().HaveCount(3);
        offset[0].Action.Should().Be(all[2].Action);
    }

    // â”€â”€â”€ VerifyChainIntegrityAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task VerifyChainIntegrity_EmptyDatabase_ReturnsTrue()
    {
        await using var sut = CreateService();

        var result = await sut.VerifyChainIntegrityAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyChainIntegrity_ValidChain_ReturnsTrue()
    {
        await using var sut = CreateService();
        await sut.LogAsync("ONE", "first");
        await sut.LogAsync("TWO", "second");
        await sut.LogAsync("THREE", "third");

        var result = await sut.VerifyChainIntegrityAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyChainIntegrity_TamperedHmac_ReturnsFalse()
    {
        await using var sut = CreateService();
        await sut.LogAsync("ENTRY_1", "data");
        await sut.LogAsync("ENTRY_2", "more data");

        // Tamper: modify the HMAC of the first entry directly in SQLite
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE audit_log SET hmac = 'TAMPERED' WHERE id = 1";
        cmd.ExecuteNonQuery();
        conn.Close();

        var result = await sut.VerifyChainIntegrityAsync();
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyChainIntegrity_BrokenChainLink_ReturnsFalse()
    {
        await using var sut = CreateService();
        await sut.LogAsync("ENTRY_1", "data");
        await sut.LogAsync("ENTRY_2", "more data");

        // Tamper: modify the previous_hash of the second entry
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE audit_log SET previous_hash = 'BROKEN_LINK' WHERE id = 2";
        cmd.ExecuteNonQuery();
        conn.Close();

        var result = await sut.VerifyChainIntegrityAsync();
        result.Should().BeFalse();
    }

    // â”€â”€â”€ Chain Persistence â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Chain_PersistsAcrossInstances()
    {
        // First instance writes entries
        await using (var sut1 = CreateService())
        {
            await sut1.LogAsync("FIRST_INSTANCE", "hello");
        }

        // Second instance should load the last hash and continue the chain
        await using var sut2 = CreateService();
        await sut2.LogAsync("SECOND_INSTANCE", "world");

        var entries = await sut2.GetEntriesAsync();
        entries.Should().HaveCount(2);

        // The chain should still be valid
        var valid = await sut2.VerifyChainIntegrityAsync();
        valid.Should().BeTrue();
    }

    // â”€â”€â”€ Concurrency â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task LogAsync_ConcurrentWrites_AllSucceed()
    {
        await using var sut = CreateService();

        var tasks = Enumerable.Range(0, 20)
            .Select(i => sut.LogAsync($"CONCURRENT_{i}", $"details_{i}"));

        await Task.WhenAll(tasks);

        var entries = await sut.GetEntriesAsync(limit: 100);
        entries.Should().HaveCount(20);

        // Chain should still be valid (semaphore ensures serial writes)
        var valid = await sut.VerifyChainIntegrityAsync();
        valid.Should().BeTrue();
    }

    // â”€â”€â”€ DisposeAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task DisposeAsync_CanBeCalledSafely()
    {
        var sut = CreateService();
        await sut.LogAsync("PRE_DISPOSE", "data");

        // Should not throw
        await sut.DisposeAsync();
    }
}

