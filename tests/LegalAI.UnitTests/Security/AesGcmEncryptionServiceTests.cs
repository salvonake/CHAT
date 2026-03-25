using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LegalAI.Domain.Interfaces;
using LegalAI.Security.Encryption;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Security;

/// <summary>
/// Tests for <see cref="AesGcmEncryptionService"/>.
/// Verifies AES-256-GCM encryption/decryption, HMAC integrity, key hierarchy,
/// and disabled-mode passthrough behavior.
/// </summary>
public sealed class AesGcmEncryptionServiceTests
{
    private readonly ILogger<AesGcmEncryptionService> _logger =
        Mock.Of<ILogger<AesGcmEncryptionService>>();

    private AesGcmEncryptionService CreateEnabled(string passphrase = "TestLegalAI!Passphrase123")
        => new(passphrase, _logger, enabled: true);

    private AesGcmEncryptionService CreateDisabled()
        => new(null, _logger, enabled: false);

    // ═══════════════════════════════════════
    //  Enabled/Disabled state
    // ═══════════════════════════════════════

    [Fact]
    public void IsEnabled_WithPassphrase_ReturnsTrue()
    {
        var svc = CreateEnabled();
        svc.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_WithoutPassphrase_ReturnsFalse()
    {
        var svc = CreateDisabled();
        svc.IsEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsEnabled_NullOrEmptyPassphrase_ReturnsFalse(string? passphrase)
    {
        var svc = new AesGcmEncryptionService(passphrase, _logger, enabled: true);
        svc.IsEnabled.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  Byte-level encrypt/decrypt round-trip
    // ═══════════════════════════════════════

    [Theory]
    [InlineData(EncryptionPurpose.VectorData)]
    [InlineData(EncryptionPurpose.AuditSigning)]
    [InlineData(EncryptionPurpose.SessionToken)]
    public void EncryptDecrypt_Bytes_RoundTrip_AllPurposes(EncryptionPurpose purpose)
    {
        var svc = CreateEnabled();
        var plaintext = Encoding.UTF8.GetBytes("بسم الله الرحمن الرحيم — قانون العمل المادة 77");

        var ciphertext = svc.Encrypt(plaintext, purpose);
        var decrypted = svc.Decrypt(ciphertext, purpose);

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void EncryptDecrypt_Bytes_DefaultPurposeIsVectorData()
    {
        var svc = CreateEnabled();
        var plaintext = "Test data"u8.ToArray();

        var ciphertext = svc.Encrypt(plaintext);
        var decrypted = svc.Decrypt(ciphertext);

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var svc = CreateEnabled();
        var plaintext = Encoding.UTF8.GetBytes("Same data");

        var c1 = svc.Encrypt(plaintext);
        var c2 = svc.Encrypt(plaintext);

        // Different nonces → different ciphertexts
        c1.Should().NotEqual(c2);
    }

    [Fact]
    public void Encrypt_CiphertextLongerThanPlaintext()
    {
        var svc = CreateEnabled();
        var plaintext = new byte[100];

        var ciphertext = svc.Encrypt(plaintext);

        // nonce(12) + tag(16) + ciphertext(100) = 128
        ciphertext.Length.Should().Be(100 + 12 + 16);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var svc = CreateEnabled();
        var plaintext = Encoding.UTF8.GetBytes("Critical legal data");
        var ciphertext = svc.Encrypt(plaintext);

        // Tamper with ciphertext
        ciphertext[^1] ^= 0xFF;

        var act = () => svc.Decrypt(ciphertext);
        act.Should().Throw<Exception>(); // AES-GCM authentication failure
    }

    [Fact]
    public void Decrypt_TooShort_ThrowsCryptographicException()
    {
        var svc = CreateEnabled();
        var tooShort = new byte[10]; // Less than nonce + tag

        var act = () => svc.Decrypt(tooShort);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_WrongPurpose_Throws()
    {
        var svc = CreateEnabled();
        var plaintext = Encoding.UTF8.GetBytes("Under the wrong key");

        var ciphertext = svc.Encrypt(plaintext, EncryptionPurpose.VectorData);

        // Different purpose = different derived key → authentication failure
        var act = () => svc.Decrypt(ciphertext, EncryptionPurpose.AuditSigning);
        act.Should().Throw<Exception>();
    }

    // ═══════════════════════════════════════
    //  String-level encrypt/decrypt
    // ═══════════════════════════════════════

    [Fact]
    public void EncryptDecryptString_RoundTrip()
    {
        var svc = CreateEnabled();
        var original = "نص قانوني سري — المادة 45 من قانون الإجراءات الجزائية";

        var encrypted = svc.EncryptString(original);
        var decrypted = svc.DecryptString(encrypted);

        decrypted.Should().Be(original);
    }

    [Fact]
    public void EncryptString_ReturnsBase64()
    {
        var svc = CreateEnabled();
        var encrypted = svc.EncryptString("test");

        // Should be valid Base64
        var act = () => Convert.FromBase64String(encrypted);
        act.Should().NotThrow();
    }

    [Fact]
    public void EncryptString_NotSameAsPlaintext()
    {
        var svc = CreateEnabled();
        var original = "Secret legal text";
        var encrypted = svc.EncryptString(original);

        encrypted.Should().NotBe(original);
    }

    // ═══════════════════════════════════════
    //  Disabled mode — passthrough
    // ═══════════════════════════════════════

    [Fact]
    public void Encrypt_Disabled_ReturnsPlaintextUnchanged()
    {
        var svc = CreateDisabled();
        var plaintext = Encoding.UTF8.GetBytes("Not encrypted");

        var result = svc.Encrypt(plaintext);

        result.Should().Equal(plaintext);
    }

    [Fact]
    public void Decrypt_Disabled_ReturnsCiphertextUnchanged()
    {
        var svc = CreateDisabled();
        var data = Encoding.UTF8.GetBytes("Already plain");

        var result = svc.Decrypt(data);

        result.Should().Equal(data);
    }

    [Fact]
    public void EncryptString_Disabled_ReturnsSameString()
    {
        var svc = CreateDisabled();
        var original = "Plain text";

        svc.EncryptString(original).Should().Be(original);
    }

    [Fact]
    public void DecryptString_Disabled_ReturnsSameString()
    {
        var svc = CreateDisabled();
        var original = "Plain text";

        svc.DecryptString(original).Should().Be(original);
    }

    // ═══════════════════════════════════════
    //  HMAC integrity
    // ═══════════════════════════════════════

    [Fact]
    public void ComputeHmac_ProducesConsistentHash()
    {
        var svc = CreateEnabled();
        var data = Encoding.UTF8.GetBytes("Audit chain entry #42");

        var hmac1 = svc.ComputeHmac(data);
        var hmac2 = svc.ComputeHmac(data);

        hmac1.Should().Equal(hmac2);
    }

    [Fact]
    public void VerifyHmac_ValidData_ReturnsTrue()
    {
        var svc = CreateEnabled();
        var data = Encoding.UTF8.GetBytes("Valid audit entry");

        var hmac = svc.ComputeHmac(data);
        svc.VerifyHmac(data, hmac).Should().BeTrue();
    }

    [Fact]
    public void VerifyHmac_TamperedData_ReturnsFalse()
    {
        var svc = CreateEnabled();
        var data = Encoding.UTF8.GetBytes("Original data");
        var hmac = svc.ComputeHmac(data);

        var tampered = Encoding.UTF8.GetBytes("Tampered data");
        svc.VerifyHmac(tampered, hmac).Should().BeFalse();
    }

    [Fact]
    public void VerifyHmac_TamperedHmac_ReturnsFalse()
    {
        var svc = CreateEnabled();
        var data = Encoding.UTF8.GetBytes("Data");
        var hmac = svc.ComputeHmac(data);

        hmac[0] ^= 0xFF; // Flip a byte
        svc.VerifyHmac(data, hmac).Should().BeFalse();
    }

    [Fact]
    public void ComputeHmac_DifferentData_DifferentHmac()
    {
        var svc = CreateEnabled();
        var hmac1 = svc.ComputeHmac("Entry A"u8.ToArray());
        var hmac2 = svc.ComputeHmac("Entry B"u8.ToArray());

        hmac1.Should().NotEqual(hmac2);
    }

    // ═══════════════════════════════════════
    //  Key hierarchy isolation
    // ═══════════════════════════════════════

    [Fact]
    public void DifferentPassphrases_ProduceDifferentCiphertexts()
    {
        var svc1 = new AesGcmEncryptionService("passphrase-alpha", _logger);
        var svc2 = new AesGcmEncryptionService("passphrase-beta", _logger);
        var plaintext = "Same data"u8.ToArray();

        var c1 = svc1.Encrypt(plaintext);
        var c2 = svc2.Encrypt(plaintext);

        // Can't compare nonce-dependent ciphertexts directly,
        // but decryption with wrong key must fail
        var act = () => svc2.Decrypt(c1);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void SamePassphrase_CanDecryptEachOthers()
    {
        var pass = "shared-secret-42!";
        var svc1 = new AesGcmEncryptionService(pass, _logger);
        var svc2 = new AesGcmEncryptionService(pass, _logger);
        var plaintext = "Shared data"u8.ToArray();

        var ciphertext = svc1.Encrypt(plaintext);
        var decrypted = svc2.Decrypt(ciphertext);

        decrypted.Should().Equal(plaintext);
    }

    // ═══════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════

    [Fact]
    public void EncryptDecrypt_EmptyArray_RoundTrip()
    {
        var svc = CreateEnabled();
        var empty = Array.Empty<byte>();

        var encrypted = svc.Encrypt(empty);
        var decrypted = svc.Decrypt(encrypted);

        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void EncryptDecrypt_LargeData_RoundTrip()
    {
        var svc = CreateEnabled();
        var large = new byte[1_000_000]; // 1 MB
        Random.Shared.NextBytes(large);

        var encrypted = svc.Encrypt(large);
        var decrypted = svc.Decrypt(encrypted);

        decrypted.Should().Equal(large);
    }

    [Fact]
    public void EncryptDecryptString_EmptyString_RoundTrip()
    {
        var svc = CreateEnabled();

        var encrypted = svc.EncryptString("");
        var decrypted = svc.DecryptString(encrypted);

        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void EncryptDecryptString_UnicodeArabic_RoundTrip()
    {
        var svc = CreateEnabled();
        var arabic = "بِسْمِ اللَّهِ الرَّحْمَنِ الرَّحِيمِ ﷽ — المادة ٤٥";

        var encrypted = svc.EncryptString(arabic);
        var decrypted = svc.DecryptString(encrypted);

        decrypted.Should().Be(arabic);
    }
}
