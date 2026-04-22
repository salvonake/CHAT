namespace LegalAI.Application.Services;

public static class ScopeNamespaceBuilder
{
    public static string? Build(string? domainId, string? datasetScope)
    {
        if (string.IsNullOrWhiteSpace(domainId) && string.IsNullOrWhiteSpace(datasetScope))
            return null;

        var normalizedDomain = Normalize(domainId, "default");
        var normalizedDataset = Normalize(datasetScope, "default");

        return $"{normalizedDomain}:{normalizedDataset}";
    }

    public static (string? DomainId, string? DatasetScope) Parse(string? caseNamespace)
    {
        if (string.IsNullOrWhiteSpace(caseNamespace))
            return (null, null);

        var parts = caseNamespace.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return (null, null);

        return (Normalize(parts[0], "default"), Normalize(parts[1], "default"));
    }

    private static string Normalize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '-')
            .ToArray();

        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
