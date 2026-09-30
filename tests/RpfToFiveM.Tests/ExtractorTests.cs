using System.Text;
using RpfToFiveM.Core.Extraction;
using RpfToFiveM.Core.Rpf;
using RpfToFiveM.Tests.Fixtures;

namespace RpfToFiveM.Tests;

public sealed class ExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpf2fivem_" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "src");
    private string Output => Path.Combine(_root, "out");

    public ExtractorTests() => Directory.CreateDirectory(Source);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class SyncProgress : IProgress<ExtractionProgress>
    {
        public List<ExtractionProgress> Reports { get; } = new();
        public void Report(ExtractionProgress value) { lock (Reports) Reports.Add(value); }
    }

    private Task<ExtractionResult> Run(OutputMode mode, PauseController? pause = null, CancellationToken ct = default,
        IProgress<ExtractionProgress>? progress = null, List<LogEntry>? log = null)
    {
        var options = new ExtractionOptions
        {
            SourceFolder = Source,
            OutputFolder = Output,
            Mode = mode,
            ResourceName = "my_map",
        };
        return new RpfExtractor().RunAsync(options, progress, e => { lock (log ?? new()) log?.Add(e); }, pause ?? new PauseController(), ct);
    }

    private void WriteSampleMod()
    {
        var inner = new RpfBuilder()
            .AddResource("x64/levels/gta5/props.ydr", Encoding.ASCII.GetBytes("drawable"))
            .AddResource("x64/levels/gta5/props.ytyp", Encoding.ASCII.GetBytes("archetypes"))
            .AddResource("x64/levels/gta5/placement.ymap", Encoding.ASCII.GetBytes("map"))
            .AddResource("x64/levels/gta5/textures.ytd", Encoding.ASCII.GetBytes("tex"))
            .AddResource("x64/levels/gta5/collision.ybn", Encoding.ASCII.GetBytes("col"))
            .AddResource("x64/audio/sfx.awc", Encoding.ASCII.GetBytes("audio"))
            .AddText("common/data/content.xml", "<content/>");
        new RpfBuilder()
            .AddText("setup2.xml", "<setup/>")
            .AddNested("dlc.rpf", inner)
            .WriteTo(Path.Combine(Source, "mymap", "dlc.rpf"));
    }

    [Fact]
    public async Task DumpMode_TurnsEveryArchiveIncludingNestedIntoFolders()
    {
        WriteSampleMod();

        var result = await Run(OutputMode.Dump);

        var top = Path.Combine(Output, "extracted", "mymap", "dlc.rpf");
        Assert.True(Directory.Exists(top));
        Assert.Equal("<setup/>", File.ReadAllText(Path.Combine(top, "setup2.xml")));
        Assert.Equal("<content/>", File.ReadAllText(Path.Combine(top, "dlc.rpf", "common", "data", "content.xml")));
        var ydr = File.ReadAllBytes(Path.Combine(top, "dlc.rpf", "x64", "levels", "gta5", "props.ydr"));
        Assert.Equal("RSC7"u8.ToArray(), ydr[..4]);
        Assert.False(File.Exists(Path.Combine(top, "dlc.rpf")), "nested archive should become a folder, not a file");
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(8, result.FilesWritten);
    }

    [Fact]
    public async Task DumpMode_WritesIntoExtractedSubfolder()
    {
        WriteSampleMod();

        var result = await Run(OutputMode.Dump);

        Assert.Equal(Path.Combine(Output, "extracted"), result.OutputRoot);
        Assert.Equal(new[] { "extracted" }, Directory.GetDirectories(Output).Select(Path.GetFileName));
    }

    [Fact]
    public async Task FiveMMode_OutputsStreamFolderWithOnlyMapFilesAndManifest()
    {
        WriteSampleMod();

        var result = await Run(OutputMode.FiveM);

        var resource = Path.Combine(Output, "my_map");
        var streamFiles = Directory.GetFiles(Path.Combine(resource, "stream"), "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "collision.ybn", "placement.ymap", "props.ydr", "props.ytyp", "textures.ytd" }, streamFiles);

        var manifest = File.ReadAllText(Path.Combine(resource, "fxmanifest.lua"));
        Assert.Contains("fx_version 'cerulean'", manifest);
        Assert.Contains("game 'gta5'", manifest);
        Assert.Contains("this_is_a_map 'yes'", manifest);
        Assert.Matches(@"data_file 'DLC_ITYP_REQUEST' 'stream/[^']*props\.ytyp'", manifest);
        Assert.Equal(resource, result.OutputRoot);
        Assert.Equal(7, result.FilesWritten); // 5 map files + content.xml and setup2.xml (kept aside in _not_used)
    }

    [Fact]
    public async Task FiveMMode_RegistersUsefulMetaFilesAndSetsAsideTheRest()
    {
        new RpfBuilder()
            .AddResource("x64/levels/gta5/props.ydr", new byte[] { 1 })
            .AddText("common/data/gtxd.meta", "<CMapParentTxds><txdRelationships /></CMapParentTxds>")
            .AddText("common/data/vehicles.meta", "<CVehicleModelInfo__InitDataList />")
            .AddText("x64/audio/config/mymap_game.dat151.rel", "binary-audio")
            .AddText("readme.ini", "[x]\ny=1")
            .WriteTo(Path.Combine(Source, "mymap", "dlc.rpf"));

        await Run(OutputMode.FiveM);

        var res = Path.Combine(Output, "my_map");
        var manifest = File.ReadAllText(Path.Combine(res, "fxmanifest.lua"));
        Assert.Matches(@"data_file 'GTXD_PARENTING_DATA' 'data/[^']*gtxd\.meta'", manifest);
        Assert.Matches(@"data_file 'AUDIO_GAMEDATA' 'data/[^']*mymap_game\.dat'", manifest);
        Assert.Single(Directory.GetFiles(Path.Combine(res, "data"), "gtxd.meta", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Path.Combine(res, "_not_used"), "vehicles.meta", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Path.Combine(res, "_not_used"), "readme.ini", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(res, ".support")), "staging folder is cleaned up");

        var report = File.ReadAllText(Path.Combine(res, SupportFileProcessor.ReportFileName));
        Assert.Contains("gtxd.meta", report);
        Assert.Contains("vehicles.meta", report);
        Assert.Contains("readme.ini", report);
    }

    [Fact]
    public async Task FiveMMode_WritesPoolSizeReport()
    {
        WriteSampleMod();

        var result = await Run(OutputMode.FiveM);

        Assert.NotNull(result.Pools);
        Assert.True(File.Exists(Path.Combine(Output, "my_map", PoolAdvisor.ReportFileName)));
        // The sample map adds one collision file; the base game counts come from the built-in snapshot.
        Assert.Equal(1, result.Pools!.Pools.Single(p => p.Pool == "StaticBounds").MapAdds);
    }

    [Fact]
    public async Task DumpMode_HasNoPoolReport()
    {
        WriteSampleMod();

        var result = await Run(OutputMode.Dump);

        Assert.Null(result.Pools);
    }

    [Fact]
    public async Task FiveMMode_SkipsDuplicateNamesAndReportsThem()
    {
        new RpfBuilder().AddResource("a/shared.ydr", new byte[] { 1 }).WriteTo(Path.Combine(Source, "a.rpf"));
        new RpfBuilder().AddResource("b/shared.ydr", new byte[] { 2 }).WriteTo(Path.Combine(Source, "b.rpf"));

        var result = await Run(OutputMode.FiveM);

        Assert.Single(Directory.GetFiles(Path.Combine(Output, "my_map", "stream"), "shared.ydr", SearchOption.AllDirectories));
        Assert.Equal(1, result.DuplicatesSkipped);
    }

    [Fact]
    public async Task FiveMMode_LeavesOutPedClothingAndVehicles()
    {
        var male = new RpfBuilder().AddResource("mp_m_freemode_01_male_pack/jbib_000_u.ydd", new byte[] { 1 });
        new RpfBuilder()
            .AddNested("x64/models/cdimages/pack_male.rpf", male)
            .AddNested("x64/vehicles.rpf", new RpfBuilder().AddResource("car.yft", new byte[] { 2 }))
            .AddResource("x64/levels/gta5/bench.ydr", new byte[] { 3 })
            .WriteTo(Path.Combine(Source, "pack", "dlc.rpf"));
        var log = new List<LogEntry>();

        var result = await Run(OutputMode.FiveM, log: log);

        var names = Directory.GetFiles(Path.Combine(Output, "my_map", "stream"), "*", SearchOption.AllDirectories).Select(Path.GetFileName);
        Assert.Equal(new[] { "bench.ydr" }, names);
        Assert.Equal(0, result.DuplicatesSkipped);
        Assert.Contains(log, e => e.Message.Contains("2 non-map"));
    }

    [Fact]
    public async Task FiveMMode_WarnsAboutEnhancedEditionAssets()
    {
        new RpfBuilder().AddResource("prop.ydr", new byte[] { 1 }, version: 159).WriteTo(Path.Combine(Source, "a.rpf"));
        var log = new List<LogEntry>();

        await Run(OutputMode.FiveM, log: log);

        Assert.Contains(log, e => e.Level == LogLevel.Warning && e.Message.Contains("Enhanced"));
    }

    [Fact]
    public async Task FiveMMode_IncludesLooseMapFilesFromSourceFolder()
    {
        File.WriteAllBytes(Path.Combine(Source, "loose.ymap"), new byte[] { 9, 9 });
        File.WriteAllText(Path.Combine(Source, "notes.txt"), "ignore me");

        await Run(OutputMode.FiveM);

        var stream = Path.Combine(Output, "my_map", "stream");
        Assert.Single(Directory.GetFiles(stream, "loose.ymap", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(stream, "notes.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptArchive_IsLoggedAndOthersStillExtract()
    {
        File.WriteAllBytes(Path.Combine(Source, "broken.rpf"), Encoding.ASCII.GetBytes("not an rpf at all"));
        new RpfBuilder().AddText("fine.txt", "ok").WriteTo(Path.Combine(Source, "good.rpf"));
        var log = new List<LogEntry>();

        var result = await Run(OutputMode.Dump, log: log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(log, e => e.Level == LogLevel.Error && e.Message.Contains("broken.rpf"));
        Assert.True(File.Exists(Path.Combine(Output, "extracted", "good.rpf", "fine.txt")));
    }

    [Fact]
    public async Task EncryptedArchiveWithoutKeys_ReportsHelpfulError()
    {
        var bytes = new RpfBuilder().AddText("a.txt", "a").Build();
        BitConverter.GetBytes((uint)RpfEncryption.Ng).CopyTo(bytes, 12);
        File.WriteAllBytes(Path.Combine(Source, "game.rpf"), bytes);
        var log = new List<LogEntry>();

        var result = await Run(OutputMode.Dump, log: log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(log, e => e.Level == LogLevel.Error && e.Message.Contains("GTA V folder"));
    }

    [Fact]
    public async Task Progress_ReachesTotalBytes()
    {
        WriteSampleMod();
        var progress = new SyncProgress();

        await Run(OutputMode.Dump, progress: progress);

        var last = progress.Reports.Last();
        Assert.Equal(ExtractionPhase.Completed, last.Phase);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(last.FilesTotal, last.FilesDone);
        Assert.True(last.BytesTotal > 0);
    }

    [Fact]
    public async Task Pause_HoldsWorkUntilResumed()
    {
        WriteSampleMod();
        var pause = new PauseController();
        pause.Pause();

        var task = Run(OutputMode.Dump, pause);
        await Task.Delay(300);

        Assert.False(task.IsCompleted);
        Assert.False(Directory.Exists(Path.Combine(Output, "extracted", "mymap")));

        pause.Resume();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(8, result.FilesWritten);
    }

    [Fact]
    public async Task Cancel_StopsExtraction()
    {
        WriteSampleMod();
        var pause = new PauseController();
        pause.Pause();
        using var cts = new CancellationTokenSource();

        var task = Run(OutputMode.Dump, pause, cts.Token);
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task MissingSourceFolder_Throws()
    {
        Directory.Delete(Source);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Run(OutputMode.Dump));
    }
}
