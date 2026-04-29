using System.Collections.Concurrent;

namespace Poseidon.Retrieval.Lexical;

/// <summary>
/// BM25 sparse lexical search implementation for hybrid retrieval.
/// Maintains an in-memory inverted index of indexed chunks.
/// </summary>
public sealed class BM25Index
{
    private readonly ConcurrentDictionary<string, Dictionary<string, double>> _invertedIndex = new();
    private readonly ConcurrentDictionary<string, int> _docLengths = new();
    private double _avgDocLength;
    private int _docCount;

    // BM25 parameters
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>
    /// Adds a document (chunk) to the BM25 index.
    /// </summary>
    public void AddDocument(string docId, string text)
    {
        var terms = Tokenize(text);
        var termFrequencies = new Dictionary<string, int>();

        foreach (var term in terms)
        {
            termFrequencies.TryGetValue(term, out var count);
            termFrequencies[term] = count + 1;
        }

        _docLengths[docId] = terms.Length;

        foreach (var (term, freq) in termFrequencies)
        {
            var tfNorm = (double)freq / terms.Length;
            _invertedIndex.AddOrUpdate(
                term,
                _ => new Dictionary<string, double> { { docId, tfNorm } },
                (_, existing) => { existing[docId] = tfNorm; return existing; });
        }

        _docCount = _docLengths.Count;
        _avgDocLength = _docLengths.Values.Average();
    }

    /// <summary>
    /// Removes a document from the BM25 index.
    /// </summary>
    public void RemoveDocument(string docId)
    {
        _docLengths.TryRemove(docId, out _);

        foreach (var entry in _invertedIndex)
        {
            entry.Value.Remove(docId);
        }

        if (_docLengths.Count > 0)
        {
            _docCount = _docLengths.Count;
            _avgDocLength = _docLengths.Values.Average();
        }
    }

    /// <summary>
    /// Searches the BM25 index and returns scored results.
    /// </summary>
    public List<(string DocId, double Score)> Search(string query, int topK)
    {
        if (_docCount == 0) return [];

        var queryTerms = Tokenize(query);
        var scores = new Dictionary<string, double>();

        foreach (var term in queryTerms)
        {
            if (!_invertedIndex.TryGetValue(term, out var postings))
                continue;

            // IDF: log((N - n + 0.5) / (n + 0.5) + 1)
            var n = postings.Count;
            var idf = Math.Log((_docCount - n + 0.5) / (n + 0.5) + 1.0);

            foreach (var (docId, tf) in postings)
            {
                if (!_docLengths.TryGetValue(docId, out var docLen))
                    continue;

                // BM25 score
                var tfComponent = (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * docLen / _avgDocLength));
                var score = idf * tfComponent;

                scores.TryGetValue(docId, out var existing);
                scores[docId] = existing + score;
            }
        }

        return scores
            .OrderByDescending(kvp => kvp.Value)
            .Take(topK)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    /// <summary>
    /// Gets the number of documents in the index.
    /// </summary>
    public int DocumentCount => _docCount;

    private static string[] Tokenize(string text)
    {
        // Simple word tokenization with Arabic-aware splitting
        return text
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1) // Filter single char tokens
            .ToArray();
    }
}

