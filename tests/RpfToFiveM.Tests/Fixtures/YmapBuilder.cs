using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using RpfToFiveM.Core.Crypto;

namespace RpfToFiveM.Tests.Fixtures;

/// <summary>
/// Test-only writer for .ymap resources (RSC7 "meta" format) with just the parts the
/// z-fight checker reads: CMapData name/parent and the entity list.
/// </summary>
internal sealed class YmapBuilder
{
    public const uint CMapDataHash = 3545841574;
    public const uint CEntityDefHash = 3461354627;
    public const uint PointerHash = 7;
    public const uint GrassDataHash = 3985044770;

    private readonly string _name;
    private string? _parent;
    private readonly List<Entity> _entities = new();

    public sealed record Entity(uint Archetype, Vector3 Position, Quaternion Rotation, float ScaleXY, float ScaleZ,
        int ParentIndex, int LodLevel, uint NumChildren, float LodDist);

    public YmapBuilder(string name) => _name = name;

    public static uint Hash(string s) => JenkHash.Hash(Encoding.ASCII.GetBytes(s.ToLowerInvariant()));

    private uint _flags;
    private int _grassBatches;
    private int _grassBytes;
    private ushort _carGens, _timecycles, _boxOccluders;

    /// <summary>Instanced grass stored in the graphics segment (0x6... addresses), like real grass ymaps.</summary>
    public YmapBuilder Grass(int batches, int bytes = 4096)
    {
        _grassBatches = batches;
        _grassBytes = bytes;
        return this;
    }

    public YmapBuilder CarGenerators(int count) { _carGens = (ushort)count; return this; }
    public YmapBuilder TimecycleModifiers(int count) { _timecycles = (ushort)count; return this; }
    public YmapBuilder BoxOccluders(int count) { _boxOccluders = (ushort)count; return this; }

    /// <summary>Marks the ymap as script-loaded (an IPL state such as burnt/unburnt).</summary>
    public YmapBuilder Scripted()
    {
        _flags |= 1;
        return this;
    }

    public YmapBuilder Parent(string parentYmap)
    {
        _parent = parentYmap;
        return this;
    }

    public YmapBuilder Add(string archetype, float x, float y, float z, int lodLevel = 0, int parentIndex = -1,
        uint numChildren = 0, Quaternion? rotation = null, float lodDist = 100)
    {
        _entities.Add(new Entity(Hash(archetype), new Vector3(x, y, z), rotation ?? Quaternion.Identity, 1, 1,
            parentIndex, lodLevel, numChildren, lodDist));
        return this;
    }

    public byte[] Build()
    {
        const int MetaHeader = 0x70;
        int BlockCount = _grassBatches > 0 ? 4 : 3;
        int blocksTable = MetaHeader;
        int mapDataPos = Align16(blocksTable + BlockCount * 16);
        int pointersPos = Align16(mapDataPos + 512);
        int entitiesPos = Align16(pointersPos + Math.Max(1, _entities.Count) * 8);
        int total = Align16(entitiesPos + Math.Max(1, _entities.Count) * 128);
        var d = new byte[total];

        // Meta header: root block 1, data block table.
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x10), 0x50524430);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x1C), 1);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0x30), 0x50000000 + blocksTable);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(0x4C), (short)BlockCount);

        WriteBlock(d, blocksTable, 0, CMapDataHash, 512, mapDataPos);
        WriteBlock(d, blocksTable, 1, PointerHash, _entities.Count * 8, pointersPos);
        WriteBlock(d, blocksTable, 2, CEntityDefHash, _entities.Count * 128, entitiesPos);
        if (_grassBatches > 0)
        {
            // Graphics segment block: address 0x6..., offset 0 in the graphics pages.
            var g = d.AsSpan(blocksTable + 3 * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(g, GrassDataHash);
            BinaryPrimitives.WriteInt32LittleEndian(g[4..], _grassBytes);
            BinaryPrimitives.WriteInt64LittleEndian(g[8..], 0x60000000);
        }

        // CMapData
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(mapDataPos + 8), Hash(_name));
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(mapDataPos + 12), _parent is null ? 0 : Hash(_parent));
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(mapDataPos + 16), _flags);
        ulong arrayPtr = (ulong)(0 << 12 | 2); // block 2 (pointers), offset 0
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(mapDataPos + 96), arrayPtr);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(mapDataPos + 104), (ushort)_entities.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(mapDataPos + 106), (ushort)_entities.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(mapDataPos + 128 + 8), _boxOccluders);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(mapDataPos + 200), _grassBatches > 0 ? 4ul : 0ul); // grass list -> block 4
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(mapDataPos + 200 + 8), (ushort)_grassBatches);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(mapDataPos + 224 + 8), _timecycles);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(mapDataPos + 240 + 8), _carGens);

        for (int i = 0; i < _entities.Count; i++)
        {
            // Pointer to entity i: block 3 (CEntityDef), offset i*128
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(pointersPos + i * 8), (ulong)((i * 128) << 12 | 3));

            var e = _entities[i];
            var s = d.AsSpan(entitiesPos + i * 128, 128);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], e.Archetype);
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)i + 1000);
            WriteFloat(s[32..], e.Position.X);
            WriteFloat(s[36..], e.Position.Y);
            WriteFloat(s[40..], e.Position.Z);
            WriteFloat(s[48..], e.Rotation.X);
            WriteFloat(s[52..], e.Rotation.Y);
            WriteFloat(s[56..], e.Rotation.Z);
            WriteFloat(s[60..], e.Rotation.W);
            WriteFloat(s[64..], e.ScaleXY);
            WriteFloat(s[68..], e.ScaleZ);
            BinaryPrimitives.WriteInt32LittleEndian(s[72..], e.ParentIndex);
            WriteFloat(s[76..], e.LodDist);
            BinaryPrimitives.WriteInt32LittleEndian(s[84..], e.LodLevel);
            BinaryPrimitives.WriteUInt32LittleEndian(s[88..], e.NumChildren);
        }

        if (_grassBatches == 0) return RpfBuilder.BuildRsc7(2, 0x20000001, 0x20000000, d);

        // System pages first (padded to the size the flags describe), then graphics pages.
        var (sysFlags, sysSize) = PageFlags(d.Length);
        var (gfxFlags, gfxSize) = PageFlags(_grassBytes);
        var all = new byte[sysSize + gfxSize];
        d.CopyTo(all, 0);
        return RpfBuilder.BuildRsc7(2, sysFlags, gfxFlags, all);
    }

    public void WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Build());
    }

    private static void WriteBlock(byte[] d, int table, int index, uint hash, int length, int pos)
    {
        var s = d.AsSpan(table + index * 16, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(s, hash);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], length);
        BinaryPrimitives.WriteInt64LittleEndian(s[8..], 0x50000000 + pos);
    }

    private static void WriteFloat(Span<byte> s, float v) => BinaryPrimitives.WriteSingleLittleEndian(s, v);

    private static int Align16(int v) => (v + 15) & ~15;

    /// <summary>Resource page flags describing one page big enough for <paramref name="length"/> bytes.</summary>
    private static (uint Flags, int Size) PageFlags(int length)
    {
        int shift = 0;
        while ((0x200 << shift) < length) shift++;
        return (0x20000000u | 1u << 27 | (uint)shift, 0x200 << shift);
    }
}
