using System.Buffers.Binary;
using System.Numerics;

namespace RpfToFiveM.Core.Maps;

public enum LodLevel
{
    Hd = 0,
    Lod = 1,
    Slod1 = 2,
    Slod2 = 3,
    Slod3 = 4,
    OrphanHd = 5,
    Slod4 = 6,
}

/// <summary>One placed object in a ymap.</summary>
public sealed record YmapEntity(
    int Index,
    uint Archetype,
    uint Flags,
    Vector3 Position,
    Quaternion Rotation,
    float ScaleXY,
    float ScaleZ,
    int ParentIndex,
    LodLevel LodLevel,
    uint NumChildren,
    bool IsMlo)
{
    public bool IsLodLevel => LodLevel is LodLevel.Lod or LodLevel.Slod1 or LodLevel.Slod2 or LodLevel.Slod3 or LodLevel.Slod4;

    /// <summary>Flag telling the game this entity's parentIndex points into the parent ymap.</summary>
    public bool LodInParentYmap => (Flags & 0x8) != 0;
}

/// <summary>Reads (and minimally edits) the entity list of a .ymap.</summary>
public sealed class YmapFile
{
    private const uint CMapDataHash = 3545841574;
    private const uint CEntityDefHash = 3461354627;
    private const uint CMloInstanceDefHash = 164374718;
    private const int EntitiesField = 96; // Array_StructurePointer inside CMapData

    private readonly MetaResource _meta;
    private readonly int _mapData;
    private List<YmapEntity> _entities = new();

    public uint NameHash { get; }
    public uint ParentHash { get; }

    /// <summary>Loaded on demand by a script (an IPL state such as burnt/unburnt), not always.</summary>
    public bool IsScripted { get; }

    // Counts of CMapData's other arrays (each count sits 8 bytes into its array field).
    private int Count(int field) =>
        _mapData + field + 10 <= _meta.Data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(_meta.Data.AsSpan(_mapData + field + 8)) : 0;

    /// <summary>Batches of instanced grass/plants.</summary>
    public int GrassBatches => Count(200);

    /// <summary>Everything a ymap can hold besides entities and grass.</summary>
    private int OtherContent =>
        Count(112)    // container LODs
        + Count(128)  // box occluders
        + Count(144)  // occlusion models
        + Count(184)  // instanced props
        + Count(224)  // timecycle modifiers
        + Count(240)  // car generators
        + Count(256)  // LOD lights
        + Count(392); // distant LOD lights

    /// <summary>No entities, grass or anything else: the file does nothing.</summary>
    public bool IsEmpty => Entities.Count == 0 && GrassBatches == 0 && OtherContent == 0;

    /// <summary>Only instanced grass/plants: removing it loses ground cover but no buildings.</summary>
    public bool IsGrassOnly => Entities.Count == 0 && GrassBatches > 0 && OtherContent == 0;
    public IReadOnlyList<YmapEntity> Entities => _entities;

    private YmapFile(MetaResource meta, int mapData)
    {
        _meta = meta;
        _mapData = mapData;
        NameHash = BinaryPrimitives.ReadUInt32LittleEndian(meta.Data.AsSpan(mapData + 8));
        ParentHash = BinaryPrimitives.ReadUInt32LittleEndian(meta.Data.AsSpan(mapData + 12));
        IsScripted = (BinaryPrimitives.ReadUInt32LittleEndian(meta.Data.AsSpan(mapData + 16)) & 1) != 0;
        ReadEntities();
    }

    public static YmapFile Parse(byte[] rsc7)
    {
        var meta = MetaResource.Load(rsc7);
        var block = meta.Find(CMapDataHash) ?? throw new InvalidDataException("No CMapData block — not a ymap.");
        if (block.Length < 112) throw new InvalidDataException("CMapData block is truncated.");
        return new YmapFile(meta, block.Offset);
    }

    /// <summary>Removes entities by index. Later entities shift down, so callers must check nothing links to them by index.</summary>
    public void RemoveEntities(IEnumerable<int> indices)
    {
        var remove = indices.ToHashSet();
        if (remove.Count == 0) return;

        var data = _meta.Data;
        ulong array = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_mapData + EntitiesField));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_mapData + EntitiesField + 8));
        if (!_meta.TryResolve(array, out _, out int ptrs)) return;

        var kept = new List<ulong>(count);
        for (int i = 0; i < count; i++)
            if (!remove.Contains(i)) kept.Add(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ptrs + i * 8)));

        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(ptrs + i * 8), i < kept.Count ? kept[i] : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(_mapData + EntitiesField + 8), (ushort)kept.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(_mapData + EntitiesField + 10), (ushort)kept.Count);

        ReadEntities();
    }

    public byte[] ToRsc7() => _meta.ToRsc7();

    private void ReadEntities()
    {
        var data = _meta.Data;
        ulong array = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_mapData + EntitiesField));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_mapData + EntitiesField + 8));
        var list = new List<YmapEntity>(count);
        _entities = list;
        if (count == 0) return;

        if (!_meta.TryResolve(array, out var ptrBlock, out int ptrs) || ptrBlock.Hash != MetaResource.PointerBlock
            || ptrs + count * 8 > ptrBlock.Offset + ptrBlock.Length)
            throw new InvalidDataException("Entity list is corrupt.");

        for (int i = 0; i < count; i++)
        {
            ulong ptr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ptrs + i * 8));
            if (!_meta.TryResolve(ptr, out var block, out int at) || at + 128 > data.Length
                || (block.Hash != CEntityDefHash && block.Hash != CMloInstanceDefHash))
                throw new InvalidDataException($"Entity {i} points at invalid data.");

            // CMloInstanceDef starts with the same fields as CEntityDef.
            var e = data.AsSpan(at, 128);
            list.Add(new YmapEntity(
                i,
                BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[12..]),
                new Vector3(F(e, 32), F(e, 36), F(e, 40)),
                new Quaternion(F(e, 48), F(e, 52), F(e, 56), F(e, 60)),
                F(e, 64),
                F(e, 68),
                BinaryPrimitives.ReadInt32LittleEndian(e[72..]),
                (LodLevel)BinaryPrimitives.ReadInt32LittleEndian(e[84..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[88..]),
                block.Hash == CMloInstanceDefHash));
        }
    }

    private static float F(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadSingleLittleEndian(s[offset..]);
}
