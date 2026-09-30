using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RpfToFiveM.Core.Crypto;

namespace RpfToFiveM.Core.Rpf;

/// <summary>
/// Read-only view of an RPF7 archive. Nested archives share the parent's stream
/// and are read in place, so nothing is buffered beyond the file being copied.
/// </summary>
public sealed class RpfArchive
{
    private const uint Magic = 0x52504637; // "RPF7"
    private const uint DirectoryMarker = 0x7FFFFF00;
    private const int BlockSize = 512;
    private const int CopyBufferSize = 1 << 20;

    private readonly Stream _stream;
    private readonly long _start;
    private readonly GtaKeys? _keys;
    private readonly List<RpfEntry> _entries = new();
    private readonly List<RpfFileEntry> _files = new();

    public string Name { get; }
    public long Size { get; }
    public RpfEncryption Encryption { get; private set; }
    public RpfDirectoryEntry Root { get; private set; } = null!;
    public IReadOnlyList<RpfEntry> Entries => _entries;
    public IReadOnlyList<RpfFileEntry> Files => _files;

    private RpfArchive(Stream stream, long start, long size, string name, GtaKeys? keys)
    {
        _stream = stream;
        _start = start;
        Size = size;
        Name = name;
        _keys = keys;
    }

    /// <summary>Opens a top-level archive occupying the whole stream.</summary>
    public static RpfArchive Open(Stream stream, string name, GtaKeys? keys) =>
        Open(stream, 0, stream.Length, name, keys);

    internal static RpfArchive Open(Stream stream, long start, long size, string name, GtaKeys? keys)
    {
        var archive = new RpfArchive(stream, start, size, name, keys);
        archive.ReadToc();
        return archive;
    }

    /// <summary>Opens an .rpf stored inside this archive.</summary>
    public RpfArchive OpenNested(RpfBinaryEntry entry)
    {
        if (!entry.IsArchive) throw new ArgumentException($"{entry.Path} is not an archive.", nameof(entry));
        if (entry.IsEncrypted)
            throw new InvalidDataException($"Nested archive {entry.Path} is encrypted as a whole, which is not supported.");
        // Nested archives are stored uncompressed; some tools still fill in FileSize, so
        // (like the game) the stored bytes are read in place and validated by the header check.
        return Open(_stream, DataPosition(entry), entry.StoredSize, entry.Name, _keys);
    }

    public byte[] ReadAll(RpfFileEntry entry)
    {
        using var ms = new MemoryStream();
        ExtractTo(entry, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the entry's usable file to <paramref name="output"/>: binary files are
    /// decrypted and inflated, resources are written as complete RSC7 files.
    /// </summary>
    /// <param name="onRead">Called with the number of archive bytes consumed.</param>
    /// <param name="checkpoint">Called between chunks so callers can pause or cancel.</param>
    public void ExtractTo(RpfFileEntry entry, Stream output, Action<long>? onRead = null, Action? checkpoint = null)
    {
        long position = DataPosition(entry);
        long size = entry.StoredSize;

        switch (entry)
        {
            // Resources: rebuilt RSC7 header + the stored (still compressed) body.
            case RpfResourceEntry res when size < 16:
                throw new InvalidDataException($"{entry.Path} is too small to be a resource.");
            case RpfResourceEntry { IsEncrypted: true } res:
            {
                var body = Decrypt(ReadBytes(position + 16, size - 16), res.Name, res.FileSize);
                onRead?.Invoke(size);
                output.Write(res.BuildHeader());
                output.Write(body);
                break;
            }
            case RpfResourceEntry res:
                output.Write(res.BuildHeader());
                onRead?.Invoke(16);
                using (var src = new SubStream(_stream, position + 16, size - 16, onRead))
                    Copy(src, output, checkpoint);
                break;
            case RpfBinaryEntry { IsEncrypted: true } bin:
            {
                var data = Decrypt(ReadBytes(position, size), bin.Name, bin.UncompressedSize);
                onRead?.Invoke(size);
                using var src = new MemoryStream(data);
                if (bin.IsCompressed)
                    using (var inflater = new DeflateStream(src, CompressionMode.Decompress))
                        Copy(inflater, output, checkpoint);
                else
                    Copy(src, output, checkpoint);
                break;
            }
            case RpfBinaryEntry bin:
                using (var src = new SubStream(_stream, position, size, onRead))
                {
                    if (bin.IsCompressed)
                        using (var inflater = new DeflateStream(src, CompressionMode.Decompress))
                            Copy(inflater, output, checkpoint);
                    else
                        Copy(src, output, checkpoint);
                }
                break;
        }
    }

    private long DataPosition(RpfFileEntry entry)
    {
        long offset = (long)entry.FileOffset * BlockSize;
        if (offset + entry.StoredSize > Size || entry.StoredSize < 0)
            throw new InvalidDataException($"{entry.Path} points outside the archive.");
        return _start + offset;
    }

    private static void Copy(Stream src, Stream dst, Action? checkpoint)
    {
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            dst.Write(buffer, 0, read);
            checkpoint?.Invoke();
        }
    }

    private byte[] ReadBytes(long position, long count)
    {
        var buffer = new byte[count];
        _stream.Position = position;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    private byte[] Decrypt(byte[] data, string name, uint length)
    {
        if (Encryption == RpfEncryption.Aes)
        {
            if (_keys is null) throw new RpfKeysRequiredException(RpfEncryption.Aes);
            return AesCrypto.Decrypt(data, _keys.AesKey);
        }
        // Encrypted entries in non-AES archives (including OPEN ones) use NG.
        if (_keys is not { HasNgKeys: true }) throw new RpfKeysRequiredException(RpfEncryption.Ng);
        return NgCrypto.Decrypt(data, name, length, _keys);
    }

    private void ReadToc()
    {
        if (Size < 16) throw new InvalidDataException($"{Name} is too small to be an RPF archive.");

        var header = ReadBytes(_start, 16);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint namesLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        Encryption = (RpfEncryption)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));

        if (magic != Magic) throw new InvalidDataException($"{Name} is not an RPF7 (GTA V) archive.");
        if (entryCount == 0 || 16L + entryCount * 16L + namesLength > Size)
            throw new InvalidDataException($"{Name} has a corrupt or truncated header.");

        var entries = ReadBytes(_start + 16, entryCount * 16L);
        var names = ReadBytes(_start + 16 + entryCount * 16L, namesLength);

        switch (Encryption)
        {
            case RpfEncryption.None:
            case RpfEncryption.Open:
                break;
            case RpfEncryption.Aes:
                if (_keys is null) throw new RpfKeysRequiredException(RpfEncryption.Aes);
                entries = AesCrypto.Decrypt(entries, _keys.AesKey);
                names = AesCrypto.Decrypt(names, _keys.AesKey);
                break;
            default: // NG (unknown values are treated as NG, as the game does)
                if (_keys is not { HasNgKeys: true }) throw new RpfKeysRequiredException(RpfEncryption.Ng);
                entries = NgCrypto.Decrypt(entries, Name, (uint)Size, _keys);
                names = NgCrypto.Decrypt(names, Name, (uint)Size, _keys);
                break;
        }

        ParseEntries(entries, names, entryCount);
        BuildTree();
    }

    private void ParseEntries(byte[] toc, byte[] names, uint count)
    {
        for (int i = 0; i < count; i++)
        {
            var e = toc.AsSpan(i * 16, 16);
            uint x = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            RpfEntry entry;

            if (x == DirectoryMarker)
            {
                entry = new RpfDirectoryEntry
                {
                    NameOffset = BinaryPrimitives.ReadUInt32LittleEndian(e),
                    EntriesIndex = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                    EntriesCount = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]),
                };
            }
            else if ((x & 0x80000000) == 0)
            {
                ulong packed = BinaryPrimitives.ReadUInt64LittleEndian(e);
                entry = new RpfBinaryEntry
                {
                    NameOffset = (uint)(packed & 0xFFFF),
                    FileSize = (uint)(packed >> 16 & 0xFFFFFF),
                    FileOffset = (uint)(packed >> 40 & 0xFFFFFF),
                    UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                    IsEncrypted = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]) != 0,
                };
            }
            else
            {
                var res = new RpfResourceEntry
                {
                    NameOffset = BinaryPrimitives.ReadUInt16LittleEndian(e),
                    FileSize = (uint)(e[2] | e[3] << 8 | e[4] << 16),
                    FileOffset = (uint)(e[5] | e[6] << 8 | e[7] << 16) & 0x7FFFFF,
                    SystemFlags = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                    GraphicsFlags = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]),
                };
                if (res.FileSize == RpfResourceEntry.OversizedMarker)
                {
                    long pos = (long)res.FileOffset * BlockSize;
                    if (pos + 16 <= Size)
                        res.FileSize = RpfResourceEntry.SizeFromHeader(ReadBytes(_start + pos, 16));
                }
                entry = res;
            }

            entry.Name = ReadName(names, entry.NameOffset);
            _entries.Add(entry);
        }
    }

    private static string ReadName(byte[] names, uint offset)
    {
        if (offset >= names.Length) return "";
        int end = Array.IndexOf(names, (byte)0, (int)offset);
        if (end < 0) end = names.Length;
        int length = Math.Min(end - (int)offset, 256);
        return Encoding.UTF8.GetString(names, (int)offset, length);
    }

    private void BuildTree()
    {
        if (_entries[0] is not RpfDirectoryEntry root)
            throw new InvalidDataException($"{Name} has no root directory.");
        Root = root;

        var visited = new bool[_entries.Count];
        visited[0] = true;
        var stack = new Stack<RpfDirectoryEntry>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            long end = (long)dir.EntriesIndex + dir.EntriesCount;
            if (end > _entries.Count)
                throw new InvalidDataException($"{Name} has a corrupt directory table.");

            for (int i = (int)dir.EntriesIndex; i < end; i++)
            {
                if (visited[i]) continue; // guards against cyclic or overlapping tables
                visited[i] = true;

                var child = _entries[i];
                child.Parent = dir;
                child.Path = dir.Path.Length == 0 ? child.Name : dir.Path + "/" + child.Name;
                child.SafeRelativePath = dir.SafeRelativePath.Length == 0
                    ? SafePath.Segment(child.Name)
                    : Path.Combine(dir.SafeRelativePath, SafePath.Segment(child.Name));
                dir.Children.Add(child);

                if (child is RpfDirectoryEntry sub) stack.Push(sub);
                else if (child is RpfFileEntry file) _files.Add(file);
            }
        }
    }
}
