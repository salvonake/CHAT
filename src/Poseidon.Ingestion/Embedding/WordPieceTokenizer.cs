using System.Collections.Concurrent;
using System.Text;

namespace Poseidon.Ingestion.Embedding;

/// <summary>
/// WordPiece tokenizer compatible with AraBERT, multilingual BERT, and
/// other Hugging Face transformer models that ship with a vocab.txt file.
///
/// This is a pure .NET implementation with no Python dependency.
///
/// Follows the original WordPiece algorithm:
///   1. Normalize and pre-tokenize (whitespace split)
///   2. For each word, greedily match the longest prefix in the vocab
///   3. Sub-word continuations are prefixed with "##"
///   4. Unknown tokens fall back to [UNK]
///
/// Thread-safe and reusable across multiple embedding calls.
/// </summary>
public sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _maxInputChars;
    private readonly string _unkToken;
    private readonly string _clsToken;
    private readonly string _sepToken;
    private readonly string _padToken;
    private readonly int _unkId;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;

    /// <summary>Total vocabulary size.</summary>
    public int VocabSize => _vocab.Count;

    /// <summary>
    /// Creates a WordPiece tokenizer from a vocab.txt file.
    /// Each line is one token, line number is the token ID.
    /// </summary>
    /// <param name="vocabPath">Path to vocab.txt (one token per line).</param>
    /// <param name="maxInputChars">Max characters per word before treating as [UNK].</param>
    /// <param name="unkToken">Unknown token string.</param>
    /// <param name="clsToken">Classification token (beginning of sequence).</param>
    /// <param name="sepToken">Separator token (end of sequence).</param>
    /// <param name="padToken">Padding token.</param>
    public WordPieceTokenizer(
        string vocabPath,
        int maxInputChars = 200,
        string unkToken = "[UNK]",
        string clsToken = "[CLS]",
        string sepToken = "[SEP]",
        string padToken = "[PAD]")
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Vocabulary file not found: {vocabPath}", vocabPath);

        _maxInputChars = maxInputChars;
        _unkToken = unkToken;
        _clsToken = clsToken;
        _sepToken = sepToken;
        _padToken = padToken;

        _vocab = LoadVocab(vocabPath);

        _unkId = _vocab.GetValueOrDefault(unkToken, 0);
        _clsId = _vocab.GetValueOrDefault(clsToken, 101);
        _sepId = _vocab.GetValueOrDefault(sepToken, 102);
        _padId = _vocab.GetValueOrDefault(padToken, 0);
    }

    /// <summary>
    /// Creates a WordPiece tokenizer from a pre-loaded vocabulary dictionary.
    /// Used for testing and scenarios where vocab is already in memory.
    /// </summary>
    public WordPieceTokenizer(
        Dictionary<string, int> vocab,
        int maxInputChars = 200,
        string unkToken = "[UNK]",
        string clsToken = "[CLS]",
        string sepToken = "[SEP]",
        string padToken = "[PAD]")
    {
        _vocab = vocab ?? throw new ArgumentNullException(nameof(vocab));
        _maxInputChars = maxInputChars;
        _unkToken = unkToken;
        _clsToken = clsToken;
        _sepToken = sepToken;
        _padToken = padToken;

        _unkId = _vocab.GetValueOrDefault(unkToken, 0);
        _clsId = _vocab.GetValueOrDefault(clsToken, 101);
        _sepId = _vocab.GetValueOrDefault(sepToken, 102);
        _padId = _vocab.GetValueOrDefault(padToken, 0);
    }

    /// <summary>
    /// Tokenizes text into token IDs suitable for BERT-family model input.
    /// Adds [CLS] at the start and [SEP] at the end.
    /// Truncates to <paramref name="maxLength"/> tokens.
    /// </summary>
    /// <param name="text">Input text (Arabic, English, or mixed).</param>
    /// <param name="maxLength">Maximum sequence length including [CLS] and [SEP].</param>
    /// <returns>Array of token IDs.</returns>
    public long[] Tokenize(string text, int maxLength = 512)
    {
        var tokens = new List<long>(maxLength) { _clsId };

        var words = PreTokenize(text);

        foreach (var word in words)
        {
            if (tokens.Count >= maxLength - 1) break;

            var subTokenIds = WordPieceTokenize(word);
            foreach (var id in subTokenIds)
            {
                if (tokens.Count >= maxLength - 1) break;
                tokens.Add(id);
            }
        }

        tokens.Add(_sepId);
        return tokens.ToArray();
    }

    /// <summary>
    /// Tokenizes text and returns both token IDs and the attention mask.
    /// Optionally pads to <paramref name="padToLength"/>.
    /// </summary>
    public (long[] InputIds, long[] AttentionMask) TokenizeWithMask(
        string text, int maxLength = 512, int? padToLength = null)
    {
        var inputIds = Tokenize(text, maxLength);
        var actualLength = inputIds.Length;
        var targetLength = padToLength ?? actualLength;

        if (targetLength < actualLength)
            targetLength = actualLength;

        var paddedIds = new long[targetLength];
        var attentionMask = new long[targetLength];

        Array.Copy(inputIds, paddedIds, actualLength);
        Array.Fill(attentionMask, 1L, 0, actualLength);

        // Remaining positions are already 0 (pad token) and 0 (no attention)
        // If pad token ID != 0, fill explicitly
        if (_padId != 0)
        {
            for (var i = actualLength; i < targetLength; i++)
                paddedIds[i] = _padId;
        }

        return (paddedIds, attentionMask);
    }

    /// <summary>
    /// Converts token IDs back to token strings (for debugging/display).
    /// </summary>
    public string[] IdsToTokens(long[] ids)
    {
        // Build reverse vocab lazily
        var reverseVocab = new Dictionary<int, string>(_vocab.Count);
        foreach (var kv in _vocab)
            reverseVocab.TryAdd(kv.Value, kv.Key);

        var tokens = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            tokens[i] = reverseVocab.GetValueOrDefault((int)ids[i], _unkToken);
        }
        return tokens;
    }

    //  Pre-tokenization

    /// <summary>
    /// Splits text into words for WordPiece processing.
    /// Handles Arabic, Latin, CJK, punctuation, and whitespace.
    /// </summary>
    private static List<string> PreTokenize(string text)
    {
        // Normalize whitespace
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return [];

        var words = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString().ToLowerInvariant());
                    current.Clear();
                }
                continue;
            }

            // Punctuation and special characters become separate tokens
            if (IsPunctuation(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString().ToLowerInvariant());
                    current.Clear();
                }
                words.Add(c.ToString());
                continue;
            }

            // CJK characters are each treated as a separate word
            if (IsCjkCharacter(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString().ToLowerInvariant());
                    current.Clear();
                }
                words.Add(c.ToString());
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            words.Add(current.ToString().ToLowerInvariant());

        return words;
    }

    //  WordPiece Algorithm

    /// <summary>
    /// Applies the WordPiece algorithm to a single pre-tokenized word.
    /// Returns token IDs. If the word cannot be fully decomposed, returns [UNK].
    /// </summary>
    private List<long> WordPieceTokenize(string word)
    {
        if (word.Length > _maxInputChars)
            return [_unkId];

        // Check if the whole word is in vocab first (common for Arabic words)
        if (_vocab.TryGetValue(word, out var wholeWordId))
            return [wholeWordId];

        var tokenIds = new List<long>();
        var start = 0;
        var isBad = false;

        while (start < word.Length)
        {
            var end = word.Length;
            long curId = -1;
            var found = false;

            while (start < end)
            {
                var substr = word[start..end];
                if (start > 0)
                    substr = "##" + substr;

                if (_vocab.TryGetValue(substr, out var id))
                {
                    curId = id;
                    found = true;
                    break;
                }

                end--;
            }

            if (!found)
            {
                isBad = true;
                break;
            }

            tokenIds.Add(curId);
            start = end;
        }

        if (isBad)
            return [_unkId];

        return tokenIds;
    }

    //  Vocab Loading

    private static Dictionary<string, int> LoadVocab(string path)
    {
        var vocab = new Dictionary<string, int>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);

        for (var i = 0; i < lines.Length; i++)
        {
            var token = lines[i].TrimEnd();
            if (!string.IsNullOrEmpty(token))
                vocab[token] = i;
        }

        return vocab;
    }

    //  Character Classification

    private static bool IsPunctuation(char c)
    {
        // ASCII punctuation ranges
        if (c is (>= '!' and <= '/') or (>= ':' and <= '@')
            or (>= '[' and <= '`') or (>= '{' and <= '~'))
            return true;

        // Unicode punctuation categories
        var cat = char.GetUnicodeCategory(c);
        return cat is System.Globalization.UnicodeCategory.ConnectorPunctuation
            or System.Globalization.UnicodeCategory.DashPunctuation
            or System.Globalization.UnicodeCategory.OpenPunctuation
            or System.Globalization.UnicodeCategory.ClosePunctuation
            or System.Globalization.UnicodeCategory.InitialQuotePunctuation
            or System.Globalization.UnicodeCategory.FinalQuotePunctuation
            or System.Globalization.UnicodeCategory.OtherPunctuation;
    }

    private static bool IsCjkCharacter(char c)
    {
        // CJK Unified Ideographs and extensions
        return c is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF');
    }
}

