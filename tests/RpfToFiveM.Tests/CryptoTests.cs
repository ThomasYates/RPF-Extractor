using System.Security.Cryptography;
using RpfToFiveM.Core.Crypto;
using RpfToFiveM.Tests.Fixtures;

namespace RpfToFiveM.Tests;

public class CryptoTests
{
    [Fact]
    public void JenkHash_MatchesKnownOneAtATimeValues()
    {
        Assert.Equal(0u, JenkHash.Hash(Array.Empty<byte>()));
        Assert.Equal(0xCA2E9442u, JenkHash.Hash("a"u8.ToArray()));
    }

    [Fact]
    public void AesDecrypt_RoundTripsFullBlocksAndLeavesTailUntouched()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = RandomNumberGenerator.GetBytes(16 * 5 + 7);

        var encrypted = RpfBuilder.AesEncrypt(plain, key);
        var decrypted = AesCrypto.Decrypt(encrypted, key);

        Assert.Equal(plain, decrypted);
        Assert.Equal(plain[^7..], encrypted[^7..]);
    }

    [Fact]
    public void AesDecrypt_DataShorterThanBlockIsReturnedAsIs()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var data = new byte[] { 1, 2, 3 };
        Assert.Equal(data, AesCrypto.Decrypt(data, key));
    }

    [Fact]
    public void FindAesKey_LocatesKeyBySha1Hash()
    {
        // The search matches on the SHA1 of each 8-byte aligned 32-byte window.
        var key = RandomNumberGenerator.GetBytes(32);
        var exe = RandomNumberGenerator.GetBytes(4096);
        key.CopyTo(exe, 1024);

        var found = GtaKeys.FindKeyByHash(exe, SHA1.HashData(key), 32);

        Assert.Equal(key, found);
    }

    [Fact]
    public void FindAesKey_ReturnsNullWhenAbsent()
    {
        var exe = RandomNumberGenerator.GetBytes(4096);
        Assert.Null(GtaKeys.FindKeyByHash(exe, SHA1.HashData(new byte[32]), 32));
    }

    [Fact]
    public void AesOnlyKeys_CannotDecryptNg()
    {
        var keys = GtaKeys.AesOnly(RandomNumberGenerator.GetBytes(32));
        Assert.False(keys.HasNgKeys);
    }

    [GameInstalledFact]
    public void RealGame_KeysLoadAndNgHeaderDecrypts()
    {
        var keys = GtaKeys.LoadFromGameFolder(GameInstalledFactAttribute.GameFolder!);
        Assert.True(keys.HasNgKeys);

        using var fs = File.OpenRead(Path.Combine(GameInstalledFactAttribute.GameFolder!, "common.rpf"));
        var archive = Core.Rpf.RpfArchive.Open(fs, "common.rpf", keys);
        Assert.Equal(Core.Rpf.RpfEncryption.Ng, archive.Encryption);
        Assert.Contains(archive.Files, f => f.Path.StartsWith("data/", StringComparison.OrdinalIgnoreCase));
    }
}
