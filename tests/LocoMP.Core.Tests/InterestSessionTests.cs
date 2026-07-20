using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// End-to-end interest management over the Loopback harness (D10): with filtering enabled, a far
/// player's pose stream is gated off and its avatar hidden, then re-shown on approach — while global
/// roster state (identity) is never filtered. The default (disabled) config is proven to change
/// nothing.
/// </summary>
public class InterestSessionTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static InterestConfig Filtering() => new()
    {
        Enabled = true,
        FilterPlayers = true,
        EnterRadiusM = 100f,
        LeaveRadiusM = 150f,
        RecomputeIntervalMs = 1, // recompute every pumped tick (the clock advances each round)
    };

    /// <summary>Advance the clock (so the throttled Recompute fires) then pump server-then-clients.</summary>
    private static void Pump(ManualClock clock, NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            clock.NowMs += 50;
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static NetClient Join(LoopbackNetwork hub, IClock clock, string name, out int id)
    {
        ITransport t = hub.Connect(out id);
        return new NetClient(t, Identity, name, clock);
    }

    private static Pose At(float x, float z) => new(x, 0f, z, 0f, 0f, 0f, 1f);

    [Fact]
    public void A_far_player_is_hidden_then_reshown_on_approach()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()), clock);

        using NetClient a = Join(hub, clock, "Alice", out int aId);
        using NetClient b = Join(hub, clock, "Bob", out int bId);
        var clients = new[] { a, b };
        Pump(clock, server, clients);

        int aliceSawBobMove = 0;
        bool aliceHidBob = false;
        a.PlayerMoved += (id, _) => { if (id == bId) aliceSawBobMove++; };
        a.PlayerHidden += id => { if (id == bId) aliceHidBob = true; };

        // Both post their first pose FAR apart (500 m ≫ the 150 m leave radius).
        a.SendPose(At(0f, 0f));
        b.SendPose(At(500f, 0f));
        Pump(clock, server, clients);

        // Alice briefly received Bob's pose (fail-open before her first anchor), then the trim hid him.
        Assert.True(aliceHidBob);
        Assert.False(server.Interest.IsRelevant(aId, new EntityKey(EntityKind.Player, bId)));

        // Bob keeps streaming from afar: none of it reaches Alice now (the point — bandwidth saved).
        int before = aliceSawBobMove;
        for (int i = 0; i < 3; i++) { b.SendPose(At(500f, 0f)); Pump(clock, server, clients); }
        Assert.Equal(before, aliceSawBobMove);

        // Bob walks into range: his stream resumes and Alice sees him move again.
        for (int i = 0; i < 5; i++) { b.SendPose(At(40f, 0f)); Pump(clock, server, clients); }
        Assert.True(aliceSawBobMove > before);
        Assert.True(server.Interest.IsRelevant(aId, new EntityKey(EntityKind.Player, bId)));
    }

    [Fact]
    public void Global_roster_identity_is_never_filtered()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()), clock);

        using NetClient a = Join(hub, clock, "Alice", out int aId);
        using NetClient b = Join(hub, clock, "Bob", out int bId);
        var clients = new[] { a, b };
        Pump(clock, server, clients);

        a.SendPose(At(0f, 0f));
        b.SendPose(At(500f, 0f));
        Pump(clock, server, clients);

        // Bob's pose stream is gated off for Alice, but his IDENTITY (roster entry) is global state and
        // must remain — interest filters telemetry, never who-is-in-the-session.
        Assert.False(server.Interest.IsRelevant(aId, new EntityKey(EntityKind.Player, bId)));
        Assert.True(a.Players.ContainsKey(bId));
        Assert.Equal("Bob", a.Players[bId].Name);
    }

    [Fact]
    public void Disabled_interest_relays_far_poses_exactly_as_before()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        // Default config → interest disabled: distance is irrelevant, everything relays.
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using NetClient a = Join(hub, clock, "Alice", out int aId);
        using NetClient b = Join(hub, clock, "Bob", out int bId);
        var clients = new[] { a, b };
        Pump(clock, server, clients);

        int aliceSawBobMove = 0;
        a.PlayerMoved += (id, _) => { if (id == bId) aliceSawBobMove++; };

        a.SendPose(At(0f, 0f));
        b.SendPose(At(9000f, 0f)); // absurdly far
        Pump(clock, server, clients);

        Assert.True(aliceSawBobMove > 0); // far pose still relayed — no behaviour change
        Assert.True(server.Interest.IsRelevant(aId, new EntityKey(EntityKind.Player, bId)));
    }
}
