using System.Text;
using System.Text.RegularExpressions;

namespace LegalAI.Ingestion.Arabic;

/// <summary>
/// Arabic text normalization for legal documents.
/// Handles tashkeel removal, hamza normalization, alef variants, and legal-specific patterns.
/// </summary>
public static partial class ArabicNormalizer
{
    // Tashkeel (diacritical marks) Unicode range
    private const string TashkeelPattern = @"[\u0610-\u061A\u064B-\u065F\u0670\u06D6-\u06DC\u06DF-\u06E4\u06E7\u06E8\u06EA-\u06ED]";

    // Tatweel (kashida) — decorative elongation
    private const char Tatweel = '\u0640';

    // Alef variants
    private const char AlefMadda = '\u0622';     // آ
    private const char AlefHamzaAbove = '\u0623'; // أ
    private const char AlefHamzaBelow = '\u0625'; // إ
    private const char AlefPlain = '\u0627';      // ا
    private const char AlefWasla = '\u0671';      // ٱ

    // Hamza variants
    private const char WawHamza = '\u0624';       // ؤ
    private const char YaHamza = '\u0626';        // ئ

    // Teh marbuta → Heh normalization
    private const char TehMarbuta = '\u0629';     // ة
    private const char Heh = '\u0647';            // ه

    // Alef Maksura → Ya
    private const char AlefMaksura = '\u0649';    // ى
    private const char Ya = '\u064A';             // ي

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

        // Expand common Arabic legal abbreviations
        text = text
            .Replace("م.", "المادة ")
            .Replace("ف.", "الفصل ")
            .Replace("ق.", "القانون ")
            .Replace("ج.", "الجزء ");

        // Remove common Arabic stop words that don't help retrieval
        var stopWords = new HashSet<string>
        {
            "في", "من", "على", "إلى", "عن", "مع", "هذا", "هذه",
            "ذلك", "تلك", "التي", "الذي", "هو", "هي", "أن", "ما",
            "لا", "قد", "كان", "كانت", "لم", "لن", "حتى", "بل"
        };

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = words.Where(w => !stopWords.Contains(w) || w.Length > 3);

        return string.Join(' ', filtered);
    }
}
