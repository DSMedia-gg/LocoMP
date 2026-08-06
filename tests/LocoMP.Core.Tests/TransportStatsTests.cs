using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.2 transport stats: the <see cref="ITransport"/> bandwidth counters + per-peer RTT that feed the
/// diagnostics panel. Deterministic half over the Loopback hub (bytes are counted uniformly, latency is
/// 0 in-process); the real-UDP RTT/bytes ride the LiteNetLib integration suite.
/// </summary>
public class TransportStatsTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void A_session_moves_bytes_and_the_diagnostics_report_them()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var alice = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice });
        Assert.True(alice.Joined);

        ServerDiagnostics d = server.CaptureDiagnostics();
        Assert.True(d.BytesReceived > 0, "the join request + burst acks arrived");
        Assert.True(d.BytesSent > 0, "the join burst went out");
        Assert.True(d.MessagesSent > 0);
        Assert.True(d.MessagesReceived > 0);
    }

    [Fact]
    public void Loopback_rtt_is_zero_for_a_live_peer_and_null_for_a_stranger()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var alice = new NetClient(hub.Connect(out int aliceId), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice });

        Assert.Equal(0, server.RttMs(aliceId));   // in-process link, no latency
        Assert.Null(server.RttMs(9999));          // no such peer
    }

    [Fact]
    public void Composite_stats_are_the_sum_of_every_inner_link()
    {
        var (a1, b1) = LoopbackTransport.CreatePair();
        var (a2, b2) = LoopbackTransport.CreatePair();
        using (b1) using (b2)
        {
            a1.Send(1, new byte[10], DeliveryMethod.ReliableOrdered);
            a2.Send(1, new byte[20], DeliveryMethod.ReliableOrdered);
            a2.Send(1, new byte[5], DeliveryMethod.SequencedUnreliable);

            using var composite = new CompositeTransport(a1, a2); // owns + disposes a1/a2
            TransportStats s = composite.Stats;
            Assert.Equal(35, s.BytesSent);       // 10 + 20 + 5
            Assert.Equal(3, s.MessagesSent);
        }
    }
}
