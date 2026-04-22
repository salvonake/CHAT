using System.Text.RegularExpressions;
using LegalAI.Domain.Interfaces;
using LegalAI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using DomainQueryAnalysis = LegalAI.Domain.ValueObjects.QueryAnalysis;

namespace LegalAI.Retrieval.QueryAnalysis;

/// <summary>
/// Analyzes user queries to determine type, extract legal entities,
/// generate semantic variants, and normalize Arabic text for retrieval.
/// </summary>
public sealed partial class LegalQueryAnalyzer : IDomainQueryAnalyzer
{
    private readonly ILogger<LegalQueryAnalyzer> _logger;

    // Arabic query type patterns
    [GeneratedRegex(@"(?:ما هي|ماذا|ما هو|عرّف|تعريف)", RegexOptions.Compiled)]
    private static partial Regex FactLookupPattern();

    [GeneratedRegex(@"(?:اشرح|وضّح|فسّر|كيف|لماذا)", RegexOptions.Compiled)]
    private static partial Regex ExplanationPattern();

    [GeneratedRegex(@"(?:قارن|الفرق|مقارنة|بين\s+.+\s+و)", RegexOptions.Compiled)]
    private static partial Regex ComparisonPattern();

    [GeneratedRegex(@"(?:تسلسل|ترتيب|متى|تاريخ|زمني)", RegexOptions.Compiled)]
    private static partial Regex TimelinePattern();

    [GeneratedRegex(@"(?:سوابق|سابقة|قضية مشابهة|حكم مماثل)", RegexOptions.Compiled)]
    private static partial Regex PrecedentPattern();

    [GeneratedRegex(@"(?:المادة|مادة|الفصل|فصل|البند)\s*(?:رقم\s*)?([\d\u0660-\u0669]+)", RegexOptions.Compiled)]
    private static partial Regex ArticleDetection();

    [GeneratedRegex(@"(?:القضية|قضية|الدعوى)\s*(?:رقم\s*)?([\d\u0660-\u0669/\-]+)", RegexOptions.Compiled)]
    private static partial Regex CaseDetection();

    [GeneratedRegex(@"(?:محكمة|المحكمة)\s+([\u0600-\u06FF\s]+?)(?:\s|$)", RegexOptions.Compiled)]
    private static partial Regex CourtDetection();

    [GeneratedRegex(@"(\d{4})", RegexOptions.Compiled)]
    private static partial Regex YearDetection();

    [GeneratedRegex(@"(?:تناقض|تعارض|اختلاف|خلاف)", RegexOptions.Compiled)]
    private static partial Regex ContradictionPattern();

    public LegalQueryAnalyzer(ILogger<LegalQueryAnalyzer> logger)
    {
        _logger = logger;
    }

    public DomainQueryAnalysis Analyze(string query)
    {
        // Normalize the query
        var normalized = NormalizeQuery(query);

        // Detect query type
        var queryType = DetectQueryType(normalized);

        // Extract legal entities
        var articleMatch = ArticleDetection().Match(normalized);
        var caseMatch = CaseDetection().Match(normalized);
        var courtMatch = CourtDetection().Match(normalized);
        var dateMatch = YearDetection().Match(normalized);

        // Determine required depth
        var depth = DetermineDepth(queryType, normalized);

        // Generate semantic variants
        var variants = GenerateVariants(normalized, queryType);

        var analysis = new DomainQueryAnalysis
        {
            OriginalQuery = query,
            NormalizedQuery = normalized,
            SemanticVariants = variants,
            QueryType = queryType,
            RequiredDepth = depth,
            DetectedArticle = articleMatch.Success ? articleMatch.Value : null,
            DetectedCaseNumber = caseMatch.Success ? caseMatch.Groups[1].Value : null,
            DetectedCourt = courtMatch.Success ? courtMatch.Groups[1].Value.Trim() : null,
            DetectedDate = dateMatch.Success ? dateMatch.Groups[1].Value : null
        };

        _logger.LogDebug("Query analysis: Type={Type}, Depth={Depth}, Article={Article}, Case={Case}",
            queryType, depth, analysis.DetectedArticle, analysis.DetectedCaseNumber);

        return analysis;
    }

    private static QueryType DetectQueryType(string query)
    {
        if (PrecedentPattern().IsMatch(query)) return QueryType.PrecedentSearch;
        if (ComparisonPattern().IsMatch(query)) return QueryType.Comparison;
        if (TimelinePattern().IsMatch(query)) return QueryType.Timeline;
        if (ContradictionPattern().IsMatch(query)) return QueryType.ContradictionDetection;
        if (ExplanationPattern().IsMatch(query)) return QueryType.Explanation;
        if (ArticleDetection().IsMatch(query)) return QueryType.ArticleSearch;
        if (FactLookupPattern().IsMatch(query)) return QueryType.FactLookup;
        return QueryType.General;
    }

    private static QueryDepth DetermineDepth(QueryType type, string query)
    {
        return type switch
        {
            QueryType.FactLookup => QueryDepth.Brief,
            QueryType.ArticleSearch => QueryDepth.Standard,
            QueryType.Explanation => QueryDepth.Detailed,
            QueryType.Comparison => QueryDepth.Detailed,
            QueryType.Timeline => QueryDepth.Exhaustive,
            QueryType.PrecedentSearch => QueryDepth.Exhaustive,
            QueryType.ContradictionDetection => QueryDepth.Exhaustive,
            _ => query.Length > 100 ? QueryDepth.Detailed : QueryDepth.Standard
        };
    }

    private static List<string> GenerateVariants(string query, QueryType type)
    {
        var variants = new List<string> { query };

        // Generate semantic rephrasing variants based on query type
        switch (type)
        {
            case QueryType.PrecedentSearch:
                variants.Add(query.Replace("سوابق", "أحكام سابقة"));
                variants.Add(query.Replace("سابقة", "حكم قضائي مماثل"));
                break;

            case QueryType.ArticleSearch:
                // Add variant with expanded article reference
                variants.Add(query.Replace("المادة", "نص المادة القانونية"));
                variants.Add(query + " تطبيق تفسير");
                break;

            case QueryType.Comparison:
                variants.Add(query + " أوجه التشابه والاختلاف");
                break;

            case QueryType.Explanation:
                variants.Add(query.Replace("اشرح", "ما هو تفسير"));
                variants.Add(query + " التفاصيل والشرح");
                break;

            default:
                // Add expanded query
                if (query.Split(' ').Length < 5)
                {
                    variants.Add(query + " في القانون");
                    variants.Add(query + " حكم قضائي");
                }
                break;
        }

        return variants.Distinct().Take(3).ToList();
    }

    private static string NormalizeQuery(string query)
    {
        // Remove extra whitespace
        query = Regex.Replace(query.Trim(), @"\s+", " ");

        // Normalize Arabic characters (same normalization as indexing)
        // Remove tashkeel, normalize alef, etc.
        query = NormalizeArabicChars(query);

        return query;
    }

    private static string NormalizeArabicChars(string text)
    {
        // Simplified Arabic normalization matching the indexing normalizer
        var result = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            switch (c)
            {
                case '\u0622': // آ
                case '\u0623': // أ
                case '\u0625': // إ
                case '\u0671': // ٱ
                    result.Append('\u0627'); // ا
                    break;
                case '\u0640': // tatweel
                    break;
                case '\u0629': // ة
                    result.Append('\u0647'); // ه
                    break;
                case '\u0649': // ى
                    result.Append('\u064A'); // ي
                    break;
                default:
                    // Skip tashkeel range
                    if (c is not (>= '\u064B' and <= '\u065F') and not '\u0670')
                        result.Append(c);
                    break;
            }
        }

        return result.ToString();
    }
}
