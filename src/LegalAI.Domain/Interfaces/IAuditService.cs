using LegalAI.Domain.Entities;

namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Append-only HMAC-signed audit log.
/// </summary>
public interface IAuditService
{
    Task LogAsync(string action, string details, string? userId = null, CancellationToken ct = default);
    Task<List<AuditEntry>> GetEntriesAsync(int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<bool> VerifyChainIntegrityAsync(CancellationToken ct = default);
}
