using System.Security.Cryptography;
using System.Text;

namespace Poseidon.ModelCertification;

public sealed class GgufMetadataInspector
{
    private const uint SupportedMagic = 0x46554747; // GGUF, little-endian.
    private const ulong MaximumMetadataEntries = 100_000;
    private const ulong MaximumTensorEntries = 5_000_000;

    public GgufInspectionResult Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("GGUF model path is required.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("GGUF model file was not found.", path);

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length <= 0)
            throw new InvalidDataException("GGUF model file is empty.");

        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: false);

        var magic = reader.ReadUInt32();
        if (magic != SupportedMagic)
            throw new InvalidDataException("Model file does not begin with GGUF magic bytes.");

        var version = reader.ReadUInt32();
        var tensorCount = reader.ReadUInt64();
        var metadataCount = reader.ReadUInt64();

        if (metadataCount > MaximumMetadataEntries)
            throw new InvalidDataException($"GGUF metadata count is implausibly large: {metadataCount}.");

        if (tensorCount > MaximumTensorEntries)
            throw new InvalidDataException($"GGUF tensor count is implausibly large: {tensorCount}.");

        var metadata = new List<GgufMetadataEntry>();
        for (ulong i = 0; i < metadataCount; i++)
        {
            var key = ReadGgufString(reader);
            var type = (GgufValueType)reader.ReadUInt32();
            var value = ReadMetadataValue(reader, type);
            metadata.Add(new GgufMetadataEntry(key, value.TypeName, value.Summary, value.ScalarValue, value.ArrayLength));
        }

        var tensorTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (ulong i = 0; i < tensorCount; i++)
        {
            _ = ReadGgufString(reader);
            var dimensions = reader.ReadUInt32();
            if (dimensions > 16)
                throw new InvalidDataException($"GGUF tensor has unsupported dimension count: {dimensions}.");

            for (var d = 0; d < dimensions; d++)
                _ = reader.ReadUInt64();

            var tensorType = reader.ReadUInt32();
            var typeName = GgmlTensorTypes.ToName(tensorType);
            tensorTypes[typeName] = tensorTypes.TryGetValue(typeName, out var count) ? count + 1 : 1;
            _ = reader.ReadUInt64();
        }

        var metadataMap = metadata.ToDictionary(entry => entry.Key, entry => entry, StringComparer.OrdinalIgnoreCase);
        var architecture = TryGetScalar(metadataMap, "general.architecture")?.Trim().ToLowerInvariant() ?? "unknown";
        var contextLength = TryGetLong(metadataMap, $"{architecture}.context_length") ??
                            TryGetLong(metadataMap, "llama.context_length");
        var quantization = GgmlTensorTypes.SelectDominantQuantization(tensorTypes);
        var tokenizerKeys = metadata
            .Where(entry => entry.Key.StartsWith("tokenizer.", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ropeKeys = metadata
            .Where(entry => entry.Key.Contains(".rope.", StringComparison.OrdinalIgnoreCase) ||
                            entry.Key.Contains("rope_", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GgufInspectionResult(
            FilePath: Path.GetFullPath(path),
            FileName: Path.GetFileName(path),
            FileSizeBytes: fileInfo.Length,
            Sha256: ComputeSha256(path),
            GgufVersion: version,
            TensorCount: tensorCount,
            MetadataCount: metadataCount,
            Architecture: architecture,
            Quantization: quantization,
            ContextLength: contextLength,
            Metadata: metadata,
            TensorTypeSummary: tensorTypes
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new TensorTypeSummary(pair.Key, pair.Value))
                .ToArray(),
            Tokenizer: new TokenizerInspectionResult(tokenizerKeys.Length > 0, tokenizerKeys, ropeKeys));
    }

    private static string? TryGetScalar(IReadOnlyDictionary<string, GgufMetadataEntry> metadata, string key)
    {
        return metadata.TryGetValue(key, out var entry) ? entry.ScalarValue : null;
    }

    private static long? TryGetLong(IReadOnlyDictionary<string, GgufMetadataEntry> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var entry) || string.IsNullOrWhiteSpace(entry.ScalarValue))
            return null;

        return long.TryParse(entry.ScalarValue, out var value) ? value : null;
    }

    private static GgufMetadataValue ReadMetadataValue(BinaryReader reader, GgufValueType type)
    {
        return type switch
        {
            GgufValueType.UInt8 => Scalar("uint8", reader.ReadByte()),
            GgufValueType.Int8 => Scalar("int8", reader.ReadSByte()),
            GgufValueType.UInt16 => Scalar("uint16", reader.ReadUInt16()),
            GgufValueType.Int16 => Scalar("int16", reader.ReadInt16()),
            GgufValueType.UInt32 => Scalar("uint32", reader.ReadUInt32()),
            GgufValueType.Int32 => Scalar("int32", reader.ReadInt32()),
            GgufValueType.Float32 => Scalar("float32", reader.ReadSingle()),
            GgufValueType.Bool => Scalar("bool", reader.ReadByte() != 0),
            GgufValueType.String => StringValue(ReadGgufString(reader)),
            GgufValueType.Array => ReadArray(reader),
            GgufValueType.UInt64 => Scalar("uint64", reader.ReadUInt64()),
            GgufValueType.Int64 => Scalar("int64", reader.ReadInt64()),
            GgufValueType.Float64 => Scalar("float64", reader.ReadDouble()),
            _ => throw new InvalidDataException($"Unsupported GGUF metadata value type: {(uint)type}.")
        };
    }

    private static GgufMetadataValue ReadArray(BinaryReader reader)
    {
        var elementType = (GgufValueType)reader.ReadUInt32();
        var count = reader.ReadUInt64();
        for (ulong i = 0; i < count; i++)
            SkipArrayElement(reader, elementType);

        var elementTypeName = GgufValueTypeNames.ToName(elementType);
        return new GgufMetadataValue(
            $"array<{elementTypeName}>",
            $"array<{elementTypeName}>[{count}]",
            ScalarValue: null,
            ArrayLength: count);
    }

    private static void SkipArrayElement(BinaryReader reader, GgufValueType type)
    {
        switch (type)
        {
            case GgufValueType.UInt8:
            case GgufValueType.Int8:
            case GgufValueType.Bool:
                reader.BaseStream.Seek(1, SeekOrigin.Current);
                break;
            case GgufValueType.UInt16:
            case GgufValueType.Int16:
                reader.BaseStream.Seek(2, SeekOrigin.Current);
                break;
            case GgufValueType.UInt32:
            case GgufValueType.Int32:
            case GgufValueType.Float32:
                reader.BaseStream.Seek(4, SeekOrigin.Current);
                break;
            case GgufValueType.UInt64:
            case GgufValueType.Int64:
            case GgufValueType.Float64:
                reader.BaseStream.Seek(8, SeekOrigin.Current);
                break;
            case GgufValueType.String:
                _ = ReadGgufString(reader);
                break;
            default:
                throw new InvalidDataException($"Unsupported GGUF array element type: {(uint)type}.");
        }
    }

    private static GgufMetadataValue Scalar(string typeName, object value)
    {
        var scalar = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return new GgufMetadataValue(typeName, scalar, scalar, ArrayLength: null);
    }

    private static GgufMetadataValue StringValue(string value)
    {
        return new GgufMetadataValue("string", value, value, ArrayLength: null);
    }

    private static string ReadGgufString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > int.MaxValue)
            throw new InvalidDataException($"GGUF string length is too large: {length}.");

        if (reader.BaseStream.Position + (long)length > reader.BaseStream.Length)
            throw new EndOfStreamException("GGUF string exceeds remaining file length.");

        var bytes = reader.ReadBytes((int)length);
        if (bytes.Length != (int)length)
            throw new EndOfStreamException("Unexpected end of GGUF string.");

        return Encoding.UTF8.GetString(bytes);
    }

    private static string ComputeSha256(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }
}

internal enum GgufValueType : uint
{
    UInt8 = 0,
    Int8 = 1,
    UInt16 = 2,
    Int16 = 3,
    UInt32 = 4,
    Int32 = 5,
    Float32 = 6,
    Bool = 7,
    String = 8,
    Array = 9,
    UInt64 = 10,
    Int64 = 11,
    Float64 = 12
}

internal static class GgufValueTypeNames
{
    public static string ToName(GgufValueType type)
    {
        return type switch
        {
            GgufValueType.UInt8 => "uint8",
            GgufValueType.Int8 => "int8",
            GgufValueType.UInt16 => "uint16",
            GgufValueType.Int16 => "int16",
            GgufValueType.UInt32 => "uint32",
            GgufValueType.Int32 => "int32",
            GgufValueType.Float32 => "float32",
            GgufValueType.Bool => "bool",
            GgufValueType.String => "string",
            GgufValueType.Array => "array",
            GgufValueType.UInt64 => "uint64",
            GgufValueType.Int64 => "int64",
            GgufValueType.Float64 => "float64",
            _ => $"unknown-{(uint)type}"
        };
    }
}

internal static class GgmlTensorTypes
{
    private static readonly IReadOnlyDictionary<uint, string> Names = new Dictionary<uint, string>
    {
        [0] = "F32",
        [1] = "F16",
        [2] = "Q4_0",
        [3] = "Q4_1",
        [6] = "Q5_0",
        [7] = "Q5_1",
        [8] = "Q8_0",
        [10] = "Q2_K",
        [11] = "Q3_K",
        [12] = "Q4_K",
        [13] = "Q5_K",
        [14] = "Q6_K",
        [15] = "Q8_K",
        [16] = "IQ2_XXS",
        [17] = "IQ2_XS",
        [18] = "IQ3_XXS",
        [19] = "IQ1_S",
        [20] = "IQ4_NL",
        [21] = "IQ3_S",
        [22] = "IQ2_S",
        [23] = "IQ4_XS",
        [24] = "I8",
        [25] = "I16",
        [26] = "I32",
        [27] = "I64",
        [28] = "F64",
        [29] = "IQ1_M",
        [30] = "BF16"
    };

    public static string ToName(uint value)
    {
        return Names.TryGetValue(value, out var name) ? name : $"UNKNOWN_{value}";
    }

    public static string SelectDominantQuantization(IReadOnlyDictionary<string, int> tensorTypes)
    {
        var quantized = tensorTypes
            .Where(pair => pair.Key.StartsWith("Q", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.StartsWith("IQ", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(quantized.Key) ? "unknown" : quantized.Key;
    }
}

internal sealed record GgufMetadataValue(string TypeName, string Summary, string? ScalarValue, ulong? ArrayLength);
