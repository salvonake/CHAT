using System.Text;
using System.IO;
using FluentAssertions;
using Poseidon.ModelCertification;

namespace Poseidon.UnitTests.ModelCertification;

public sealed class ModelCertificationServiceTests
{
    [Fact]
    public void InspectorReadsGgufMetadataWithoutRuntimeDependencies()
    {
        var path = CreateMinimalGguf("llama", tensorType: 2);
        try
        {
            var result = new GgufMetadataInspector().Inspect(path);

            result.GgufVersion.Should().Be(3);
            result.Architecture.Should().Be("llama");
            result.Quantization.Should().Be("Q4_0");
            result.TensorCount.Should().Be(1);
            result.Tokenizer.MetadataPresent.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CertificationAcceptsValidatedLlamaQ4ZeroWithTokenizerWarningAccepted()
    {
        var path = CreateMinimalGguf("llama", tensorType: 2);
        try
        {
            var report = new ModelCertificationService().Certify(
                path,
                new ModelCertificationOptions(
                    ModelCompatibilityMatrix.CertifiedBackend,
                    "NonProduction",
                    "warning",
                    TokenizerPath: Path.Combine(Path.GetTempPath(), "missing-vocab.txt"),
                    AllowUncertifiedModel: false,
                    WarningAccepted: true));

            report.Compatible.Should().BeTrue();
            report.AcceptedForPackaging.Should().BeTrue();
            report.CompatibilityStatus.Should().Be("compatible-with-warnings");
            report.Tokenizer.WarningAccepted.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CertificationRejectsBlockedQwenArchitecture()
    {
        var path = CreateMinimalGguf("qwen2", tensorType: 2);
        try
        {
            var report = new ModelCertificationService().Certify(
                path,
                new ModelCertificationOptions(
                    ModelCompatibilityMatrix.CertifiedBackend,
                    "Production",
                    "not-required",
                    TokenizerPath: null,
                    AllowUncertifiedModel: false,
                    WarningAccepted: false));

            report.Compatible.Should().BeFalse();
            report.AcceptedForPackaging.Should().BeFalse();
            report.FailureReasons.Should().Contain(reason => reason.Contains("explicitly blocked"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CertificationRejectsMissingTokenizerWhenPolicyIsRequired()
    {
        var path = CreateMinimalGguf("llama", tensorType: 2);
        try
        {
            var report = new ModelCertificationService().Certify(
                path,
                new ModelCertificationOptions(
                    ModelCompatibilityMatrix.CertifiedBackend,
                    "Production",
                    "required",
                    TokenizerPath: Path.Combine(Path.GetTempPath(), "missing-vocab.txt"),
                    AllowUncertifiedModel: false,
                    WarningAccepted: false));

            report.Compatible.Should().BeFalse();
            report.AcceptedForPackaging.Should().BeFalse();
            report.FailureReasons.Should().Contain("Required tokenizer asset missing: vocab.txt.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateMinimalGguf(string architecture, uint tensorType)
    {
        var path = Path.Combine(Path.GetTempPath(), $"poseidon-test-{Guid.NewGuid():N}.gguf");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes("GGUF"));
        writer.Write((uint)3);
        writer.Write((ulong)1);
        writer.Write((ulong)4);

        WriteStringMetadata(writer, "general.architecture", architecture);
        WriteUInt32Metadata(writer, $"{architecture}.context_length", 2048);
        WriteStringMetadata(writer, "tokenizer.ggml.model", "llama");
        WriteFloat32Metadata(writer, $"{architecture}.rope.freq_base", 10000f);

        WriteGgufString(writer, "blk.0.attn_q.weight");
        writer.Write((uint)2);
        writer.Write((ulong)32);
        writer.Write((ulong)32);
        writer.Write(tensorType);
        writer.Write((ulong)0);

        return path;
    }

    private static void WriteStringMetadata(BinaryWriter writer, string key, string value)
    {
        WriteGgufString(writer, key);
        writer.Write((uint)8);
        WriteGgufString(writer, value);
    }

    private static void WriteUInt32Metadata(BinaryWriter writer, string key, uint value)
    {
        WriteGgufString(writer, key);
        writer.Write((uint)4);
        writer.Write(value);
    }

    private static void WriteFloat32Metadata(BinaryWriter writer, string key, float value)
    {
        WriteGgufString(writer, key);
        writer.Write((uint)6);
        writer.Write(value);
    }

    private static void WriteGgufString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
