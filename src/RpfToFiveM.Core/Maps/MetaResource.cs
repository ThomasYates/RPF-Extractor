using System.Buffers.Binary;
using System.IO.Compression;

namespace RpfToFiveM.Core.Maps;

/// <summary>
/// An RSC7 "meta" resource (.ymap, .ytyp) decompressed into memory. The data is a set of
/// typed blocks; fields that point at other data use 32-bit "meta pointers"
/// (low 12 bits = 1-based block id, next 20 bits = byte offset in that block).
/// </summary>
internal sealed class MetaResource
{
    public const uint PointerBlock = 7;
    private const uint Rsc7Magic = 0x37435352;
    private const int HeaderSize = 0x70;

    public readonly record struct Block(uint Hash, int Length, int Offset);

    private readonly byte[] _header;
    public byte[] Data { get; }
    public IReadOnlyList<Block> Blocks { get; }

    private MetaResource(byte[] header, byte[] data, IReadOnlyList<Block> blocks)
    {
        _header = header;
        Data = data;
        Blocks = blocks;
    }

    public static MetaResource Load(byte[] rsc7)
    {
        if (rsc7.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(rsc7) != Rsc7Magic)
            throw new InvalidDataException("Not an RSC7 resource.");

        var data = Inflate(rsc7.AsSpan(16));
        if (data.Length < HeaderSize) throw new InvalidDataException("Resource is too small to be meta data.");

        long blocksPtr = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x30));
        int count = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(0x4C));
        int table = SystemOffset(blocksPtr, data.Length);
        if (count <= 0 || table < 0 || table + count * 16 > data.Length)
            throw new InvalidDataException("Resource has no valid meta block table.");

        var blocks = new List<Block>(count);
        for (int i = 0; i < count; i++)
        {
            var s = data.AsSpan(table + i * 16, 16);
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(s);
            int length = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
            int offset = SystemOffset(BinaryPrimitives.ReadInt64LittleEndian(s[8..]), data.Length);
            if (length < 0 || offset < 0 || offset + (long)length > data.Length)
                throw new InvalidDataException("Meta block points outside the resource.");
            blocks.Add(new Block(hash, length, offset));
        }

        return new MetaResource(rsc7[..16], data, blocks);
    }

    public Block? Find(uint hash)
    {
        foreach (var b in Blocks) if (b.Hash == hash) return b;
        return null;
    }

    /// <summary>Resolves a meta pointer to an absolute offset in <see cref="Data"/>.</summary>
    public bool TryResolve(ulong pointer, out Block block, out int offset)
    {
        int id = (int)(pointer & 0xFFF);
        int inner = (int)((pointer >> 12) & 0xFFFFF);
        block = default;
        offset = -1;
        if (id < 1 || id > Blocks.Count) return false;
        block = Blocks[id - 1];
        if (inner >= block.Length) return false;
        offset = block.Offset + inner;
        return true;
    }

    /// <summary>Recompresses the (possibly edited) data with the original header.</summary>
    public byte[] ToRsc7()
    {
        using var ms = new MemoryStream();
        ms.Write(_header);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(Data);
        return ms.ToArray();
    }

    internal static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray());
            using var ds = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            ds.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Resource data is not valid compressed data.", ex);
        }
    }

    /// <summary>Maps a 0x5xxxxxxx system-page virtual address to an offset, or -1.</summary>
    internal static int SystemOffset(long address, int length)
    {
        if ((address >> 28) != 5) return -1;
        long offset = address & 0x0FFFFFFF;
        return offset < length ? (int)offset : -1;
    }
}
