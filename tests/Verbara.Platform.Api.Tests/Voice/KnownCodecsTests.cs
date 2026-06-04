using Verbara.Platform.Api.Voice;

namespace Verbara.Platform.Api.Tests.Voice;

public sealed class KnownCodecsTests
{
    private static readonly string[] CommonCodecs = ["ulaw", "alaw", "g722", "opus"];
    [Fact]
    public void InvalidTokens_ShouldReturnEmpty_WhenAllTokensKnown()
    {
        KnownCodecs.InvalidTokens("ulaw,alaw,g722").Should().BeEmpty();
    }

    [Fact]
    public void InvalidTokens_ShouldReturnEmpty_WhenStringNullOrBlank()
    {
        KnownCodecs.InvalidTokens(null).Should().BeEmpty();
        KnownCodecs.InvalidTokens("  ").Should().BeEmpty();
    }

    [Fact]
    public void InvalidTokens_ShouldReturnOffenders_WhenTokenMisspelled()
    {
        KnownCodecs.InvalidTokens("ulaw,ulwa,g722").Should().ContainSingle().Which.Should().Be("ulwa");
    }

    [Fact]
    public void InvalidTokens_ShouldBeCaseInsensitiveAndTrim()
    {
        KnownCodecs.InvalidTokens(" ULAW , Opus ").Should().BeEmpty();
    }

    [Fact]
    public void ParseInstalledCodecs_ShouldExtractKnownTokens_FromTabularOutput()
    {
        const string output = """
            ID   TYPE   NAME      FORMAT   DESCRIPTION
            0    audio  ulaw      ulaw     G.711 u-law
            1    audio  alaw      alaw     G.711 a-law
            2    audio  g722      g722     G.722
            3    audio  slin      slin     Signed Linear PCM (8kHz)
            100  video  vp8       vp8      VP8 video
            """;

        var codecs = KnownCodecs.ParseInstalledCodecs(output);

        codecs.Should().Equal("ulaw", "alaw", "g722", "vp8"); // slin filtered (not negotiable)
    }

    [Fact]
    public void ParseInstalledCodecs_ShouldReturnEmpty_WhenOutputBlank()
    {
        KnownCodecs.ParseInstalledCodecs("").Should().BeEmpty();
    }

    [Fact]
    public void FallbackCatalog_ShouldContainCommonCodecs()
    {
        KnownCodecs.FallbackCatalog.Should().Contain(CommonCodecs);
    }
}
