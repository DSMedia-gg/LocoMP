using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

public class PresenceSessionTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    /// <summary>Poll server-then-clients a few rounds to let queued sends/connects settle.</summary>
    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static NetClient Join(LoopbackNetwork hub, IClock clock, string name, out int id, string? password = null)
    {
        ITransport t = hub.Connect(out id);
        return new NetClient(t, Identity, name, clock, password);
    }

    [Fact]
    public void Two_clients_are_admitted_and_see_each_other_but_not_themselves()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using NetClient a = Join(hub, clock, "Alice", out int aId);
        using NetClient b = Join(hub, clock, "Bob", out int bId);
        Pump(server, new[] { a, b });

        Assert.Equal(2, server.PlayerCount);
        Assert.True(a.Joined);
        Assert.True(b.Joined);
        Assert.Equal(aId, a.LocalId);
        Assert.Equal(bId, b.LocalId);

        // Each mirrors the other, and never itself.
        Assert.True(a.Players.ContainsKey(bId));
        Assert.False(a.Players.ContainsKey(aId));
        Assert.True(b.Players.ContainsKey(aId));
        Assert.Equal("Bob", a.Players[bId].Name);
        Assert.Equal("Alice", b.Players[aId].Name);
    }

    [Fact]
    public void A_pose_from_one_client_is_relayed_to_the_other()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using NetClient a = Join(hub, clock, "Alice", out int aId);
        using NetClient b = Join(hub, clock, "Bob", out int bId);
        Pump(server, new[] { a, b });

        int moved = 0;
        Pose? seen = null;
        b.PlayerMoved += (id, pose) => { if (id == aId) { moved++; seen = pose; } };

        var p = new Pose(10f, 20f, 30f, 0f, 0f, 0f, 1f);
        a.SendPose(p);
        Pump(server, new[] { a, b });

        Assert.Equal(1, moved);
        Assert.Equal(p, seen);
        Assert.Equal(p, b.Players[aId].Pose);
        // The server holds the authoritative copy too.
        Assert.Equal(p, server.Players[aId].Pose);
    }

    [Fact]
    public void Wrong_password_is_rejected_with_a_reason_and_no_roster_entry()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, password: "hunter2"), clock);

        using NetClient a = Join(hub, clock, "Alice", out int _, password: "wrong");
        Pump(server, new[] { a });

        Assert.False(a.Joined);
        Assert.Equal(0, server.PlayerCount);
        Assert.NotNull(a.RejectReason);
        Assert.Contains("password", a.RejectReason);
    }

    [Fact]
    public void Correct_password_is_admitted()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, password: "hunter2"), clock);

        using NetClient a = Join(hub, clock, "Alice", out int _, password: "hunter2");
        Pump(server, new[] { a });

        Assert.True(a.Joined);
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void A_build_mismatch_is_rejected_before_admission()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var serverExpects = new HandshakeRequest(ProtocolVersion.Current, "B100", "0.0.2");
        using var server = new NetServer(hub.Server, new ServerConfig(serverExpects), clock);

        // Client advertises B99.7 (the shared Identity) — mismatch against the server's B100.
        using NetClient a = Join(hub, clock, "Alice", out int _);
        Pump(server, new[] { a });

        Assert.False(a.Joined);
        Assert.Equal(0, server.PlayerCount);
        Assert.Contains("build", a.RejectReason);
    }

    [Fact]
    public void A_full_server_rejects_further_joins()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using NetClient a = Join(hub, clock, "Alice", out int _);
        using NetClient b = Join(hub, clock, "Bob", out int _);
        Pump(server, new[] { a, b });

        Assert.Equal(1, server.PlayerCount);
        Assert.True(a.Joined);
        Assert.False(b.Joined);
        Assert.Contains("full", b.RejectReason);
    }

    [Fact]
    public void Client_derives_server_time_offset_from_join_and_time_sync()
    {
        var hub = new LoopbackNetwork();
        var serverClock = new ManualClock { NowMs = 5000 };
        var clientClock = new ManualClock { NowMs = 0 };
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), serverClock);

        ITransport t = hub.Connect(out int _);
        using var a = new NetClient(t, Identity, "Alice", clientClock);
        Pump(server, new[] { a });

        Assert.Equal(5000, a.ServerTimeOffsetMs);
        Assert.Equal(5000, a.EstimatedServerTimeMs);

        // Server clock advances; a broadcast re-syncs the client's offset.
        serverClock.NowMs = 8000;
        server.BroadcastTime();
        Pump(server, new[] { a });

        Assert.Equal(8000, a.ServerTimeOffsetMs);
    }
}
