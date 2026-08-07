using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// D19 host-pause propagation (v18): the host's native ESC pause becomes an acknowledged session
/// state — broadcast to every peer, carried by the join burst for a mid-pause joiner, and freezing
/// the flowing world CLOCK with it (a paused host's sky stops, so restatements must stop too). A
/// dedicated server never pauses: nothing there calls the setter, and the default is pinned.
/// </summary>
public class WorldPauseTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static readonly double Anchor = new DateTime(2000, 6, 15, 8, 0, 0).ToOADate();

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void A_pause_reaches_every_peer_and_a_mid_pause_joiner_learns_it_from_the_burst()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { bob });
        Assert.False(bob.WorldPaused); // the dedicated default: unpaused until a host says otherwise

        var events = new List<(bool paused, string reason)>();
        bob.WorldPauseChanged += (p, r) => events.Add((p, r));

        server.SetWorldPaused(true, "the host paused the game");
        Pump(server, new[] { bob });

        Assert.True(bob.WorldPaused);
        Assert.Equal((true, "the host paused the game"), Assert.Single(events));

        // A joiner mid-pause must freeze from its first frame — the state rides the join burst.
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { bob, carol });
        Assert.True(carol.WorldPaused);
        Assert.Equal("the host paused the game", carol.WorldPauseReason);

        server.SetWorldPaused(false, string.Empty);
        Pump(server, new[] { bob, carol });
        Assert.False(bob.WorldPaused);
        Assert.False(carol.WorldPaused);

        // And a post-resume joiner gets NO pause line at all (nothing stale in the burst).
        using var dave = new NetClient(hub.Connect(out _), Identity, "Dave", clock, playerKey: "kD");
        int daveEvents = 0;
        dave.WorldPauseChanged += (_, _) => daveEvents++;
        Pump(server, new[] { bob, carol, dave });
        Assert.False(dave.WorldPaused);
        Assert.Equal(0, daveEvents);
    }

    [Fact]
    public void A_restated_pause_state_does_not_refire()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { bob });

        int fired = 0;
        bob.WorldPauseChanged += (_, _) => fired++;

        server.SetWorldPaused(true, "x");
        server.SetWorldPaused(true, "x"); // the 4 Hz host poll restating itself must stay silent
        Pump(server, new[] { bob });
        Assert.Equal(1, fired);
    }

    [Fact]
    public void The_pause_freezes_the_world_clock_and_resume_flows_from_the_pin()
    {
        // The D19 × time-of-day interaction: a paused host's sky stops, so the server's flowing
        // restatement must stop with it — otherwise every heartbeat runs ahead of the truth and
        // the clients' drift correction fights the frozen host.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        server.CommitWorldTime(Anchor, 30f);

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { bob });

        clock.Advance(10_000);
        server.SetWorldPaused(true, "paused");
        double atPause = server.WorldTime.CurrentOa;

        clock.Advance(600_000); // ten real minutes of pause menu
        server.BroadcastWorldTime();
        Pump(server, new[] { bob });
        Assert.Equal(atPause, bob.WorldTimeOa, 12); // the restatement is the PINNED value

        server.SetWorldPaused(false, string.Empty);
        clock.Advance(1_000);
        double worldSeconds = (server.WorldTime.CurrentOa - atPause) * 86400;
        Assert.InRange(worldSeconds, 47.9, 48.1); // resumed from the pin — no catch-up leap
    }
}
