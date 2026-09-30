using System.Text;
using RpfToFiveM.Core.Extraction;

namespace RpfToFiveM.Tests;

public class SupportFileTests
{
    private static byte[] Xml(string s) => Encoding.UTF8.GetBytes(s);

    private const string ContentXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <CDataFileMgr__ContentsOfDataFileXml>
          <dataFiles>
            <Item>
              <filename>dlc_mymap:/common/data/my_lighting.xml</filename>
              <fileType>TIMECYCLEMOD_FILE</fileType>
            </Item>
            <Item>
              <filename>dlc_mymap:/%PLATFORM%/levels/gta5/mymap_burnt.rpf</filename>
              <fileType>RPF_FILE</fileType>
              <disabled value="true" />
            </Item>
            <Item>
              <filename>dlc_mymap:/%PLATFORM%/levels/gta5/mymap.rpf</filename>
              <fileType>RPF_FILE</fileType>
            </Item>
          </dataFiles>
        </CDataFileMgr__ContentsOfDataFileXml>
        """;

    [Theory]
    [InlineData("gtxd.meta", true)]
    [InlineData("content.xml", true)]
    [InlineData("config.ini", true)]
    [InlineData("mymap_game.dat151.rel", true)]
    [InlineData("12345.ymt", true)]
    [InlineData("props.ydr", false)]
    [InlineData("readme.pdf", false)]
    public void IsCandidate_PicksMetaXmlIniAndAudioData(string name, bool expected)
    {
        Assert.Equal(expected, SupportFiles.IsCandidate(name));
    }

    [Fact]
    public void ContentXml_ListsTypesAndOnDemandArchives()
    {
        var entries = ContentXmlFile.Parse(Xml(ContentXml));

        Assert.Contains(entries, e => e.FileName == "my_lighting.xml" && e.FileType == "TIMECYCLEMOD_FILE" && !e.Disabled);
        Assert.Contains(entries, e => e.FileName == "mymap_burnt.rpf" && e.Disabled);
    }

    [Fact]
    public void ContentXml_OtherXmlGivesNothing()
    {
        Assert.Empty(ContentXmlFile.Parse(Xml("<CVehicleModelInfo__InitDataList />")));
        Assert.Empty(ContentXmlFile.Parse(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Classify_UsesContentXmlTypeWhenKnown()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["my_lighting.xml"] = "TIMECYCLEMOD_FILE" };

        var d = SupportFiles.Classify("dlc.rpf/common/data/my_lighting.xml", Xml("<whatever />"), types);

        Assert.Equal(SupportKind.DataFile, d.Kind);
        Assert.Equal("TIMECYCLEMOD_FILE", d.DataFileType);
    }

    [Theory]
    [InlineData("gtxd.meta", "<CMapParentTxds><txdRelationships /></CMapParentTxds>", "GTXD_PARENTING_DATA")]
    [InlineData("parents.meta", "<CMapParentTxds />", "GTXD_PARENTING_DATA")]
    [InlineData("interiorproxies.meta", "<SInteriorOrderData />", "INTERIOR_PROXY_ORDER_FILE")]
    [InlineData("tc.xml", "<timecycle_modifier_data />", "TIMECYCLEMOD_FILE")]
    public void Classify_RecognisesMapDataByContent(string name, string xml, string type)
    {
        var d = SupportFiles.Classify("dlc.rpf/common/data/" + name, Xml(xml), null);

        Assert.Equal(SupportKind.DataFile, d.Kind);
        Assert.Equal(type, d.DataFileType);
    }

    [Theory]
    [InlineData("mymap_game.dat151.rel", "AUDIO_GAMEDATA")]
    [InlineData("mymap_mix.dat15.rel", "AUDIO_DYNAMIXDATA")]
    [InlineData("mymap_sounds.dat54.rel", "AUDIO_SOUNDDATA")]
    [InlineData("mymap_amp.dat10.rel", "AUDIO_SYNTHDATA")]
    public void Classify_AudioDataEvenInsideAudioFolders(string name, string type)
    {
        var d = SupportFiles.Classify("dlc.rpf/x64/audio/config/" + name, new byte[] { 1, 2 }, null);

        Assert.Equal(SupportKind.DataFile, d.Kind);
        Assert.Equal(type, d.DataFileType);
    }

    [Fact]
    public void DataFilePath_DropsTheAudioSuffixAsFiveMExpects()
    {
        Assert.Equal("data/a/mymap_game.dat", SupportFiles.DataFilePath("data/a/mymap_game.dat151.rel"));
        Assert.Equal("data/a/gtxd.meta", SupportFiles.DataFilePath("data/a/gtxd.meta"));
    }

    [Fact]
    public void Classify_NumericYmtIsAudioOcclusionForStream()
    {
        var d = SupportFiles.Classify("dlc.rpf/x64/levels/gta5/-1234567.ymt", new byte[] { 1 }, null);
        Assert.Equal(SupportKind.Stream, d.Kind);
    }

    [Theory]
    [InlineData("dlc.rpf/common/data/vehicles.meta", "<CVehicleModelInfo__InitDataList />")]
    [InlineData("dlc.rpf/common/data/handling.meta", "<CHandlingDataMgr />")]
    [InlineData("dlc.rpf/common/data/peds.meta", "<CPedModelInfo__InitDataList />")]
    [InlineData("dlc.rpf/content.xml", ContentXml)]
    [InlineData("dlc.rpf/setup2.xml", "<SSetupData />")]
    [InlineData("dlc.rpf/settings.ini", "[General]\nfoo=1")]
    [InlineData("dlc.rpf/common/data/dlctext.meta", "<CExtraTextMetaFile />")]
    [InlineData("dlc.rpf/x64/data/bink_casino_trailer.ymt", "")]
    [InlineData("dlc.rpf/x64/levels/gta5/streaming/mpcas_int_srl.ymt", "")]
    [InlineData("dlc.rpf/common/data/zonedassets.meta", "<CZonedAssets />")]
    [InlineData("dlc.rpf/x64/audio/dlc_speech.dat4.rel", "")]
    public void Classify_NonMapFilesAreNotUsed(string path, string content)
    {
        var d = SupportFiles.Classify(path, Xml(content), null);
        Assert.Equal(SupportKind.NotUsed, d.Kind);
        Assert.False(string.IsNullOrWhiteSpace(d.Reason));
    }

    [Theory]
    [InlineData("dlc.rpf/stuff/unknown.xml", "<SomethingNew />", "SomethingNew")]
    [InlineData("dlc.rpf/stuff/building.ymap.xml", "<CMapData />", "CodeWalker")]
    public void Classify_UnrecognisedFilesAreFlaggedForReview(string path, string content, string reasonContains)
    {
        var d = SupportFiles.Classify(path, Xml(content), null);
        Assert.Equal(SupportKind.Review, d.Kind);
        Assert.Contains(reasonContains, d.Reason);
    }

    [Fact]
    public void BuildManifest_AddsFilesAndDataFileEntries()
    {
        var manifest = FiveMResource.BuildManifest(
            new[] { @"stream\a.ytyp" },
            new[] { new DataFileEntry("GTXD_PARENTING_DATA", "data/x/gtxd.meta", "data/x/gtxd.meta"),
                    new DataFileEntry("AUDIO_GAMEDATA", "data/x/m_game.dat", "data/x/m_game.dat151.rel") });

        Assert.Contains("files {", manifest);
        Assert.Contains("'data/x/gtxd.meta',", manifest);
        Assert.Contains("'data/x/m_game.dat151.rel',", manifest);
        Assert.Contains("data_file 'GTXD_PARENTING_DATA' 'data/x/gtxd.meta'", manifest);
        Assert.Contains("data_file 'AUDIO_GAMEDATA' 'data/x/m_game.dat'", manifest);
        Assert.Contains("data_file 'DLC_ITYP_REQUEST' 'stream/a.ytyp'", manifest);
    }
}
