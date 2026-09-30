using System.Buffers.Binary;

namespace RpfToFiveM.Core.Crypto;

/// <summary>
/// Decryption for the "NG" cipher used by retail GTA V archives: a 17-round
/// table-driven block cipher with one of 101 keys selected per archive/file.
/// </summary>
internal static class NgCrypto
{
    private const int KeyCount = 101;

    /// <summary>The game's filename hash used to pick an NG key.</summary>
    public static uint HashName(string name, byte[] lut)
    {
        uint result = 0;
        foreach (char c in name)
        {
            uint temp = 1025 * (lut[c & 0xFF] + result);
            result = (temp >> 6) ^ temp;
        }
        return 32769 * (((9 * result) >> 11) ^ (9 * result));
    }

    public static byte[] Decrypt(byte[] data, string name, uint length, GtaKeys keys)
    {
        uint index = (HashName(name, keys.HashLut!) + length + (KeyCount - 40)) % KeyCount;
        return Decrypt(data, keys.NgKeys![index], keys.NgTables!);
    }

    public static byte[] Decrypt(byte[] data, uint[] key, uint[][][] tables)
    {
        var output = (byte[])data.Clone();
        Span<byte> block = stackalloc byte[16];
        for (int offset = 0; offset + 16 <= output.Length; offset += 16)
        {
            var span = output.AsSpan(offset, 16);
            span.CopyTo(block);
            RoundA(block, key, 0, tables[0]);
            RoundA(block, key, 1, tables[1]);
            for (int r = 2; r <= 15; r++) RoundB(block, key, r, tables[r]);
            RoundA(block, key, 16, tables[16]);
            block.CopyTo(span);
        }
        return output;
    }

    // Rounds 1, 2 and 17.
    private static void RoundA(Span<byte> d, uint[] key, int round, uint[][] t)
    {
        int k = round * 4;
        uint x1 = t[0][d[0]] ^ t[1][d[1]] ^ t[2][d[2]] ^ t[3][d[3]] ^ key[k];
        uint x2 = t[4][d[4]] ^ t[5][d[5]] ^ t[6][d[6]] ^ t[7][d[7]] ^ key[k + 1];
        uint x3 = t[8][d[8]] ^ t[9][d[9]] ^ t[10][d[10]] ^ t[11][d[11]] ^ key[k + 2];
        uint x4 = t[12][d[12]] ^ t[13][d[13]] ^ t[14][d[14]] ^ t[15][d[15]] ^ key[k + 3];
        Write(d, x1, x2, x3, x4);
    }

    // Rounds 3 to 16.
    private static void RoundB(Span<byte> d, uint[] key, int round, uint[][] t)
    {
        int k = round * 4;
        uint x1 = t[0][d[0]] ^ t[7][d[7]] ^ t[10][d[10]] ^ t[13][d[13]] ^ key[k];
        uint x2 = t[1][d[1]] ^ t[4][d[4]] ^ t[11][d[11]] ^ t[14][d[14]] ^ key[k + 1];
        uint x3 = t[2][d[2]] ^ t[5][d[5]] ^ t[8][d[8]] ^ t[15][d[15]] ^ key[k + 2];
        uint x4 = t[3][d[3]] ^ t[6][d[6]] ^ t[9][d[9]] ^ t[12][d[12]] ^ key[k + 3];
        Write(d, x1, x2, x3, x4);
    }

    private static void Write(Span<byte> d, uint x1, uint x2, uint x3, uint x4)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, x1);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], x2);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], x3);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], x4);
    }
}
