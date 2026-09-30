namespace RpfToFiveM.Core.Crypto;

/// <summary>Jenkins one-at-a-time hash, as used throughout RAGE.</summary>
public static class JenkHash
{
    public static uint Hash(ReadOnlySpan<byte> data)
    {
        uint h = 0;
        foreach (var b in data)
        {
            h += b;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }
}
