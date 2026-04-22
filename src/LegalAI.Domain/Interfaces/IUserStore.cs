using LegalAI.Domain.Entities;

namespace LegalAI.Domain.Interfaces;

public interface IUserStore
{
    Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<UserAccount?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<UserAccount>> GetAllAsync(CancellationToken ct = default);
    Task<int> GetCountAsync(CancellationToken ct = default);
    Task UpsertAsync(UserAccount user, CancellationToken ct = default);
    Task SetDisabledAsync(string id, bool isDisabled, CancellationToken ct = default);
    Task SetLastLoginAsync(string id, DateTimeOffset loggedInAt, CancellationToken ct = default);
}
