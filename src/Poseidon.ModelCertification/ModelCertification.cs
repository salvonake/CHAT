using System.Text.Json;

namespace Poseidon.ModelCertification;

public sealed class ModelCertificationService
{
    private readonly GgufMetadataInspector _inspector;
    private readonly ModelCompatibilityMatrix _compatibilityMatrix;
    private readonly TokenizerAssetValidator _tokenizerValidator;

    public ModelCertificationService()
        : this(new GgufMetadataInspector(), new ModelCompatibilityMatrix(), new TokenizerAssetValidator())
    {
    }

    public ModelCertificationService(
        GgufMetadataInspector inspector,
        ModelCompatibilityMatrix compatibilityMatrix,
        TokenizerAssetValidator tokenizerValidator)
    {
        _inspector = inspector;
        _compatibilityMatrix = compatibilityMatrix;
        _tokenizerValidator = tokenizerValidator;
    }

    public ModelCertificationReport Certify(string modelPath, ModelCertificationOptions options)
    {
        var inspection = _inspector.Inspect(modelPath);
        var tokenizer = _tokenizerValidator.Validate(options.TokenizerPolicy, options.TokenizerPath, options.WarningAccepted);
        var decision = _compatibilityMatrix.Evaluate(inspection, tokenizer, options);
        var acceptedForPackaging = decision.Compatible ||
                                   (options.BuildProfile.Equals("NonProduction", StringComparison.OrdinalIgnoreCase) &&
                                    options.AllowUncertifiedModel &&
                                    options.WarningAccepted);

        var status = decision.Compatible
            ? (decision.Warnings.Count > 0 ? "compatible-with-warnings" : "compatible")
            : acceptedForPackaging
                ? "override-accepted"
                : "incompatible";

        return new ModelCertificationReport
        {
            SchemaVersion = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            FileName = inspection.FileName,
            FileSizeBytes = inspection.FileSizeBytes,
            Sha256 = inspection.Sha256,
            GgufVersion = inspection.GgufVersion,
            Architecture = inspection.Architecture,
            Quantization = inspection.Quantization,
            TensorCount = inspection.TensorCount,
            MetadataCount = inspection.MetadataCount,
            ContextLength = inspection.ContextLength,
            Backend = options.Backend,
            Compatible = decision.Compatible,
            AcceptedForPackaging = acceptedForPackaging,
            CompatibilityStatus = status,
            Tokenizer = tokenizer,
            TensorTypeSummary = inspection.TensorTypeSummary,
            TokenizerMetadataKeys = inspection.Tokenizer.Keys,
            RopeMetadataKeys = inspection.Tokenizer.RopeKeys,
            Warnings = decision.Warnings,
            FailureReasons = decision.FailureReasons,
            SecurityPosture = "static-inspection-only; no LLamaSharp, llama.cpp, GPU, or inference initialization executed"
        };
    }

    public static void WriteReport(ModelCertificationReport report, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(report, ModelCertificationJson.Options);
        File.WriteAllText(path, json);
    }
}

public sealed class ModelCompatibilityMatrix
{
    public const string CertifiedBackend = "LLamaSharp 0.19.0 CPU AVX2";

    private static readonly ISet<string> AllowedArchitectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "llama"
    };

    private static readonly ISet<string> BlockedArchitectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "qwen",
        "qwen2",
        "qwen3",
        "mistral",
        "gemma",
        "falcon"
    };

    private static readonly ISet<string> AllowedQuantizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Q4_0",
        "Q4_K",
        "Q4_K_M",
        "Q5_K",
        "Q5_K_M",
        "Q8_0"
    };

    public ModelCompatibilityDecision Evaluate(
        GgufInspectionResult inspection,
        TokenizerValidationResult tokenizer,
        ModelCertificationOptions options)
    {
        var failures = new List<string>();
        var warnings = new List<string>();

        if (!options.Backend.Equals(CertifiedBackend, StringComparison.OrdinalIgnoreCase))
            failures.Add($"Unsupported certified backend target: {options.Backend}.");

        if (inspection.GgufVersion is < 2 or > 3)
            failures.Add($"Unsupported GGUF version: {inspection.GgufVersion}.");

        if (BlockedArchitectures.Contains(inspection.Architecture))
            failures.Add($"Architecture is explicitly blocked for {CertifiedBackend}: {inspection.Architecture}.");
        else if (!AllowedArchitectures.Contains(inspection.Architecture))
            failures.Add($"Architecture is not certified for {CertifiedBackend}: {inspection.Architecture}.");

        if (!AllowedQuantizations.Contains(inspection.Quantization))
            failures.Add($"Quantization is not certified for {CertifiedBackend}: {inspection.Quantization}.");

        if (!inspection.Tokenizer.MetadataPresent)
            warnings.Add("GGUF tokenizer metadata was not declared.");

        if (tokenizer.MissingAssets.Count > 0 &&
            tokenizer.Policy.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            warnings.AddRange(tokenizer.MissingAssets.Select(asset => $"Tokenizer asset missing under warning policy: {asset}."));
        }

        if (!tokenizer.Valid)
        {
            if (tokenizer.Policy.Equals("required", StringComparison.OrdinalIgnoreCase))
                failures.AddRange(tokenizer.MissingAssets.Select(asset => $"Required tokenizer asset missing: {asset}."));
            else if (!tokenizer.Policy.Equals("warning", StringComparison.OrdinalIgnoreCase))
                warnings.AddRange(tokenizer.MissingAssets.Select(asset => $"Tokenizer asset missing under warning policy: {asset}."));
        }

        if (inspection.FileName.Contains("qwen", StringComparison.OrdinalIgnoreCase) &&
            inspection.Architecture.Equals("llama", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Filename contains Qwen while GGUF metadata reports llama architecture; preserved filename contract must be documented.");
        }

        var compatible = failures.Count == 0 &&
                         (tokenizer.Valid || (tokenizer.Policy.Equals("warning", StringComparison.OrdinalIgnoreCase) &&
                                              tokenizer.WarningAccepted));

        return new ModelCompatibilityDecision(compatible, failures, warnings);
    }
}

public sealed class TokenizerAssetValidator
{
    public TokenizerValidationResult Validate(string policy, string? tokenizerPath, bool warningAccepted)
    {
        var normalizedPolicy = string.IsNullOrWhiteSpace(policy)
            ? "warning"
            : policy.Trim().ToLowerInvariant();

        if (normalizedPolicy is not ("required" or "warning" or "not-required"))
            throw new ArgumentException($"Unsupported tokenizer policy: {policy}", nameof(policy));

        if (normalizedPolicy == "not-required")
        {
            return new TokenizerValidationResult(
                Policy: normalizedPolicy,
                RequiredAssets: Array.Empty<string>(),
                MissingAssets: Array.Empty<string>(),
                WarningAccepted: warningAccepted,
                Valid: true);
        }

        var requiredAssets = new[] { "vocab.txt" };
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(tokenizerPath) || !File.Exists(tokenizerPath))
            missing.Add("vocab.txt");

        var valid = missing.Count == 0 || (normalizedPolicy == "warning" && warningAccepted);
        return new TokenizerValidationResult(
            Policy: normalizedPolicy,
            RequiredAssets: requiredAssets,
            MissingAssets: missing,
            WarningAccepted: warningAccepted,
            Valid: valid);
    }
}

public static class ModelCertificationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed record ModelCertificationOptions(
    string Backend,
    string BuildProfile,
    string TokenizerPolicy,
    string? TokenizerPath,
    bool AllowUncertifiedModel,
    bool WarningAccepted);

public sealed record ModelCompatibilityDecision(
    bool Compatible,
    IReadOnlyList<string> FailureReasons,
    IReadOnlyList<string> Warnings);

public sealed class ModelCertificationReport
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string FileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public uint GgufVersion { get; set; }
    public string Architecture { get; set; } = "";
    public string Quantization { get; set; } = "";
    public ulong TensorCount { get; set; }
    public ulong MetadataCount { get; set; }
    public long? ContextLength { get; set; }
    public string Backend { get; set; } = "";
    public bool Compatible { get; set; }
    public bool AcceptedForPackaging { get; set; }
    public string CompatibilityStatus { get; set; } = "";
    public TokenizerValidationResult Tokenizer { get; set; } = new("warning", Array.Empty<string>(), Array.Empty<string>(), false, true);
    public IReadOnlyList<TensorTypeSummary> TensorTypeSummary { get; set; } = Array.Empty<TensorTypeSummary>();
    public IReadOnlyList<string> TokenizerMetadataKeys { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> RopeMetadataKeys { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> FailureReasons { get; set; } = Array.Empty<string>();
    public string SecurityPosture { get; set; } = "";
}

public sealed record GgufInspectionResult(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    string Sha256,
    uint GgufVersion,
    ulong TensorCount,
    ulong MetadataCount,
    string Architecture,
    string Quantization,
    long? ContextLength,
    IReadOnlyList<GgufMetadataEntry> Metadata,
    IReadOnlyList<TensorTypeSummary> TensorTypeSummary,
    TokenizerInspectionResult Tokenizer);

public sealed record GgufMetadataEntry(
    string Key,
    string Type,
    string Summary,
    string? ScalarValue,
    ulong? ArrayLength);

public sealed record TensorTypeSummary(string TypeName, int Count);

public sealed record TokenizerInspectionResult(
    bool MetadataPresent,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> RopeKeys);

public sealed record TokenizerValidationResult(
    string Policy,
    IReadOnlyList<string> RequiredAssets,
    IReadOnlyList<string> MissingAssets,
    bool WarningAccepted,
    bool Valid);
