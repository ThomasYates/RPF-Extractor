using System.Buffers.Binary;
using System.Numerics;

namespace RpfToFiveM.Tests.Fixtures;

/// <summary>Test-only writer for .ytyp resources: CMapTypes with base archetype definitions.</summary>
internal sealed class YtypBuilder
{
    public const uint CMapTypesHash = 3649811809;
    public const uint CBaseArchetypeDefHash = 2195127427;

    private readonly string _name;
    private readonly List<(string Name, string Txd, Vector3 Min, Vector3 Max)> _archetypes = new();

    public YtypBuilder(string name) => _name = name;

    /// <param name="txd">Texture dictionary name; empty for none.</param>
    public YtypBuilder Add(string name, string txd, Vector3 bbMin, Vector3 bbMax)
    {
        _archetypes.Add((name, txd, bbMin, bbMax));
        return this;
    }

    public YtypBuilder Add(string name, string txd, float size = 10) =>
        Add(name, txd, new Vector3(-size / 2, -size / 2, 0), new Vector3(size / 2, size / 2, size));

    public byte[] Build()
    {
        const int BlockCount = 3;
        int table = 0x70;
        int typesPos = Align16(table + BlockCount * 16);
        int ptrsPos = Align16(typesPos + 80);
        int defsPos = Align16(ptrsPos + Math.Max(1, _archetypes.Count) * 8);
        int total = Align16(defsPos + Math.Max(1, _archetypes.Count) * 144);
        var d = new byte[total];

        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x1C), 1);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0x30), 0x50000000 + table);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(0x4C), BlockCount);
        Block(d, table, 0, CMapTypesHash, 80, typesPos);
        Block(d, table, 1, YmapBuilder.PointerHash, _archetypes.Count * 8, ptrsPos);
        Block(d, table, 2, CBaseArchetypeDefHash, _archetypes.Count * 144, defsPos);

        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(typesPos + 24), 2); // archetypes -> block 2
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(typesPos + 32), (ushort)_archetypes.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(typesPos + 34), (ushort)_archetypes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(typesPos + 40), YmapBuilder.Hash(_name));

        for (int i = 0; i < _archetypes.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(ptrsPos + i * 8), (ulong)((i * 144) << 12 | 3));
            var (name, txd, min, max) = _archetypes[i];
            var s = d.AsSpan(defsPos + i * 144, 144);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], 100);
            BinaryPrimitives.WriteSingleLittleEndian(s[32..], min.X);
            BinaryPrimitives.WriteSingleLittleEndian(s[36..], min.Y);
            BinaryPrimitives.WriteSingleLittleEndian(s[40..], min.Z);
            BinaryPrimitives.WriteSingleLittleEndian(s[48..], max.X);
            BinaryPrimitives.WriteSingleLittleEndian(s[52..], max.Y);
            BinaryPrimitives.WriteSingleLittleEndian(s[56..], max.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(s[88..], YmapBuilder.Hash(name));
            BinaryPrimitives.WriteUInt32LittleEndian(s[92..], txd.Length == 0 ? 0 : YmapBuilder.Hash(txd));
            BinaryPrimitives.WriteUInt32LittleEndian(s[112..], YmapBuilder.Hash(name));
        }
        return RpfBuilder.BuildRsc7(2, 0x20000001, 0x20000000, d);
    }

    public void WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Build());
    }

    /// <summary>A minimal legacy (v165) .ydr whose shader group does or doesn't embed textures.</summary>
    public static byte[] Drawable(bool embeddedTextures)
    {
        var d = new byte[0x100];
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x10), 0x50000040);          // ShaderGroupPointer
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x48), embeddedTextures ? 0x50000080UL : 0); // TextureDictionaryPointer
        return RpfBuilder.BuildRsc7(165, 0xA0000001, 0x50000000, d);
    }

    private static void Block(byte[] d, int table, int index, uint hash, int length, int pos)
    {
        var s = d.AsSpan(table + index * 16, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(s, hash);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], length);
        BinaryPrimitives.WriteInt64LittleEndian(s[8..], 0x50000000 + pos);
    }

    private static int Align16(int v) => (v + 15) & ~15;
}
