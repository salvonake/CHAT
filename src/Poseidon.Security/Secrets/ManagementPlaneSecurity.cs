using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Poseidon.Security.Secrets;

public static class ManagementPlaneSecurity
{
    public const string TimestampHeader = "X-Poseidon-Timestamp";
    public const string NonceHeader = "X-Poseidon-Nonce";
    public const string SignatureHeader = "X-Poseidon-Signature";
    public const string KeyVersionHeader = "X-Poseidon-Key-Version";
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UsedNonces = new(StringComparer.Ordinal);

    public static void Sign(HttpRequestMessage request, string secret, string keyVersion, string role, DateTimeOffset? now = null)
    {
        now ??= DateTimeOffset.UtcNow;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var timestamp = now.Value.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = request.RequestUri?.PathAndQuery ?? "/";
        var signature = ComputeSignature(secret, request.Method.Method, path, timestamp, nonce, role);

        request.Headers.Remove(TimestampHeader);
        request.Headers.Remove(NonceHeader);
        request.Headers.Remove(SignatureHeader);
        request.Headers.Remove(KeyVersionHeader);
        request.Headers.Add(TimestampHeader, timestamp);
        request.Headers.Add(NonceHeader, nonce);
        request.Headers.Add(SignatureHeader, signature);
        request.Headers.Add(KeyVersionHeader, keyVersion);
    }

    public static bool Verify(
        HttpContext context,
        IEnumerable<(string Secret, string KeyVersion)> candidateSecrets,
        string role,
        out string failureReason)
    {
        failureReason = "";
        PruneNonces();

        var timestamp = context.Request.Headers[TimestampHeader].ToString();
        var nonce = context.Request.Headers[NonceHeader].ToString();
        var signature = context.Request.Headers[SignatureHeader].ToString();
        var keyVersion = context.Request.Headers[KeyVersionHeader].ToString();

        if (string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(signature))
        {
            failureReason = "missing-signed-auth-headers";
            return false;
        }

        if (!long.TryParse(timestamp, out var seconds))
        {
            failureReason = "invalid-timestamp";
            return false;
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (DateTimeOffset.UtcNow - issuedAt > ReplayWindow || issuedAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1))
        {
            failureReason = "timestamp-outside-window";
            return false;
        }

        var nonceKey = $"{role}:{keyVersion}:{nonce}";
        if (!UsedNonces.TryAdd(nonceKey, DateTimeOffset.UtcNow.Add(ReplayWindow)))
        {
            failureReason = "replayed-nonce";
            return false;
        }

        var path = context.Request.PathBase.Add(context.Request.Path).ToString() + context.Request.QueryString;
        foreach (var candidate in candidateSecrets)
        {
            if (!string.IsNullOrWhiteSpace(keyVersion) &&
                !string.Equals(candidate.KeyVersion, keyVersion, StringComparison.Ordinal))
            {
                continue;
            }

            var expected = ComputeSignature(candidate.Secret, context.Request.Method, path, timestamp, nonce, role);
            if (FixedTimeEquals(expected, signature))
                return true;
        }

        failureReason = "signature-mismatch";
        return false;
    }

    private static string ComputeSignature(
        string secret,
        string method,
        string pathAndQuery,
        string timestamp,
        string nonce,
        string role)
    {
        var canonical = $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{role}";
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return Convert.ToBase64String(HMACSHA256.HashData(key, data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        try
        {
            var expectedBytes = Convert.FromBase64String(expected);
            var actualBytes = Convert.FromBase64String(actual);
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void PruneNonces()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in UsedNonces)
        {
            if (item.Value < now)
                UsedNonces.TryRemove(item.Key, out _);
        }
    }
}
