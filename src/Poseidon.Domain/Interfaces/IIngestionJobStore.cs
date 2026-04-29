using Poseidon.Domain.Entities;

namespace Poseidon.Domain.Interfaces;

public interface IIngestionJobStore
{
    Task<IngestionJob?> GetByFileAndHashAsync(string filePath, string contentHash, CancellationToken ct = default);
    Task<IngestionJob> CreateAsync(IngestionJob job, CancellationToken ct = default);
    Task MarkRunningAsync(string id, int attemptCount, CancellationToken ct = default);
    Task MarkSucceededAsync(string id, CancellationToken ct = default);
    Task MarkFailedAsync(string id, string error, DateTimeOffset? nextAttemptAt, CancellationToken ct = default);
    Task MarkQuarantinedAsync(string id, string quarantinePath, string error, CancellationToken ct = default);
    Task<List<IngestionJob>> GetRecentAsync(int limit = 100, CancellationToken ct = default);
}

