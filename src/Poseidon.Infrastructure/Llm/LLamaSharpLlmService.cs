using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Poseidon.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Poseidon.Infrastructure.Llm;

/// <summary>
/// LLM service using LLamaSharp (llama.cpp .NET binding) for fully embedded local inference.
/// No external process (Ollama) required. Supports CUDA GPU acceleration with CPU fallback.
///
/// This is an ADDITIONAL implementation â€” OllamaLlmService remains available as an alternative.
///
/// NuGet dependencies:
///   LLamaSharp 0.19.*
///   LLamaSharp.Backend.Cuda12  (for NVIDIA GPU support)
///   -- OR --
///   LLamaSharp.Backend.Cpu     (CPU-only fallback)
/// </summary>
public sealed class LLamaSharpLlmService : ILlmService, IDisposable
{
    private readonly string _modelPath;
    private readonly int _gpuLayers;
    private readonly int _contextSize;
    private readonly ILogger<LLamaSharpLlmService> _logger;

    // LLamaSharp objects â€” lazily initialized
    private object? _model;      // LLamaWeights
    private object? _context;    // LLamaContext
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private bool _initialized;
    private bool _modelAvailable;
    private string? _initError;

    // Inference parameters
    private const float Temperature = 0.1f;
    private const int MaxTokens = 2048;
    private static readonly string[] AntiPrompts = ["</s>", "<|im_end|>", "<|endoftext|>"];

    public LLamaSharpLlmService(
        string modelPath,
        int gpuLayers,
        int contextSize,
        ILogger<LLamaSharpLlmService> logger)
    {
        _modelPath = modelPath;
        _gpuLayers = gpuLayers;
        _contextSize = contextSize;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the LLamaSharp model. Called lazily on first use.
    /// Uses reflection to load LLamaSharp types so the code compiles
    /// even when the NuGet package is not yet installed.
    /// </summary>
    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            _logger.LogInformation(
                "Loading LLM model from {Path} (GPU layers: {Gpu}, context: {Ctx})",
                _modelPath, _gpuLayers, _contextSize);

            if (!File.Exists(_modelPath))
            {
                _initError = $"Model file not found: {_modelPath}";
                _logger.LogError(_initError);
                _modelAvailable = false;
                _initialized = true;
                return;
            }

            try
            {
                // Load via reflection to avoid hard compile-time dependency on LLamaSharp
                var llamaAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "LLamaSharp")
                    ?? System.Reflection.Assembly.Load("LLamaSharp");

                var modelParamsType = llamaAssembly.GetType("LLama.Common.ModelParams")
                    ?? throw new TypeLoadException("Cannot find LLama.Common.ModelParams");
                var llamaWeightsType = llamaAssembly.GetType("LLama.LLamaWeights")
                    ?? throw new TypeLoadException("Cannot find LLama.LLamaWeights");
                var llamaContextType = llamaAssembly.GetType("LLama.LLamaContext")
                    ?? throw new TypeLoadException("Cannot find LLama.LLamaContext");

                // Create ModelParams
                var modelParams = Activator.CreateInstance(modelParamsType, _modelPath)!;
                modelParamsType.GetProperty("ContextSize")?.SetValue(modelParams, (uint)_contextSize);
                modelParamsType.GetProperty("GpuLayerCount")?.SetValue(modelParams, _gpuLayers);
                modelParamsType.GetProperty("Seed")?.SetValue(modelParams, (uint)42);

                // Load model weights
                var loadMethod = llamaWeightsType.GetMethod("LoadFromFile",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                _model = loadMethod?.Invoke(null, [modelParams])
                    ?? throw new InvalidOperationException("Failed to load model weights");

                // Create context
                var createContextMethod = llamaWeightsType.GetMethod("CreateContext");
                _context = createContextMethod?.Invoke(_model, [modelParams])
                    ?? throw new InvalidOperationException("Failed to create context");

                _modelAvailable = true;
                _logger.LogInformation("LLM model loaded successfully via LLamaSharp");

                // Detect GPU
                try
                {
                    var nativeApiType = llamaAssembly.GetType("LLama.Native.NativeApi");
                    var devicesMethod = nativeApiType?.GetMethod("llama_max_devices",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (devicesMethod != null)
                    {
                        var devices = devicesMethod.Invoke(null, null);
                        _logger.LogInformation("GPU devices available: {Devices}", devices);
                    }
                }
                catch { /* GPU detection is best-effort */ }
            }
            catch (Exception ex)
            {
                _initError = $"Failed to load LLamaSharp: {ex.Message}";
                _logger.LogError(ex, "LLamaSharp initialization failed. Ensure LLamaSharp NuGet package is installed.");
                _modelAvailable = false;
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<LlmResponse> GenerateAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        if (!_modelAvailable)
        {
            return new LlmResponse
            {
                Content = "",
                Success = false,
                Error = _initError ?? "LLM model not available"
            };
        }

        var sw = Stopwatch.StartNew();

        await _inferenceLock.WaitAsync(ct);
        try
        {
            var fullPrompt = FormatChatPrompt(systemPrompt, userPrompt);
            var result = new StringBuilder();

            // Use reflection to run inference
            var llamaAssembly = _model!.GetType().Assembly;
            var executorType = llamaAssembly.GetType("LLama.StatelessExecutor")
                ?? throw new TypeLoadException("Cannot find LLama.StatelessExecutor");
            var inferParamsType = llamaAssembly.GetType("LLama.Common.InferenceParams")
                ?? throw new TypeLoadException("Cannot find LLama.Common.InferenceParams");

            var executor = Activator.CreateInstance(executorType, _model, null)!;
            var inferParams = Activator.CreateInstance(inferParamsType)!;
            inferParamsType.GetProperty("Temperature")?.SetValue(inferParams, Temperature);
            inferParamsType.GetProperty("MaxTokens")?.SetValue(inferParams, MaxTokens);
            inferParamsType.GetProperty("AntiPrompts")?.SetValue(inferParams, AntiPrompts.ToList());

            var inferMethod = executorType.GetMethod("InferAsync");
            if (inferMethod == null)
                throw new MissingMethodException("Cannot find InferAsync method");

            var asyncEnumerable = inferMethod.Invoke(executor, [fullPrompt, inferParams, ct]);
            if (asyncEnumerable == null)
                throw new InvalidOperationException("InferAsync returned null");

            // Iterate the IAsyncEnumerable<string>
            var getEnumeratorMethod = asyncEnumerable.GetType().GetMethod("GetAsyncEnumerator");
            var enumerator = getEnumeratorMethod?.Invoke(asyncEnumerable, [ct])!;
            var moveNextMethod = enumerator.GetType().GetMethod("MoveNextAsync");
            var currentProp = enumerator.GetType().GetProperty("Current");

            while (true)
            {
                var moveTask = (ValueTask<bool>)moveNextMethod!.Invoke(enumerator, null)!;
                if (!await moveTask) break;
                var token = (string?)currentProp!.GetValue(enumerator) ?? "";
                result.Append(token);
            }

            sw.Stop();

            return new LlmResponse
            {
                Content = result.ToString().Trim(),
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                Success = true
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "LLamaSharp inference failed");
            return new LlmResponse
            {
                Content = "",
                Success = false,
                Error = ex.Message,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async IAsyncEnumerable<string> StreamGenerateAsync(
        string systemPrompt, string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        if (!_modelAvailable)
        {
            yield return _initError ?? "LLM model not available";
            yield break;
        }

        await _inferenceLock.WaitAsync(ct);
        try
        {
            var fullPrompt = FormatChatPrompt(systemPrompt, userPrompt);

            var llamaAssembly = _model!.GetType().Assembly;
            var executorType = llamaAssembly.GetType("LLama.StatelessExecutor")!;
            var inferParamsType = llamaAssembly.GetType("LLama.Common.InferenceParams")!;

            var executor = Activator.CreateInstance(executorType, _model, null)!;
            var inferParams = Activator.CreateInstance(inferParamsType)!;
            inferParamsType.GetProperty("Temperature")?.SetValue(inferParams, Temperature);
            inferParamsType.GetProperty("MaxTokens")?.SetValue(inferParams, MaxTokens);
            inferParamsType.GetProperty("AntiPrompts")?.SetValue(inferParams, AntiPrompts.ToList());

            var inferMethod = executorType.GetMethod("InferAsync")!;
            var asyncEnumerable = inferMethod.Invoke(executor, [fullPrompt, inferParams, ct])!;

            var getEnumeratorMethod = asyncEnumerable.GetType().GetMethod("GetAsyncEnumerator");
            var enumerator = getEnumeratorMethod?.Invoke(asyncEnumerable, [ct])!;
            var moveNextMethod = enumerator.GetType().GetMethod("MoveNextAsync")!;
            var currentProp = enumerator.GetType().GetProperty("Current")!;

            while (true)
            {
                var moveTask = (ValueTask<bool>)moveNextMethod.Invoke(enumerator, null)!;
                if (!await moveTask) break;
                var token = (string?)currentProp.GetValue(enumerator) ?? "";
                yield return token;
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        return _modelAvailable;
    }

    /// <summary>
    /// Formats the system + user prompt using the ChatML template (used by Qwen).
    /// </summary>
    private static string FormatChatPrompt(string systemPrompt, string userPrompt)
    {
        return $"""
            <|im_start|>system
            {systemPrompt}<|im_end|>
            <|im_start|>user
            {userPrompt}<|im_end|>
            <|im_start|>assistant
            """;
    }

    public void Dispose()
    {
        if (_context is IDisposable ctxDisp) ctxDisp.Dispose();
        if (_model is IDisposable modelDisp) modelDisp.Dispose();
        _initLock.Dispose();
        _inferenceLock.Dispose();
    }
}

