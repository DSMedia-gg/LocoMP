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
}
