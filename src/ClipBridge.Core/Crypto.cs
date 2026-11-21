using System.Security.Cryptography;
using System.Text;

namespace ClipBridge.Core;

internal static class Crypto
{
    private static readonly byte[] Magic = "CB01"u8.ToArray();
    internal const int Iterations = 210_000;
    internal const int MaxEncryptedBytes = 12 * 1024 * 1024;

    internal static byte[] Derive(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);

    internal static byte[] Encrypt(byte[] plaintext, byte[] key, string purpose)
    {
        var result = new byte[32 + plaintext.Length];
        Magic.CopyTo(result, 0);
        RandomNumberGenerator.Fill(result.AsSpan(4, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(4, 12), plaintext, result.AsSpan(32), result.AsSpan(16, 16), Encoding.UTF8.GetBytes(purpose));
        return result;
    }

    internal static byte[] Decrypt(byte[] ciphertext, byte[] key, string purpose)
    {
        if (ciphertext.Length < 32 || !ciphertext.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("暗号化ファイルの形式が不正です。");
        var plaintext = new byte[ciphertext.Length - 32];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(ciphertext.AsSpan(4, 12), ciphertext.AsSpan(32), ciphertext.AsSpan(16, 16), plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }
}
