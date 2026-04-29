using System.Text;
using System.Text.RegularExpressions;

namespace Poseidon.Ingestion.Arabic;

/// <summary>
/// Arabic text normalization for legal documents.
/// Handles tashkeel removal, hamza normalization, alef variants, and legal-specific patterns.
/// </summary>
public static partial class ArabicNormalizer
{
    // Tashkeel (diacritical marks) Unicode range
    private const string TashkeelPattern = @"[\u0610-\u061A\u064B-\u065F\u0670\u06D6-\u06DC\u06DF-\u06E4\u06E7\u06E8\u06EA-\u06ED]";

    // Tatweel (kashida), decorative elongation.
    private const char Tatweel = '\u0640';

    // Alef variants
    private const char AlefMadda = '\u0622';     // Ø¢
    private const char AlefHamzaAbove = '\u0623'; // Ø£
    private const char AlefHamzaBelow = '\u0625'; // Ø¥
    private const char AlefPlain = '\u0627';      // Ø§
    private const char AlefWasla = '\u0671';      // Ù±

    // Hamza variants
    private const char WawHamza = '\u0624';       // Ø¤
    private const char YaHamza = '\u0626';        // Ø¦

    // Teh marbuta to Heh normalization.
    private const char TehMarbuta = '\u0629';     // Ø©
    private const char Heh = '\u0647';            // Ù‡

    // Alef Maksura to Ya.
    private const char AlefMaksura = '\u0649';    // Ù‰
    private const char Ya = '\u064A';             // ÙŠ

    [GeneratedRegex(TashkeelPattern)]
    private static partial Regex TashkeelRegex();

    /// <summary>
    /// Normalizes Arabic text for consistent embeddings and retrieval.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);

        // Remove tashkeel
        text = TashkeelRegex().Replace(text, "");

        foreach (var c in text)
        {
            switch (c)
            {
                // Normalize alef variants to plain alef
                case AlefMadda:
                case AlefHamzaAbove:
                case AlefHamzaBelow:
                case AlefWasla:
                    sb.Append(AlefPlain);
                    break;

                // Remove tatweel
                case Tatweel:
                    break;

                // Normalize teh marbuta to heh
                case TehMarbuta:
                    sb.Append(Heh);
                    break;

                // Normalize alef maksura to ya
                case AlefMaksura:
                    sb.Append(Ya);
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        // Collapse multiple spaces
        var result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();

        return result;
    }

    /// <summary>
    /// Detects if text is primarily Arabic.
    /// </summary>
    public static bool IsArabic(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var arabicCount = 0;
        var totalLetters = 0;

        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                totalLetters++;
                // Arabic Unicode blocks
                if (c is >= '\u0600' and <= '\u06FF' or
                    >= '\u0750' and <= '\u077F' or
                    >= '\uFB50' and <= '\uFDFF' or
                    >= '\uFE70' and <= '\uFEFF')
                {
                    arabicCount++;
                }
            }
        }

        return totalLetters > 0 && (double)arabicCount / totalLetters > 0.3;
    }

    /// <summary>
    /// Normalizes a query for retrieval. Applied to both indexing and search queries.
    /// </summary>
    public static string NormalizeForRetrieval(string text)
    {
        text = Normalize(text);

        // Expand common Arabic legal abbreviations using already-normalized forms.
        text = text
            .Replace("\u0645.", "\u0627\u0644\u0645\u0627\u062f\u0647 ")
            .Replace("\u0641.", "\u0627\u0644\u0641\u0635\u0644 ")
            .Replace("\u0642.", "\u0627\u0644\u0642\u0627\u0646\u0648\u0646 ")
            .Replace("\u062c.", "\u0627\u0644\u062c\u0632\u0621 ");

        // Remove common Arabic stop words that don't help retrieval
        var stopWords = new HashSet<string>
        {
            "\u0641\u064a", "\u0645\u0646", "\u0639\u0644\u0649", "\u0639\u0644\u064a", "\u0625\u0644\u0649", "\u0627\u0644\u0649",
            "\u0639\u0646", "\u0645\u0639", "\u0647\u0630\u0627", "\u0647\u0630\u0647", "\u0630\u0644\u0643", "\u062a\u0644\u0643",
            "\u0627\u0644\u062a\u064a", "\u0627\u0644\u0630\u064a", "\u0647\u0648", "\u0647\u064a", "\u0623\u0646", "\u0627\u0646",
            "\u0645\u0627", "\u0644\u0627", "\u0642\u062f", "\u0643\u0627\u0646", "\u0643\u0627\u0646\u062a", "\u0644\u0645",
            "\u0644\u0646", "\u062d\u062a\u0649", "\u062d\u062a\u064a", "\u0628\u0644"
        };

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = words.Where(w => !stopWords.Contains(w) || w.Length > 3);

        return string.Join(' ', filtered);
    }
}

