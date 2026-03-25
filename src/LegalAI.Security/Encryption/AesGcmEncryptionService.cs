using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace LegalAI.Security.Encryption;

/// <summary>
/// AES-256-GCM encryption with HKDF-derived key hierarchy.
/// 
/// Key Hierarchy:
///   Master Key (derived from passphrase via Argon2id)
///     → Vector Encryption Key (HKDF, info="vector-encryption")
///     → Audit Signing Key    (HKDF, info="audit-signing")
///     → Session HMAC Key     (HKDF, info="session-hmac")
/// </summary>
public sealed class AesGcmEncryptionService : IEncryptionService
{
    private readonly byte[] _vectorKey;
    private readonly byte[] _auditKey;
    private readonly byte[] _sessionKey;
    private readonly byte[] _hmacKey;
    private readonly ILogger<AesGcmEncryptionService> _logger;

    private const int NonceSize = 12; // 96 bits for AES-GCM
    private const int TagSize = 16;   // 128 bits
    private const int KeySize = 32;   // 256 bits

    public bool IsEnabled { get; }

    /// <summary>
    /// Creates an encryption service with a passphrase-derived master key.
    /// </summary>
    public AesGcmEncryptionService(
        string? passphrase,
        ILogger<AesGcmEncryptionService> logger,
        bool enabled = true)
    {
        _logger = logger;
        IsEnabled = enabled && !string.IsNullOrEmpty(passphrase);

        if (!IsEnabled)
        {
            _vectorKey = _auditKey = _sessionKey = _hmacKey = Array.Empty<byte>();
            _logger.LogWarning("Encryption is DISABLED. Vector store and audit logs will not be encrypted.");
            return;
        }

        // Derive master key from passphrase using Argon2id
        var masterKey = DeriveMasterKey(passphrase!);

        // Derive sub-keys using HKDF
        _vectorKey = DeriveSubKey(masterKey, "vector-encryption");
        _auditKey = DeriveSubKey(masterKey, "audit-signing");
        _sessionKey = DeriveSubKey(masterKey, "session-hmac");
        _hmacKey = DeriveSubKey(masterKey, "hmac-integrity");

        // Clear master key from memory
        CryptographicOperations.ZeroMemory(masterKey);

        _logger.LogInformation("Encryption initialized with HKDF key hierarchy.");
    }

    public byte[] Encrypt(byte[] plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
    {
        if (!IsEnabled) return plaintext;

        var key = GetKeyForPurpose(purpose);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Output: [nonce (12)] [tag (16)] [ciphertext]
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);

        return result;
    }

    public byte[] Decrypt(byte[] ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
    {
        if (!IsEnabled) return ciphertext;

        if (ciphertext.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid ciphertext: too short");

        var key = GetKeyForPurpose(purpose);

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var encrypted = new byte[ciphertext.Length - NonceSize - TagSize];

        Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(ciphertext, NonceSize + TagSize, encrypted, 0, encrypted.Length);

        var plaintext = new byte[encrypted.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, encrypted, tag, plaintext);

        return plaintext;
    }

    public string EncryptString(string plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
    {
        if (!IsEnabled) return plaintext;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = Encrypt(bytes, purpose);
        return Convert.ToBase64String(encrypted);
    }

    public string DecryptString(string ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
    {
        if (!IsEnabled) return ciphertext;
        var bytes = Convert.FromBase64String(ciphertext);
        var decrypted = Decrypt(bytes, purpose);
        return Encoding.UTF8.GetString(decrypted);
    }

    public byte[] ComputeHmac(byte[] data)
    {
        return HMACSHA256.HashData(_hmacKey, data);
    }

    public bool VerifyHmac(byte[] data, byte[] hmac)
    {
        var computed = ComputeHmac(data);
        return CryptographicOperations.FixedTimeEquals(computed, hmac);
    }

    private byte[] GetKeyForPurpose(EncryptionPurpose purpose)
    {
        return purpose switch
        {
            EncryptionPurpose.VectorData => _vectorKey,
            EncryptionPurpose.AuditSigning => _auditKey,
            EncryptionPurpose.SessionToken => _sessionKey,
            _ => _vectorKey
        };
    }

    /// <summary>
    /// Derives master key from passphrase using Argon2id.
    /// Parameters chosen for security on modern hardware.
    /// </summary>
    private static byte[] DeriveMasterKey(string passphrase)
    {
        var salt = DeterministicSalt(passphrase);

        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt = salt,
            DegreeOfParallelism = 4,
            MemorySize = 65536,   // 64 MB
            Iterations = 3
        };

        return argon2.GetBytes(KeySize);
    }

    /// <summary>
    /// Derives a sub-key from the master key using HKDF.
    /// </summary>
    private static byte[] DeriveSubKey(byte[] masterKey, string info)
    {
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            KeySize,
            info: Encoding.UTF8.GetBytes(info));
    }

    /// <summary>
    /// Generates a deterministic salt from passphrase.
    /// In production, store a random salt alongside the encrypted vault.
    /// </summary>
    private static byte[] DeterministicSalt(string passphrase)
    {
        return SHA256.HashData(
            Encoding.UTF8.GetBytes($"LegalAI-Salt-{passphrase.Length}"));
    }
}
