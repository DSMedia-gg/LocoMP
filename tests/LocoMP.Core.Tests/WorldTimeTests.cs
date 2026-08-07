using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// v18 world time-of-day (02 §3): the WorldClock's flow/freeze math, and the session wiring over
/// Loopback — the world source's report anchors everyone, the join burst carries the sun, a
/// non-source peer's report is ignored (their local sleep skip must not move the shared world), and
/// a restatement is CURRENT (the server flows the clock between anchors).
/// </summary>
public class WorldTimeTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    // 2000-06-15 08:00 — the same TOD-epoch range DV's own saves use.
    private static readonly double Anchor = new DateTime(2000, 6, 15, 8, 0, 0).ToOADate();

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    // ── WorldClock unit ──

    [Fact]
    public void The_clock_flows_at_the_world_rate()
    {
        var clock = new ManualClock();
        var world = new WorldClock(clock);
        Assert.False(world.HasValue);

        Assert.True(world.Set(Anchor, 30f)); // stock: 24 world-hours in 30 real minutes = 48×
        clock.Advance(60_000);               // 1 real minute → 48 world-minutes

        double worldMinutes = (world.CurrentOa - Anchor) * 24 * 60;
        Assert.InRange(worldMinutes, 47.99, 48.01);
        Assert.Equal(48.0, world.WorldSecondsPerRealSecond, 3);
    }

    [Fact]
    public void Freeze_pins_the_value_and_unfreeze_resumes_without_a_jump()
    {
        var clock = new ManualClock();
        var world = new WorldClock(clock);
        world.Set(Anchor, 30f);
        clock.Advance(10_000);
        double atFreeze = world.CurrentOa;

        world.Freeze();
        clock.Advance(3_600_000); // an hour of real pause
        Assert.Equal(atFreeze, world.CurrentOa, 12); // pinned exactly

        world.Unfreeze();
        clock.Advance(1_000);
        double worldSeconds = (world.CurrentOa - atFreeze) * 86400;
        Assert.InRange(worldSeconds, 47.9, 48.1); // resumed from the pin, not from a catch-up leap
    }

    [Fact]
    public void Corrupt_anchors_are_rejected()
    {
        var world = new WorldClock(new ManualClock());
        Assert.False(world.Set(Anchor, 0f));
        Assert.False(world.Set(Anchor, -5f));
        Assert.False(world.Set(double.NaN, 30f));
        Assert.False(world.HasValue);
    }

    // ── session wiring over Loopback ──

    [Fact]
    public void The_join_burst_carries_the_world_time_once_anchored()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        Assert.True(server.CommitWorldTime(Anchor, 30f));

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { bob });

        Assert.True(bob.Joined);
        Assert.Equal(Anchor, bob.WorldTimeOa, 9);
        Assert.Equal(30f, bob.WorldDayLengthMinutes);
    }

    [Fact]
    public void A_world_source_report_reaches_every_other_peer_but_never_echoes()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        host.SendWorldTimeReport(Anchor, 30f);
        Pump(server, new[] { host, bob });

        Assert.Equal(Anchor, bob.WorldTimeOa, 9);
        Assert.Equal(0.0, host.WorldTimeOa); // the source's own sky IS the truth — no echo to fight it
        Assert.True(server.WorldTime.HasValue);
    }

    [Fact]
    public void A_non_source_peers_report_is_ignored()
    {
        // 02 §3: skips are host/server-approved. A guest's local bed nap reports nothing (the Shim
        // only reports as host) — but even a hand-rolled report from a guest must not move the sun.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        host.SendWorldTimeReport(Anchor, 30f);
        Pump(server, new[] { host, bob });

        double forged = new DateTime(2000, 6, 15, 23, 0, 0).ToOADate();
        bob.SendWorldTimeReport(forged, 30f);
        Pump(server, new[] { host, bob });

        Assert.Equal(0.0, host.WorldTimeOa);                      // nothing relayed to the source
        Assert.Equal(Anchor, server.WorldTime.CurrentOa, 9);    // and the server kept the truth
    }

    [Fact]
    public void A_restatement_is_current_not_the_stale_anchor()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        server.CommitWorldTime(Anchor, 30f);

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { bob });

        clock.Advance(60_000); // 1 real minute → the world moved 48 minutes
        server.BroadcastWorldTime();
        Pump(server, new[] { bob });

        double worldMinutes = (bob.WorldTimeOa - Anchor) * 24 * 60;
        Assert.InRange(worldMinutes, 47.99, 48.01);
    }

    [Fact]
    public void The_dedicated_day_length_travels_with_the_time()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        server.CommitWorldTime(Anchor, 120f); // a slow server: 24 world-hours in 2 real hours

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { bob });

        Assert.Equal(120f, bob.WorldDayLengthMinutes);
        Assert.Equal(12.0, server.WorldTime.WorldSecondsPerRealSecond, 3);
    }
}
