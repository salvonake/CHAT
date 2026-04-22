using LegalAI.Domain.Entities;

namespace LegalAI.Domain.Interfaces;

public interface IUserDomainGrantStore
{
    Task UpsertAsync(UserDomainGrant grant, CancellationToken ct = default);
    Task RemoveAsync(
        string userId,
        string domainId,
        string? datasetScope = null,
        CancellationToken ct = default);
    Task<List<UserDomainGrant>> GetForUserAsync(string userId, CancellationToken ct = default);
    Task<bool> HasAccessAsync(
        string userId,
        string domainId,
        string? datasetScope = null,
        CancellationToken ct = default);
}
