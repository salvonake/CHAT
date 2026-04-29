using Poseidon.Domain.Interfaces;

namespace Poseidon.Domain.DomainModules;

public sealed class InMemoryDomainModuleRegistry : IDomainModuleRegistry
{
    private readonly Dictionary<string, IDomainModule> _modules;

    public InMemoryDomainModuleRegistry(
        IEnumerable<IDomainModule> modules,
        string? activeDomainId = null)
    {
        _modules = modules.ToDictionary(
            m => m.DomainId,
            m => m,
            StringComparer.OrdinalIgnoreCase);

        if (_modules.Count == 0)
        {
            throw new InvalidOperationException("At least one domain module must be registered.");
        }

        ActiveDomainId = string.IsNullOrWhiteSpace(activeDomainId)
            ? _modules.Keys.First()
            : activeDomainId;

        if (!_modules.ContainsKey(ActiveDomainId))
        {
            ActiveDomainId = _modules.Keys.First();
        }
    }

    public string ActiveDomainId { get; }

    public IDomainModule ActiveModule => GetRequired(ActiveDomainId);

    public IReadOnlyCollection<IDomainModule> GetAll()
    {
        return _modules.Values.ToList();
    }

    public bool TryGet(string domainId, out IDomainModule module)
    {
        return _modules.TryGetValue(domainId, out module!);
    }

    public IDomainModule GetRequired(string domainId)
    {
        if (TryGet(domainId, out var module))
        {
            return module;
        }

        throw new KeyNotFoundException($"Domain module '{domainId}' is not registered.");
    }
}

