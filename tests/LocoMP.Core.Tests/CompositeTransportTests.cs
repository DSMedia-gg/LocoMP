using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The host-embed topology (03 §6), game-free: one NetServer over a CompositeTransport whose inner
/// links are two separate Loopback hubs — standing in for "the host's own player over Loopback" and
/// "remote players over UDP". Both hubs number their peers from 1, so these tests prove the
/// composite's id remapping keeps the roster collision-free across links.
/// </summary>
public class CompositeTransportTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, NetClient[] clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void Clients_on_different_inner_links_join_one_roster_and_see_each_other()
    {
        var localHub = new LoopbackNetwork();   // stands in for the host player's Loopback link
        var remoteHub = new LoopbackNetwork();  // stands in for the UDP link
        var clock = new ManualClock();
        using var server = new NetServer(
            new CompositeTransport(localHub.Server, remoteHub.Server),
            new ServerConfig(Identity), clock);

        using var host = new NetClient(localHub.Connect(out _), Identity, "Host", clock);
        using var remote = new NetClient(remoteHub.Connect(out _), Identity, "Remote", clock);
        Pump(server, new[] { host, remote });

        Assert.True(host.Joined);
        Assert.True(remote.Joined);
        Assert.Equal(2, server.PlayerCount);

        // Both inner links assigned inner id 1 — the composite must still give distinct outer ids.
        Assert.NotEqual(host.LocalId, remote.LocalId);

        // Cross-link visibility with the right names.
        Assert.Equal("Remote", host.Players[remote.LocalId!.Value].Name);
        Assert.Equal("Host", remote.Players[host.LocalId!.Value].Name);
    }

    [Fact]
    public void A_pose_relays_across_inner_links()
    {
        var localHub = new LoopbackNetwork();
        var remoteHub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(
            new CompositeTransport(localHub.Server, remoteHub.Server),
            new ServerConfig(Identity), clock);

        using var host = new NetClient(localHub.Connect(out _), Identity, "Host", clock);
        using var remote = new NetClient(remoteHub.Connect(out _), Identity, "Remote", clock);
        Pump(server, new[] { host, remote });

        Pose? seen = null;
        host.PlayerMoved += (_, pose) => seen = pose;
        var p = new Pose(500f, 132f, 620f, 0f, 0.7071f, 0f, 0.7071f);
        remote.SendPose(p);
        Pump(server, new[] { host, remote });

        Assert.Equal(p, seen);
    }

    [Fact]
    public void A_disconnect_on_one_link_evicts_only_that_player()
    {
        var localHub = new LoopbackNetwork();
        var remoteHub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(
            new CompositeTransport(localHub.Server, remoteHub.Server),
            new ServerConfig(Identity), clock);

        using var host = new NetClient(localHub.Connect(out _), Identity, "Host", clock);
        var remoteTransport = remoteHub.Connect(out int remoteInnerId);
        using var remote = new NetClient(remoteTransport, Identity, "Remote", clock);
        Pump(server, new[] { host, remote });
        Assert.Equal(2, server.PlayerCount);

        int? leftSeen = null;
        host.PlayerLeft += id => leftSeen = id;
        int remoteOuterId = remote.LocalId!.Value;

        remoteHub.Disconnect(remoteInnerId);
        Pump(server, new[] { host, remote });

        Assert.Equal(1, server.PlayerCount);
        Assert.True(host.Joined);                    // the other link is untouched
        Assert.Equal(remoteOuterId, leftSeen);
        Assert.False(host.Players.ContainsKey(remoteOuterId));
    }
}
