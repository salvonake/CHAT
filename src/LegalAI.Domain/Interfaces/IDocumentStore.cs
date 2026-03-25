using LegalAI.Domain.Entities;

namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Manages the document index state: tracking which files have been indexed,
/// their hashes, and quarantine state.
/// </summary>
public interface IDocumentStore
{
    Task<LegalDocument?> GetByFilePathAsync(string filePath, CancellationToken ct = default);
    Task<LegalDocument?> GetByIdAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(LegalDocument document, CancellationToken ct = default);
    Task<List<LegalDocument>> GetAllAsync(CancellationToken ct = default);
    Task<List<LegalDocument>> GetByStatusAsync(DocumentStatus status, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<int> GetDocumentCountAsync(CancellationToken ct = default);
    Task<List<QuarantineRecord>> GetQuarantineRecordsAsync(CancellationToken ct = default);
    Task AddQuarantineRecordAsync(QuarantineRecord record, CancellationToken ct = default);
}
