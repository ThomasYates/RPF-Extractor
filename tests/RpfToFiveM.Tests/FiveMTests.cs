using RpfToFiveM.Core.Extraction;

namespace RpfToFiveM.Tests;

public class FiveMTests
{
    [Theory]
    [InlineData("a.ymap", true)]
    [InlineData("A.YTYP", true)]
    [InlineData("b.ydr", true)]
    [InlineData("b.ydd", true)]
    [InlineData("b.yft", true)]
    [InlineData("b.ytd", true)]
    [InlineData("b.ybn", true)]
    [InlineData("b.ynv", true)]
    [InlineData("b.ynd", true)]
    [InlineData("b.ycd", true)]
    [InlineData("b.awc", false)]
    [InlineData("b.xml", false)]
    [InlineData("b.rpf", false)]
    [InlineData("noext", false)]
    public void IsMapFile_RecognisesStreamableMapAssets(string name, bool expected)
    {
        Assert.Equal(expected, FiveMResource.IsMapFile(name));
    }

    [Theory]
    [InlineData("dlc.rpf/x64/levels/gta5/props", false)]
    [InlineData("dlc.rpf/x64/models/cdimages/mymap_props.rpf", false)]
    [InlineData("speedway.rpf/stream", false)]
    [InlineData("dlc.rpf/x64/models/cdimages/pack_male.rpf/mp_m_freemode_01_male_x", true)]
    [InlineData("dlc.rpf/x64/models/cdimages/pack_female.rpf", true)]
    [InlineData("x64e.rpf/models/cdimages/streamedpeds_mp.rpf", true)]
    [InlineData("dlc.rpf/x64/vehicles.rpf", true)]
    [InlineData("dlc.rpf/x64/weapons.rpf", true)]
    [InlineData("dlc.rpf/x64/anim/clips", true)]
    [InlineData("dlc.rpf/x64/audio/sfx", true)]
    public void IsNonMapFolder_ExcludesPedsVehiclesWeaponsAnimsAndAudio(string folder, bool expected)
    {
        Assert.Equal(expected, FiveMResource.IsNonMapFolder(folder));
    }

    [Theory]
    [InlineData("a.ydr", 165u, false)]
    [InlineData("a.ydr", 159u, true)]
    [InlineData("a.ydd", 159u, true)]
    [InlineData("a.ytd", 13u, false)]
    [InlineData("a.ytd", 5u, true)]
    [InlineData("a.yft", 162u, false)]
    [InlineData("a.yft", 171u, true)]
    [InlineData("a.ymap", 2u, false)]
    public void IsGen9Resource_DetectsEnhancedEditionFormats(string name, uint version, bool expected)
    {
        Assert.Equal(expected, FiveMResource.IsGen9Resource(name, version));
    }

    [Theory]
    [InlineData("My Cool Map!", "my_cool_map")]
    [InlineData("  ", "my_map")]
    [InlineData("already-ok_1", "already-ok_1")]
    public void SanitizeResourceName_ProducesFiveMFriendlyName(string input, string expected)
    {
        Assert.Equal(expected, FiveMResource.SanitizeResourceName(input));
    }

    [Fact]
    public void BuildManifest_ListsYtypsWithForwardSlashes()
    {
        var manifest = FiveMResource.BuildManifest(new[] { @"stream\pack\b.ytyp", @"stream\pack\a.ytyp" });

        Assert.Contains("this_is_a_map 'yes'", manifest);
        var a = manifest.IndexOf("data_file 'DLC_ITYP_REQUEST' 'stream/pack/a.ytyp'", StringComparison.Ordinal);
        var b = manifest.IndexOf("data_file 'DLC_ITYP_REQUEST' 'stream/pack/b.ytyp'", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a);
    }
}
