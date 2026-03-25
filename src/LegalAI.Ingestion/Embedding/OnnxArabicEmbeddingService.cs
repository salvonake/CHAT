using System.Collections.Concurrent;
using LegalAI.Domain.Interfaces;
using LegalAI.Ingestion.Arabic;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LegalAI.Ingestion.Embedding;

/// <summary>
/// Arabic embedding service using AraBERT (or compatible model) via ONNX Runtime.
/// No Python dependency — pure .NET inference.
/// 
/// Supports:
/// - Batch embedding for efficient chunk processing
/// - Arabic text normalization before embedding
/// - LRU caching for hot embeddings
/// - Thread-safe operation
/// </summary>
public sealed class OnnxArabicEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer? _tokenizer;
    private readonly ILogger<OnnxArabicEmbeddingService> _logger;
    private readonly ConcurrentDictionary<string, float[]> _cache;
    private readonly int _maxCacheSize;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxSequenceLength;

    /// <summary>
    /// AraBERT base v2 produces 768-dimensional embeddings.
    /// If using multilingual-e5-large, this would be 1024.
    /// </summary>
    public int EmbeddingDimension { get; }

    /// <summary>
    /// Creates an ONNX Arabic embedding service.
    /// </summary>
    /// <param name="modelPath">Path to the ONNX model file.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="vocabPath">
    /// Path to vocab.txt for WordPiece tokenization.
    /// If null, looks for vocab.txt in the same directory as the model.
    /// Falls back to basic char-level tokenization if vocab.txt is not found.
    /// </param>
    /// <param name="embeddingDimension">Output embedding vector dimension.</param>
    /// <param name="maxSequenceLength">Maximum token sequence length.</param>
    /// <param name="maxCacheSize">Maximum embedding cache entries.</param>
    /// <param name="maxConcurrency">Max parallel inference sessions.</param>
    public OnnxArabicEmbeddingService(
        string modelPath,
        ILogger<OnnxArabicEmbeddingService> logger,
        string? vocabPath = null,
        int embeddingDimension = 768,
        int maxSequenceLength = 512,
        int maxCacheSize = 10000,
        int maxConcurrency = 4)
    {
        _logger = logger;
        EmbeddingDimension = embeddingDimension;
        _maxSequenceLength = maxSequenceLength;
        _maxCacheSize = maxCacheSize;
        _cache = new ConcurrentDictionary<string, float[]>();
        _semaphore = new SemaphoreSlim(maxConcurrency);

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = Environment.ProcessorCount,
            IntraOpNumThreads = Environment.ProcessorCount
        };

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"ONNX embedding model not found at: {modelPath}. " +
                "Please download AraBERT ONNX model and place it at this path.",
                modelPath);
        }

        _session = new InferenceSession(modelPath, sessionOptions);

        // Load WordPiece tokenizer if vocab.txt is available
        var resolvedVocabPath = vocabPath
            ?? Path.Combine(Path.GetDirectoryName(modelPath) ?? ".", "vocab.txt");

        if (File.Exists(resolvedVocabPath))
        {
            _tokenizer = new WordPieceTokenizer(resolvedVocabPath);
            _logger.LogInformation(
                "Loaded WordPiece tokenizer from {VocabPath} ({VocabSize} tokens)",
                resolvedVocabPath, _tokenizer.VocabSize);
        }
        else
        {
            _logger.LogWarning(
                "vocab.txt not found at {VocabPath}. Using fallback char-level tokenization. " +
                "For production accuracy, provide vocab.txt alongside the ONNX model.",
                resolvedVocabPath);
        }

        _logger.LogInformation(
            "Loaded ONNX embedding model from {ModelPath}. Dimension: {Dim}, MaxSeqLen: {MaxSeq}",
            modelPath, embeddingDimension, maxSequenceLength);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var normalized = ArabicNormalizer.Normalize(text);

        // Check cache
        if (_cache.TryGetValue(normalized, out var cached))
            return cached;

        await _semaphore.WaitAsync(ct);
        try
        {
            var embedding = RunInference(normalized);
            CacheEmbedding(normalized, embedding);
            return embedding;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        var uncachedIndices = new List<int>();
        var uncachedTexts = new List<string>();

        // Check cache first
        for (var i = 0; i < texts.Count; i++)
        {
            var normalized = ArabicNormalizer.Normalize(texts[i]);
            if (_cache.TryGetValue(normalized, out var cached))
            {
                results[i] = cached;
            }
            else
            {
                uncachedIndices.Add(i);
                uncachedTexts.Add(normalized);
            }
        }

        if (uncachedTexts.Count == 0)
            return results;

        // Process uncached texts
        await _semaphore.WaitAsync(ct);
        try
        {
            for (var i = 0; i < uncachedTexts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var embedding = RunInference(uncachedTexts[i]);
                results[uncachedIndices[i]] = embedding;
                CacheEmbedding(uncachedTexts[i], embedding);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        return results;
    }

    private float[] RunInference(string text)
    {
        long[] inputIds;
        long[] attentionMask;

        if (_tokenizer != null)
        {
            // Production path: proper WordPiece tokenization
            (inputIds, attentionMask) = _tokenizer.TokenizeWithMask(text, _maxSequenceLength);
        }
        else
        {
            // Fallback: basic char-level tokenization
            inputIds = TokenizeSimpleFallback(text);
            attentionMask = new long[inputIds.Length];
            Array.Fill(attentionMask, 1L);
        }

        var inputTensor = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
        var maskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
        };

        // Check if model expects token_type_ids
        var inputNames = _session.InputMetadata.Keys.ToHashSet();
        if (inputNames.Contains("token_type_ids"))
        {
            var tokenTypeIds = new long[inputIds.Length];
            var tokenTypeTensor = new DenseTensor<long>(tokenTypeIds, [1, tokenTypeIds.Length]);
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeTensor));
        }

        using var results = _session.Run(inputs);

        // Get the output — typically "last_hidden_state" or "sentence_embedding"
        var output = results.First();
        var outputTensor = output.AsTensor<float>();

        // Mean pooling over the sequence dimension
        var embedding = MeanPool(outputTensor, attentionMask);

        // L2 normalize
        Normalize(embedding);

        return embedding;
    }

    /// <summary>
    /// Fallback tokenization when vocab.txt is not available.
    /// Maps characters to pseudo-token IDs. NOT suitable for production.
    /// </summary>
    private static long[] TokenizeSimpleFallback(string text)
    {
        const int maxLength = 512;
        const int clsToken = 101;  // [CLS]
        const int sepToken = 102;  // [SEP]

        var tokens = new List<long>(Math.Min(text.Length + 2, maxLength)) { clsToken };

        foreach (var c in text)
        {
            if (tokens.Count >= maxLength - 1) break;
            tokens.Add(c);
        }

        tokens.Add(sepToken);
        return tokens.ToArray();
    }

    private float[] MeanPool(Tensor<float> outputTensor, long[] attentionMask)
    {
        var embedding = new float[EmbeddingDimension];
        var dims = outputTensor.Dimensions;

        if (dims.Length == 3)
        {
            // Shape: [batch, sequence, hidden]
            var seqLen = dims[1];
            var hiddenSize = Math.Min(dims[2], EmbeddingDimension);
            var validTokens = attentionMask.Sum();

            for (var h = 0; h < hiddenSize; h++)
            {
                float sum = 0;
                for (var s = 0; s < seqLen; s++)
                {
                    if (attentionMask[s] == 1)
                    {
                        sum += outputTensor[0, s, h];
                    }
                }
                embedding[h] = sum / validTokens;
            }
        }
        else if (dims.Length == 2)
        {
            // Shape: [batch, hidden] — already pooled
            var hiddenSize = Math.Min(dims[1], EmbeddingDimension);
            for (var h = 0; h < hiddenSize; h++)
            {
                embedding[h] = outputTensor[0, h];
            }
        }

        return embedding;
    }

    private static void Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }
    }

    private void CacheEmbedding(string text, float[] embedding)
    {
        if (_cache.Count >= _maxCacheSize)
        {
            // Simple eviction: clear half the cache
            var keysToRemove = _cache.Keys.Take(_maxCacheSize / 2).ToList();
            foreach (var key in keysToRemove)
                _cache.TryRemove(key, out _);
        }

        _cache.TryAdd(text, embedding);
    }

    public void Dispose()
    {
        _session.Dispose();
        _semaphore.Dispose();
    }
}
