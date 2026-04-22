using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace LegalAI.Application.Services;

public interface IPromptTemplateEngine
{
    string BuildSystemPrompt(string? domainId, bool strictMode);
    string BuildInsufficientEvidenceMessage(string? domainId);
}

public sealed class PromptTemplateEngine : IPromptTemplateEngine
{
    private readonly IDomainModuleRegistry _registry;
    private readonly ILogger<PromptTemplateEngine> _logger;

    public PromptTemplateEngine(
        IDomainModuleRegistry registry,
        ILogger<PromptTemplateEngine> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public string BuildSystemPrompt(string? domainId, bool strictMode)
    {
        var module = ResolveModule(domainId);
        return module.PromptTemplates.GetSystemPrompt(strictMode);
    }

    public string BuildInsufficientEvidenceMessage(string? domainId)
    {
        var module = ResolveModule(domainId);
        return module.PromptTemplates.GetInsufficientEvidenceMessage();
    }

    private IDomainModule ResolveModule(string? domainId)
    {
        if (!string.IsNullOrWhiteSpace(domainId) && _registry.TryGet(domainId, out var module))
        {
            return module;
        }

        if (!string.IsNullOrWhiteSpace(domainId))
        {
            _logger.LogWarning(
                "Domain module '{DomainId}' is not registered. Falling back to active domain '{ActiveDomainId}'.",
                domainId,
                _registry.ActiveDomainId);
        }

        return _registry.ActiveModule;
    }
}
