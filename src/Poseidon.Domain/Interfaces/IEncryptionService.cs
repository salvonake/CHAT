namespace Poseidon.Domain.Interfaces;

/// <summary>
/// Encryption service using AES-256-GCM with key hierarchy.
/// </summary>
public interface IEncryptionService
{
    byte[] Encrypt(byte[] plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData);
    byte[] Decrypt(byte[] ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData);
    string EncryptString(string plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData);
    string DecryptString(string ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData);
    byte[] ComputeHmac(byte[] data);
    bool VerifyHmac(byte[] data, byte[] hmac);
    bool IsEnabled { get; }
}

public enum EncryptionPurpose
{
    VectorData,
    AuditSigning,
    SessionToken
}

