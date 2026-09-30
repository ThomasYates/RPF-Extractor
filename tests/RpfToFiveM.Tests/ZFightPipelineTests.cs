using RpfToFiveM.Core.Extraction;
using RpfToFiveM.Core.Maps;
using RpfToFiveM.Tests.Fixtures;

namespace RpfToFiveM.Tests;

public sealed class ZFightPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpf2fivem_zfp_" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "src");
    private string Output => Path.Combine(_root, "out");

    public ZFightPipelineTests()
    {
        Directory.CreateDirectory(Source);
        // A map with an untextured copy sitting on a textured building.
        var hd = new YmapBuilder("hd").Add("bank", 0, 0, 0).Build();
        var extra = new YmapBuilder("extra").Add("bank_lod", 0, 0, 0).Build();
        new RpfBuilder()
            .AddRaw("stream/hd.ymap", hd)
            .AddRaw("stream/extra.ymap", extra)
            .AddRaw("stream/bank.ydr", YtypBuilder.Drawable(embeddedTextures: true))
            .AddRaw("stream/bank_lod.ydr", YtypBuilder.Drawable(embeddedTextures: false))
            .AddRaw("stream/types.ytyp", new YtypBuilder("types").Add("bank", "").Add("bank_lod", "").Build())
            .WriteTo(Path.Combine(Source, "mymap", "dlc.rpf"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Task<ExtractionResult> Run(OutputMode mode, ZFightMode zfight) =>
        new RpfExtractor().RunAsync(new ExtractionOptions
        {
            SourceFolder = Source,
            OutputFolder = Output,
            Mode = mode,
            ResourceName = "my_map",
            ZFight = zfight,
        }, null, null, new PauseController());

    private static int CountFiles(string dir, string name) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, name, SearchOption.AllDirectories).Length : 0;

    [Fact]
    public async Task Off_LeavesMapUntouchedAndWritesNoReport()
    {
        await Run(OutputMode.FiveM, ZFightMode.Off);

        Assert.Equal(1, CountFiles(Path.Combine(Output, "my_map"), "extra.ymap"));
        Assert.Equal(0, CountFiles(Output, ZFightFixer.ReportFileName));
    }

    [Fact]
    public async Task On_FixesTheSingleOutput()
    {
        var result = await Run(OutputMode.FiveM, ZFightMode.On);

        var res = Path.Combine(Output, "my_map");
        Assert.Equal(0, CountFiles(res, "extra.ymap"));
        Assert.True(File.Exists(Path.Combine(res, ZFightFixer.ReportFileName)));
        Assert.Equal(res, result.OutputRoot);
        Assert.Null(result.FixedOutputRoot);
    }

    [Fact]
    public async Task Both_FiveM_ProducesTwoResources()
    {
        var result = await Run(OutputMode.FiveM, ZFightMode.Both);

        var plain = Path.Combine(Output, "my_map");
        var fixedRes = Path.Combine(Output, "my_map_zfix");
        Assert.Equal(1, CountFiles(plain, "extra.ymap"));
        Assert.Equal(0, CountFiles(fixedRes, "extra.ymap"));
        Assert.True(File.Exists(Path.Combine(plain, "fxmanifest.lua")));
        Assert.True(File.Exists(Path.Combine(fixedRes, "fxmanifest.lua")));
        Assert.False(File.Exists(Path.Combine(plain, ZFightFixer.ReportFileName)));
        Assert.True(File.Exists(Path.Combine(fixedRes, ZFightFixer.ReportFileName)));
        Assert.Equal(plain, result.OutputRoot);
        Assert.Equal(fixedRes, result.FixedOutputRoot);
    }

    [Fact]
    public async Task Both_Dump_ProducesTwoFolders()
    {
        var result = await Run(OutputMode.Dump, ZFightMode.Both);

        var plain = Path.Combine(Output, "extracted");
        var fixedDir = Path.Combine(Output, "extracted (ZFightFix)");
        Assert.Equal(1, CountFiles(plain, "extra.ymap"));
        Assert.Equal(0, CountFiles(fixedDir, "extra.ymap"));
        Assert.Equal(1, CountFiles(fixedDir, "hd.ymap"));
        Assert.Equal(plain, result.OutputRoot);
        Assert.Equal(fixedDir, result.FixedOutputRoot);
    }

    [Fact]
    public async Task Progress_ReportsZFightPhase()
    {
        var phases = new List<ExtractionPhase>();
        await new RpfExtractor().RunAsync(new ExtractionOptions
        {
            SourceFolder = Source, OutputFolder = Output, Mode = OutputMode.FiveM, ZFight = ZFightMode.Both,
        }, new SyncProgress(p => { lock (phases) phases.Add(p.Phase); }), null, new PauseController());

        Assert.Contains(ExtractionPhase.CheckingZFight, phases);
    }

    [Fact]
    public async Task Both_ReportsEachStepInOrder()
    {
        var phases = new List<ExtractionPhase>();
        await new RpfExtractor().RunAsync(new ExtractionOptions
        {
            SourceFolder = Source, OutputFolder = Output, Mode = OutputMode.FiveM, ZFight = ZFightMode.Both,
        }, new SyncProgress(p => { lock (phases) phases.Add(p.Phase); }), null, new PauseController());

        var order = phases.Distinct().ToList();
        Assert.Equal(new[]
        {
            ExtractionPhase.Scanning, ExtractionPhase.Extracting, ExtractionPhase.Finishing,
            ExtractionPhase.CopyingForZFix, ExtractionPhase.CheckingZFight, ExtractionPhase.Completed,
        }, order);
    }

    [Fact]
    public async Task CopyStep_ReportsBytesUntilComplete()
    {
        var copy = new List<ExtractionProgress>();
        await new RpfExtractor().RunAsync(new ExtractionOptions
        {
            SourceFolder = Source, OutputFolder = Output, Mode = OutputMode.FiveM, ZFight = ZFightMode.Both,
        }, new SyncProgress(p => { if (p.Phase == ExtractionPhase.CopyingForZFix) lock (copy) copy.Add(p); }), null, new PauseController());

        var last = copy.Last();
        Assert.True(last.BytesTotal > 0);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(last.FilesTotal, last.FilesDone);
    }

    [Fact]
    public async Task NotEnoughSpaceForCopy_SkipsZFixButKeepsTheMap()
    {
        var log = new List<LogEntry>();
        var result = await new RpfExtractor().RunAsync(new ExtractionOptions
        {
            SourceFolder = Source, OutputFolder = Output, Mode = OutputMode.FiveM, ZFight = ZFightMode.Both,
            FreeSpace = _ => 10, // bytes
        }, null, e => { lock (log) log.Add(e); }, new PauseController());

        Assert.True(File.Exists(Path.Combine(Output, "my_map", "fxmanifest.lua")));
        Assert.False(Directory.Exists(Path.Combine(Output, "my_map_zfix")));
        Assert.Null(result.FixedOutputRoot);
        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(log, e => e.Level == LogLevel.Error && e.Message.Contains("space"));
    }

    [Fact]
    public void GameAssetIndex_CollectsTextureDictionariesAndModelNames()
    {
        var game = Path.Combine(_root, "game");
        var inner = new RpfBuilder().AddResource("textures/vanilla_txd.ytd", new byte[] { 1 });
        new RpfBuilder().AddNested("x64/levels.rpf", inner).AddResource("other.ydr", new byte[] { 2 })
            .WriteTo(Path.Combine(game, "x64a.rpf"));

        var index = GameAssetIndex.Build(game, keys: null, CancellationToken.None);

        Assert.Equal(1, index.CountOf(".ytd"));
        Assert.Equal(1, index.CountOf(".ydr"));
        Assert.True(index.Contains(".ydr", "OTHER"));
        Assert.Contains(YmapBuilder.Hash("vanilla_txd"), index.TextureDictionaries);
        Assert.DoesNotContain(YmapBuilder.Hash("other"), index.TextureDictionaries);
        Assert.Equal("other", index.Names[YmapBuilder.Hash("other")]);
    }

    private sealed class SyncProgress(Action<ExtractionProgress> onReport) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) => onReport(value);
    }
}
