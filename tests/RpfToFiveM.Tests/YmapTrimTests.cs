using RpfToFiveM.Core.Extraction;
using RpfToFiveM.Core.Maps;
using RpfToFiveM.Tests.Fixtures;

namespace RpfToFiveM.Tests;

public sealed class YmapTrimTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpf2fivem_trim_" + Guid.NewGuid().ToString("N"));
    private string Stream => Path.Combine(_root, "stream", "pack");

    public YmapTrimTests() => Directory.CreateDirectory(Stream);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Ymap(string name) => Path.Combine(Stream, name + ".ymap");

    private static string[] Names(IEnumerable<string> paths) =>
        paths.Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToArray()!;

    // ---- reading ----

    [Fact]
    public void GrassYmap_WithDataInGraphicsPages_Parses()
    {
        var ymap = YmapFile.Parse(new YmapBuilder("field_grass_0").Grass(batches: 12).Build());

        Assert.Empty(ymap.Entities);
        Assert.Equal(12, ymap.GrassBatches);
        Assert.False(ymap.IsEmpty);
        Assert.True(ymap.IsGrassOnly);
    }

    [Fact]
    public void YmapWithNothingInIt_IsEmpty()
    {
        var ymap = YmapFile.Parse(new YmapBuilder("tmp_03").Build());
        Assert.True(ymap.IsEmpty);
        Assert.False(ymap.IsGrassOnly);
    }

    [Theory]
    [InlineData("cargen")]
    [InlineData("timecycle")]
    [InlineData("occluder")]
    public void YmapWithOnlyNonEntityContent_IsNotEmpty(string kind)
    {
        var b = new YmapBuilder("x");
        _ = kind switch
        {
            "cargen" => b.CarGenerators(2),
            "timecycle" => b.TimecycleModifiers(1),
            _ => b.BoxOccluders(3),
        };

        var ymap = YmapFile.Parse(b.Build());

        Assert.False(ymap.IsEmpty);
        Assert.False(ymap.IsGrassOnly);
    }

    // ---- trimming ----

    [Fact]
    public void EmptyYmaps_AreAlwaysSetAside()
    {
        new YmapBuilder("tmp_01").WriteTo(Ymap("tmp_01"));
        new YmapBuilder("buildings").Add("house", 0, 0, 0).WriteTo(Ymap("buildings"));

        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 1000, limit: 12000, spare: 300, log: null);

        Assert.Equal(new[] { "tmp_01" }, Names(r.RemovedEmpty));
        Assert.Empty(r.RemovedGrass);
        Assert.False(File.Exists(Ymap("tmp_01")));
        Assert.True(File.Exists(Ymap("buildings")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "_not_used", "ymaps_removed"), "tmp_01.ymap", SearchOption.AllDirectories));
    }

    [Fact]
    public void GrassIsKeptWhenThereIsRoom()
    {
        new YmapBuilder("field_grass_0").Grass(5).WriteTo(Ymap("field_grass_0"));

        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 1000, limit: 12000, spare: 300, log: null);

        Assert.Empty(r.RemovedGrass);
        Assert.True(File.Exists(Ymap("field_grass_0")));
        Assert.False(r.StillOver);
    }

    [Fact]
    public void OverTheLimit_SmallestGrassGoesFirstUntilItFits()
    {
        // 4 map ymaps + 96 base = 100; the limit minus spare leaves room for 98, so 2 must go.
        new YmapBuilder("big_grass_0").Grass(50, bytes: 60000).WriteTo(Ymap("big_grass_0"));
        new YmapBuilder("mid_grass_0").Grass(20, bytes: 20000).WriteTo(Ymap("mid_grass_0"));
        new YmapBuilder("tiny_grass_0").Grass(1, bytes: 600).WriteTo(Ymap("tiny_grass_0"));
        new YmapBuilder("buildings").Add("house", 0, 0, 0).WriteTo(Ymap("buildings"));

        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 96, limit: 100, spare: 2, log: null);

        Assert.Equal(new[] { "mid_grass_0", "tiny_grass_0" }, Names(r.RemovedGrass));
        Assert.True(File.Exists(Ymap("big_grass_0")));
        Assert.True(File.Exists(Ymap("buildings")));
        Assert.Equal(98, r.ProjectedAfter);
        Assert.False(r.StillOver);
    }

    [Fact]
    public void EmptyYmapsCountTowardsFitting()
    {
        new YmapBuilder("tmp_01").WriteTo(Ymap("tmp_01"));
        new YmapBuilder("a_grass_0").Grass(3).WriteTo(Ymap("a_grass_0"));
        new YmapBuilder("buildings").Add("house", 0, 0, 0).WriteTo(Ymap("buildings"));

        // 3 map + 95 base = 98; room for 97 -> removing the empty one is enough.
        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 95, limit: 100, spare: 3, log: null);

        Assert.Single(r.RemovedEmpty);
        Assert.Empty(r.RemovedGrass);
    }

    [Fact]
    public void YmapsReplacingBaseGameOnes_DontCountAsNewSlots()
    {
        new YmapBuilder("vanilla_area").Add("house", 0, 0, 0).WriteTo(Ymap("vanilla_area"));
        new YmapBuilder("new_area").Add("shop", 0, 0, 0).WriteTo(Ymap("new_area"));

        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 100, limit: 12000, spare: 0, log: null,
            isBaseGameYmap: name => name == "vanilla_area");

        Assert.Equal(101, r.ProjectedBefore);
    }

    [Fact]
    public void BuildingsAreNeverRemoved_EvenIfStillOver()
    {
        new YmapBuilder("a").Add("house", 0, 0, 0).WriteTo(Ymap("a"));
        new YmapBuilder("b").Add("shop", 0, 0, 0).WriteTo(Ymap("b"));

        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 200, limit: 100, spare: 0, log: null);

        Assert.True(File.Exists(Ymap("a")));
        Assert.True(File.Exists(Ymap("b")));
        Assert.True(r.StillOver);
    }

    [Fact]
    public void EmptyYmapThatOthersUseAsLodParent_IsKept()
    {
        new YmapBuilder("area_lod").WriteTo(Ymap("area_lod"));
        new YmapBuilder("area").Parent("area_lod").Add("house", 0, 0, 0).WriteTo(Ymap("area"));

        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 0, limit: 12000, spare: 0, log: null);

        Assert.Empty(r.RemovedEmpty);
        Assert.True(File.Exists(Ymap("area_lod")));
    }

    [Fact]
    public void Summary_ListsWhatWasSetAside()
    {
        new YmapBuilder("tmp_01").WriteTo(Ymap("tmp_01"));

        var r = YmapTrimmer.Trim(_root, baseGameYmaps: 0, limit: 12000, spare: 0, log: null);

        Assert.Contains("tmp_01", YmapTrimmer.Describe(r));
    }

    [Fact]
    public async Task FiveMExtraction_TrimsEmptyYmapsBeforeCountingPools()
    {
        var src = Path.Combine(_root, "src");
        new RpfBuilder()
            .AddRaw("stream/tmp_01.ymap", new YmapBuilder("tmp_01").Build())
            .AddRaw("stream/real.ymap", new YmapBuilder("real").Add("house", 0, 0, 0).Build())
            .WriteTo(Path.Combine(src, "pack.rpf"));
        var output = Path.Combine(_root, "out");

        var result = await new RpfExtractor().RunAsync(new ExtractionOptions
        {
            SourceFolder = src, OutputFolder = output, Mode = OutputMode.FiveM,
        }, null, null, new PauseController());

        var res = Path.Combine(output, "my_map");
        Assert.Empty(Directory.GetFiles(Path.Combine(res, "stream"), "tmp_01.ymap", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Path.Combine(res, "stream"), "real.ymap", SearchOption.AllDirectories));
        Assert.Equal(1, result.Pools!.Pools.Single(p => p.Pool == "MapDataStore").MapAdds);
        Assert.Contains("tmp_01", File.ReadAllText(Path.Combine(res, PoolAdvisor.ReportFileName)));
    }
}
