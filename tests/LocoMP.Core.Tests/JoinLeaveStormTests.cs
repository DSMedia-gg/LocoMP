using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M1 exit criterion (harness half, 07 §M1): "8 simulated clients join/leave storm without leaks."
/// Runs entirely game-free over the Loopback hub (hard rule 8) — this is the test that would live in
/// pr.yml. It hammers the join → roster → evict lifecycle many rounds and asserts the server returns
/// to a clean state each time, so no roster entry, hub endpoint, or subscription accumulates.
/// </summary>
public class JoinLeaveStormTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void Eight_clients_join_and_leave_repeatedly_without_leaking_state()
    {
        const int waves = 25;
        const int perWave = 8;

        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: perWave), clock);

        for (int wave = 0; wave < waves; wave++)
        {
            var clients = new List<NetClient>(perWave);
            var ids = new List<int>(perWave);

            // Storm in.
            for (int k = 0; k < perWave; k++)
            {
                ITransport t = hub.Connect(out int id);
                clients.Add(new NetClient(t, Identity, "P" + id, clock));
                ids.Add(id);
            }
            Pump(server, clients);

            Assert.Equal(perWave, server.PlayerCount);
            Assert.Equal(perWave, hub.ClientCount);
            foreach (NetClient c in clients) Assert.True(c.Joined);
            // Every admitted client sees the other seven.
            foreach (NetClient c in clients) Assert.Equal(perWave - 1, c.Players.Count);

            // Storm out.
            foreach (int id in ids) hub.Disconnect(id);
            Pump(server, clients);

            Assert.Equal(0, server.PlayerCount);
            Assert.Equal(0, hub.ClientCount);

            foreach (NetClient c in clients) c.Dispose();
        }

        // After every wave the server is empty — no accumulation across 25 storms.
        Assert.Equal(0, server.PlayerCount);
        Assert.Equal(0, hub.ClientCount);
    }

    [Fact]
    public void A_graceful_leave_evicts_the_player_and_notifies_the_rest()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        ITransport ta = hub.Connect(out int aId);
        using var a = new NetClient(ta, Identity, "Alice", clock);
        ITransport tb = hub.Connect(out int bId);
        using var b = new NetClient(tb, Identity, "Bob", clock);
        Pump(server, new[] { a, b });
        Assert.Equal(2, server.PlayerCount);

        int leftSeen = -1;
        b.PlayerLeft += id => leftSeen = id;

        a.Leave();
        Pump(server, new[] { a, b });

        Assert.Equal(1, server.PlayerCount);
        Assert.False(server.Players.ContainsKey(aId));
        Assert.Equal(aId, leftSeen);
        Assert.False(b.Players.ContainsKey(aId));
    }
}
