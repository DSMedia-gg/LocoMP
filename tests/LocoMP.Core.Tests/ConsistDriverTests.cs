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
/// The ghost train end-to-end over the Loopback hub: the driver registers its consist, streams
/// current-epoch snapshots that an observing client admits (zero discards — the M2.1 invariant
/// holding under a new producer), and throws the junctions it crosses. This is the harness half of
/// the M2 exit's "two consists" scene; the in-game half adds the Shim rendering it.
/// </summary>
public class ConsistDriverTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "99-build2702", "0.0.2");

    /// <summary>Triangle loop with a junction-guarded spur (same shape as TopologyWalkerTests).</summary>
    private static WorldTopology Synthetic() => new(
        "test",
        new[]
        {
            new TrackEdge(0, 100f, nodeA: 1, nodeB: 2),
            new TrackEdge(1, 100f, nodeA: 2, nodeB: 3),
            new TrackEdge(2, 100f, nodeA: 3, nodeB: 1),
            new TrackEdge(3, 80f, nodeA: 2, nodeB: 4),
        },
        new[]
        {
            new JunctionDef(7, entryEdgeId: 0, branchEdgeIds: new uint[] { 1, 3 }),
        });

    [Fact]
    public void The_ghost_registers_streams_admitted_snapshots_and_throws_junctions()
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

        var driver = new ConsistDriver(Synthetic(), carCount: 3, speed: 20, seed: 5, "Ghost", _ => { });

        // Long enough for registration, trail warm-up (3 cars = 48 m), laps, and junction hits.
        for (int i = 0; i < 400; i++)
        {
            clock.Advance(100);
            driver.Tick(ghost, 0.1);
            Pump();
        }

        // Registered exactly one trainset, owned by the ghost.
        TrainsetDef set = Assert.Single(server.Trains.Registry.Sets.Values);
        Assert.Equal(ghost.LocalId, set.OwnerId);
        Assert.Equal(3, set.Cars.Count);
        Assert.Equal(new[] { "ghost-loco", "ghost-car", "ghost-car" }, set.Cars.Select(c => c.Kind));

        // The observer admits the stream: latest snapshot is current-epoch, right car count, and
        // every bogie sits on a real edge. Zero discards — the epoch invariant holds end-to-end.
        Assert.True(driver.SnapshotsSent > 100, $"only {driver.SnapshotsSent} snapshots sent");
        TrainsetSnapshot snap = observer.Trains.View.LatestSnapshots[set.Id];
        Assert.Equal(set.Epoch, snap.Epoch);
        Assert.Equal(3, snap.Cars.Length);
        var world = Synthetic();
        foreach (CarSnapshot car in snap.Cars)
        {
            Assert.False(car.Derailed);
            foreach (BogieState bogie in new[] { car.Front, car.Rear })
            {
                TrackEdge edge = Assert.Single(world.Edges, e => e.Id == bogie.EdgeId);
                Assert.InRange(bogie.S, 0f, edge.LengthM);
            }
        }
        Assert.Equal(0, observer.Trains.View.StaleSnapshotsDiscarded);

        // Junction throws made it to everyone: 4 km of triangle laps must cross junction 7.
        Assert.True(driver.JunctionsThrown > 0, "the ghost never crossed the junction");
        Assert.True(observer.Trains.Junctions.ContainsKey(7u), "junction state never reached the observer");
        Assert.InRange(observer.Trains.Junctions[7u], (byte)0, (byte)1);
    }

    [Fact]
    public void The_driver_rebinds_and_reregisters_on_a_fresh_client_after_churn()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        var driver = new ConsistDriver(Synthetic(), carCount: 1, speed: 20, seed: 6, "Ghost", _ => { });

        NetClient Join()
        {
            var c = new NetClient(hub.Connect(out _), Identity, "Ghost", clock);
            for (int i = 0; i < 6; i++) { server.Poll(); c.Poll(); }
            Assert.True(c.Joined);
            return c;
        }
        void Drive(NetClient c, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                clock.Advance(100);
                driver.Tick(c, 0.1);
                for (int p = 0; p < 4; p++) { server.Poll(); c.Poll(); }
            }
        }

        NetClient first = Join();
        Drive(first, 60);
        long sentOnFirst = driver.SnapshotsSent;
        Assert.True(sentOnFirst > 0);
        first.Leave();
        for (int i = 0; i < 6; i++) { server.Poll(); first.Poll(); }
        first.Dispose();

        // Fresh NetClient (what BotClient creates after churn): the driver must notice, re-register,
        // and resume streaming — the old trainset was parked/kept server-side but is not ours anymore.
        NetClient second = Join();
        Drive(second, 120);
        Assert.True(driver.SnapshotsSent > sentOnFirst, "driver never resumed after the reconnect");
        int ownedBysecond = server.Trains.Registry.Sets.Values.Count(s => s.OwnerId == second.LocalId);
        Assert.Equal(1, ownedBysecond);
    }

    /// <summary>The debt-pass fix: bot owners used to IGNORE remote chain acts, so the one-PC rig
    /// could never fire the owner-side half of the M3.5c request path. Now a player's physical
    /// uncouple/couple on bot cars round-trips: request → owner proposes → server commits → both
    /// mirrors converge — and the driver adopts the split/merge product instead of registering a
    /// duplicate consist.</summary>
    [Fact]
    public void The_driver_honors_remote_chain_requests_and_adopts_the_products()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        var ghost = new NetClient(hub.Connect(out _), Identity, "Ghost", clock);
        var player = new NetClient(hub.Connect(out _), Identity, "Player", clock);
        void Pump()
        {
            for (int i = 0; i < 6; i++)
            {
                server.Poll();
                ghost.Poll();
                player.Poll();
            }
        }
        Pump();
        Assert.True(ghost.Joined && player.Joined);

        var driver = new ConsistDriver(Synthetic(), carCount: 3, speed: 20, seed: 5, "Ghost", _ => { });
        void Drive(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                clock.Advance(100);
                driver.Tick(ghost, 0.1);
                Pump();
            }
        }
        Drive(60);

        TrainsetDef set = Assert.Single(server.Trains.Registry.Sets.Values);
        Assert.Equal(3, set.Cars.Count);
        int leadCarId = set.Cars[0].Id;
        int midCarId = set.Cars[1].Id;
        int tailCarId = set.Cars[2].Id;

        // The player unhooks the chain at the middle car's rear: [A,B,C] → [A,B] + [C].
        player.Trains.RequestUncouple(midCarId, CoupleEnd.Rear);
        Drive(30);

        Assert.Equal(2, server.Trains.Registry.Sets.Count);
        TrainsetDef leadSet = Assert.Single(server.Trains.Registry.Sets.Values,
            s => s.Cars.Any(c => c.Id == leadCarId));
        TrainsetDef tailSet = Assert.Single(server.Trains.Registry.Sets.Values,
            s => s.Cars.Any(c => c.Id == tailCarId));
        Assert.Equal(2, leadSet.Cars.Count);
        Assert.Single(tailSet.Cars);

        // The driver adopted the lead product and kept streaming it at the fresh epoch.
        Assert.True(player.Trains.View.LatestSnapshots.ContainsKey(leadSet.Id),
            "no snapshots admitted for the adopted product");
        Assert.Equal(leadSet.Epoch, player.Trains.View.LatestSnapshots[leadSet.Id].Epoch);

        // Re-hooking the chain (mid car's rear to the tail car): the couple request re-merges.
        player.Trains.RequestCouple(midCarId, CoupleEnd.Rear, tailCarId, CoupleEnd.Front);
        Drive(30);

        TrainsetDef merged = Assert.Single(server.Trains.Registry.Sets.Values);
        Assert.Equal(3, merged.Cars.Count);
        Assert.Equal(ghost.LocalId, merged.OwnerId);
        Assert.True(player.Trains.View.LatestSnapshots.ContainsKey(merged.Id),
            "no snapshots admitted after the re-merge");
        Assert.Equal(0, player.Trains.View.StaleSnapshotsDiscarded);
    }

    /// <summary>--derail-car: the flagged car streams as a 6-DOF off-rail pose (the client-side
    /// null-track spawn rig) while the rest of the consist stays railed.</summary>
    [Fact]
    public void A_derail_flagged_car_streams_off_rail_at_the_anchor()
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

        var anchor = new Presence.Pose(6f, 1f, 6f, 0f, 0f, 0f, 1f);
        var driver = new ConsistDriver(Synthetic(), carCount: 3, speed: 20, seed: 5, "Ghost", _ => { },
            derailCarIndex: 1, derailPose: anchor);
        for (int i = 0; i < 120; i++)
        {
            clock.Advance(100);
            driver.Tick(ghost, 0.1);
            Pump();
        }

        TrainsetDef set = Assert.Single(server.Trains.Registry.Sets.Values);
        TrainsetSnapshot snap = observer.Trains.View.LatestSnapshots[set.Id];
        Assert.False(snap.Cars[0].Derailed);
        Assert.True(snap.Cars[1].Derailed);
        Assert.Equal(anchor.Px, snap.Cars[1].Pose.Px);
        Assert.False(snap.Cars[2].Derailed);
    }
}
