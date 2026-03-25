using LegalAI.Domain.ValueObjects;
using MediatR;

namespace LegalAI.Application.Queries;

/// <summary>
/// Ask a legal question — the core query that triggers the full EC-RAG pipeline.
/// </summary>
public sealed class AskLegalQuestionQuery : IRequest<LegalAnswer>
{
    public required string Question { get; init; }
    public string? CaseNamespace { get; init; }
    public bool StrictMode { get; init; } = true;
    public int TopK { get; init; } = 10;
    public string? UserId { get; init; }
}
