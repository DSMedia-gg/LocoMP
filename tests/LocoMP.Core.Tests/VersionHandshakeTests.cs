using LocoMP.Core.Protocol;
using Xunit;

namespace LocoMP.Core.Tests;

public class VersionHandshakeTests
{
    private static HandshakeRequest Req(int protocol, string build) => new(protocol, build, modVersion: "0.0.2");

    [Fact]
    public void Matching_versions_are_compatible()
    {
        var result = VersionHandshake.Check(Req(ProtocolVersion.Current, "B99.7"), Req(ProtocolVersion.Current, "B99.7"));

        Assert.True(result.Compatible);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Protocol_mismatch_is_rejected_with_have_need_reason()
    {
        var result = VersionHandshake.Check(Req(1, "B99.7"), Req(2, "B99.7"));

        Assert.False(result.Compatible);
        Assert.Contains("protocol", result.Reason);
    }

    [Fact]
    public void Game_build_mismatch_is_rejected_with_have_need_reason()
    {
        var result = VersionHandshake.Check(Req(1, "B99.7"), Req(1, "B100"));

        Assert.False(result.Compatible);
        Assert.Contains("build", result.Reason);
    }
}
