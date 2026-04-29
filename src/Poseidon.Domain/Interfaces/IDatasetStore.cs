using Poseidon.Domain.Entities;

namespace Poseidon.Domain.Interfaces;

public interface IDatasetStore
{
    Task CreateAsync(Dataset dataset, CancellationToken ct = default);
    Task<Dataset?> GetByIdAsync(string datasetId, CancellationToken ct = default);
    Task<List<Dataset>> GetByDomainAsync(
        string domainId,
        bool includeArchived = false,
        CancellationToken ct = default);
    Task<List<Dataset>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default);
    Task UpdateAsync(Dataset dataset, CancellationToken ct = default);
    Task SetLifecycleAsync(string datasetId, DatasetLifecycle lifecycle, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(string domainId, string datasetName, CancellationToken ct = default);
}

