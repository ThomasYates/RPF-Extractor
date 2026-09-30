using RpfToFiveM.Core.Extraction;

namespace RpfToFiveM.Tests;

public class PoolAdvisorTests
{
    private static readonly Dictionary<string, int> LiveLimits = new()
    {
        ["StaticBounds"] = 5000,
        ["TxdStore"] = 50000,
        ["FragmentStore"] = 30000,
        ["InteriorProxy"] = 450,
    };

    private static Dictionary<string, int> Base(int ybn = 13926, int ymap = 11086, int ytd = 40118, int ydr = 82690) => new()
    {
        [".ytd"] = ytd, [".ydr"] = ydr, [".yft"] = 42040, [".ybn"] = ybn, [".ydd"] = 10604, [".ytyp"] = 1717, [".ymap"] = ymap,
    };

    private static PoolUsage Pool(PoolReport r, string name) => r.Pools.Single(p => p.Pool == name);

    [Fact]
    public void OverflowingRaisablePool_RecommendsAnIncreaseWithHeadroom()
    {
        // Real numbers from a large map: 13,926 base + 6,847 new collisions vs a 20,200 pool.
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ybn"] = 6847 }, Base(), LiveLimits, mloInstances: 0);

        var p = Pool(r, "StaticBounds");
        Assert.Equal(PoolStatus.Increase, p.Status);
        Assert.Equal(20773, p.Projected);
        Assert.Equal(3000, p.RecommendedIncrease); // 20,773 * 1.1 = 22,850 -> +2,650 -> rounded up to 3,000
        Assert.Contains("increase_pool_size \"StaticBounds\" 3000", r.ServerCfgLines);
    }

    [Fact]
    public void PoolWithRoomToSpare_NeedsNothing()
    {
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ytd"] = 892 }, Base(), LiveLimits, 0);

        Assert.Equal(PoolStatus.Fine, Pool(r, "TxdStore").Status);
        Assert.DoesNotContain("TxdStore", r.ServerCfgLines);
    }

    [Fact]
    public void NearlyFullPool_IsRaisedEvenBeforeItOverflows()
    {
        // 19,000 of 20,200 is over 90% full; other resources on the server would tip it over.
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ybn"] = 5074 }, Base(), LiveLimits, 0);

        Assert.Equal(PoolStatus.Increase, Pool(r, "StaticBounds").Status);
    }

    [Fact]
    public void PoolFiveMWontLetYouRaise_GivesAWarningNotALine()
    {
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ymap"] = 1194 }, Base(), LiveLimits, 0);

        var p = Pool(r, "MapDataStore");
        Assert.Equal(PoolStatus.NotRaisable, p.Status);
        Assert.DoesNotContain("MapDataStore", r.ServerCfgLines);
        Assert.Contains("can't be raised", p.Advice);
        Assert.True(r.HasWarnings);
    }

    [Fact]
    public void UnraisablePoolThatStillFits_IsNotAWarning()
    {
        // After trimming, 11,700 of 12,000 ymaps: tight, but it fits.
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ymap"] = 614 }, Base(), LiveLimits, 0);

        var p = Pool(r, "MapDataStore");
        Assert.Equal(PoolStatus.Fine, p.Status);
        Assert.Contains("close", p.Advice);
        Assert.False(r.HasWarnings);
    }

    [Fact]
    public void NeedMoreThanTheMaximum_UsesTheMaximumAndSaysSo()
    {
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ybn"] = 20000 }, Base(), LiveLimits, 0);

        var p = Pool(r, "StaticBounds");
        Assert.Equal(PoolStatus.CannotFit, p.Status);
        Assert.Equal(5000, p.RecommendedIncrease);
        Assert.Contains("increase_pool_size \"StaticBounds\" 5000", r.ServerCfgLines);
        Assert.Contains("maximum", p.Advice);
    }

    [Fact]
    public void PoolMissingFromLiveLimits_IsTreatedAsNotRaisable()
    {
        // DrawableStore used to be raisable but isn't in FiveM's current limits list.
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ydr"] = 200000 }, Base(), LiveLimits, 0);

        Assert.Equal(PoolStatus.NotRaisable, Pool(r, "DrawableStore").Status);
    }

    [Fact]
    public void ManyInteriors_RecommendInteriorProxyIncrease()
    {
        // 300 interiors + 10% -> 330 -> rounded up to 500 -> capped at FiveM's +450.
        var r = PoolAdvisor.Analyze(new Dictionary<string, int>(), Base(), LiveLimits, mloInstances: 300);

        var p = Pool(r, "InteriorProxy");
        Assert.Equal(PoolStatus.Increase, p.Status);
        Assert.Equal(450, p.RecommendedIncrease);
    }

    [Fact]
    public void FewInteriors_NeedNothing()
    {
        var r = PoolAdvisor.Analyze(new Dictionary<string, int>(), Base(), LiveLimits, mloInstances: 12);
        Assert.Equal(PoolStatus.Fine, Pool(r, "InteriorProxy").Status);
    }

    [Fact]
    public void NoGameIndex_FallsBackToBuiltInBaseGameCounts()
    {
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ybn"] = 6847 }, baseGame: null, LiveLimits, 0);

        Assert.True(r.UsedBuiltInBaseCounts);
        Assert.Equal(PoolStatus.Increase, Pool(r, "StaticBounds").Status);
    }

    [Fact]
    public void NoLiveLimits_FallsBackToBuiltInLimits()
    {
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ybn"] = 6847 }, Base(), limits: null, 0);

        Assert.True(r.UsedBuiltInLimits);
        Assert.Contains("increase_pool_size \"StaticBounds\"", r.ServerCfgLines);
    }

    [Fact]
    public void ReportText_ExplainsEachPool()
    {
        var r = PoolAdvisor.Analyze(new Dictionary<string, int> { [".ybn"] = 6847, [".ymap"] = 1194 }, Base(), LiveLimits, 0);
        var text = PoolAdvisor.BuildReport(r);

        Assert.Contains("StaticBounds", text);
        Assert.Contains("MapDataStore", text);
        Assert.Contains("server.cfg", text);
    }
}
