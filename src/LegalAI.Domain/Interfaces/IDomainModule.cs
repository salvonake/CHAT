using LegalAI.Domain.ValueObjects;

namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Describes a pluggable domain module that provides prompts, metadata schema,
/// and pipeline defaults for a specific industry domain.
/// </summary>
public interface IDomainModule
{
    string DomainId { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<string> SupportedDocumentTypes { get; }
    DomainPipelineSettings PipelineSettings { get; }
    IReadOnlyDictionary<string, string> MetadataSchema { get; }
    IDomainPromptTemplateProvider PromptTemplates { get; }
}

/// <summary>
/// Provides domain-specific prompt templates and answer constraints.
/// </summary>
public interface IDomainPromptTemplateProvider
{
    string CitationFormat { get; }
    string GetSystemPrompt(bool strictMode);
    string GetInsufficientEvidenceMessage();
}

/// <summary>
/// Resolves available domain modules and tracks the active default module.
/// </summary>
public interface IDomainModuleRegistry
{
    string ActiveDomainId { get; }
    IDomainModule ActiveModule { get; }
    IReadOnlyCollection<IDomainModule> GetAll();
    bool TryGet(string domainId, out IDomainModule module);
    IDomainModule GetRequired(string domainId);
}

/// <summary>
/// Query analysis abstraction so intent/entity extraction can vary by domain.
/// </summary>
public interface IDomainQueryAnalyzer
{
    QueryAnalysis Analyze(string query);
}

/// <summary>
/// Marker interface for domain chunkers.
/// </summary>
public interface IDomainChunker : IDocumentChunker
{
}

public sealed record DomainPipelineSettings(
    int ChunkSize,
    int ChunkOverlap,
    bool EnableNormalization,
    bool EnableMetadataExtraction);
