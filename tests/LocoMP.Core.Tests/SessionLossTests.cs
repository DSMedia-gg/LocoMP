using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The "session lost" seam: <see cref="NetClient.Disconnected"/> surfaces a post-admission
/// transport drop (host died, eviction, network loss) so the frontend can show its
/// leave-to-restore prompt — and stays silent for links that never got admitted, because a failed
/// join is Rejected/timeout territory, not session loss.
/// </summary>
public class SessionLossTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "99-build2702", "0.0.2");

    [Fact]
    public void A_joined_client_learns_the_server_died_and_resets_its_mirrors()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        var client = new NetClient(hub.Connect(out _), Identity, "P", clock);
        for (int i = 0; i < 6; i++) { server.Poll(); client.Poll(); }
        Assert.True(client.Joined);

        int disconnects = 0;
        client.Disconnected += () => disconnects++;

        // Host process dies: its transport goes away and every client observes the drop (over
        // UDP this arrives via LiteNetLib's disconnect-on-Stop or the timeout — same seam).
        server.Dispose();
        hub.Server.Dispose();
        for (int i = 0; i < 4; i++) client.Poll();

        Assert.Equal(1, disconnects);
        Assert.False(client.Joined);
        Assert.Empty(client.Players);
        Assert.Empty(client.Trains.View.Sets);
    }

    [Fact]
    public void A_never_admitted_client_gets_no_session_lost_signal()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        var wrongBuild = new HandshakeRequest(ProtocolVersion.Current, "WRONG-BUILD", "0.0.2");
        var client = new NetClient(hub.Connect(out _), wrongBuild, "P", clock);

        int disconnects = 0;
        client.Disconnected += () => disconnects++;
        string? reason = null;
        client.Rejected += r => reason = r;

        for (int i = 0; i < 6; i++) { server.Poll(); client.Poll(); }
        Assert.NotNull(reason); // the join was refused — we never held a session to lose

        server.Dispose();
        hub.Server.Dispose();
        for (int i = 0; i < 4; i++) client.Poll();
        Assert.Equal(0, disconnects);
    }
}
