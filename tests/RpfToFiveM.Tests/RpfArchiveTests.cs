using System.Security.Cryptography;
using System.Text;
using RpfToFiveM.Core.Crypto;
using RpfToFiveM.Core.Rpf;
using RpfToFiveM.Tests.Fixtures;

namespace RpfToFiveM.Tests;

public class RpfArchiveTests
{
    private static RpfArchive Open(byte[] bytes, string name = "test.rpf", GtaKeys? keys = null) =>
        RpfArchive.Open(new MemoryStream(bytes), name, keys);

    [Fact]
    public void Open_ParsesDirectoryTreeAndFilePaths()
    {
        var bytes = new RpfBuilder()
            .AddText("readme.txt", "hello")
            .AddText("x64/levels/data.meta", "<xml/>")
            .AddResource("x64/props.ydr", new byte[100])
            .Build();

        var archive = Open(bytes);

        Assert.Equal(RpfEncryption.Open, archive.Encryption);
        var paths = archive.Files.Select(f => f.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "readme.txt", "x64/levels/data.meta", "x64/props.ydr" }, paths);
        Assert.IsType<RpfResourceEntry>(archive.Files.Single(f => f.Name == "props.ydr"));
        Assert.IsType<RpfBinaryEntry>(archive.Files.Single(f => f.Name == "readme.txt"));
    }

    [Fact]
    public void Open_RejectsNonRpfData()
    {
        Assert.Throws<InvalidDataException>(() => Open(new byte[64]));
    }

    [Fact]
    public void Open_RejectsTruncatedToc()
    {
        var bytes = new RpfBuilder().AddText("a.txt", "a").Build();
        Assert.Throws<InvalidDataException>(() => Open(bytes[..20]));
    }

    [Fact]
    public void ExtractBinary_Compressed_ReturnsOriginalBytes()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compress me ", 500)));
        var archive = Open(new RpfBuilder().AddBinary("a.bin", data, compress: true).Build());

        Assert.Equal(data, archive.ReadAll(archive.Files.Single()));
    }

    [Fact]
    public void ExtractBinary_Uncompressed_ReturnsOriginalBytes()
    {
        var data = RandomNumberGenerator.GetBytes(3000);
        var archive = Open(new RpfBuilder().AddBinary("a.bin", data, compress: false).Build());

        Assert.Equal(data, archive.ReadAll(archive.Files.Single()));
    }

    [Fact]
    public void ExtractResource_ReturnsCompleteRsc7File()
    {
        var body = RandomNumberGenerator.GetBytes(2048);
        var archive = Open(new RpfBuilder().AddResource("thing.ytyp", body, version: 2).Build());
        var entry = (RpfResourceEntry)archive.Files.Single();

        var extracted = archive.ReadAll(entry);

        var expected = RpfBuilder.BuildRsc7(2, entry.SystemFlags, entry.GraphicsFlags, body);
        Assert.Equal(expected, extracted);
    }

    [Fact]
    public void ExtractResource_RebuildsHeaderWhenStoredHeaderIsNotRsc7()
    {
        var body = RandomNumberGenerator.GetBytes(1024);
        var archive = Open(new RpfBuilder().AddResource("tex.ytd", body, version: 13 << 4 | 5, scrambleHeader: true).Build());
        var entry = (RpfResourceEntry)archive.Files.Single();

        var extracted = archive.ReadAll(entry);

        Assert.Equal(RpfBuilder.BuildRsc7(13 << 4 | 5, entry.SystemFlags, entry.GraphicsFlags, body), extracted);
        Assert.Equal(entry.StoredSize, extracted.Length);
    }

    [Fact]
    public void ResourceVersion_ComesFromTopNibblesOfFlags()
    {
        Assert.Equal(0xD5u, RpfResourceEntry.VersionFromFlags(0xD0000000, 0x50000000));
    }

    [Fact]
    public void AesArchive_WithKey_DecryptsTocAndEncryptedFiles()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("secret ", 300)));
        var bytes = new RpfBuilder()
            .AddBinary("secret.meta", data, compress: true, encrypt: true)
            .AddText("plain.txt", "plain")
            .Build(RpfEncryption.Aes, key);

        var archive = Open(bytes, keys: GtaKeys.AesOnly(key));

        Assert.Equal(RpfEncryption.Aes, archive.Encryption);
        Assert.Equal(data, archive.ReadAll(archive.Files.Single(f => f.Name == "secret.meta")));
        Assert.Equal("plain"u8.ToArray(), archive.ReadAll(archive.Files.Single(f => f.Name == "plain.txt")));
    }

    [Fact]
    public void AesArchive_WithoutKeys_ThrowsKeysRequired()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var bytes = new RpfBuilder().AddText("a.txt", "a").Build(RpfEncryption.Aes, key);

        var ex = Assert.Throws<RpfKeysRequiredException>(() => Open(bytes));
        Assert.Equal(RpfEncryption.Aes, ex.Encryption);
    }

    [Fact]
    public void NgArchive_WithoutKeys_ThrowsKeysRequired()
    {
        var bytes = new RpfBuilder().AddText("a.txt", "a").Build();
        BitConverter.GetBytes((uint)RpfEncryption.Ng).CopyTo(bytes, 12);

        var ex = Assert.Throws<RpfKeysRequiredException>(() => Open(bytes));
        Assert.Equal(RpfEncryption.Ng, ex.Encryption);
    }

    [Fact]
    public void NestedArchive_CanBeOpenedAndExtracted()
    {
        var inner = new RpfBuilder()
            .AddText("stream/inner.txt", "deep")
            .AddResource("stream/deep.ymap", new byte[64]);
        var outer = new RpfBuilder()
            .AddNested("dlcpacks/pack/dlc.rpf", inner)
            .AddText("outer.txt", "top");

        var archive = Open(outer.Build());
        var nestedEntry = archive.Files.OfType<RpfBinaryEntry>().Single(f => f.IsArchive);
        var nested = archive.OpenNested(nestedEntry);

        Assert.Equal("dlcpacks/pack/dlc.rpf", nestedEntry.Path);
        Assert.Equal("deep"u8.ToArray(), nested.ReadAll(nested.Files.Single(f => f.Name == "inner.txt")));
        Assert.Contains(nested.Files, f => f.Name == "deep.ymap");
    }

    [Fact]
    public void ExtractTo_ReportsInputBytesConsumed()
    {
        var data = RandomNumberGenerator.GetBytes(5000);
        var archive = Open(new RpfBuilder().AddBinary("a.bin", data, compress: false).Build());
        var entry = archive.Files.Single();
        long reported = 0;

        archive.ExtractTo(entry, Stream.Null, n => reported += n);

        Assert.Equal(entry.StoredSize, reported);
    }

    [Fact]
    public void ResourceSizeFromHeader_DecodesOversizedLength()
    {
        // Resources >= 16MB store 0xFFFFFF in the TOC; the real size is spread over the RSC7 header.
        var header = new byte[16];
        uint size = 0x12345678;
        header[7] = (byte)(size & 0xFF);
        header[14] = (byte)(size >> 8 & 0xFF);
        header[5] = (byte)(size >> 16 & 0xFF);
        header[2] = (byte)(size >> 24 & 0xFF);

        Assert.Equal(size, RpfResourceEntry.SizeFromHeader(header));
    }

    [Fact]
    public void EntryNamesAreSanitisedAgainstPathTraversal()
    {
        var archive = Open(new RpfBuilder().AddText("..\\..\\evil.txt", "x").AddText("ok:name?.txt", "y").Build());

        Assert.All(archive.Files, f => Assert.DoesNotContain("..", f.SafeRelativePath));
        Assert.All(archive.Files, f => Assert.DoesNotContain(":", f.SafeRelativePath));
    }
}
