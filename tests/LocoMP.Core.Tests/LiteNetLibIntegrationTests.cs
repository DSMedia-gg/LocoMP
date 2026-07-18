using System;
using System.Diagnostics;
using System.Threading;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M1.2 integration: the SAME NetServer/NetClient stack that passed the Loopback storm, now over real
/// LiteNetLib UDP on localhost (two NetManagers in one process — no second machine, hard rule 8). Proves
/// the transport swap is invisible above the seam (03 §2): connect → handshake → pose relay → leave.
/// Uses ephemeral ports (StartServer(0)) so parallel test runs never collide, and bounded spin-pumps so
/// a hung socket fails fast instead of blocking the suite.
/// </summary>
public class LiteNetLibIntegrationTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");
    private const string Key = "locomp-test";

    /// <summary>Pump the given tick actions until <paramref name="cond"/> holds or the timeout elapses.</summary>
    private static bool SpinUntil(Func<bool> cond, int timeoutMs, params Action[] pumps)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (Action p in pumps) p();
            if (cond()) return true;
            Thread.Sleep(10);
        }
        foreach (Action p in pumps) p();
        return cond();
    }

    [Fact]
    public void Two_clients_connect_over_udp_see_each_other_relay_pose_and_leave()
    {
        using var serverT = LiteNetLibTransport.StartServer(0, Key);
        using var server = new NetServer(serverT, new ServerConfig(Identity), new ManualClock());

        using var aT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key);
        using var a = new NetClient(aT, Identity, "Alice", new ManualClock());
        using var bT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key);
        using var b = new NetClient(bT, Identity, "Bob", new ManualClock());

        Action[] pumps = { server.Poll, a.Poll, b.Poll };

        Assert.True(SpinUntil(() => a.Joined && b.Joined && server.PlayerCount == 2, 5000, pumps),
            "both clients should complete the handshake over UDP");

        int aId = a.LocalId!.Value;
        Assert.True(b.Players.ContainsKey(aId));

        // Pose from A must reach B over real UDP (sequenced-unreliable channel).
        Pose? seen = null;
        b.PlayerMoved += (id, pose) => { if (id == aId) seen = pose; };
        var p = new Pose(12.5f, 3f, -7f, 0f, 0f, 0f, 1f);
        a.SendPose(p);

        Assert.True(SpinUntil(() => seen.HasValue, 5000, pumps), "pose should relay over UDP");
        Assert.Equal(p, seen);

        // Graceful leave (app-level message) evicts A and notifies B.
        a.Leave();
        Assert.True(SpinUntil(() => server.PlayerCount == 1 && !b.Players.ContainsKey(aId), 5000, pumps),
            "leave should evict the player and notify the rest");
    }

    [Fact]
    public void A_wrong_connect_key_is_refused_by_the_transport()
    {
        using var serverT = LiteNetLibTransport.StartServer(0, Key);
        using var server = new NetServer(serverT, new ServerConfig(Identity), new ManualClock());

        using var badT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, "wrong-key");
        using var bad = new NetClient(badT, Identity, "Mallory", new ManualClock());

        Action[] pumps = { server.Poll, bad.Poll };

        // The server rejects the connection before any handshake — the client never joins.
        bool joined = SpinUntil(() => bad.Joined, 1500, pumps);
        Assert.False(joined);
        Assert.Equal(0, server.PlayerCount);
    }
}
