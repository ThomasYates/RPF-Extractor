using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RpfToFiveM.Core.Rpf;

namespace RpfToFiveM.Tests.Fixtures;

/// <summary>
/// Test-only writer that produces RPF7 archives so the reader and extractor
/// can be exercised without shipping real game data.
/// </summary>
internal sealed class RpfBuilder
{
    private abstract class Node
    {
        public required string Name { get; init; }
    }

    private sealed class DirNode : Node
    {
        public List<Node> Children { get; } = new();
        public int EntriesIndex;
    }

    private sealed class FileNode : Node
    {
        public required byte[] Data { get; init; }
        public bool IsResource { get; init; }
        public bool Compress { get; init; }
        public bool Encrypt { get; init; }
        public uint SystemFlags { get; init; }
        public uint GraphicsFlags { get; init; }
    }

    private readonly DirNode _root = new() { Name = "" };

    public RpfBuilder AddBinary(string path, byte[] data, bool compress = true, bool encrypt = false)
    {
        var (dir, name) = Resolve(path);
        dir.Children.Add(new FileNode { Name = name, Data = data, Compress = compress, Encrypt = encrypt });
        return this;
    }

    /// <summary>Stores a complete file (e.g. a prebuilt RSC7) byte-for-byte.</summary>
    public RpfBuilder AddRaw(string path, byte[] data) => AddBinary(path, data, compress: false);

    public RpfBuilder AddText(string path, string text, bool compress = true) =>
        AddBinary(path, Encoding.UTF8.GetBytes(text), compress);

    /// <summary>Adds a resource; the stored payload is a complete RSC7 file.</summary>
    /// <param name="scrambleHeader">
    /// Store junk instead of the RSC7 header, as GTA V Enhanced archives (and oversized
    /// legacy resources) do; readers must rebuild the header from the TOC flags.
    /// </param>
    public RpfBuilder AddResource(string path, byte[] body, uint version = 2, bool scrambleHeader = false)
    {
        var (dir, name) = Resolve(path);
        uint sysFlags = (version >> 4 & 0xF) << 28 | 0x1;
        uint gfxFlags = (version & 0xF) << 28 | 0x1;
        var stored = BuildRsc7(version, sysFlags, gfxFlags, body);
        if (scrambleHeader) RandomNumberGenerator.Fill(stored.AsSpan(0, 16));
        dir.Children.Add(new FileNode
        {
            Name = name,
            Data = stored,
            IsResource = true,
            SystemFlags = sysFlags,
            GraphicsFlags = gfxFlags,
        });
        return this;
    }

    public RpfBuilder AddNested(string path, RpfBuilder nested, RpfEncryption encryption = RpfEncryption.Open, byte[]? aesKey = null) =>
        AddBinary(path, nested.Build(encryption, aesKey), compress: false);

    public RpfBuilder AddDirectory(string path)
    {
        Resolve(path + "/placeholder");
        return this;
    }

    public static byte[] BuildRsc7(uint version, uint sysFlags, uint gfxFlags, byte[] body)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(0x37435352u); // "RSC7"
            bw.Write(version);
            bw.Write(sysFlags);
            bw.Write(gfxFlags);
        }
        var compressed = Deflate(body);
        ms.Write(compressed);
        return ms.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data);
        return ms.ToArray();
    }

    public static byte[] AesEncrypt(byte[] data, byte[] key)
    {
        var result = (byte[])data.Clone();
        int length = data.Length - data.Length % 16;
        if (length == 0) return result;
        using var aes = Aes.Create();
        aes.Key = key;
        var enc = aes.EncryptEcb(data.AsSpan(0, length), PaddingMode.None);
        enc.CopyTo(result, 0);
        return result;
    }

    public byte[] Build(RpfEncryption encryption = RpfEncryption.Open, byte[]? aesKey = null)
    {
        // Entries are laid out breadth-first: each directory's children are contiguous.
        var entries = new List<Node> { _root };
        var queue = new Queue<DirNode>();
        queue.Enqueue(_root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            dir.EntriesIndex = entries.Count;
            foreach (var child in dir.Children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                entries.Add(child);
                if (child is DirNode d) queue.Enqueue(d);
            }
        }

        // Names table.
        var nameOffsets = new Dictionary<string, int>();
        var names = new MemoryStream();
        foreach (var e in entries)
        {
            if (nameOffsets.ContainsKey(e.Name)) continue;
            nameOffsets[e.Name] = (int)names.Length;
            names.Write(Encoding.ASCII.GetBytes(e.Name));
            names.WriteByte(0);
        }
        while (names.Length % 16 != 0) names.WriteByte(0);
        var namesBytes = names.ToArray();

        // File payloads.
        long headerLen = 16 + entries.Count * 16L + namesBytes.Length;
        long dataPos = Align(headerLen);
        var payloads = new Dictionary<FileNode, (byte[] Bytes, long Block)>();
        foreach (var f in entries.OfType<FileNode>())
        {
            byte[] bytes = f.Data;
            if (!f.IsResource)
            {
                if (f.Compress) bytes = Deflate(bytes);
                if (f.Encrypt) bytes = AesEncrypt(bytes, aesKey ?? throw new InvalidOperationException("AES key required"));
            }
            payloads[f] = (bytes, dataPos / 512);
            dataPos = Align(dataPos + bytes.Length);
        }

        // Entry table.
        var toc = new MemoryStream();
        var tw = new BinaryWriter(toc);
        foreach (var e in entries)
        {
            uint nameOff = (uint)nameOffsets[e.Name];
            switch (e)
            {
                case DirNode d:
                    tw.Write(nameOff);
                    tw.Write(0x7FFFFF00u);
                    tw.Write((uint)d.EntriesIndex);
                    tw.Write((uint)d.Children.Count);
                    break;
                case FileNode { IsResource: true } r:
                {
                    var (bytes, block) = payloads[r];
                    tw.Write((ushort)nameOff);
                    WriteUInt24(tw, (uint)bytes.Length);
                    WriteUInt24(tw, (uint)block | 0x800000);
                    tw.Write(r.SystemFlags);
                    tw.Write(r.GraphicsFlags);
                    break;
                }
                case FileNode b:
                {
                    var (bytes, block) = payloads[b];
                    ulong packed = nameOff & 0xFFFFUL
                        | (ulong)(b.Compress ? bytes.Length : 0) << 16
                        | (ulong)block << 40;
                    tw.Write(packed);
                    tw.Write((uint)b.Data.Length);
                    tw.Write(b.Encrypt ? 1u : 0u);
                    break;
                }
            }
        }
        tw.Flush();
        var tocBytes = toc.ToArray();

        if (encryption == RpfEncryption.Aes)
        {
            var key = aesKey ?? throw new InvalidOperationException("AES key required");
            tocBytes = AesEncrypt(tocBytes, key);
            namesBytes = AesEncrypt(namesBytes, key);
        }

        var output = new byte[dataPos];
        using (var ow = new BinaryWriter(new MemoryStream(output)))
        {
            ow.Write(0x52504637u);
            ow.Write((uint)entries.Count);
            ow.Write((uint)namesBytes.Length);
            ow.Write((uint)encryption);
            ow.Write(tocBytes);
            ow.Write(namesBytes);
        }
        foreach (var (bytes, block) in payloads.Values)
            Buffer.BlockCopy(bytes, 0, output, (int)(block * 512), bytes.Length);
        return output;
    }

    public void WriteTo(string filePath, RpfEncryption encryption = RpfEncryption.Open, byte[]? aesKey = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, Build(encryption, aesKey));
    }

    private (DirNode Dir, string Name) Resolve(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dir = _root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var next = dir.Children.OfType<DirNode>().FirstOrDefault(d => d.Name == parts[i]);
            if (next is null)
            {
                next = new DirNode { Name = parts[i] };
                dir.Children.Add(next);
            }
            dir = next;
        }
        return (dir, parts[^1]);
    }

    private static void WriteUInt24(BinaryWriter w, uint v)
    {
        w.Write((byte)(v & 0xFF));
        w.Write((byte)(v >> 8 & 0xFF));
        w.Write((byte)(v >> 16 & 0xFF));
    }

    private static long Align(long v) => (v + 511) / 512 * 512;
}
