using System.Security.Cryptography;

var options = ParseArgs(args);
var sourceDirectory = Required(options, "source");
var fileName = Required(options, "file");
var expectedSha256 = Required(options, "sha256").Trim().ToLowerInvariant();
var targetDirectory = options.TryGetValue("target", out var configuredTarget) && !string.IsNullOrWhiteSpace(configuredTarget)
    ? configuredTarget
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Poseidon", "Models");

try
{
    if (expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c)))
        throw new InvalidOperationException("--sha256 must be a 64-character SHA-256 hash.");

    var sourcePath = Path.Combine(sourceDirectory, fileName);
    if (!File.Exists(sourcePath))
        throw new FileNotFoundException("External LLM payload was not found.", sourcePath);

    Directory.CreateDirectory(targetDirectory);
    var targetPath = Path.Combine(targetDirectory, fileName);

    if (File.Exists(targetPath))
    {
        var existingHash = ComputeSha256(targetPath);
        if (existingHash == expectedSha256)
        {
            Console.WriteLine("External LLM payload already installed.");
            return 0;
        }
    }

    var sourceHash = ComputeSha256(sourcePath);
    if (sourceHash != expectedSha256)
        throw new InvalidOperationException($"External LLM payload hash mismatch. Expected {expectedSha256}, actual {sourceHash}.");

    var tempPath = targetPath + ".installing";
    File.Copy(sourcePath, tempPath, overwrite: true);

    var copiedHash = ComputeSha256(tempPath);
    if (copiedHash != expectedSha256)
        throw new InvalidOperationException($"Copied LLM payload hash mismatch. Expected {expectedSha256}, actual {copiedHash}.");

    if (File.Exists(targetPath))
        File.Delete(targetPath);

    File.Move(tempPath, targetPath);
    Console.WriteLine("External LLM payload installed.");
    return 0;
}
catch (Exception ex)
{
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

static string ComputeSha256(string path)
{
    using var sha256 = SHA256.Create();
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
}
