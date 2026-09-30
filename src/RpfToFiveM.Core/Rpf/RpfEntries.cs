using System.Buffers.Binary;

namespace RpfToFiveM.Core.Rpf;

public enum RpfEncryption : uint
{
    None = 0,
    Open = 0x4E45504F, // "OPEN" - OpenIV style, unencrypted TOC
    Aes = 0x0FFFFFF9,
    Ng = 0x0FEFFFFF,
}

public sealed class RpfKeysRequiredException : Exception
{
    public RpfEncryption Encryption { get; }

    public RpfKeysRequiredException(RpfEncryption encryption)
        : base($"This archive is {encryption}-encrypted. Set the GTA V folder so the keys can be loaded.")
    {
        Encryption = encryption;
    }
}

public abstract class RpfEntry
{
    public string Name { get; internal set; } = "";

    /// <summary>Path inside the archive, '/' separated, as stored.</summary>
    public string Path { get; internal set; } = "";

    /// <summary>Path that is safe to create on disk (no traversal, no invalid characters).</summary>
    public string SafeRelativePath { get; internal set; } = "";

    public RpfDirectoryEntry? Parent { get; internal set; }
    internal uint NameOffset { get; set; }
}

public sealed class RpfDirectoryEntry : RpfEntry
{
    internal uint EntriesIndex { get; set; }
    internal uint EntriesCount { get; set; }
    public List<RpfEntry> Children { get; } = new();
}

public abstract class RpfFileEntry : RpfEntry
{
    /// <summary>Offset of the data in 512-byte blocks from the start of the archive.</summary>
    internal uint FileOffset { get; set; }

    /// <summary>Number of bytes the entry occupies inside the archive.</summary>
    public abstract long StoredSize { get; }
}

public sealed class RpfBinaryEntry : RpfFileEntry
{
    /// <summary>Compressed size, or 0 when the data is stored uncompressed.</summary>
    public uint FileSize { get; internal set; }
    public uint UncompressedSize { get; internal set; }
    public bool IsEncrypted { get; internal set; }

    public bool IsCompressed => FileSize > 0;
    public bool IsArchive => Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);
    public override long StoredSize => FileSize == 0 ? UncompressedSize : FileSize;
}

public sealed class RpfResourceEntry : RpfFileEntry
{
    public const uint OversizedMarker = 0xFFFFFF;

    /// <summary>Size of the stored RSC7 file (header + compressed body).</summary>
    public uint FileSize { get; internal set; }
    public uint SystemFlags { get; internal set; }
    public uint GraphicsFlags { get; internal set; }

    /// <summary>Only compiled scripts are encrypted inside archives.</summary>
    public bool IsEncrypted => Name.EndsWith(".ysc", StringComparison.OrdinalIgnoreCase);

    public override long StoredSize => FileSize;

    public uint Version => VersionFromFlags(SystemFlags, GraphicsFlags);

    /// <summary>Byte size of the system or graphics pages described by a resource's page flags.</summary>
    public static int SizeFromFlags(uint flags)
    {
        uint s0 = (flags >> 27 & 0x1) << 0;
        uint s1 = (flags >> 26 & 0x1) << 1;
        uint s2 = (flags >> 25 & 0x1) << 2;
        uint s3 = (flags >> 24 & 0x1) << 3;
        uint s4 = (flags >> 17 & 0x7F) << 4;
        uint s5 = (flags >> 11 & 0x3F) << 5;
        uint s6 = (flags >> 7 & 0xF) << 6;
        uint s7 = (flags >> 5 & 0x3) << 7;
        uint s8 = (flags >> 4 & 0x1) << 8;
        int baseSize = 0x200 << (int)(flags & 0xF);
        return (int)(baseSize * (s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8));
    }

    public static uint VersionFromFlags(uint systemFlags, uint graphicsFlags) =>
        (systemFlags >> 28 & 0xF) << 4 | graphicsFlags >> 28 & 0xF;

    /// <summary>
    /// A clean 16-byte RSC7 header. The copy stored in the archive can't be trusted: Enhanced
    /// archives don't keep it and oversized resources pack their length into it.
    /// </summary>
    public byte[] BuildHeader()
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x37435352); // "RSC7"
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), SystemFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), GraphicsFlags);
        return header;
    }

    /// <summary>Real size of resources larger than the 24-bit TOC field, read from their RSC7 header.</summary>
    public static uint SizeFromHeader(ReadOnlySpan<byte> header) =>
        (uint)header[7] | (uint)header[14] << 8 | (uint)header[5] << 16 | (uint)header[2] << 24;
}
