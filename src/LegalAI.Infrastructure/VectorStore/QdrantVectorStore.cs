using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace LegalAI.Infrastructure.VectorStore;

/// <summary>
/// Qdrant vector store implementation.
/// Stores document chunk embeddings with rich legal metadata as payload.
/// Supports namespace-based case isolation via payload filtering.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;
    private readonly string _collectionName;
    private readonly int _embeddingDimension;

    public QdrantVectorStore(
        string host,
        int port,
        string collectionName,
        int embeddingDimension,
        ILogger<QdrantVectorStore> logger)
    {
        _client = new QdrantClient(host, port);
        _collectionName = collectionName;
        _embeddingDimension = embeddingDimension;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            var exists = collections.Any(c => c == _collectionName);

            if (!exists)
            {
                await _client.CreateCollectionAsync(
                    _collectionName,
                    new VectorParams
                    {
                        Size = (ulong)_embeddingDimension,
                        Distance = Distance.Cosine,
                        HnswConfig = new HnswConfigDiff
                        {
                            M = 16,
                            EfConstruct = 256
                        }
                    },
                    cancellationToken: ct);

                // Create payload indexes for common filter fields
                await _client.CreatePayloadIndexAsync(
                    _collectionName, "document_id",
                    PayloadSchemaType.Keyword, cancellationToken: ct);

                await _client.CreatePayloadIndexAsync(
                    _collectionName, "case_namespace",
                    PayloadSchemaType.Keyword, cancellationToken: ct);

                await _client.CreatePayloadIndexAsync(
                    _collectionName, "content_hash",
                    PayloadSchemaType.Keyword, cancellationToken: ct);

                await _client.CreatePayloadIndexAsync(
                    _collectionName, "article_reference",
                    PayloadSchemaType.Keyword, cancellationToken: ct);

                await _client.CreatePayloadIndexAsync(
                    _collectionName, "case_number",
                    PayloadSchemaType.Keyword, cancellationToken: ct);

                _logger.LogInformation(
                    "Created Qdrant collection '{Collection}' with {Dim}-dim vectors",
                    _collectionName, _embeddingDimension);
            }
            else
            {
                _logger.LogInformation("Qdrant collection '{Collection}' already exists", _collectionName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Qdrant collection");
            throw;
        }
    }

    public async Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        var points = chunks.Select(chunk => new PointStruct
        {
            Id = new PointId { Uuid = chunk.Id },
            Vectors = chunk.Embedding!,
            Payload =
            {
                ["document_id"] = chunk.DocumentId,
                ["content"] = chunk.Content,
                ["chunk_index"] = chunk.ChunkIndex,
                ["page_number"] = chunk.PageNumber,
                ["section_title"] = chunk.SectionTitle ?? "",
                ["article_reference"] = chunk.ArticleReference ?? "",
                ["case_number"] = chunk.CaseNumber ?? "",
                ["court_name"] = chunk.CourtName ?? "",
                ["case_date"] = chunk.CaseDate ?? "",
                ["case_namespace"] = chunk.CaseNamespace ?? "default",
                ["content_hash"] = chunk.ContentHash,
                ["token_count"] = chunk.TokenCount,
                ["source_file"] = chunk.SourceFileName
            }
        }).ToList();

        // Upsert in batches of 100
        const int batchSize = 100;
        for (var i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.Skip(i).Take(batchSize).ToList();
            await _client.UpsertAsync(_collectionName, batch, cancellationToken: ct);
        }

        _logger.LogDebug("Upserted {Count} vectors to Qdrant", chunks.Count);
    }

    public async Task<List<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding, int topK, double scoreThreshold = 0.0,
        string? caseNamespace = null, CancellationToken ct = default)
    {
        // Build filter for case namespace isolation
        Filter? filter = null;
        if (!string.IsNullOrEmpty(caseNamespace))
        {
            filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "case_namespace",
                            Match = new Match { Keyword = caseNamespace }
                        }
                    }
                }
            };
        }

        var results = await _client.SearchAsync(
            _collectionName,
            queryEmbedding,
            limit: (ulong)topK,
            scoreThreshold: (float)scoreThreshold,
            filter: filter,
            payloadSelector: true,
            cancellationToken: ct);

        return results.Select(r => new RetrievedChunk
        {
            SimilarityScore = r.Score,
            Chunk = new DocumentChunk
            {
                Id = r.Id.Uuid,
                DocumentId = GetPayloadString(r, "document_id"),
                Content = GetPayloadString(r, "content"),
                ChunkIndex = GetPayloadInt(r, "chunk_index"),
                PageNumber = GetPayloadInt(r, "page_number"),
                SectionTitle = GetPayloadStringOrNull(r, "section_title"),
                ArticleReference = GetPayloadStringOrNull(r, "article_reference"),
                CaseNumber = GetPayloadStringOrNull(r, "case_number"),
                CourtName = GetPayloadStringOrNull(r, "court_name"),
                CaseDate = GetPayloadStringOrNull(r, "case_date"),
                CaseNamespace = GetPayloadStringOrNull(r, "case_namespace"),
                ContentHash = GetPayloadString(r, "content_hash"),
                TokenCount = GetPayloadInt(r, "token_count"),
                SourceFileName = GetPayloadString(r, "source_file")
            }
        }).ToList();
    }

    public async Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "document_id",
                        Match = new Match { Keyword = documentId }
                    }
                }
            }
        };

        await _client.DeleteAsync(_collectionName, filter, cancellationToken: ct);
        _logger.LogDebug("Deleted vectors for document {DocumentId}", documentId);
    }

    public async Task<bool> ExistsByHashAsync(string contentHash, CancellationToken ct = default)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "content_hash",
                        Match = new Match { Keyword = contentHash }
                    }
                }
            }
        };

        var results = await _client.ScrollAsync(_collectionName, filter, limit: 1, cancellationToken: ct);
        return results.Result.Any();
    }

    public async Task<long> GetVectorCountAsync(CancellationToken ct = default)
    {
        var info = await _client.GetCollectionInfoAsync(_collectionName, ct);
        return (long)info.PointsCount;
    }

    public async Task<VectorStoreHealth> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await _client.GetCollectionInfoAsync(_collectionName, ct);
            return new VectorStoreHealth
            {
                IsHealthy = info.Status == CollectionStatus.Green,
                VectorCount = (long)info.PointsCount,
                IndexedSegments = (long)info.SegmentsCount,
                Status = info.Status.ToString()
            };
        }
        catch (Exception ex)
        {
            return new VectorStoreHealth
            {
                IsHealthy = false,
                Error = ex.Message
            };
        }
    }

    private static string GetPayloadString(ScoredPoint point, string key)
    {
        return point.Payload.TryGetValue(key, out var val) ? val.StringValue ?? "" : "";
    }

    private static string? GetPayloadStringOrNull(ScoredPoint point, string key)
    {
        if (point.Payload.TryGetValue(key, out var val))
        {
            var s = val.StringValue;
            return string.IsNullOrEmpty(s) ? null : s;
        }
        return null;
    }

    private static int GetPayloadInt(ScoredPoint point, string key)
    {
        return point.Payload.TryGetValue(key, out var val) ? (int)val.IntegerValue : 0;
    }
}
