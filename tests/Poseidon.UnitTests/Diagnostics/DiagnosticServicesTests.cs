using FluentAssertions;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Poseidon.Desktop;
using Poseidon.Desktop.Diagnostics;
using Poseidon.Domain.Interfaces;
using Serilog;
using Serilog.Events;

namespace Poseidon.UnitTests.Diagnostics;

public sealed class DiagnosticServicesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "poseidon-diagnostics-tests", Guid.NewGuid().ToString("N"));

    public DiagnosticServicesTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void StartupModeController_ClassifiesFullDegradedRecovery()
    {
        var controller = new StartupModeController();

        controller.Evaluate([
            new HealthCheckResult("LLM", HealthStatus.OK, "ok", null)
        ]).Mode.Should().Be(StartupMode.Full);

        controller.Evaluate([
            new HealthCheckResult("Encryption", HealthStatus.Warning, "disabled", FixAction.EnableEncryption)
        ]).Mode.Should().Be(StartupMode.Degraded);

        var recovery = controller.Evaluate([
            new HealthCheckResult("LLM", HealthStatus.Error, "missing", FixAction.EditLlmPath)
        ]);
        recovery.Mode.Should().Be(StartupMode.Recovery);
        recovery.CanAskQuestions.Should().BeFalse();
    }

    [Fact]
    public async Task SystemHealthService_MissingModels_ProducesRecoveryWithFixActions()
    {
        var health = CreateHealthService(CreateConfig(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "llamasharp",
            ["Embedding:Provider"] = "onnx",
            ["Retrieval:StrictMode"] = "true",
            ["Security:EncryptionEnabled"] = "true"
        }));

        var snapshot = await health.CheckAllAsync();

        snapshot.Mode.Should().Be(StartupMode.Recovery);
        snapshot.CanAskQuestions.Should().BeFalse();
        snapshot.Results.Select(r => r.Component).Should().Contain(["LLM", "Embeddings", "Vector DB", "Configuration", "Encryption"]);
        snapshot.Results
            .Where(r => r.Status == HealthStatus.Error)
            .Should()
            .OnlyContain(r => r.FixAction != null);
    }

    [Fact]
    public async Task RuntimeConfig_FixModelPaths_RecoversWithoutRestart()
    {
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        await File.WriteAllTextAsync(configPath, "{}");

        var config = new ConfigurationBuilder()
            .SetBasePath(_tempDir)
            .AddJsonFile("appsettings.user.json", optional: false, reloadOnChange: true)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "llamasharp",
                ["Embedding:Provider"] = "onnx",
                ["Retrieval:StrictMode"] = "true",
                ["Security:EncryptionEnabled"] = "true"
            })
            .Build();

        var paths = CreatePaths(configPath);
        var health = CreateHealthService(config, paths);
        var runtime = new RuntimeConfigurationService(
            config,
            paths,
            [],
            NullLogger<RuntimeConfigurationService>.Instance);

        (await health.CheckAllAsync()).Mode.Should().Be(StartupMode.Recovery);

        var llmPath = Path.Combine(_tempDir, "model.gguf");
        var embeddingPath = Path.Combine(_tempDir, "embedding.onnx");
        await File.WriteAllTextAsync(llmPath, "test-model");
        await File.WriteAllTextAsync(embeddingPath, "test-embedding");

        await runtime.SaveSettingAsync("Llm", "ModelPath", llmPath);
        await runtime.SaveSettingAsync("Embedding", "OnnxModelPath", embeddingPath);

        var recovered = await health.CheckAllAsync();
        recovered.Mode.Should().Be(StartupMode.Full);
        recovered.CanAskQuestions.Should().BeTrue();
    }

    [Fact]
    public async Task RuntimeConfig_ReloadAsync_RefreshesReloadableServicesBeforeHostedStartup()
    {
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        await File.WriteAllTextAsync(configPath, "{}");
        var config = CreateConfig(new Dictionary<string, string?>(), configPath);
        var events = new List<string>();
        var reloadable = new RecordingReloadableService(events);
        var runtime = new RuntimeConfigurationService(
            config,
            CreatePaths(configPath),
            [reloadable],
            NullLogger<RuntimeConfigurationService>.Instance);

        var reloaded = await runtime.ReloadAsync("First-launch provisioning completed");
        events.Add("hosted-services-start");

        reloaded.Should().BeTrue();
        events.Should().Equal("runtime-reload", "hosted-services-start");
    }

    [Fact]
    public async Task RuntimeConfig_ReloadFailure_PreventsHostedStartupMarker()
    {
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        await File.WriteAllTextAsync(configPath, "{}");
        var config = CreateConfig(new Dictionary<string, string?>(), configPath);
        var runtime = new RuntimeConfigurationService(
            config,
            CreatePaths(configPath),
            [new ThrowingReloadableService()],
            NullLogger<RuntimeConfigurationService>.Instance);
        var events = new List<string>();

        var reloaded = await runtime.ReloadAsync("First-launch provisioning completed");
        if (reloaded)
            events.Add("hosted-services-start");

        reloaded.Should().BeFalse();
        events.Should().BeEmpty("hosted services must not start when the authoritative runtime reload fails");
    }

    [Fact]
    public async Task SystemHealthService_OllamaModelsAvailable_IsFull()
    {
        var server = await StartOllamaTagsServerAsync("""
        {"models":[{"name":"qwen2.5:14b"},{"name":"nomic-embed-text"}]}
        """, requestCount: 2);

        var health = CreateHealthService(CreateConfig(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "ollama",
            ["Embedding:Provider"] = "ollama",
            ["Ollama:Url"] = server.Url,
            ["Ollama:Model"] = "qwen2.5:14b",
            ["Embedding:Model"] = "nomic-embed-text",
            ["Retrieval:StrictMode"] = "true",
            ["Security:EncryptionEnabled"] = "true"
        }));

        var snapshot = await health.CheckAllAsync();

        snapshot.Mode.Should().Be(StartupMode.Full);
        snapshot.CanAskQuestions.Should().BeTrue();
        await server.Completion;
    }

    [Fact]
    public async Task SystemHealthService_OllamaMissingModel_IsRecovery()
    {
        var server = await StartOllamaTagsServerAsync("""
        {"models":[{"name":"qwen2.5:14b"}]}
        """, requestCount: 2);

        var health = CreateHealthService(CreateConfig(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "ollama",
            ["Embedding:Provider"] = "ollama",
            ["Ollama:Url"] = server.Url,
            ["Ollama:Model"] = "qwen2.5:14b",
            ["Embedding:Model"] = "nomic-embed-text",
            ["Retrieval:StrictMode"] = "true",
            ["Security:EncryptionEnabled"] = "true"
        }));

        var snapshot = await health.CheckAllAsync();

        snapshot.Mode.Should().Be(StartupMode.Recovery);
        snapshot.CanAskQuestions.Should().BeFalse();
        snapshot.Results.Should().Contain(r => r.Component == "Embeddings" && r.Status == HealthStatus.Error);
        await server.Completion;
    }

    [Fact]
    public async Task SystemHealthService_MixedOllamaLlmAndLocalEmbedding_IsFullWhenBothSatisfied()
    {
        var embeddingPath = Path.Combine(_tempDir, "arabert.onnx");
        await File.WriteAllTextAsync(embeddingPath, "embedding");
        var server = await StartOllamaTagsServerAsync("""
        {"models":[{"name":"qwen2.5:14b"}]}
        """, requestCount: 1);

        var health = CreateHealthService(CreateConfig(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "ollama",
            ["Embedding:Provider"] = "onnx",
            ["Embedding:OnnxModelPath"] = embeddingPath,
            ["Ollama:Url"] = server.Url,
            ["Ollama:Model"] = "qwen2.5:14b",
            ["Retrieval:StrictMode"] = "true",
            ["Security:EncryptionEnabled"] = "true"
        }));

        var snapshot = await health.CheckAllAsync();

        snapshot.Mode.Should().Be(StartupMode.Full);
        await server.Completion;
    }

    [Fact]
    public async Task SystemHealthService_MixedLocalLlmAndOllamaEmbedding_IsFullWhenBothSatisfied()
    {
        var llmPath = Path.Combine(_tempDir, "qwen2.5-14b.Q5_K_M.gguf");
        await File.WriteAllTextAsync(llmPath, "llm");
        var server = await StartOllamaTagsServerAsync("""
        {"models":[{"name":"nomic-embed-text"}]}
        """, requestCount: 1);

        var health = CreateHealthService(CreateConfig(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "llamasharp",
            ["Llm:ModelPath"] = llmPath,
            ["Embedding:Provider"] = "ollama",
            ["Ollama:Url"] = server.Url,
            ["Embedding:Model"] = "nomic-embed-text",
            ["Retrieval:StrictMode"] = "true",
            ["Security:EncryptionEnabled"] = "true"
        }));

        var snapshot = await health.CheckAllAsync();

        snapshot.Mode.Should().Be(StartupMode.Full);
        await server.Completion;
    }

    [Fact]
    public async Task SystemHealthService_MixedProviderMissingLocalRequirement_IsRecovery()
    {
        var server = await StartOllamaTagsServerAsync("""
        {"models":[{"name":"qwen2.5:14b"}]}
        """, requestCount: 1);

        var health = CreateHealthService(CreateConfig(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "ollama",
            ["Embedding:Provider"] = "onnx",
            ["Ollama:Url"] = server.Url,
            ["Ollama:Model"] = "qwen2.5:14b",
            ["Retrieval:StrictMode"] = "true",
            ["Security:EncryptionEnabled"] = "true"
        }));

        var snapshot = await health.CheckAllAsync();

        snapshot.Mode.Should().Be(StartupMode.Recovery);
        snapshot.Results.Should().Contain(r => r.Component == "Embeddings" && r.Status == HealthStatus.Error);
        await server.Completion;
    }

    [Fact]
    public async Task RuntimeConfig_ProtectedSecret_DoesNotWritePlaintext()
    {
        var secret = "super-secret-passphrase";
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        var config = CreateConfig(new Dictionary<string, string?>(), configPath);
        var runtime = new RuntimeConfigurationService(
            config,
            CreatePaths(configPath),
            [],
            NullLogger<RuntimeConfigurationService>.Instance);

        await runtime.SaveEncryptionSecretAsync(secret);

        var json = await File.ReadAllTextAsync(configPath);
        json.Should().NotContain(secret);
        json.Should().Contain("ProtectedPassphrase");
        RuntimeSecretProtector.TryUnprotect(
            config["Security:ProtectedPassphrase"]).Should().Be(secret);
    }

    [Fact]
    public void LiveLogBuffer_FiltersByLevelAndSearch()
    {
        var buffer = new LiveLogBuffer();
        buffer.Add(new LiveLogEntry(DateTimeOffset.Now, LogEventLevel.Information, "LLM", "ready", null));
        buffer.Add(new LiveLogEntry(DateTimeOffset.Now, LogEventLevel.Warning, "Vector DB", "slow", null));
        buffer.Add(new LiveLogEntry(DateTimeOffset.Now, LogEventLevel.Error, "LLM", "missing model", null));

        buffer.Query(LogEventLevel.Warning).Should().HaveCount(2);
        buffer.Query(LogEventLevel.Information, "model").Should().ContainSingle(e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public void LiveLogBuffer_CollapsesDuplicatesAndDropsOldest()
    {
        var buffer = new LiveLogBuffer(capacity: 100);

        buffer.Add(new LiveLogEntry(DateTimeOffset.Now, LogEventLevel.Error, "LLM", "missing model", null));
        buffer.Add(new LiveLogEntry(DateTimeOffset.Now, LogEventLevel.Error, "LLM", "missing model", null));
        for (var i = 0; i < 100; i++)
            buffer.Add(new LiveLogEntry(DateTimeOffset.Now, LogEventLevel.Information, "Config", $"saved {i}", null));

        buffer.Snapshot.Should().HaveCount(100);
        buffer.Query(LogEventLevel.Error).Should().BeEmpty();
        buffer.Query(LogEventLevel.Information, "saved 99").Should().ContainSingle();
    }

    [Fact]
    public async Task RuntimeConfig_RollbacksUserConfigWhenReloadFails()
    {
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        await File.WriteAllTextAsync(configPath, """
        {
          "Llm": {
            "ModelPath": "old.gguf"
          }
        }
        """);

        var config = CreateConfig(new Dictionary<string, string?>(), configPath);
        var runtime = new RuntimeConfigurationService(
            config,
            CreatePaths(configPath),
            [new ThrowingReloadableService()],
            NullLogger<RuntimeConfigurationService>.Instance);

        var act = () => runtime.SaveSettingAsync("Llm", "ModelPath", "new.gguf");

        await act.Should().ThrowAsync<InvalidOperationException>();
        var json = await File.ReadAllTextAsync(configPath);
        json.Should().Contain("old.gguf");
        json.Should().NotContain("new.gguf");
        runtime.LastReloadResult?.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Serilog_FileSinks_WriteAppAndStartupLogs()
    {
        var appLog = Path.Combine(_tempDir, "app.log");
        var startupLog = Path.Combine(_tempDir, "startup.log");
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(appLog)
            .WriteTo.File(startupLog)
            .CreateLogger();

        logger.Information("diagnostic log test");
        await logger.DisposeAsync();

        File.Exists(appLog).Should().BeTrue();
        File.Exists(startupLog).Should().BeTrue();
        (await File.ReadAllTextAsync(appLog)).Should().Contain("diagnostic log test");
        (await File.ReadAllTextAsync(startupLog)).Should().Contain("diagnostic log test");
    }

    private SystemHealthService CreateHealthService(IConfiguration configuration, DataPaths? paths = null)
    {
        paths ??= CreatePaths(Path.Combine(_tempDir, "appsettings.user.json"));
        return new SystemHealthService(
            configuration,
            paths,
            new FakeLlmService(),
            new FakeEmbeddingService(),
            new FakeVectorStore(),
            new FakeEncryptionService(true),
            new StartupModeController(),
            NullLogger<SystemHealthService>.Instance);
    }

    private IConfigurationRoot CreateConfig(
        IReadOnlyDictionary<string, string?> values,
        string? userConfigPath = null)
    {
        if (userConfigPath is not null && !File.Exists(userConfigPath))
            File.WriteAllText(userConfigPath, "{}");

        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(values);

        if (userConfigPath is not null)
        {
            builder.SetBasePath(Path.GetDirectoryName(userConfigPath)!);
            builder.AddJsonFile(Path.GetFileName(userConfigPath), optional: false, reloadOnChange: true);
        }

        return builder.Build();
    }

    private DataPaths CreatePaths(string userConfigPath)
    {
        return new DataPaths
        {
            DataDirectory = _tempDir,
            ModelsDirectory = _tempDir,
            VectorDbPath = Path.Combine(_tempDir, "vectors.db"),
            HnswIndexPath = Path.Combine(_tempDir, "vectors.hnsw"),
            DocumentDbPath = Path.Combine(_tempDir, "documents.db"),
            AuditDbPath = Path.Combine(_tempDir, "audit.db"),
            WatchDirectory = Path.Combine(_tempDir, "watch"),
            UserConfigPath = userConfigPath,
            LogsDirectory = _tempDir,
            AppLogPath = Path.Combine(_tempDir, "app.log"),
            StartupLogPath = Path.Combine(_tempDir, "startup.log")
        };
    }

    private static async Task<OllamaStubServer> StartOllamaTagsServerAsync(string body, int requestCount)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var completion = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < requestCount; i++)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    var buffer = new byte[1024];
                    _ = await stream.ReadAsync(buffer);

                    var payload = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json\r\n" +
                        $"Content-Length: {payload.Length}\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(payload);
                }
            }
            finally
            {
                listener.Stop();
            }
        });

        await Task.Yield();
        return new OllamaStubServer($"http://127.0.0.1:{port}", completion);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class FakeLlmService : ILlmService
    {
        public Task<LlmResponse> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
            => Task.FromResult(new LlmResponse { Content = "ok", Success = true });
        public async IAsyncEnumerable<string> StreamGenerateAsync(string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return "ok";
        }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public int EmbeddingDimension => 768;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new float[EmbeddingDimension]);
        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult(texts.Select(_ => new float[EmbeddingDimension]).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<Poseidon.Domain.Entities.DocumentChunk> chunks, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Poseidon.Domain.Entities.RetrievedChunk>> SearchAsync(float[] queryEmbedding, int topK, double scoreThreshold = 0, string? caseNamespace = null, CancellationToken ct = default) => Task.FromResult(new List<Poseidon.Domain.Entities.RetrievedChunk>());
        public Task<List<Poseidon.Domain.Entities.RetrievedChunk>> SearchAsync(float[] queryEmbedding, int topK, double scoreThreshold, string? caseNamespace, CancellationToken ct, string? domainId = null, string? datasetScope = null) => Task.FromResult(new List<Poseidon.Domain.Entities.RetrievedChunk>());
        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsByHashAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> GetVectorCountAsync(CancellationToken ct = default) => Task.FromResult(10L);
        public Task<VectorStoreHealth> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new VectorStoreHealth { IsHealthy = true, VectorCount = 10, IndexedSegments = 10, Status = "OK" });
    }

    private sealed class FakeEncryptionService : IEncryptionService
    {
        public FakeEncryptionService(bool enabled) => IsEnabled = enabled;
        public bool IsEnabled { get; }
        public byte[] Encrypt(byte[] plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => plaintext;
        public byte[] Decrypt(byte[] ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => ciphertext;
        public string EncryptString(string plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => plaintext;
        public string DecryptString(string ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => ciphertext;
        public byte[] ComputeHmac(byte[] data) => [];
        public bool VerifyHmac(byte[] data, byte[] hmac) => true;
    }

    private sealed class ThrowingReloadableService : IReloadableRuntimeService
    {
        public Task ReloadAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("reload failed");
    }

    private sealed class RecordingReloadableService : IReloadableRuntimeService
    {
        private readonly List<string> _events;

        public RecordingReloadableService(List<string> events)
        {
            _events = events;
        }

        public Task ReloadAsync(CancellationToken ct = default)
        {
            _events.Add("runtime-reload");
            return Task.CompletedTask;
        }
    }

    private sealed record OllamaStubServer(string Url, Task Completion);
}
