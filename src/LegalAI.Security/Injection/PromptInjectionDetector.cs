using System.Text.RegularExpressions;
using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace LegalAI.Security.Injection;

/// <summary>
/// Detects and sanitizes prompt injection attempts in user queries.
/// Covers both English and Arabic injection patterns.
/// </summary>
public sealed partial class PromptInjectionDetector : IInjectionDetector
{
    private readonly ILogger<PromptInjectionDetector> _logger;

    // English injection patterns
    private static readonly string[] EnglishPatterns =
    [
        @"ignore\s+(all\s+)?previous\s+instructions",
        @"ignore\s+above",
        @"disregard\s+(all\s+)?previous",
        @"forget\s+(everything|all|your\s+instructions)",
        @"you\s+are\s+now\s+a",
        @"act\s+as\s+if",
        @"pretend\s+(you|to\s+be)",
        @"new\s+instruction[s]?\s*:",
        @"system\s*:\s*",
        @"override\s+(system|instructions|rules)",
        @"bypass\s+(security|filter|rules)",
        @"jailbreak",
        @"do\s+anything\s+now",
        @"developer\s+mode",
        @"sudo\s+",
        @"output\s+(?:your|the|system)\s+(?:system\s+)?(?:instructions|prompt)",
        @"reveal\s+(your|system)\s+(instructions|prompt|rules)",
        @"what\s+are\s+your\s+instructions"
    ];

    // Arabic injection patterns
    private static readonly string[] ArabicPatterns =
    [
        @"تجاهل\s+(جميع\s+)?التعليمات\s+السابقة",
        @"انسَ\s+(كل|جميع)\s+التعليمات",
        @"أنت\s+الآن",
        @"تصرف\s+كـ?أنك",
        @"تعليمات\s+جديدة\s*:",
        @"تجاوز\s+(الأمان|القواعد|القيود)",
        @"اكشف\s+(تعليماتك|النظام)",
        @"ما\s+هي\s+تعليماتك"
    ];

    // URL and file path patterns
    private static readonly string[] DangerousPatterns =
    [
        @"https?://",
        @"ftp://",
        @"file://",
        @"\\\\[a-zA-Z]",  // UNC paths
        @"[a-zA-Z]:\\",    // Windows paths
        @"\.\./",           // Path traversal
        @"<script",
        @"javascript:",
        @"data:text"
    ];

    public PromptInjectionDetector(ILogger<PromptInjectionDetector> logger)
    {
        _logger = logger;
    }

    public InjectionDetectionResult Analyze(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new InjectionDetectionResult
            {
                SanitizedQuery = string.Empty,
                IsInjectionDetected = false,
                InjectionConfidence = 0
            };
        }

        var detectedPatterns = new List<string>();
        var sanitized = query;

        // Check English injection patterns
        foreach (var pattern in EnglishPatterns)
        {
            if (Regex.IsMatch(query, pattern, RegexOptions.IgnoreCase))
            {
                detectedPatterns.Add($"EN: {pattern}");
                sanitized = Regex.Replace(sanitized, pattern, "[BLOCKED]", RegexOptions.IgnoreCase);
            }
        }

        // Check Arabic injection patterns
        foreach (var pattern in ArabicPatterns)
        {
            if (Regex.IsMatch(query, pattern, RegexOptions.IgnoreCase))
            {
                detectedPatterns.Add($"AR: {pattern}");
                sanitized = Regex.Replace(sanitized, pattern, "[محظور]", RegexOptions.IgnoreCase);
            }
        }

        // Check dangerous patterns (URLs, file paths)
        foreach (var pattern in DangerousPatterns)
        {
            if (Regex.IsMatch(query, pattern, RegexOptions.IgnoreCase))
            {
                detectedPatterns.Add($"DANGER: {pattern}");
                sanitized = Regex.Replace(sanitized, pattern, "[REMOVED]", RegexOptions.IgnoreCase);
            }
        }

        // Compute injection confidence
        var confidence = detectedPatterns.Count switch
        {
            0 => 0.0,
            1 => 0.5,
            2 => 0.75,
            _ => 0.95
        };

        // Should block if confidence is high (2+ patterns detected)
        var shouldBlock = confidence >= 0.75;

        if (detectedPatterns.Count > 0)
        {
            _logger.LogWarning(
                "Injection detected: {Count} patterns, confidence: {Confidence:F2}, block: {Block}. Patterns: {Patterns}",
                detectedPatterns.Count, confidence, shouldBlock, string.Join(", ", detectedPatterns));
        }

        return new InjectionDetectionResult
        {
            SanitizedQuery = sanitized.Trim(),
            IsInjectionDetected = detectedPatterns.Count > 0,
            InjectionConfidence = confidence,
            DetectedPatterns = detectedPatterns,
            ShouldBlock = shouldBlock
        };
    }
}
