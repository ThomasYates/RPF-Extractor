using System.Security.Cryptography;

namespace RpfToFiveM.Core.Crypto;

/// <summary>
/// AES-256 ECB as used by RPF7. Only whole 16-byte blocks are encrypted;
/// any trailing bytes are stored in the clear.
/// </summary>
public static class AesCrypto
{
    public static byte[] Decrypt(byte[] data, byte[] key)
    {
        var result = (byte[])data.Clone();
        int length = data.Length - data.Length % 16;
        if (length == 0) return result;

        using var aes = Aes.Create();
        aes.Key = key;
        aes.DecryptEcb(data.AsSpan(0, length), result.AsSpan(0, length), PaddingMode.None);
        return result;
    }
}
