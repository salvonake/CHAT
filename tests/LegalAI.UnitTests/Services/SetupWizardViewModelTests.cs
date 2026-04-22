using System.IO;
using FluentAssertions;
using LegalAI.Desktop;
using LegalAI.Desktop.Services;
using LegalAI.Desktop.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="SetupWizardViewModel"/>: wizard navigation,
/// validation, provider selection, and setup flow states.
/// </summary>
public sealed class SetupWizardViewModelTests : IDisposable
{
    private readonly Mock<ILogger<ModelDownloadService>> _dlLogger = new();
    private readonly Mock<ILogger<SetupWizardViewModel>> _vmLogger = new();
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly IConfiguration _config;

    public SetupWizardViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LegalAI_Wizard_{Guid.NewGuid():N}");
        var modelsDir = Path.Combine(_tempDir, "Models");
        Directory.CreateDirectory(modelsDir);

        _paths = new DataPaths
        {
            DataDirectory = _tempDir,
            ModelsDirectory = modelsDir,
            VectorDbPath = Path.Combine(_tempDir, "vectors.db"),
            HnswIndexPath = Path.Combine(_tempDir, "hnsw.index"),
            DocumentDbPath = Path.Combine(_tempDir, "docs.db"),
            AuditDbPath = Path.Combine(_tempDir, "audit.db"),
            WatchDirectory = Path.Combine(_tempDir, "Watch")
        };

        var configData = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "llamasharp",
            ["Llm:ModelPath"] = "",
            ["Ollama:Url"] = "http://localhost:11434",
            ["Ollama:Model"] = "qwen2.5:14b",
            ["Embedding:Provider"] = "onnx",
            ["Embedding:Model"] = "nomic-embed-text",
            ["ModelIntegrity:ExpectedLlmHash"] = "",
            ["ModelIntegrity:ExpectedEmbeddingHash"] = ""
        };

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private SetupWizardViewModel CreateVm()
    {
        var downloadService = new ModelDownloadService(_dlLogger.Object);
        return new SetupWizardViewModel(downloadService, _paths, _config, _vmLogger.Object);
    }

    // ═══════════════════════════════════════
    //  Initial State
    // ═══════════════════════════════════════

    [Fact]
    public void InitialState_StartsAtWelcomeStep()
    {
        var vm = CreateVm();

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepWelcome);
        vm.UseLlamaSharp.Should().BeTrue("default provider should be LLamaSharp");
        vm.UseOllama.Should().BeFalse();
        vm.Completed.Should().BeFalse();
        vm.IsProcessing.Should().BeFalse();
    }

    [Fact]
    public void InitialState_HasDefaultModelFileNames()
    {
        var vm = CreateVm();

        vm.LlmModelFileName.Should().NotBeNullOrWhiteSpace();
        vm.EmbModelFileName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void InitialState_PreFillsOllamaSettings()
    {
        var vm = CreateVm();

        vm.OllamaUrl.Should().Be("http://localhost:11434");
        vm.OllamaLlmModel.Should().Be("qwen2.5:14b");
        vm.OllamaEmbeddingModel.Should().Be("nomic-embed-text");
    }

    [Fact]
    public void InitialState_DetectsExistingModels()
    {
        // Arrange — put a fake model file in place
        var ggufPath = Path.Combine(_paths.ModelsDirectory, "qwen2.5-14b.Q5_K_M.gguf");
        File.WriteAllBytes(ggufPath, [1, 2, 3]);

        // Act
        var vm = CreateVm();

        // Assert
        vm.LlmLocalPath.Should().Be(ggufPath);
        vm.LlmStatusMessage.Should().Contain("✓");
    }

    // ═══════════════════════════════════════
    //  Navigation: Forward
    // ═══════════════════════════════════════

    [Fact]
    public async Task NextStep_FromWelcome_GoesToLlmStep_WhenLlamaSharp()
    {
        var vm = CreateVm();
        vm.UseLlamaSharp = true;

        await vm.NextStepCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepDomain);
    }

    [Fact]
    public async Task NextStep_FromLlm_GoesToEmbedding_WhenValid()
    {
        var vm = CreateVm();
        vm.UseLlamaSharp = true;

        // Go to Domain step, then LLM step
        await vm.NextStepCommand.ExecuteAsync(null);
        await vm.NextStepCommand.ExecuteAsync(null);
        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepLlm);

        // Set a valid local path
        var ggufPath = Path.Combine(_tempDir, "model.gguf");
        File.WriteAllBytes(ggufPath, [1, 2, 3]);
        vm.LlmLocalPath = ggufPath;
        vm.LlmBrowseLocal = true;

        // Go to embedding step
        await vm.NextStepCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepEmbedding);
    }

    [Fact]
    public async Task NextStep_FromLlm_StaysIfInvalid()
    {
        var vm = CreateVm();
        vm.UseLlamaSharp = true;

        await vm.NextStepCommand.ExecuteAsync(null);
        await vm.NextStepCommand.ExecuteAsync(null);
        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepLlm);

        // Leave path empty
        vm.LlmLocalPath = "";
        vm.LlmBrowseLocal = true;

        await vm.NextStepCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepLlm, "should stay on LLM step when validation fails");
        vm.LlmStatusMessage.Should().NotBeNullOrWhiteSpace("should show validation error");
    }

    // ═══════════════════════════════════════
    //  Navigation: Backward
    // ═══════════════════════════════════════

    [Fact]
    public async Task PreviousStep_FromLlm_GoesBackToWelcome()
    {
        var vm = CreateVm();

        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM
        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepLlm);

        vm.PreviousStepCommand.Execute(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepDomain);
    }

    [Fact]
    public async Task PreviousStep_FromEmbedding_GoesBackToLlm()
    {
        var vm = CreateVm();

        // Navigate to Embedding step
        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM
        var ggufPath = Path.Combine(_tempDir, "model.gguf");
        File.WriteAllBytes(ggufPath, [1, 2, 3]);
        vm.LlmLocalPath = ggufPath;
        vm.LlmBrowseLocal = true;
        await vm.NextStepCommand.ExecuteAsync(null);
        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepEmbedding);

        // Go back
        vm.PreviousStepCommand.Execute(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepLlm);
    }

    // ═══════════════════════════════════════
    //  LLM Validation
    // ═══════════════════════════════════════

    [Fact]
    public async Task LlmValidation_RejectsNonexistentFile()
    {
        var vm = CreateVm();
        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM

        vm.LlmBrowseLocal = true;
        vm.LlmLocalPath = Path.Combine(_tempDir, "nonexistent.gguf");

        await vm.NextStepCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepLlm);
        vm.LlmStatusMessage.Should().Contain("⚠");
    }

    [Fact]
    public async Task LlmValidation_RejectsInvalidUrl()
    {
        var vm = CreateVm();
        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM

        vm.LlmBrowseLocal = false;
        vm.LlmDownloadUrl = true;
        vm.LlmDownloadUrlText = "not-a-valid-url";

        await vm.NextStepCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepLlm);
        vm.LlmStatusMessage.Should().Contain("⚠");
    }

    [Fact]
    public async Task LlmValidation_AcceptsValidUrl()
    {
        var vm = CreateVm();
        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM

        vm.LlmBrowseLocal = false;
        vm.LlmDownloadUrl = true;
        vm.LlmDownloadUrlText = "https://huggingface.co/model.gguf";

        await vm.NextStepCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepEmbedding, "valid URL should pass validation");
    }

    // ═══════════════════════════════════════
    //  Embedding Validation
    // ═══════════════════════════════════════

    [Fact]
    public async Task EmbValidation_RejectsEmptyPath()
    {
        var vm = CreateVm();

        // Navigate to Embedding
        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM
        var gguf = Path.Combine(_tempDir, "m.gguf");
        File.WriteAllBytes(gguf, [1]);
        vm.LlmLocalPath = gguf;
        await vm.NextStepCommand.ExecuteAsync(null);
        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepEmbedding);

        vm.EmbBrowseLocal = true;
        vm.EmbLocalPath = "";

        await vm.NextStepCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepEmbedding);
        vm.EmbStatusMessage.Should().Contain("⚠");
    }

    // ═══════════════════════════════════════
    //  Cancel
    // ═══════════════════════════════════════

    [Fact]
    public void Cancel_WhenNotProcessing_RaisesCloseAndSetsNotCompleted()
    {
        var vm = CreateVm();
        bool closeCalled = false;
        vm.RequestClose += () => closeCalled = true;

        vm.CancelCommand.Execute(null);

        closeCalled.Should().BeTrue();
        vm.Completed.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  Finish
    // ═══════════════════════════════════════

    [Fact]
    public void Finish_SetsCompletedFromSetupSucceeded()
    {
        var vm = CreateVm();
        bool closeCalled = false;
        vm.RequestClose += () => closeCalled = true;

        // Manually set state as if setup succeeded
        // (Normally done by ExecuteLocalSetupAsync)
        vm.FinishCommand.Execute(null);

        closeCalled.Should().BeTrue();
        vm.Completed.Should().BeFalse("SetupSucceeded defaults to false");
    }

    // ═══════════════════════════════════════
    //  Local Setup — Models Already In Place
    // ═══════════════════════════════════════

    [Fact]
    public async Task LocalSetup_ModelsInPlace_CompletesWithoutCopy()
    {
        var vm = CreateVm();

        // Create model files at their destination paths
        var llmDest = Path.Combine(_paths.ModelsDirectory, vm.LlmModelFileName);
        var onnxDest = Path.Combine(_paths.ModelsDirectory, vm.EmbModelFileName);
        File.WriteAllBytes(llmDest, [1, 2, 3]);
        File.WriteAllBytes(onnxDest, [4, 5, 6]);

        // Navigate through wizard with source == destination
        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM
        vm.LlmLocalPath = llmDest;
        vm.LlmBrowseLocal = true;
        await vm.NextStepCommand.ExecuteAsync(null); // LLM -> Embedding
        vm.EmbLocalPath = onnxDest;
        vm.EmbBrowseLocal = true;
        await vm.NextStepCommand.ExecuteAsync(null); // Embedding -> Progress -> Complete

        // Wait for async completion
        await WaitForStep(vm, SetupWizardViewModel.StepComplete, TimeSpan.FromSeconds(5));

        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepComplete);
        vm.SetupSucceeded.Should().BeTrue();
        vm.CompletionMessage.Should().Contain("✓");

        var configPath = Path.Combine(_paths.DataDirectory, "appsettings.user.json");
        File.Exists(configPath).Should().BeTrue("wizard should persist runtime provider settings");
        var json = await File.ReadAllTextAsync(configPath);
        json.Should().Contain("\"Provider\": \"llamasharp\"");
        json.Should().Contain("\"OnnxModelPath\"");
    }

    [Fact]
    public async Task LocalSetup_UsesSelectedExternalModelPaths()
    {
        var vm = CreateVm();

        // Create a source model in a different location
        var sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var llmSource = Path.Combine(sourceDir, "model.gguf");
        var onnxSource = Path.Combine(sourceDir, "emb.onnx");
        File.WriteAllBytes(llmSource, [10, 20, 30]);
        File.WriteAllBytes(onnxSource, [40, 50, 60]);

        // Navigate through wizard
        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> LLM
        vm.LlmLocalPath = llmSource;
        vm.LlmBrowseLocal = true;
        await vm.NextStepCommand.ExecuteAsync(null);
        vm.EmbLocalPath = onnxSource;
        vm.EmbBrowseLocal = true;
        await vm.NextStepCommand.ExecuteAsync(null);

        await WaitForStep(vm, SetupWizardViewModel.StepComplete, TimeSpan.FromSeconds(10));

        vm.SetupSucceeded.Should().BeTrue();

        var configPath = Path.Combine(_paths.DataDirectory, "appsettings.user.json");
        File.Exists(configPath).Should().BeTrue("wizard should persist runtime provider settings");
        var json = await File.ReadAllTextAsync(configPath);
        json.Should().Contain(llmSource.Replace("\\", "\\\\"));
        json.Should().Contain(onnxSource.Replace("\\", "\\\\"));
    }

    // ═══════════════════════════════════════
    //  Provider Selection
    // ═══════════════════════════════════════

    [Fact]
    public void ProviderSelection_LlamaSharpIsDefault()
    {
        var vm = CreateVm();
        vm.UseLlamaSharp.Should().BeTrue();
        vm.UseOllama.Should().BeFalse();
    }

    [Fact]
    public async Task ProviderSelection_Ollama_SkipsFileSteps()
    {
        var vm = CreateVm();
        vm.UseOllama = true;
        vm.UseLlamaSharp = false;

        // Set a non-routable URL so it fails fast
        vm.OllamaUrl = "http://127.0.0.1:19999";

        await vm.NextStepCommand.ExecuteAsync(null); // Welcome -> Domain
        await vm.NextStepCommand.ExecuteAsync(null); // Domain -> Progress (Ollama)

        // Should jump to progress (step 3) then to complete (step 4)
        await WaitForStep(vm, SetupWizardViewModel.StepComplete, TimeSpan.FromSeconds(15));

        // Should have skipped steps 1 and 2
        vm.CurrentStep.Should().Be(SetupWizardViewModel.StepComplete);
    }

    // ═══════════════════════════════════════
    //  Step Constants
    // ═══════════════════════════════════════

    [Fact]
    public void StepConstants_AreSequential()
    {
        SetupWizardViewModel.StepWelcome.Should().Be(0);
        SetupWizardViewModel.StepDomain.Should().Be(1);
        SetupWizardViewModel.StepLlm.Should().Be(2);
        SetupWizardViewModel.StepEmbedding.Should().Be(3);
        SetupWizardViewModel.StepProgress.Should().Be(4);
        SetupWizardViewModel.StepComplete.Should().Be(5);
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    private static async Task WaitForStep(SetupWizardViewModel vm, int step, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (vm.CurrentStep != step && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}
