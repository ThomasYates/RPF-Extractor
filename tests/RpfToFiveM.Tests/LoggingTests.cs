using RpfToFiveM.Core.Logging;

namespace RpfToFiveM.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpf2fivem_log_" + Guid.NewGuid().ToString("N"));

    public LoggingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static LogRedactor Redactor() => new(
        new[]
        {
            (@"C:\Users\bob\Downloads\gtaVI", "<source>"),
            (@"\\TRUENAS\Apps\fivem\resources\[test]", "<export>"),
            (@"I:\SteamLibrary\steamapps\common\Grand Theft Auto V", "<gta>"),
            (@"C:\Users\bob", "<home>"),
        },
        userName: "bob");

    [Theory]
    [InlineData(@"Found 2 archive(s) in C:\Users\bob\Downloads\gtaVI.", "Found 2 archive(s) in <source>.")]
    [InlineData(@"Done: 5 file(s) written to \\TRUENAS\Apps\fivem\resources\[test]\florida with 0 error(s).",
                @"Done: 5 file(s) written to <export>\florida with 0 error(s).")]
    [InlineData(@"Keys from I:\SteamLibrary\steamapps\common\Grand Theft Auto V\GTA5.exe", @"Keys from <gta>\GTA5.exe")]
    [InlineData(@"cache at C:\Users\bob\AppData\Roaming\x.json", @"cache at <home>\AppData\Roaming\x.json")]
    public void KnownFoldersBecomeLabels(string input, string expected)
    {
        Assert.Equal(expected, Redactor().Redact(input));
    }

    [Fact]
    public void KnownFoldersMatchRegardlessOfCaseAndSlashDirection()
    {
        Assert.Equal("in <source>/mymap/dlc.rpf", Redactor().Redact("in c:/users/BOB/downloads/GTAVI/mymap/dlc.rpf"));
    }

    [Theory]
    [InlineData(@"Could not find a part of the path 'D:\Secret Stuff\Mods\thing.ymap'.", "Could not find a part of the path '<path>\\thing.ymap'.")]
    [InlineData(@"Access to the path E:\private folder\out is denied.", @"Access to the path <path> is denied.")]
    [InlineData(@"Failed at \\NAS\share\my maps\a.ydr, retrying", @"Failed at <path>\a.ydr, retrying")]
    public void UnknownAbsolutePathsAreHidden(string input, string expected)
    {
        Assert.Equal(expected, Redactor().Redact(input));
    }

    [Theory]
    [InlineData("gta6map3-dlc.rpf/x64/levels/gta5/gta6map/ap/ap_01.rpf/hi@ap_01_0.ybn: bad data")]
    [InlineData(@"mymap\dlc.rpf: not an RPF7 archive")]
    [InlineData("Scan complete: 19,989 file(s), 7.7 GB to extract.")]
    public void ArchiveInternalPathsAreKept(string input)
    {
        Assert.Equal(input, Redactor().Redact(input));
    }

    [Fact]
    public void UserNameIsScrubbedEverywhere()
    {
        Assert.Equal("owner <user> said hi; <user>'s map", Redactor().Redact("owner bob said hi; Bob's map"));
    }

    [Fact]
    public void ShortUserNamesAreLeftAlone()
    {
        // A 1-2 letter name would mangle ordinary words.
        var r = new LogRedactor(Array.Empty<(string, string)>(), userName: "ab");
        Assert.Equal("about a tab", r.Redact("about a tab"));
    }

    [Fact]
    public void FileLog_AppendsRedactedLinesAcrossSessions()
    {
        var path = Path.Combine(_root, "logs.txt");
        using (var log = new FileLog(path, Redactor))
            log.Write("INF", @"Found 1 archive(s) in C:\Users\bob\Downloads\gtaVI.");
        using (var log = new FileLog(path, Redactor))
            log.Write("ERR", @"boom at D:\x\y\z.rpf");

        var text = File.ReadAllText(path);
        Assert.Contains("INF  Found 1 archive(s) in <source>.", text);
        Assert.Contains(@"ERR  boom at <path>\z.rpf", text);
        Assert.DoesNotContain("bob", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\x", text);
    }

    [Fact]
    public void FileLog_RotatesWhenTooBig()
    {
        var path = Path.Combine(_root, "logs.txt");
        File.WriteAllText(path, new string('x', 2048));

        using (var log = new FileLog(path, Redactor, maxBytes: 1024))
            log.Write("INF", "fresh");

        Assert.True(File.Exists(Path.Combine(_root, "logs.old.txt")));
        Assert.DoesNotContain(new string('x', 100), File.ReadAllText(path));
    }

    [Fact]
    public void FileLog_FallsBackWhenFolderIsNotWritable()
    {
        var bad = Path.Combine(_root, "missing\0dir", "logs.txt");
        var fallback = Path.Combine(_root, "fallback", "logs.txt");

        using var log = FileLog.OpenWithFallback(new[] { bad, fallback }, Redactor);
        log.Write("INF", "hello");

        Assert.Equal(fallback, log.FilePath);
        // The log stays open while the app runs; readers (e.g. Notepad) share it.
        using var reader = new StreamReader(new FileStream(fallback, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Contains("hello", reader.ReadToEnd());
    }
}
