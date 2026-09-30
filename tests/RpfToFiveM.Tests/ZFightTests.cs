using System.Numerics;
using RpfToFiveM.Core.Extraction;
using RpfToFiveM.Core.Maps;
using RpfToFiveM.Tests.Fixtures;

namespace RpfToFiveM.Tests;

public sealed class ZFightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpf2fivem_zf_" + Guid.NewGuid().ToString("N"));
    private string Stream => Path.Combine(_root, "stream");

    public ZFightTests() => Directory.CreateDirectory(Stream);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Model(string name, bool embedded = false) =>
        File.WriteAllBytes(Path.Combine(Stream, name + ".ydr"), YtypBuilder.Drawable(embedded));

    private void Txd(string name) => File.WriteAllBytes(Path.Combine(Stream, name + ".ytd"), RpfBuilder.BuildRsc7(13, 0xD0000001, 0x50000000, new byte[64]));

    private string YmapPath(string name) => Path.Combine(Stream, name + ".ymap");

    private YmapFile Reload(string ymap) => YmapFile.Parse(File.ReadAllBytes(YmapPath(ymap)));

    private ZFightSummary Fix(ISet<uint>? vanillaTxds = null) =>
        ZFightFixer.Run(_root, new ZFightOptions { VanillaTextureDictionaries = vanillaTxds }, null, CancellationToken.None);

    // ---- ymap / ytyp / ydr parsing ----

    [Fact]
    public void Ymap_ParsesNameParentAndEntities()
    {
        var bytes = new YmapBuilder("my_map").Parent("my_map_lod")
            .Add("bank", 10, 20, 30)
            .Add("bank_lod", 10, 20, 30, lodLevel: 1, numChildren: 1)
            .Build();

        var ymap = YmapFile.Parse(bytes);

        Assert.Equal(YmapBuilder.Hash("my_map"), ymap.NameHash);
        Assert.Equal(YmapBuilder.Hash("my_map_lod"), ymap.ParentHash);
        Assert.Equal(2, ymap.Entities.Count);
        Assert.Equal(YmapBuilder.Hash("bank"), ymap.Entities[0].Archetype);
        Assert.Equal(new Vector3(10, 20, 30), ymap.Entities[0].Position);
        Assert.Equal(LodLevel.Lod, ymap.Entities[1].LodLevel);
        Assert.Equal(1u, ymap.Entities[1].NumChildren);
        Assert.Equal(-1, ymap.Entities[0].ParentIndex);
    }

    [Fact]
    public void Ymap_RemoveEntitiesRoundTrips()
    {
        var ymap = YmapFile.Parse(new YmapBuilder("m").Add("a", 0, 0, 0).Add("b", 1, 0, 0).Add("c", 2, 0, 0).Build());

        ymap.RemoveEntities(new[] { 1 });
        var reparsed = YmapFile.Parse(ymap.ToRsc7());

        Assert.Equal(new[] { YmapBuilder.Hash("a"), YmapBuilder.Hash("c") }, reparsed.Entities.Select(e => e.Archetype));
    }

    [Fact]
    public void Ymap_RejectsNonMetaData()
    {
        Assert.Throws<InvalidDataException>(() => YmapFile.Parse(RpfBuilder.BuildRsc7(2, 0, 0, new byte[32])));
    }

    [Fact]
    public void Ytyp_ParsesArchetypes()
    {
        var bytes = new YtypBuilder("props").Add("bank", "bank_txd", new Vector3(-1, -2, 0), new Vector3(1, 2, 3)).Build();

        var archetypes = YtypFile.Parse(bytes);

        var a = Assert.Single(archetypes);
        Assert.Equal(YmapBuilder.Hash("bank"), a.Name);
        Assert.Equal(YmapBuilder.Hash("bank_txd"), a.TextureDictionary);
        Assert.Equal(new Vector3(1, 2, 3), a.BoundsMax);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Drawable_DetectsEmbeddedTextures(bool embedded, bool expected)
    {
        Assert.Equal(expected, DrawableInfo.HasEmbeddedTextures(YtypBuilder.Drawable(embedded)));
    }

    [Fact]
    public void Drawable_UnknownFormatReturnsNull()
    {
        Assert.Null(DrawableInfo.HasEmbeddedTextures(RpfBuilder.BuildRsc7(159, 0, 0, new byte[64])));
    }

    // ---- the z-fight rule ----

    [Fact]
    public void GreyLodModelOnTopOfTexturedBuilding_GreyOneIsDeleted()
    {
        Model("bank", embedded: true);
        Model("bank_lod");
        new YtypBuilder("types").Add("bank", "").Add("bank_lod", "").WriteTo(Path.Combine(Stream, "types.ytyp"));
        new YmapBuilder("hd").Add("bank", 100, 200, 30).WriteTo(YmapPath("hd"));
        new YmapBuilder("extra").Add("bank_lod", 100, 200, 30).WriteTo(YmapPath("extra"));

        var result = Fix();

        Assert.Single(Reload("hd").Entities);
        Assert.False(File.Exists(YmapPath("extra")), "a ymap left with no entities is removed");
        Assert.Equal(1, result.Removed);
        Assert.Contains("bank_lod", File.ReadAllText(result.ReportPath!));
    }

    [Fact]
    public void UntexturedCopyOfSameSizedBuilding_IsDeleted()
    {
        Model("house_a", embedded: true);
        Model("house_grey");
        new YtypBuilder("types").Add("house_a", "", 12).Add("house_grey", "missing_txd", 12).WriteTo(Path.Combine(Stream, "types.ytyp"));
        new YmapBuilder("map").Add("house_a", 5, 5, 5).Add("house_grey", 5, 5, 5).Add("tree", 50, 50, 0).WriteTo(YmapPath("map"));

        var result = Fix(vanillaTxds: new HashSet<uint>());

        var left = Reload("map").Entities.Select(e => e.Archetype).ToList();
        Assert.Equal(new[] { YmapBuilder.Hash("house_a"), YmapBuilder.Hash("tree") }, left);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void ExactDuplicateOfTexturedBuilding_ExtraCopyIsDeleted()
    {
        Model("shop");
        Txd("shop_txd");
        new YtypBuilder("types").Add("shop", "shop_txd").WriteTo(Path.Combine(Stream, "types.ytyp"));
        new YmapBuilder("a").Add("shop", 1, 2, 3).WriteTo(YmapPath("a"));
        new YmapBuilder("b").Add("shop", 1.01f, 2, 3).Add("bench", 9, 9, 9).WriteTo(YmapPath("b"));

        var result = Fix();

        Assert.Single(Reload("a").Entities);
        Assert.Equal(new[] { YmapBuilder.Hash("bench") }, Reload("b").Entities.Select(e => e.Archetype));
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void ProperlyLinkedLod_IsLeftAlone()
    {
        Model("bank", embedded: true);
        Model("bank_lod");
        new YmapBuilder("city_lod").Add("bank_lod", 0, 0, 0, lodLevel: 1, numChildren: 1).WriteTo(YmapPath("city_lod"));
        new YmapBuilder("city").Parent("city_lod").Add("bank", 0, 0, 0, parentIndex: 0).WriteTo(YmapPath("city"));

        var result = Fix();

        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(YmapPath("city_lod")));
        Assert.Single(Reload("city").Entities);
    }

    [Fact]
    public void DifferentTexturedModelsSharingAPivot_AreLeftAlone()
    {
        Model("building", embedded: true);
        Model("building_glass", embedded: true);
        new YtypBuilder("types").Add("building", "", 30).Add("building_glass", "", 4).WriteTo(Path.Combine(Stream, "types.ytyp"));
        new YmapBuilder("map").Add("building", 0, 0, 0).Add("building_glass", 0, 0, 0).WriteTo(YmapPath("map"));

        var result = Fix();

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, Reload("map").Entities.Count);
    }

    [Fact]
    public void TexturesThatMightBeInTheBaseGame_AreNotTreatedAsGrey()
    {
        Model("house_a", embedded: true);
        Model("house_b");
        new YtypBuilder("types").Add("house_a", "", 12).Add("house_b", "vanilla_txd", 12).WriteTo(Path.Combine(Stream, "types.ytyp"));
        new YmapBuilder("map").Add("house_a", 5, 5, 5).Add("house_b", 5, 5, 5).WriteTo(YmapPath("map"));

        // No base-game index available: can't prove house_b is untextured.
        var unknown = Fix(vanillaTxds: null);
        Assert.Equal(0, unknown.Removed);

        // The base game has that texture dictionary: house_b is textured.
        var vanilla = Fix(vanillaTxds: new HashSet<uint> { YmapBuilder.Hash("vanilla_txd") });
        Assert.Equal(0, vanilla.Removed);
        Assert.Equal(2, Reload("map").Entities.Count);
    }

    [Fact]
    public void GreyInALodYmapWithOtherLinks_IsReportedNotEdited()
    {
        Model("bank", embedded: true);
        Model("bank_lod");
        new YtypBuilder("types").Add("bank", "").Add("bank_lod", "").WriteTo(Path.Combine(Stream, "types.ytyp"));
        new YmapBuilder("hd").Add("bank", 0, 0, 0).WriteTo(YmapPath("hd"));
        // bank_lod shares its ymap with a LOD that other (unknown) maps may point to by index.
        new YmapBuilder("lods").Add("bank_lod", 0, 0, 0).Add("other_lod", 500, 500, 0, lodLevel: 1, numChildren: 3)
            .WriteTo(YmapPath("lods"));

        var result = Fix();

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, Reload("lods").Entities.Count);
        Assert.Contains("not changed", File.ReadAllText(result.ReportPath!));
    }

    // ---- regressions from real game data ----

    [Fact]
    public void OverlappingLodLevels_AreTheLodSystemAndIgnored()
    {
        // A LOD and an SLOD of the same building overlap by design, even when not directly linked.
        new YmapBuilder("lods").Add("tower_lod", 0, 0, 0, lodLevel: 1).Add("tower_slod1", 0, 0, 0, lodLevel: 2)
            .WriteTo(YmapPath("lods"));

        var result = Fix();

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void HdLinkedToSlodThroughItsLod_IsLeftAlone()
    {
        Model("tower", embedded: true);
        // SLOD (index 1) is parent of LOD (index 0) inside the lod ymap; HD links to the LOD.
        new YmapBuilder("tower_lods")
            .Add("tower_lod", 0, 0, 0, lodLevel: 1, parentIndex: 1, numChildren: 1)
            .Add("tower_slod1", 0, 0, 0, lodLevel: 2, numChildren: 1)
            .WriteTo(YmapPath("tower_lods"));
        new YmapBuilder("tower_hd").Parent("tower_lods").Add("tower", 0, 0, 0, parentIndex: 0).WriteTo(YmapPath("tower_hd"));

        var result = Fix();

        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void TexturedDecalOverlayOfSameSize_IsIgnored()
    {
        Model("block", embedded: true);
        Model("block_decals", embedded: true);
        new YtypBuilder("types").Add("block", "", 20).Add("block_decals", "", 20).WriteTo(Path.Combine(Stream, "types.ytyp"));
        new YmapBuilder("map").Add("block", 0, 0, 0).Add("block_decals", 0, 0, 0).WriteTo(YmapPath("map"));

        var result = Fix();

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void VanillaLodModelBesideItsDetailedModel_IsLeftAlone()
    {
        // Rockstar ships x / x_lod pairs on purpose; the LOD is a real (low-res) textured model.
        new YmapBuilder("hd").Add("dt1_bank", 3, 3, 3).WriteTo(YmapPath("hd"));
        new YmapBuilder("mod").Add("dt1_bank_lod", 3, 3, 3).WriteTo(YmapPath("mod"));

        var result = ZFightFixer.Run(_root, new ZFightOptions { KnownNames = Names("dt1_bank", "dt1_bank_lod") }, null, CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Report_UsesModelNamesFromGameIndex()
    {
        new YmapBuilder("a").Add("dt1_bank", 3, 3, 3).WriteTo(YmapPath("a"));
        new YmapBuilder("b").Add("dt1_bank", 3, 3, 3).WriteTo(YmapPath("b"));

        var result = ZFightFixer.Run(_root, new ZFightOptions { KnownNames = Names("dt1_bank") }, null, CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Contains("dt1_bank in b.ymap", File.ReadAllText(result.ReportPath!));
    }

    private static Dictionary<uint, string> Names(params string[] names) => names.ToDictionary(YmapBuilder.Hash, n => n);

    [Fact]
    public void ScriptedStateVariants_AreNotComparedWithEachOther()
    {
        // e.g. house_unburnt / house_burnt: a script loads one or the other, never both.
        new YmapBuilder("house_unburnt").Scripted().Add("porch", 7, 7, 7).WriteTo(YmapPath("house_unburnt"));
        new YmapBuilder("house_burnt").Scripted().Add("porch", 7, 7, 7).WriteTo(YmapPath("house_burnt"));

        var result = Fix();

        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void DuplicateInsideOneScriptedYmap_IsStillRemoved()
    {
        new YmapBuilder("state_a").Scripted().Add("porch", 7, 7, 7).Add("porch", 7, 7, 7).WriteTo(YmapPath("state_a"));

        Assert.Equal(1, Fix().Removed);
    }

    [Theory]
    [InlineData(0.05f, 0f)]   // 5cm apart: stacked pieces, not a duplicate
    [InlineData(0f, 10f)]     // same spot but turned 10 degrees
    public void NearlyButNotExactlyTheSamePlacement_IsNotADuplicate(float offset, float degrees)
    {
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180);
        new YmapBuilder("map").Add("crate", 0, 0, 0).Add("crate", offset, 0, 0, rotation: turned).WriteTo(YmapPath("map"));

        Assert.Equal(0, Fix().Removed);
    }

    [Fact]
    public void CleanMap_ReportsNothingFound()
    {
        Model("a", embedded: true);
        new YmapBuilder("map").Add("a", 0, 0, 0).Add("a", 100, 0, 0).WriteTo(YmapPath("map"));

        var result = Fix();

        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BrokenYmap_IsSkippedWithWarning()
    {
        File.WriteAllBytes(YmapPath("broken"), new byte[] { 1, 2, 3 });
        var log = new List<LogEntry>();

        ZFightFixer.Run(_root, new ZFightOptions(), log.Add, CancellationToken.None);

        Assert.Contains(log, e => e.Level == LogLevel.Warning && e.Message.Contains("broken.ymap"));
    }
}
