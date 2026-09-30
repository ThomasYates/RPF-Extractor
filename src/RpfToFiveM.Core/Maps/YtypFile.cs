using System.Buffers.Binary;
using System.Numerics;

namespace RpfToFiveM.Core.Maps;

/// <summary>What the game knows about a model: its texture dictionary and bounds.</summary>
public sealed record ArchetypeInfo(uint Name, uint TextureDictionary, uint DrawableDictionary, uint AssetName,
    Vector3 BoundsMin, Vector3 BoundsMax)
{
    public Vector3 Size => BoundsMax - BoundsMin;
}

/// <summary>Reads archetype definitions from a .ytyp.</summary>
public static class YtypFile
{
    private const uint CMapTypesHash = 3649811809;
    private const int ArchetypesField = 24;

    // CBaseArchetypeDef, CTimeArchetypeDef and CMloArchetypeDef all begin with the base layout.
    private static readonly HashSet<uint> ArchetypeBlocks = new() { 2195127427, 1991296364, 273704021 };

    public static IReadOnlyList<ArchetypeInfo> Parse(byte[] rsc7)
    {
        var meta = MetaResource.Load(rsc7);
        var types = meta.Find(CMapTypesHash) ?? throw new InvalidDataException("No CMapTypes block — not a ytyp.");
        var data = meta.Data;

        ulong array = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(types.Offset + ArchetypesField));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(types.Offset + ArchetypesField + 8));
        var list = new List<ArchetypeInfo>(count);
        if (count == 0 || !meta.TryResolve(array, out var ptrBlock, out int ptrs) || ptrBlock.Hash != MetaResource.PointerBlock)
            return list;

        for (int i = 0; i < count && ptrs + i * 8 + 8 <= data.Length; i++)
        {
            ulong ptr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ptrs + i * 8));
            if (!meta.TryResolve(ptr, out var block, out int at) || !ArchetypeBlocks.Contains(block.Hash) || at + 144 > data.Length)
                continue;

            var a = data.AsSpan(at, 144);
            list.Add(new ArchetypeInfo(
                BinaryPrimitives.ReadUInt32LittleEndian(a[88..]),
                BinaryPrimitives.ReadUInt32LittleEndian(a[92..]),
                BinaryPrimitives.ReadUInt32LittleEndian(a[100..]),
                BinaryPrimitives.ReadUInt32LittleEndian(a[112..]),
                new Vector3(F(a, 32), F(a, 36), F(a, 40)),
                new Vector3(F(a, 48), F(a, 52), F(a, 56))));
        }
        return list;
    }

    private static float F(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadSingleLittleEndian(s[offset..]);
}

/// <summary>Looks inside .ydr models.</summary>
public static class DrawableInfo
{
    private const int LegacyDrawableVersion = 165;

    /// <summary>
    /// True/false when the model does/doesn't carry its own textures; null when the
    /// format isn't one we can read (e.g. Enhanced-edition models).
    /// </summary>
    public static bool? HasEmbeddedTextures(byte[] rsc7)
    {
        if (rsc7.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(rsc7) != 0x37435352) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(rsc7.AsSpan(4)) != LegacyDrawableVersion) return null;

        byte[] data;
        try { data = MetaResource.Inflate(rsc7.AsSpan(16)); }
        catch (InvalidDataException) { return null; }
        if (data.Length < 0x18) return null;

        // Drawable+0x10 -> ShaderGroup; ShaderGroup+0x08 -> embedded TextureDictionary.
        int shaderGroup = MetaResource.SystemOffset(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x10)), data.Length);
        if (shaderGroup < 0 || shaderGroup + 16 > data.Length) return null;
        return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(shaderGroup + 8)) != 0;
    }
}
