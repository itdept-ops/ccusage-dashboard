using System.Security.Cryptography;
using System.Text;

namespace Ccusage.Api.Services;

/// <summary>
/// Symmetric encryption for share tokens at rest. Keyed off the (stable) app JWT key so ciphertext
/// survives restarts — unlike ASP.NET Data Protection keys, which aren't persisted in the container.
/// A DB leak alone can't reveal live links without the app secret.
/// </summary>
public sealed class TokenProtector
{
    private readonly byte[] _key;

    public TokenProtector(IConfiguration config)
    {
        var k = config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required.");
        // Domain-separated 256-bit key derived from the app secret.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("usage-iq:share-token:" + k));
    }

    /// <summary>AES-GCM encrypt → base64(nonce(12) | tag(16) | ciphertext).</summary>
    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, pt, ct, tag);

        var blob = new byte[nonce.Length + tag.Length + ct.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(tag, 0, blob, 12, 16);
        Buffer.BlockCopy(ct, 0, blob, 28, ct.Length);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Reverse of <see cref="Protect"/>; returns null if missing or tampered/undecryptable.</summary>
    public string? Unprotect(string? blob64)
    {
        if (string.IsNullOrEmpty(blob64)) return null;
        try
        {
            var blob = Convert.FromBase64String(blob64);
            if (blob.Length < 28) return null;
            var nonce = blob.AsSpan(0, 12);
            var tag = blob.AsSpan(12, 16);
            var ct = blob.AsSpan(28);
            var pt = new byte[ct.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch
        {
            return null;
        }
    }
}
