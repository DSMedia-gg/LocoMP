using System;
using System.Linq;
using LocoMP.Bot;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Core.World;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The consist-spacing geometry hint (G3′ tuning finding, 2026-08-05): DV seats a spawned remote
/// car BY ITS BOGIES, so the bot must stream bogie points at each car's REAL coupler pitch and
/// REAL per-end bogie insets — the min(3.5, len/4) inset guess left the rear joint wide even with
/// a measured pitch. --car-geometry carries all three per car; --car-lengths (pitch only) keeps
/// the guess for older hosts' hints. The bot separates consecutive cars by the COMPRESSED coupled
/// rest (0.2 m — the coupler joint's own rest), not DV's 0.3 m spawn gap: a synthesised replica
/// streamed at the spawn gap sits too wide for DV's chain-hook proximity check and renders gapped
/// + unlinked (Cody's live catch, 2026-08-05).
/// </summary>
public class CarGeometryTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "99-build2702", "0.0.2");

    /// <summary>Two-edge loop, long enough that a short consist regularly sits on ONE edge —
    /// there, S differences ARE path distances and placement asserts read directly.</summary>
    private static WorldTopology Loop() => new(
        "test",
        new[]
        {
            new TrackEdge(0, 400f, nodeA: 1, nodeB: 2),
            new TrackEdge(1, 400f, nodeA: 2, nodeB: 1),
        },
        Array.Empty<JunctionDef>());

    [Fact]
    public void The_car_geometry_flag_parses_pitch_and_both_insets()
    {
        BotOptions? o = BotOptions.Parse(new[] { "--car-geometry", "20.50:2.00:6.00,12.00:2.50:2.60" });
        Assert.NotNull(o);
        CarGeometry[] g = o!.ConsistGeometry();
        Assert.Equal(2, g.Length);
        Assert.Equal(20.50, g[0].Length, 3);
        Assert.Equal(2.00, g[0].FrontInset, 3);
        Assert.Equal(6.00, g[0].RearInset, 3);
        Assert.Equal(12.00, g[1].Length, 3);
        Assert.Equal(2.50, g[1].FrontInset, 3);
        Assert.Equal(2.60, g[1].RearInset, 3);
    }

    [Fact]
    public void A_malformed_geometry_entry_fails_the_parse()
    {
        // Two fields is the old pitch hint pasted into the wrong flag — refuse, don't guess.
        Assert.Null(BotOptions.Parse(new[] { "--car-geometry", "16.30:3.50" }));
    }

    [Fact]
    public void Pitch_only_car_lengths_leaves_the_insets_to_the_driver_guess()
    {
        BotOptions? o = BotOptions.Parse(new[] { "--car-lengths", "16.30,12.00" });
        Assert.NotNull(o);
        CarGeometry[] g = o!.ConsistGeometry();
        Assert.Equal(2, g.Length);
        Assert.Equal(16.30, g[0].Length, 3);
        Assert.True(double.IsNaN(g[0].FrontInset));
        Assert.True(double.IsNaN(g[0].RearInset));
    }

    [Fact]
    public void Streamed_bogies_sit_at_the_real_insets_not_the_guess()
    {
        // Asymmetric insets on car 0: a front/rear swap or a lingering symmetric guess both fail.
        var geometry = new[]
        {
            new CarGeometry(20.0, 2.0, 6.0),
            new CarGeometry(12.0, 2.5, 2.6),
        };
        CarSnapshot[] cars = Sample(geometry);

        AssertNear(20.0 - 2.0 - 6.0, Span(cars[0]), "car 0 bogie span");
        AssertNear(12.0 - 2.5 - 2.6, Span(cars[1]), "car 1 bogie span");
        // The joint: car 0's rear coupler meets car 1's nose across the COMPRESSED coupled gap
        // (0.2 m — the coupler joint's rest, not DV's 0.3 m spawn separation; 2026-08-05 tuning).
        AssertNear(6.0 + 0.2 + 2.5, Math.Abs(cars[0].Rear.S - cars[1].Front.S), "coupler joint");
    }

    [Fact]
    public void Without_insets_the_driver_keeps_its_len_over_4_guess()
    {
        // The --car-lengths back-compat placement: min(3.5, len/4) both ends (characterization).
        CarSnapshot[] cars = Sample(new[] { new CarGeometry(16.0), new CarGeometry(16.0) });

        AssertNear(16.0 - 3.5 - 3.5, Span(cars[0]), "car 0 bogie span");
        AssertNear(3.5 + 0.2 + 3.5, Math.Abs(cars[0].Rear.S - cars[1].Front.S), "coupler joint");
    }

    [Fact]
    public void Consecutive_cars_stream_at_the_compressed_coupled_gap_not_the_spawn_gap()
    {
        // Coupler-anchor to coupler-anchor across the joint is the gap ALONE once the insets are
        // subtracted off each side — pin it at DV's coupled joint rest (0.2 m), never the 0.3 m
        // spawn separation. Reverting the constant fails here (and the two joint asserts above).
        CarSnapshot[] cars = Sample(new[]
        {
            new CarGeometry(18.0, 2.0, 3.0),
            new CarGeometry(14.0, 4.0, 2.5),
        });
        double couplerToCoupler =
            Math.Abs(cars[0].Rear.S - cars[1].Front.S) - 3.0 /* car0 rear inset */ - 4.0 /* car1 front inset */;
        AssertNear(0.2, couplerToCoupler, "compressed coupled gap");
    }

    private static double Span(CarSnapshot car) => Math.Abs(car.Front.S - car.Rear.S);

    private static void AssertNear(double expected, double actual, string what) =>
        Assert.True(Math.Abs(expected - actual) < 0.02,
            $"{what}: expected {expected:F2} m, streamed {actual:F2} m");

    /// <summary>Drive a two-car consist over the Loopback hub until a snapshot lands with every
    /// bogie on one edge, and return its cars.</summary>
    private static CarSnapshot[] Sample(CarGeometry[] geometry)
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        var ghost = new NetClient(hub.Connect(out _), Identity, "Ghost", clock);
        var observer = new NetClient(hub.Connect(out _), Identity, "Observer", clock);
        void Pump()
        {
            for (int i = 0; i < 6; i++)
            {
                server.Poll();
                ghost.Poll();
                observer.Poll();
            }
        }
        Pump();
        Assert.True(ghost.Joined && observer.Joined);

        var driver = new ConsistDriver(Loop(), carCount: 2, speed: 20, seed: 5, "Ghost", _ => { },
            carGeometry: geometry);

        for (int i = 0; i < 400; i++)
        {
            clock.Advance(100);
            driver.Tick(ghost, 0.1);
            Pump();
            if (server.Trains.Registry.Sets.Count != 1) continue;
            TrainsetDef set = server.Trains.Registry.Sets.Values.Single();
            if (!observer.Trains.View.LatestSnapshots.TryGetValue(set.Id, out TrainsetSnapshot? snap)
                || snap is null || snap.Cars.Length != 2)
            {
                continue;
            }
            uint edge = snap.Cars[0].Front.EdgeId;
            if (snap.Cars.All(c => !c.Derailed && c.Front.EdgeId == edge && c.Rear.EdgeId == edge))
                return snap.Cars;
        }
        throw new Xunit.Sdk.XunitException("the consist never sampled with all bogies on one edge");
    }
}
