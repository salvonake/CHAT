using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Poseidon.ModelCertification;
using Poseidon.Security.Configuration;
using Poseidon.Security.Secrets;

var options = ParseArgs(args);
var logPath = ResolveLogPath(options);
var installDir = ResolveInstallDir(options);

try
{
    Log(logPath, "provisioning-check started");

    if (options.TryGetValue("certify-model", out var modelPath))
        return RunModelCertification(options, modelPath, logPath);

    if (options.TryGetValue("set-secret", out var secretReference))
    {
        var secretValue = Required(options, "value");
        if (!ProtectedSecretReference.TryParse(secretReference, out var reference))
            throw new InvalidOperationException("--set-secret must use dpapi:<scope>:<name>:<version>.");

        ProtectedSecretStore.Save(reference, secretValue);
        Log(logPath, "protected secret provisioned: " + RedactReference(reference.ToString()));
        Console.WriteLine("Protected secret provisioned: " + RedactReference(reference.ToString()));
        return 0;
    }

    var configPath = Path.GetFullPath(Required(options, "config"));
    var manifestPath = options.TryGetValue("manifest", out var manifest) ? manifest : "";
    var mode = options.TryGetValue("mode", out var configuredMode)
        ? configuredMode.Trim().ToLowerInvariant()
        : "full";

    var provisioningConfiguration = new ConfigurationBuilder()
        .AddJsonFile(configPath, optional: false, reloadOnChange: false)
        .Build();
    var allowDeferredSecrets =
        options.TryGetValue("allow-deferred-secrets", out var allowDeferred) &&
        bool.TryParse(allowDeferred, out var parsedAllowDeferred) &&
        parsedAllowDeferred;
    SecurityConfigurationValidator.ValidateProvisioning(
        provisioningConfiguration,
        mode,
        allowDeferredSecrets: allowDeferredSecrets);

    var validation = ValidateConfig(configPath, installDir);
    var certificationReportPath = options.TryGetValue("certification-report", out var reportPath) ? reportPath : "";
    if (!string.IsNullOrWhiteSpace(manifestPath))
        ValidateManifest(manifestPath, validation, installDir, mode, certificationReportPath);

    if (mode == "degraded" && validation.LlmProvider == "llamasharp" && !string.IsNullOrWhiteSpace(validation.LlmModelPath))
        throw new InvalidOperationException("Degraded mode must not declare a local LLM model.");

    if (mode != "degraded" && validation.LlmProvider == "llamasharp")
        ValidateModelFile(validation.LlmModelPath, ".gguf", "LLM", validation.ExpectedLlmHash, requireHash: true);

    if (validation.EmbeddingProvider == "onnx")
        ValidateModelFile(validation.EmbeddingOnnxModelPath, ".onnx", "Embedding", validation.ExpectedEmbeddingHash, requireHash: true);

    if (validation.LlmProvider == "ollama" || validation.EmbeddingProvider == "ollama")
        ValidateOllamaConfiguration(validation);

    Log(logPath, mode == "degraded"
        ? "provisioning-check succeeded in degraded mode"
        : "provisioning-check succeeded in full mode");

    return 0;
}
catch (Exception ex)
{
    Log(logPath, "provisioning-check failed: " + ex.Message);
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            continue;

        var key = arg[2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            values[key] = args[++i];
        else
            values[key] = "true";
    }

    return values;
}

static string Required(IReadOnlyDictionary<string, string> options, string key)
{
    if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"Missing required argument: --{key}");

    return value;
}

static int RunModelCertification(IReadOnlyDictionary<string, string> options, string modelPath, string logPath)
{
    var reportPath = Required(options, "report");
    var buildProfile = options.TryGetValue("build-profile", out var profile) ? profile : "Production";
    var backend = options.TryGetValue("backend", out var configuredBackend)
        ? configuredBackend
        : ModelCompatibilityMatrix.CertifiedBackend;
    var tokenizerPolicy = options.TryGetValue("tokenizer-policy", out var configuredPolicy)
        ? configuredPolicy
        : "required";
    var tokenizerPath = options.TryGetValue("tokenizer-path", out var configuredTokenizerPath)
        ? configuredTokenizerPath
        : "";
    var allowUncertified = ReadBooleanOption(options, "allow-uncertified-model");
    var warningAccepted = ReadBooleanOption(options, "warning-accepted");

    if (buildProfile.Equals("Production", StringComparison.OrdinalIgnoreCase) &&
        (allowUncertified || warningAccepted))
    {
        throw new InvalidOperationException("Production model certification does not allow uncertified or warning-accepted overrides.");
    }

    var service = new ModelCertificationService();
    var report = service.Certify(
        modelPath,
        new ModelCertificationOptions(
            backend,
            buildProfile,
            tokenizerPolicy,
            tokenizerPath,
            allowUncertified,
            warningAccepted));

    ModelCertificationService.WriteReport(report, reportPath);
    Log(logPath, $"model certification report generated: {reportPath}");

    if (!report.AcceptedForPackaging)
    {
        var reasons = report.FailureReasons.Count == 0
            ? "model certification failed"
            : string.Join("; ", report.FailureReasons);
        throw new InvalidOperationException(reasons);
    }

    Console.WriteLine($"Model certification accepted: {report.CompatibilityStatus}");
    return 0;
}

static bool ReadBooleanOption(IReadOnlyDictionary<string, string> options, string key)
{
    return options.TryGetValue(key, out var value) &&
           bool.TryParse(value, out var parsed) &&
           parsed;
}

static ProvisioningConfig ValidateConfig(string path, string installDir)
{
    if (!File.Exists(path))
        throw new FileNotFoundException("Config file not found.", path);

    using var stream = File.OpenRead(path);
    using var document = JsonDocument.Parse(stream);
    var root = document.RootElement;

    var llmProvider = ReadRequiredString(root, "Llm", "Provider").Trim().ToLowerInvariant();
    var embeddingProvider = ReadRequiredString(root, "Embedding", "Provider").Trim().ToLowerInvariant();

    if (llmProvider is not ("llamasharp" or "ollama"))
        throw new InvalidOperationException($"Unsupported LLM provider: {llmProvider}");

    if (embeddingProvider is not ("onnx" or "ollama"))
        throw new InvalidOperationException($"Unsupported embedding provider: {embeddingProvider}");

    var strictMode = ReadRequiredBoolean(root, "Retrieval", "StrictMode");
    if (!strictMode)
        throw new InvalidOperationException("Retrieval:StrictMode must be true.");

    var config = new ProvisioningConfig(
        ConfigPath: path,
        LlmProvider: llmProvider,
        LlmModelPath: ExpandInstallDir(ReadOptionalString(root, "Llm", "ModelPath"), installDir),
        EmbeddingProvider: embeddingProvider,
        EmbeddingOnnxModelPath: ExpandInstallDir(ReadOptionalString(root, "Embedding", "OnnxModelPath"), installDir),
        OllamaUrl: ReadOptionalString(root, "Ollama", "Url"),
        OllamaModel: ReadOptionalString(root, "Ollama", "Model"),
        EmbeddingModel: ReadOptionalString(root, "Embedding", "Model"),
        ExpectedLlmHash: ReadOptionalString(root, "ModelIntegrity", "ExpectedLlmHash"),
        ExpectedEmbeddingHash: ReadOptionalString(root, "ModelIntegrity", "ExpectedEmbeddingHash"));

    if (config.LlmProvider == "llamasharp" && string.IsNullOrWhiteSpace(config.LlmModelPath))
        throw new InvalidOperationException("Llm:ModelPath is required for llamasharp.");

    if (config.EmbeddingProvider == "onnx" && string.IsNullOrWhiteSpace(config.EmbeddingOnnxModelPath))
        throw new InvalidOperationException("Embedding:OnnxModelPath is required for onnx.");

    return config;
}

static void ValidateManifest(string path, ProvisioningConfig config, string installDir, string mode, string certificationReportPath)
{
    if (!File.Exists(path))
        throw new FileNotFoundException("Model manifest not found.", path);

    using var stream = File.OpenRead(path);
    using var document = JsonDocument.Parse(stream);
    var root = document.RootElement;

    if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
        schemaVersion.ValueKind != JsonValueKind.Number ||
        schemaVersion.GetInt32() != 3)
    {
        throw new InvalidOperationException("Model manifest schemaVersion must be 3.");
    }

    var manifestMode = ReadRequiredPropertyString(root, "mode").Trim().ToLowerInvariant();
    if (!string.Equals(manifestMode, mode, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Model manifest mode mismatch. Expected {mode}, found {manifestMode}.");

    var packaging = ReadRequiredPropertyString(root, "packaging").Trim().ToLowerInvariant();
    if (packaging != "wix-burn")
        throw new InvalidOperationException($"Unsupported model manifest packaging value: {packaging}.");

    var buildProfile = ReadRequiredPropertyString(root, "buildProfile").Trim();

    if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        throw new InvalidOperationException("Model manifest must contain a models array.");

    var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var llmEntries = 0;
    var embeddingEntries = 0;
    var matchedLlm = false;
    var matchedEmbedding = false;

    foreach (var model in models.EnumerateArray())
    {
        if (model.TryGetProperty("sourcePath", out _))
            throw new InvalidOperationException("Model manifest must not contain sourcePath.");

        var targetPath = ExpandInstallDir(ReadRequiredPropertyString(model, "targetPath"), installDir);
        var sha256 = ReadRequiredPropertyString(model, "sha256");
        var type = ReadRequiredPropertyString(model, "type").Trim().ToLowerInvariant();
        var fileName = ReadRequiredPropertyString(model, "filename");
        var entryMode = ReadRequiredPropertyString(model, "mode").Trim().ToLowerInvariant();

        if (entryMode != mode)
            throw new InvalidOperationException($"Manifest model entry mode mismatch: {fileName}.");

        if (!model.TryGetProperty("required", out var requiredElement) ||
            requiredElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !requiredElement.GetBoolean())
        {
            throw new InvalidOperationException($"Manifest model entry must be required: {fileName}.");
        }

        if (!model.TryGetProperty("sizeBytes", out var sizeElement) ||
            sizeElement.ValueKind != JsonValueKind.Number ||
            sizeElement.GetInt64() <= 0)
        {
            throw new InvalidOperationException($"Manifest model entry has invalid size: {fileName}.");
        }

        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException($"Manifest model entry has invalid SHA-256: {fileName}.");

        var key = $"{type}|{fileName}|{targetPath}";
        if (!seenEntries.Add(key))
            throw new InvalidOperationException($"Duplicate model manifest entry: {fileName}.");

        if (type == "llm")
        {
            llmEntries++;
        }
        else if (type == "embedding")
        {
            embeddingEntries++;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported manifest model type: {type}.");
        }

        if (type == "llm" && !string.IsNullOrWhiteSpace(config.LlmModelPath) &&
            PathsEqual(targetPath, config.LlmModelPath))
        {
            if (!string.Equals(config.ExpectedLlmHash, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Manifest LLM hash does not match machine config.");

            ValidateModelFile(targetPath, ".gguf", "Manifest LLM", sha256, requireHash: true);
            ValidateModelCertification(model, targetPath, sha256, certificationReportPath, buildProfile);
            matchedLlm = true;
        }

        if (type == "embedding" && !string.IsNullOrWhiteSpace(config.EmbeddingOnnxModelPath) &&
            PathsEqual(targetPath, config.EmbeddingOnnxModelPath))
        {
            if (!string.Equals(config.ExpectedEmbeddingHash, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Manifest embedding hash does not match machine config.");

            ValidateModelFile(targetPath, ".onnx", "Manifest embedding", sha256, requireHash: true);
            matchedEmbedding = true;
        }
    }

    if (mode == "full")
    {
        if (llmEntries != 1 || !matchedLlm)
            throw new InvalidOperationException("Full model manifest must contain exactly one matching LLM entry.");

        if (embeddingEntries != 1 || !matchedEmbedding)
            throw new InvalidOperationException("Full model manifest must contain exactly one matching embedding entry.");
    }

    if (mode == "degraded")
    {
        if (llmEntries != 0)
            throw new InvalidOperationException("Degraded model manifest must not contain a local LLM entry.");

        if (embeddingEntries != 1 || !matchedEmbedding)
            throw new InvalidOperationException("Degraded model manifest must contain exactly one matching embedding entry.");
    }
}

static void ValidateModelCertification(
    JsonElement manifestEntry,
    string modelPath,
    string modelSha256,
    string certificationReportPath,
    string buildProfile)
{
    if (string.IsNullOrWhiteSpace(certificationReportPath))
        throw new InvalidOperationException("LLM certification report path is required.");

    if (!File.Exists(certificationReportPath))
        throw new FileNotFoundException("LLM certification report not found.", certificationReportPath);

    var reportHash = ComputeSha256(certificationReportPath);
    var expectedReportHash = ReadRequiredPropertyString(manifestEntry, "certificationReportHash");
    if (!string.Equals(reportHash, expectedReportHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("LLM certification report hash does not match manifest.");

    using var stream = File.OpenRead(certificationReportPath);
    using var document = JsonDocument.Parse(stream);
    var report = document.RootElement;

    if (!report.TryGetProperty("schemaVersion", out var reportSchema) ||
        reportSchema.ValueKind != JsonValueKind.Number ||
        reportSchema.GetInt32() != 1)
    {
        throw new InvalidOperationException("LLM certification report schemaVersion must be 1.");
    }

    if (!ReadRequiredPropertyString(report, "sha256").Equals(modelSha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("LLM certification report SHA-256 does not match manifest model hash.");

    if (!ReadRequiredPropertyString(report, "fileName").Equals(Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("LLM certification report filename does not match installed model.");

    var backend = ReadRequiredPropertyString(report, "backend");
    if (!backend.Equals(ModelCompatibilityMatrix.CertifiedBackend, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"LLM certification backend mismatch: {backend}.");

    if (!report.TryGetProperty("acceptedForPackaging", out var acceptedElement) ||
        acceptedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
        !acceptedElement.GetBoolean())
    {
        throw new InvalidOperationException("LLM certification report is not accepted for packaging.");
    }

    if (buildProfile.Equals("Production", StringComparison.OrdinalIgnoreCase))
    {
        if (!report.TryGetProperty("compatible", out var compatibleElement) ||
            compatibleElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !compatibleElement.GetBoolean())
        {
            throw new InvalidOperationException("Production LLM certification report must be compatible.");
        }
    }

    CompareManifestAndReport(manifestEntry, report, "architecture");
    CompareManifestAndReport(manifestEntry, report, "quantization");
    CompareManifestAndReport(manifestEntry, report, "ggufVersion");
    CompareManifestAndReport(manifestEntry, report, "certifiedBackend", reportProperty: "backend");
    CompareManifestAndReport(manifestEntry, report, "certifiedAtUtc", reportProperty: "generatedAtUtc");
    CompareManifestAndReport(manifestEntry, report, "compatibilityStatus");

    if (!report.TryGetProperty("tokenizer", out var tokenizer) || tokenizer.ValueKind != JsonValueKind.Object)
        throw new InvalidOperationException("LLM certification report tokenizer section is missing.");

    CompareManifestAndReport(manifestEntry, tokenizer, "tokenizerPolicy", reportProperty: "policy");

    if (!manifestEntry.TryGetProperty("warningAccepted", out var manifestWarning) ||
        manifestWarning.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
    {
        throw new InvalidOperationException("Manifest LLM certification warningAccepted is missing or invalid.");
    }

    if (!tokenizer.TryGetProperty("warningAccepted", out var reportWarning) ||
        reportWarning.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
        manifestWarning.GetBoolean() != reportWarning.GetBoolean())
    {
        throw new InvalidOperationException("Manifest LLM tokenizer warningAccepted does not match certification report.");
    }

    if (buildProfile.Equals("Production", StringComparison.OrdinalIgnoreCase))
    {
        var tokenizerPolicy = ReadRequiredPropertyString(tokenizer, "policy");
        if (!tokenizerPolicy.Equals("required", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production LLM certification must use required tokenizer policy.");

        if (!tokenizer.TryGetProperty("valid", out var tokenizerValid) ||
            tokenizerValid.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !tokenizerValid.GetBoolean())
        {
            throw new InvalidOperationException("Production LLM certification tokenizer policy is not satisfied.");
        }
    }
}

static void CompareManifestAndReport(
    JsonElement manifest,
    JsonElement report,
    string manifestProperty,
    string? reportProperty = null)
{
    reportProperty ??= manifestProperty;
    var manifestValue = ReadScalarAsString(manifest, manifestProperty);
    var reportValue = ReadScalarAsString(report, reportProperty);
    if (!string.Equals(manifestValue, reportValue, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Manifest LLM certification field '{manifestProperty}' does not match report field '{reportProperty}'.");
    }
}

static string ReadScalarAsString(JsonElement element, string property)
{
    if (!element.TryGetProperty(property, out var value))
        throw new InvalidOperationException($"{property} is missing or invalid.");

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new InvalidOperationException($"{property} is missing or invalid.")
    };
}

static void ValidateOllamaConfiguration(ProvisioningConfig config)
{
    if (!Uri.TryCreate(config.OllamaUrl, UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https"))
    {
        throw new InvalidOperationException("Ollama:Url must be a valid HTTP(S) URL.");
    }

    if (config.LlmProvider == "ollama" && string.IsNullOrWhiteSpace(config.OllamaModel))
        throw new InvalidOperationException("Ollama:Model is required for Ollama LLM.");

    if (config.EmbeddingProvider == "ollama" && string.IsNullOrWhiteSpace(config.EmbeddingModel))
        throw new InvalidOperationException("Embedding:Model is required for Ollama embeddings.");
}

static string ReadRequiredString(JsonElement root, string section, string key)
{
    if (!root.TryGetProperty(section, out var sectionElement))
        throw new InvalidOperationException($"{section} section is missing.");

    return ReadRequiredPropertyString(sectionElement, key, $"{section}:{key}");
}

static string ReadRequiredPropertyString(JsonElement element, string key, string? label = null)
{
    label ??= key;
    if (!element.TryGetProperty(key, out var valueElement) ||
        valueElement.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(valueElement.GetString()))
    {
        throw new InvalidOperationException($"{label} is missing or invalid.");
    }

    return valueElement.GetString()!;
}

static string ReadOptionalString(JsonElement root, string section, string key)
{
    if (!root.TryGetProperty(section, out var sectionElement) ||
        !sectionElement.TryGetProperty(key, out var valueElement) ||
        valueElement.ValueKind != JsonValueKind.String)
    {
        return "";
    }

    return valueElement.GetString() ?? "";
}

static bool ReadRequiredBoolean(JsonElement root, string section, string key)
{
    if (!root.TryGetProperty(section, out var sectionElement) ||
        !sectionElement.TryGetProperty(key, out var valueElement) ||
        valueElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
    {
        throw new InvalidOperationException($"{section}:{key} is missing or invalid.");
    }

    return valueElement.GetBoolean();
}

static void ValidateModelFile(string path, string extension, string label, string? expectedSha256, bool requireHash)
{
    if (string.IsNullOrWhiteSpace(path))
        throw new InvalidOperationException($"{label} path is empty.");

    if (!File.Exists(path))
        throw new FileNotFoundException($"{label} file not found.", path);

    if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{label} file must be {extension}.");

    var info = new FileInfo(path);
    if (info.Length <= 0)
        throw new InvalidOperationException($"{label} file is empty.");

    using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Span<byte> buffer = stackalloc byte[1];
        if (stream.Read(buffer) != 1)
            throw new InvalidOperationException($"{label} file is not readable.");
    }

    if (string.IsNullOrWhiteSpace(expectedSha256))
    {
        if (requireHash)
            throw new InvalidOperationException($"{label} expected SHA-256 is required.");

        return;
    }

    if (expectedSha256.Trim().Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c)))
        throw new InvalidOperationException($"{label} expected SHA-256 is invalid.");

    if (!string.IsNullOrWhiteSpace(expectedSha256))
    {
        var actual = ComputeSha256(path);
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{label} SHA-256 mismatch. Expected {expectedSha256}, actual {actual}.");
        }
    }
}

static string ComputeSha256(string path)
{
    using var sha256 = SHA256.Create();
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
}

static bool PathsEqual(string left, string right)
{
    return string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

static string ResolveLogPath(IReadOnlyDictionary<string, string> options)
{
    if (options.TryGetValue("log", out var configured) && !string.IsNullOrWhiteSpace(configured))
        return configured;

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(localAppData, "Poseidon", "Logs", "startup.log");
}

static string ResolveInstallDir(IReadOnlyDictionary<string, string> options)
{
    if (options.TryGetValue("install-dir", out var configured) && !string.IsNullOrWhiteSpace(configured))
        return Path.TrimEndingDirectorySeparator(configured) + Path.DirectorySeparatorChar;

    return AppContext.BaseDirectory;
}

static string ExpandInstallDir(string path, string installDir)
{
    if (string.IsNullOrWhiteSpace(path))
        return "";

    return path.Replace("[INSTALLDIR]", installDir, StringComparison.OrdinalIgnoreCase);
}

static void Log(string path, string message)
{
    try
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.AppendAllText(
            path,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
    }
    catch
    {
        // Do not mask validation failures with logging failures.
    }
}

static string RedactReference(string reference)
{
    return ProtectedSecretReference.TryParse(reference, out var parsed)
        ? $"{parsed.Provider}:{parsed.Scope}:{parsed.Name}:<version>"
        : "<invalid-reference>";
}

internal sealed record ProvisioningConfig(
    string ConfigPath,
    string LlmProvider,
    string LlmModelPath,
    string EmbeddingProvider,
    string EmbeddingOnnxModelPath,
    string OllamaUrl,
    string OllamaModel,
    string EmbeddingModel,
    string ExpectedLlmHash,
    string ExpectedEmbeddingHash);
