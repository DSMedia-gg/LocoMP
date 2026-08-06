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
        // Must SPIN, not assert flat: the join condition above is server-side + own-side state
        // only — B's PlayerJoined(A) notification is a separate reliable-ordered packet that can
        // still be in flight over real UDP when that condition first samples true. This was the
        // suite's long-unidentified ~1-in-30 flake (captured 2026-08-03, fail-run29): the one
        // cross-client assertion in the file with no wait around it. Machine load widens the
        // window, which is why it correlated with a live game — the 5 s budgets were never the
        // problem, and widening them would not have fixed this.
        Assert.True(SpinUntil(() => b.Players.ContainsKey(aId), 5000, pumps),
            "B should learn about A (roster broadcast) over UDP");

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

    /// <summary>
    /// F3 (2026-08-04 gauntlet): the connect key used to embed the protocol version, so a
    /// cross-protocol peer died at the SOCKET — a silent 10 s timeout the MismatchScreen could
    /// never explain. With the fixed key the socket admits the peer and the handshake names the
    /// mismatch: an exact, structured protocol reject over real UDP. Regression test — reverting
    /// to a versioned key turns this back into a timeout and fails it.
    /// </summary>
    [Fact]
    public void A_cross_protocol_peer_gets_a_named_reject_not_a_silent_timeout()
    {
        // The load-bearing property: the key must not depend on the protocol version. (A versioned
        // key can't be caught by the UDP half below — in one process both peers share this build's
        // constant, so the socket agrees regardless; on real builds each side derives its own.)
        Assert.DoesNotContain(ProtocolVersion.Current.ToString(), NetDefaults.ConnectKey);
        Assert.DoesNotContain((ProtocolVersion.Current - 1).ToString(), NetDefaults.ConnectKey);

        using var serverT = LiteNetLibTransport.StartServer(0, NetDefaults.ConnectKey);
        using var server = new NetServer(serverT, new ServerConfig(Identity), new ManualClock());

        var oldIdentity = new HandshakeRequest(ProtocolVersion.Current - 1, "B99.7", "0.0.2");
        using var oldT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, NetDefaults.ConnectKey);
        using var old = new NetClient(oldT, oldIdentity, "TimeTraveller", new ManualClock());

        Action[] pumps = { server.Poll, old.Poll };
        string? reason = null;
        old.Rejected += r => reason = r;

        Assert.True(SpinUntil(() => reason != null, 5000, pumps),
            "the cross-protocol peer must be rejected by the handshake, not left to time out");
        Assert.Contains("protocol mismatch", reason);
        Assert.Equal(RejectKind.Protocol, old.RejectDetail!.Value.Kind);
        Assert.Equal($"v{ProtocolVersion.Current - 1}", old.RejectDetail!.Value.ClientHas);
        Assert.Equal($"v{ProtocolVersion.Current}", old.RejectDetail!.Value.ServerNeeds);
        Assert.False(old.Joined);
    }

    /// <summary>
    /// Soak-teardown finding (2026-08-04): LiteNetLib 1.3.5 can raise PeerDisconnectedEvent with a
    /// NULL peer — a connect attempt dying at the socket layer queues a disconnect event that
    /// carries no peer object. The unguarded Dictionary.TryGetValue(null) crashed the whole bot
    /// swarm (unhandled ArgumentNullException in the poll loop); a real game client mid connect-
    /// retry has the same exposure. The null arrives from a socket-level race LiteNetLib won't
    /// reproduce on demand in-process, so this drives the private handler directly by reflection —
    /// the crash was in OUR handler, not the library, and this is exactly the call it received.
    /// Regression test: reverting the null guard fails it with the soak's exact exception.
    /// </summary>
    [Fact]
    public void A_null_peer_disconnect_event_is_ignored_not_a_crash()
    {
        using var clientT = LiteNetLibTransport.ConnectClient("127.0.0.1", 1, Key); // nobody listens
        bool disconnectRaised = false;
        clientT.PeerDisconnected += _ => disconnectRaised = true;

        System.Reflection.MethodInfo handler = typeof(LiteNetLibTransport).GetMethod(
            "OnPeerDisconnected",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotNull(handler);

        Exception? thrown = Record.Exception(() =>
        {
            try
            {
                handler.Invoke(clientT, new object?[] { null, default(LiteNetLib.DisconnectInfo) });
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                throw e.InnerException!; // surface the handler's own exception, as the poll loop would
            }
        });

        Assert.Null(thrown);
        Assert.False(disconnectRaised); // an unmapped non-peer is nothing to announce upward
    }

    [Fact]
    public void Udp_transport_counts_traffic_and_reports_a_peer_rtt()
    {
        using var serverT = LiteNetLibTransport.StartServer(0, Key);
        using var server = new NetServer(serverT, new ServerConfig(Identity), new ManualClock());

        using var aT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key);
        using var a = new NetClient(aT, Identity, "Alice", new ManualClock());

        Action[] pumps = { server.Poll, a.Poll };
        Assert.True(SpinUntil(() => a.Joined && server.PlayerCount == 1, 5000, pumps),
            "the client should join over UDP");

        // The join handshake alone crossed real bytes both ways.
        Assert.True(serverT.Stats.BytesReceived > 0, "the join request arrived over UDP");
        Assert.True(serverT.Stats.BytesSent > 0, "the join burst went out over UDP");

        // A live peer reports an RTT (0+ ms on localhost — the point is non-null); an unknown id is null.
        int aId = a.LocalId!.Value;
        Assert.NotNull(serverT.RttMs(aId));
        Assert.True(serverT.RttMs(aId) >= 0);
        Assert.Null(serverT.RttMs(9999));
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
