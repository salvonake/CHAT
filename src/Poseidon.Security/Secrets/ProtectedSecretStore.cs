using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Poseidon.Security.Secrets;

public enum ProtectedSecretScope
{
    CurrentUser,
    LocalMachine
}

public sealed record ProtectedSecretReference(
    string Provider,
    ProtectedSecretScope Scope,
    string Name,
    string Version)
{
    public bool IsDpapi => string.Equals(Provider, "dpapi", StringComparison.OrdinalIgnoreCase);

    public override string ToString()
        => $"{Provider}:{Scope}:{Name}:{Version}";

    public static bool TryParse(string? value, out ProtectedSecretReference reference)
    {
        reference = new ProtectedSecretReference("", ProtectedSecretScope.CurrentUser, "", "");
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split(':', 4, StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
            return false;

        if (!parts[0].Equals("dpapi", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Enum.TryParse<ProtectedSecretScope>(parts[1], ignoreCase: true, out var scope))
            return false;

        if (string.IsNullOrWhiteSpace(parts[2]) || string.IsNullOrWhiteSpace(parts[3]))
            return false;

        reference = new ProtectedSecretReference("dpapi", scope, parts[2], parts[3]);
        return true;
    }
}

public enum ProtectedSecretLoadStatus
{
    Loaded,
    MissingReference,
    InvalidReference,
    MissingSecret,
    CorruptSecret,
    UnsupportedProvider,
    UnsupportedPlatform
}

public sealed record ProtectedSecretLoadResult(
    ProtectedSecretLoadStatus Status,
    string? Value = null,
    string? Reference = null,
    string? Message = null)
{
    public bool Succeeded => Status == ProtectedSecretLoadStatus.Loaded && !string.IsNullOrEmpty(Value);
}

public static class ProtectedSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ProtectedSecretLoadResult Load(string? referenceValue)
    {
        if (string.IsNullOrWhiteSpace(referenceValue))
            return new ProtectedSecretLoadResult(ProtectedSecretLoadStatus.MissingReference);

        if (!ProtectedSecretReference.TryParse(referenceValue, out var reference))
        {
            return new ProtectedSecretLoadResult(
                ProtectedSecretLoadStatus.InvalidReference,
                Reference: referenceValue,
                Message: "Secret reference must use dpapi:<scope>:<name>:<version>.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new ProtectedSecretLoadResult(
                ProtectedSecretLoadStatus.UnsupportedPlatform,
                Reference: referenceValue,
                Message: "DPAPI secret references require Windows.");
        }

        if (!reference.IsDpapi)
        {
            return new ProtectedSecretLoadResult(
                ProtectedSecretLoadStatus.UnsupportedProvider,
                Reference: reference.ToString());
        }

        var path = ResolveSecretPath(reference);
        if (!File.Exists(path))
        {
            return new ProtectedSecretLoadResult(
                ProtectedSecretLoadStatus.MissingSecret,
                Reference: reference.ToString(),
                Message: $"Protected secret not found: {reference.Name}:{reference.Version}");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ProtectedSecretEnvelope>(File.ReadAllText(path));
            if (envelope is null ||
                envelope.SchemaVersion != 1 ||
                !string.Equals(envelope.Provider, "dpapi", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(envelope.Name, reference.Name, StringComparison.Ordinal) ||
                !string.Equals(envelope.Version, reference.Version, StringComparison.Ordinal) ||
                !Enum.TryParse<ProtectedSecretScope>(envelope.Scope, ignoreCase: true, out var storedScope) ||
                storedScope != reference.Scope ||
                string.IsNullOrWhiteSpace(envelope.ProtectedValue))
            {
                return new ProtectedSecretLoadResult(
                    ProtectedSecretLoadStatus.CorruptSecret,
                    Reference: reference.ToString(),
                    Message: "Protected secret metadata is invalid.");
            }

            var protectedBytes = Convert.FromBase64String(envelope.ProtectedValue);
            var plaintext = ProtectedData.Unprotect(
                protectedBytes,
                BuildEntropy(reference),
                ToDataProtectionScope(reference.Scope));

            return new ProtectedSecretLoadResult(
                ProtectedSecretLoadStatus.Loaded,
                Encoding.UTF8.GetString(plaintext),
                reference.ToString());
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or IOException)
        {
            return new ProtectedSecretLoadResult(
                ProtectedSecretLoadStatus.CorruptSecret,
                Reference: reference.ToString(),
                Message: ex.Message);
        }
    }

    public static void Save(ProtectedSecretReference reference, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Secret cannot be empty.", nameof(secret));

        if (!reference.IsDpapi)
            throw new InvalidOperationException("Only DPAPI secret references are supported.");

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI secret storage requires Windows.");

        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                bytes,
                BuildEntropy(reference),
                ToDataProtectionScope(reference.Scope));

            var envelope = new ProtectedSecretEnvelope
            {
                SchemaVersion = 1,
                Provider = "dpapi",
                Scope = reference.Scope.ToString(),
                Name = reference.Name,
                Version = reference.Version,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ProtectedValue = Convert.ToBase64String(protectedBytes)
            };

            var path = ResolveSecretPath(reference);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(envelope, JsonOptions), Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string CreateReference(
        string name,
        string version,
        ProtectedSecretScope scope = ProtectedSecretScope.LocalMachine)
    {
        return new ProtectedSecretReference("dpapi", scope, name, version).ToString();
    }

    private static string ResolveSecretPath(ProtectedSecretReference reference)
    {
        var root = reference.Scope == ProtectedSecretScope.LocalMachine
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Poseidon", "Secrets")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Poseidon", "Secrets");

        var material = $"{reference.Provider}|{reference.Scope}|{reference.Name}|{reference.Version}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return Path.Combine(root, hash + ".secret.json");
    }

    private static byte[] BuildEntropy(ProtectedSecretReference reference)
    {
        return Encoding.UTF8.GetBytes($"Poseidon-LCDSS-DPAPI-v1|{reference.Scope}|{reference.Name}|{reference.Version}");
    }

    [SupportedOSPlatform("windows")]
    private static DataProtectionScope ToDataProtectionScope(ProtectedSecretScope scope)
        => scope == ProtectedSecretScope.LocalMachine
            ? DataProtectionScope.LocalMachine
            : DataProtectionScope.CurrentUser;

    private sealed class ProtectedSecretEnvelope
    {
        public int SchemaVersion { get; init; }
        public string Provider { get; init; } = "";
        public string Scope { get; init; } = "";
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public DateTimeOffset CreatedAtUtc { get; init; }
        public string ProtectedValue { get; init; } = "";
    }
}
