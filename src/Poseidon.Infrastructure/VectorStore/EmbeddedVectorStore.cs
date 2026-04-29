using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Poseidon.Infrastructure.VectorStore;

/// <summary>
/// Embedded vector store using SQLite for chunk storage and an in-memory HNSW-style
/// graph for approximate nearest-neighbor search. Zero external dependencies â€” no Docker,
/// no Qdrant. Designed for single-machine enterprise deployment.
/// 
/// Storage layout:
///   vectors.db   â€” SQLite database with chunk metadata + raw embedding blobs
///   vectors.hnsw â€” serialized neighbor-graph for fast ANN queries (rebuilt from DB on startup)
/// </summary>
public sealed class EmbeddedVectorStore : IVectorStore, IDisposable
{
    // â”€â”€ HNSW hyper-parameters â”€â”€
    private const int HnswM = 16;                // max edges per node per layer
    private const int HnswEfConstruction = 200;   // search width during build
    private const int HnswEfSearch = 100;         // search width during query
    private const int HnswMMax0 = 32;             // max edges on layer 0

    private readonly string _dbPath;
    private readonly string _hnswPath;
    private readonly ILogger<EmbeddedVectorStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Random _rng = new(42);

    // â”€â”€ In-memory HNSW graph â”€â”€
    private readonly ConcurrentDictionary<string, float[]> _vectors = new();          // id â†’ embedding
    private readonly ConcurrentDictionary<string, List<string>[]> _graph = new();     // id â†’ layers â†’ neighbor ids
    private readonly ConcurrentDictionary<string, ChunkMeta> _metaCache = new();      // id â†’ lightweight meta
    private string? _entryPoint;
    private int _maxLevel;
    private int _embeddingDim;
    private bool _initialized;
    private bool _dirty;

    // Timer for periodic flush
    private Timer? _flushTimer;
    private const int FlushIntervalMs = 60_000;

    public EmbeddedVectorStore(string dbPath, string hnswPath, ILogger<EmbeddedVectorStore> logger)
    {
        _dbPath = dbPath;
        _hnswPath = hnswPath;
        _logger = logger;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  IVectorStore.InitializeAsync
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            InitializeSchema();
            await LoadGraphFromDbAsync(ct);

            _flushTimer = new Timer(_ => _ = FlushGraphAsync(), null, FlushIntervalMs, FlushIntervalMs);
            _initialized = true;
            _logger.LogInformation(
                "EmbeddedVectorStore initialized: {Count} vectors, dim={Dim}",
                _vectors.Count, _embeddingDim);
        }
        finally
        {
            _lock.Release();
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  IVectorStore.UpsertAsync
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public async Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        await _lock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            foreach (var chunk in chunks)
            {
                if (chunk.Embedding is null || chunk.Embedding.Length == 0)
                {
                    _logger.LogWarning("Chunk {Id} has no embedding, skipping", chunk.Id);
                    continue;
                }

                if (_embeddingDim == 0)
                    _embeddingDim = chunk.Embedding.Length;

                // SQLite upsert
                UpsertChunkToDb(conn, chunk);

                // HNSW insert
                _vectors[chunk.Id] = chunk.Embedding;
                _metaCache[chunk.Id] = new ChunkMeta(
                    chunk.DocumentId,
                    chunk.CaseNamespace,
                    NormalizeScopeValue(chunk.DomainId),
                    NormalizeScopeValue(chunk.DatasetId),
                    NormalizeScopeValue(chunk.DatasetScope),
                    chunk.ContentHash);
                InsertIntoGraph(chunk.Id, chunk.Embedding);
            }

            tx.Commit();
            _dirty = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  IVectorStore.SearchAsync
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public Task<List<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        double scoreThreshold = 0.0,
        string? caseNamespace = null,
        CancellationToken ct = default)
    {
        return SearchAsync(queryEmbedding, topK, scoreThreshold, caseNamespace, ct, null, null);
    }

    public Task<List<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        double scoreThreshold,
        string? caseNamespace,
        CancellationToken ct,
        string? domainId = null,
        string? datasetScope = null)
    {
        if (_vectors.IsEmpty)
            return Task.FromResult(new List<RetrievedChunk>());

        var normalizedDomainId = NormalizeScopeValue(domainId);
        var normalizedDatasetScope = NormalizeScopeValue(datasetScope);

        // Search with extra candidates to accommodate filtering
        var efSearch = Math.Max(HnswEfSearch, topK * 4);
        var candidates = SearchGraph(queryEmbedding, efSearch);

        // Apply namespace filter and threshold
        var results = new List<RetrievedChunk>();
        using var conn = OpenConnection();

        foreach (var (id, score) in candidates)
        {
            if (score < scoreThreshold) continue;

            if (!_metaCache.TryGetValue(id, out var meta))
                continue;

            if (!string.IsNullOrEmpty(caseNamespace)
                && !string.Equals(meta.CaseNamespace, caseNamespace, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(normalizedDomainId)
                && !string.Equals(meta.DomainId, normalizedDomainId, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(normalizedDatasetScope)
                && !string.Equals(meta.DatasetScope, normalizedDatasetScope, StringComparison.Ordinal))
                continue;

            var chunk = LoadChunkFromDb(conn, id);
            if (chunk is null) continue;

            results.Add(new RetrievedChunk
            {
                Chunk = chunk,
                SimilarityScore = (float)score
            });

            if (results.Count >= topK) break;
        }

        return Task.FromResult(results);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  IVectorStore.DeleteByDocumentIdAsync
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public async Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Find all chunk IDs for this document
            var idsToRemove = _metaCache
                .Where(kv => kv.Value.DocumentId == documentId)
                .Select(kv => kv.Key)
                .ToList();

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM vector_chunks WHERE document_id = @docId";
            cmd.Parameters.AddWithValue("@docId", documentId);
            cmd.ExecuteNonQuery();

            foreach (var id in idsToRemove)
            {
                _vectors.TryRemove(id, out _);
                _metaCache.TryRemove(id, out _);
                _graph.TryRemove(id, out _);
            }

            // Remove references from other nodes' neighbor lists
            foreach (var kv in _graph)
            {
                foreach (var layer in kv.Value)
                {
                    layer.RemoveAll(n => idsToRemove.Contains(n));
                }
            }

            // Reset entry point if deleted
            if (_entryPoint != null && idsToRemove.Contains(_entryPoint))
            {
                _entryPoint = _vectors.Keys.FirstOrDefault();
                _maxLevel = _entryPoint != null && _graph.TryGetValue(_entryPoint, out var layers)
                    ? layers.Length - 1 : 0;
            }

            _dirty = true;
            _logger.LogInformation("Deleted {Count} vectors for document {DocId}", idsToRemove.Count, documentId);
        }
        finally
        {
            _lock.Release();
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  IVectorStore.ExistsByHashAsync
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public Task<bool> ExistsByHashAsync(string contentHash, CancellationToken ct = default)
    {
        // Fast path: check in-memory cache
        var exists = _metaCache.Values.Any(m => m.ContentHash == contentHash);
        if (exists) return Task.FromResult(true);

        // Fallback: check SQLite
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM vector_chunks WHERE content_hash = @hash LIMIT 1";
        cmd.Parameters.AddWithValue("@hash", contentHash);
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.FromResult(count > 0);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  IVectorStore.GetVectorCountAsync
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public Task<long> GetVectorCountAsync(CancellationToken ct = default)
    {
        return Task.FromResult((long)_vectors.Count);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  IVectorStore.GetHealthAsync
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public Task<VectorStoreHealth> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(new VectorStoreHealth
            {
                IsHealthy = _initialized,
                VectorCount = _vectors.Count,
                IndexedSegments = _graph.Count,
                Status = _initialized ? "Healthy (Embedded HNSW)" : "Not initialized",
                Error = null
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VectorStoreHealth
            {
                IsHealthy = false,
                VectorCount = 0,
                Status = "Error",
                Error = ex.Message
            });
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  HNSW GRAPH IMPLEMENTATION
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    /// <summary>Assign a random level to a new node (geometric distribution).</summary>
    private int RandomLevel()
    {
        var ml = 1.0 / Math.Log(HnswM);
        return (int)(-Math.Log(_rng.NextDouble()) * ml);
    }

    /// <summary>Cosine similarity between two vectors.</summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    /// <summary>Insert a new node into the HNSW graph.</summary>
    private void InsertIntoGraph(string id, float[] vec)
    {
        var level = RandomLevel();
        var mMax = HnswM;

        // Initialize layers for this node
        var layers = new List<string>[level + 1];
        for (int i = 0; i <= level; i++)
            layers[i] = new List<string>();
        _graph[id] = layers;

        if (_entryPoint == null)
        {
            _entryPoint = id;
            _maxLevel = level;
            return;
        }

        var ep = _entryPoint;

        // Phase 1: Greedy traverse from top to level+1
        for (int lc = _maxLevel; lc > level; lc--)
        {
            ep = GreedyClosest(vec, ep, lc);
        }

        // Phase 2: Insert at each layer from min(level, _maxLevel) down to 0
        for (int lc = Math.Min(level, _maxLevel); lc >= 0; lc--)
        {
            var maxEdges = lc == 0 ? HnswMMax0 : mMax;
            var neighbors = SearchLayer(vec, ep, HnswEfConstruction, lc);

            // Select top-M neighbors
            var selected = neighbors
                .OrderByDescending(n => n.Score)
                .Take(maxEdges)
                .ToList();

            // Add bidirectional edges
            foreach (var (nid, _) in selected)
            {
                layers[lc].Add(nid);

                if (_graph.TryGetValue(nid, out var nLayers) && lc < nLayers.Length)
                {
                    nLayers[lc].Add(id);
                    // Prune if over capacity
                    if (nLayers[lc].Count > maxEdges)
                    {
                        nLayers[lc] = nLayers[lc]
                            .Select(nn => (Id: nn, Score: _vectors.TryGetValue(nn, out var nv)
                                ? CosineSimilarity(_vectors[nid], nv) : 0.0))
                            .OrderByDescending(x => x.Score)
                            .Take(maxEdges)
                            .Select(x => x.Id)
                            .ToList();
                    }
                }
            }

            if (selected.Count > 0)
                ep = selected[0].Id;
        }

        // Update entry point if new node is at a higher level
        if (level > _maxLevel)
        {
            _entryPoint = id;
            _maxLevel = level;
        }
    }

    /// <summary>Greedy search for the closest node on a given layer.</summary>
    private string GreedyClosest(float[] query, string ep, int layer)
    {
        var current = ep;
        var bestDist = _vectors.TryGetValue(current, out var cv)
            ? CosineSimilarity(query, cv) : -1;

        while (true)
        {
            var changed = false;
            if (_graph.TryGetValue(current, out var layers) && layer < layers.Length)
            {
                foreach (var neighbor in layers[layer])
                {
                    if (_vectors.TryGetValue(neighbor, out var nv))
                    {
                        var dist = CosineSimilarity(query, nv);
                        if (dist > bestDist)
                        {
                            bestDist = dist;
                            current = neighbor;
                            changed = true;
                        }
                    }
                }
            }
            if (!changed) break;
        }
        return current;
    }

    /// <summary>Search a single layer with ef-width beam search.</summary>
    private List<(string Id, double Score)> SearchLayer(
        float[] query, string ep, int ef, int layer)
    {
        var visited = new HashSet<string> { ep };
        var epScore = _vectors.TryGetValue(ep, out var epv)
            ? CosineSimilarity(query, epv) : -1;

        // candidates: max-heap by score (we want to expand best first)
        var candidates = new SortedList<double, string>(new DuplicateKeyComparer());
        candidates.Add(epScore, ep);

        // results: all found within ef
        var results = new List<(string Id, double Score)> { (ep, epScore) };

        while (candidates.Count > 0)
        {
            // Take best candidate
            var bestScore = candidates.Keys[candidates.Count - 1];
            var bestId = candidates.Values[candidates.Count - 1];
            candidates.RemoveAt(candidates.Count - 1);

            // If worst result is better than best candidate, stop
            if (results.Count >= ef)
            {
                var worstResult = results.Min(r => r.Score);
                if (bestScore < worstResult)
                    break;
            }

            // Expand neighbors
            if (_graph.TryGetValue(bestId, out var layers) && layer < layers.Length)
            {
                foreach (var neighbor in layers[layer])
                {
                    if (!visited.Add(neighbor)) continue;

                    if (_vectors.TryGetValue(neighbor, out var nv))
                    {
                        var score = CosineSimilarity(query, nv);
                        results.Add((neighbor, score));
                        candidates.Add(score, neighbor);
                    }
                }
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(ef)
            .ToList();
    }

    /// <summary>Top-level search across all HNSW layers.</summary>
    private List<(string Id, double Score)> SearchGraph(float[] query, int ef)
    {
        if (_entryPoint == null) return [];

        var ep = _entryPoint;

        // Phase 1: Greedy traverse from top to layer 1
        for (int lc = _maxLevel; lc > 0; lc--)
        {
            ep = GreedyClosest(query, ep, lc);
        }

        // Phase 2: Search layer 0 with full ef
        return SearchLayer(query, ep, ef, 0);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  SQLite OPERATIONS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-64000;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS vector_chunks (
                id TEXT PRIMARY KEY,
                document_id TEXT NOT NULL,
                content TEXT NOT NULL,
                embedding BLOB NOT NULL,
                chunk_index INTEGER NOT NULL DEFAULT 0,
                page_number INTEGER NOT NULL DEFAULT 0,
                section_title TEXT,
                article_reference TEXT,
                case_number TEXT,
                court_name TEXT,
                case_date TEXT,
                domain_id TEXT,
                dataset_id TEXT,
                dataset_scope TEXT,
                case_namespace TEXT,
                content_hash TEXT NOT NULL,
                token_count INTEGER NOT NULL DEFAULT 0,
                source_file_name TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_vc_document_id ON vector_chunks(document_id);
            CREATE INDEX IF NOT EXISTS idx_vc_content_hash ON vector_chunks(content_hash);
            CREATE INDEX IF NOT EXISTS idx_vc_case_namespace ON vector_chunks(case_namespace);
            CREATE INDEX IF NOT EXISTS idx_vc_domain_id ON vector_chunks(domain_id);
            CREATE INDEX IF NOT EXISTS idx_vc_dataset_scope ON vector_chunks(dataset_scope);
            CREATE INDEX IF NOT EXISTS idx_vc_article_ref ON vector_chunks(article_reference);
            CREATE INDEX IF NOT EXISTS idx_vc_case_number ON vector_chunks(case_number);
            """;
        cmd.ExecuteNonQuery();

        EnsureColumnExists(conn, "vector_chunks", "domain_id", "TEXT");
        EnsureColumnExists(conn, "vector_chunks", "dataset_id", "TEXT");
        EnsureColumnExists(conn, "vector_chunks", "dataset_scope", "TEXT");
    }

    private void UpsertChunkToDb(SqliteConnection conn, DocumentChunk chunk)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vector_chunks
                (id, document_id, content, embedding, chunk_index, page_number,
                 section_title, article_reference, case_number, court_name,
                 case_date, domain_id, dataset_id, dataset_scope, case_namespace,
                 content_hash, token_count, source_file_name)
            VALUES
                (@id, @docId, @content, @embedding, @chunkIndex, @pageNum,
                 @section, @article, @caseNum, @court,
                 @caseDate, @domainId, @datasetId, @datasetScope, @caseNs,
                 @hash, @tokens, @srcFile)
            """;
        cmd.Parameters.AddWithValue("@id", chunk.Id);
        cmd.Parameters.AddWithValue("@docId", chunk.DocumentId);
        cmd.Parameters.AddWithValue("@content", chunk.Content);
        cmd.Parameters.AddWithValue("@embedding", FloatsToBlob(chunk.Embedding!));
        cmd.Parameters.AddWithValue("@chunkIndex", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("@pageNum", chunk.PageNumber);
        cmd.Parameters.AddWithValue("@section", (object?)chunk.SectionTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@article", (object?)chunk.ArticleReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@caseNum", (object?)chunk.CaseNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@court", (object?)chunk.CourtName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@caseDate", (object?)chunk.CaseDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@domainId", (object?)NormalizeScopeValue(chunk.DomainId) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@datasetId", (object?)NormalizeScopeValue(chunk.DatasetId) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@datasetScope", (object?)NormalizeScopeValue(chunk.DatasetScope) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@caseNs", (object?)chunk.CaseNamespace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", chunk.ContentHash);
        cmd.Parameters.AddWithValue("@tokens", chunk.TokenCount);
        cmd.Parameters.AddWithValue("@srcFile", chunk.SourceFileName);
        cmd.ExecuteNonQuery();
    }

    private DocumentChunk? LoadChunkFromDb(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, document_id, content, embedding, chunk_index, page_number,
                   section_title, article_reference, case_number, court_name,
                 case_date, domain_id, dataset_id, dataset_scope, case_namespace,
                 content_hash, token_count, source_file_name
            FROM vector_chunks WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new DocumentChunk
        {
            Id = reader.GetString(0),
            DocumentId = reader.GetString(1),
            Content = reader.GetString(2),
            Embedding = BlobToFloats((byte[])reader[3]),
            ChunkIndex = reader.GetInt32(4),
            PageNumber = reader.GetInt32(5),
            SectionTitle = reader.IsDBNull(6) ? null : reader.GetString(6),
            ArticleReference = reader.IsDBNull(7) ? null : reader.GetString(7),
            CaseNumber = reader.IsDBNull(8) ? null : reader.GetString(8),
            CourtName = reader.IsDBNull(9) ? null : reader.GetString(9),
            CaseDate = reader.IsDBNull(10) ? null : reader.GetString(10),
            DomainId = reader.IsDBNull(11) ? null : reader.GetString(11),
            DatasetId = reader.IsDBNull(12) ? null : reader.GetString(12),
            DatasetScope = reader.IsDBNull(13) ? null : reader.GetString(13),
            CaseNamespace = reader.IsDBNull(14) ? null : reader.GetString(14),
            ContentHash = reader.GetString(15),
            TokenCount = reader.GetInt32(16),
            SourceFileName = reader.GetString(17)
        };
    }

    private async Task LoadGraphFromDbAsync(CancellationToken ct)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, document_id, embedding, case_namespace, domain_id, dataset_id, dataset_scope, content_hash FROM vector_chunks";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var docId = reader.GetString(1);
            var embedding = BlobToFloats((byte[])reader[2]);
            var caseNs = reader.IsDBNull(3) ? null : reader.GetString(3);
            var domainId = reader.IsDBNull(4) ? null : reader.GetString(4);
            var datasetId = reader.IsDBNull(5) ? null : reader.GetString(5);
            var datasetScope = reader.IsDBNull(6) ? null : reader.GetString(6);
            var hash = reader.GetString(7);

            if (_embeddingDim == 0) _embeddingDim = embedding.Length;

            _vectors[id] = embedding;
            _metaCache[id] = new ChunkMeta(
                docId,
                caseNs,
                NormalizeScopeValue(domainId),
                NormalizeScopeValue(datasetId),
                NormalizeScopeValue(datasetScope),
                hash);
            InsertIntoGraph(id, embedding);
            count++;

            if (count % 10000 == 0)
                _logger.LogInformation("Loaded {Count} vectors into HNSW graph...", count);
        }

        _logger.LogInformation("HNSW graph built with {Count} nodes", count);
    }

    private async Task FlushGraphAsync()
    {
        if (!_dirty) return;

        await _lock.WaitAsync();
        try
        {
            // Persistence of the graph structure is handled by SQLite.
            // The HNSW graph is rebuilt from DB on startup.
            // In the future, we can serialize the graph structure to _hnswPath for faster startup.
            _dirty = false;
            _logger.LogDebug("Graph state flushed");
        }
        finally
        {
            _lock.Release();
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  BINARY HELPERS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    private static byte[] FloatsToBlob(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static void EnsureColumnExists(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnType)
    {
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"PRAGMA table_info({tableName})";

        using var reader = checkCommand.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType}";
        alterCommand.ExecuteNonQuery();
    }

    private static string? NormalizeScopeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  HELPERS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    /// <summary>Comparer that allows duplicate keys in SortedList.</summary>
    private sealed class DuplicateKeyComparer : IComparer<double>
    {
        public int Compare(double x, double y)
        {
            int result = x.CompareTo(y);
            return result == 0 ? 1 : result; // never return 0 to allow duplicates
        }
    }

    private sealed record ChunkMeta(
        string DocumentId,
        string? CaseNamespace,
        string? DomainId,
        string? DatasetId,
        string? DatasetScope,
        string ContentHash);

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _lock.Dispose();
    }
}

