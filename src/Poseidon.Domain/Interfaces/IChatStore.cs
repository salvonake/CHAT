using Poseidon.Domain.Entities;

namespace Poseidon.Domain.Interfaces;

public interface IChatStore
{
    Task<ChatSession> CreateSessionAsync(ChatSession session, CancellationToken ct = default);
    Task<ChatSession?> GetSessionAsync(string sessionId, CancellationToken ct = default);
    Task<List<ChatSession>> GetSessionsForUserAsync(string userId, bool includeArchived, CancellationToken ct = default);
    Task RenameSessionAsync(string sessionId, string title, CancellationToken ct = default);
    Task ArchiveSessionAsync(string sessionId, bool archived, CancellationToken ct = default);
    Task AddMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task<List<ChatMessage>> GetMessagesAsync(string sessionId, int limit = 200, CancellationToken ct = default);
}

