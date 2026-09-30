using RpfToFiveM.Core.Rpf;

namespace RpfToFiveM.Tests;

public class SafePathTests
{
    [Theory]
    [InlineData(@"mymap\dlc.rpf", @"mymap\dlc.rpf")]
    [InlineData("a/b/c.ydr", @"a\b\c.ydr")]
    [InlineData(@"..\..\evil.txt", @"_\_\evil.txt")]
    [InlineData("con.txt", "_con.txt")]
    public void Relative_SplitsOnBothSeparatorsAndSanitises(string input, string expected)
    {
        Assert.Equal(expected, SafePath.Relative(input));
    }

    [Theory]
    [InlineData("ok.ymap", "ok.ymap")]
    [InlineData("bad:name?.ydr", "bad_name_.ydr")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("", "_")]
    public void Segment_ReplacesInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, SafePath.Segment(input));
    }
}
