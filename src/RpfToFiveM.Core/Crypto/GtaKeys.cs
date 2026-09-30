using System.IO.Compression;
using System.Security.Cryptography;

namespace RpfToFiveM.Core.Crypto;

/// <summary>
/// Decryption keys for GTA V archives. No key material ships with the tool:
/// the AES key is located inside the user's own game executable by its SHA1
/// hash, and it then unlocks the bundled (encrypted) NG key tables.
/// </summary>
public sealed class GtaKeys
{
    private const int NgKeyCount = 101;
    private const int NgKeyLength = 272;
    private const int NgKeysBytes = NgKeyCount * NgKeyLength; // 27472
    private const int NgTablesBytes = 17 * 16 * 256 * 4;      // 278528
    private const int LutBytes = 256;

    // SHA1 of the PC AES key (not the key itself).
    internal static readonly byte[] AesKeyHash =
    {
        0xA0, 0x79, 0x61, 0x28, 0xA7, 0x75, 0x72, 0x0A, 0xC2, 0x04,
        0xD9, 0x81, 0x9F, 0x68, 0xC1, 0x72, 0xE3, 0x95, 0x2C, 0x6D,
    };

    public byte[] AesKey { get; }
    internal uint[][]? NgKeys { get; }
    internal uint[][][]? NgTables { get; }
    internal byte[]? HashLut { get; }

    public bool HasNgKeys => NgKeys is not null && NgTables is not null && HashLut is not null;

    private GtaKeys(byte[] aesKey, uint[][]? ngKeys, uint[][][]? ngTables, byte[]? hashLut)
    {
        AesKey = aesKey;
        NgKeys = ngKeys;
        NgTables = ngTables;
        HashLut = hashLut;
    }

    /// <summary>Keys that can only handle AES archives (used for tests and AES-only mods).</summary>
    public static GtaKeys AesOnly(byte[] aesKey) => new(aesKey, null, null, null);

    /// <summary>Returns true when the given bytes are the genuine PC AES key.</summary>
    public static bool IsValidAesKey(byte[] key) =>
        key.Length == 32 && SHA1.HashData(key).AsSpan().SequenceEqual(AesKeyHash);

    /// <summary>Scans the game folder's executable for the AES key and builds the full key set.</summary>
    public static GtaKeys LoadFromGameFolder(string gameFolder)
    {
        var exe = GameLocator.FindExecutable(gameFolder)
            ?? throw new FileNotFoundException($"No GTA5.exe or GTA5_Enhanced.exe found in \"{gameFolder}\".");
        var key = FindKeyByHash(File.ReadAllBytes(exe), AesKeyHash, 32)
            ?? throw new InvalidDataException($"Could not find the archive key inside {Path.GetFileName(exe)}.");
        return FromAesKey(key);
    }

    /// <summary>Builds the full key set (AES + NG) from a known-good AES key.</summary>
    public static GtaKeys FromAesKey(byte[] aesKey)
    {
        if (!IsValidAesKey(aesKey))
            throw new ArgumentException("The supplied key is not the GTA V PC AES key.", nameof(aesKey));

        var magic = ReadMagic();

        // Undo the byte obfuscation layered on top of the AES encryption.
        var rnd = new Random((int)JenkHash.Hash(aesKey));
        var noise = new byte[4][];
        for (int n = 0; n < 4; n++)
        {
            noise[n] = new byte[magic.Length];
            rnd.NextBytes(noise[n]);
        }
        for (int i = 0; i < magic.Length; i++)
            magic[i] = (byte)(magic[i] - noise[0][i] - noise[1][i] - noise[2][i] - noise[3][i]);

        var inflated = Inflate(AesCrypto.Decrypt(magic, aesKey));
        if (inflated.Length < NgKeysBytes + NgTablesBytes + LutBytes)
            throw new InvalidDataException("Key data is truncated.");

        var ngKeys = new uint[NgKeyCount][];
        for (int i = 0; i < NgKeyCount; i++)
        {
            ngKeys[i] = new uint[NgKeyLength / 4];
            Buffer.BlockCopy(inflated, i * NgKeyLength, ngKeys[i], 0, NgKeyLength);
        }

        var tables = new uint[17][][];
        int pos = NgKeysBytes;
        for (int r = 0; r < 17; r++)
        {
            tables[r] = new uint[16][];
            for (int t = 0; t < 16; t++)
            {
                tables[r][t] = new uint[256];
                Buffer.BlockCopy(inflated, pos, tables[r][t], 0, 1024);
                pos += 1024;
            }
        }

        var lut = inflated.AsSpan(pos, LutBytes).ToArray();
        return new GtaKeys(aesKey, ngKeys, tables, lut);
    }

    /// <summary>
    /// Finds a block of <paramref name="length"/> bytes whose SHA1 equals <paramref name="sha1"/>,
    /// checking every 8-byte aligned position.
    /// </summary>
    internal static byte[]? FindKeyByHash(byte[] data, byte[] sha1, int length)
    {
        const int Align = 8;
        const int ChunkPositions = 1 << 16;
        long positions = (data.Length - length) / Align + 1;
        if (positions <= 0) return null;

        byte[]? found = null;
        long chunks = (positions + ChunkPositions - 1) / ChunkPositions;
        Parallel.For(0L, chunks, (chunk, state) =>
        {
            Span<byte> hash = stackalloc byte[20];
            long start = chunk * ChunkPositions;
            long end = Math.Min(start + ChunkPositions, positions);
            for (long p = start; p < end && found is null; p++)
            {
                var window = data.AsSpan((int)(p * Align), length);
                SHA1.HashData(window, hash);
                if (hash.SequenceEqual(sha1))
                {
                    found = window.ToArray();
                    state.Stop();
                }
            }
        });
        return found;
    }

    private static byte[] ReadMagic()
    {
        using var s = typeof(GtaKeys).Assembly.GetManifestResourceStream("RpfToFiveM.Core.magic.dat")
            ?? throw new InvalidOperationException("Embedded key data is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        using var ds = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        ds.CopyTo(ms);
        return ms.ToArray();
    }
}
