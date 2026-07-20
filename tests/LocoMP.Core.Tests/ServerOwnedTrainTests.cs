using System.Collections.Generic;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The server as its own sim owner (M6-B.2 — the dedicated server's kinematic coaster). A consist the
/// server spawns and drives reaches clients exactly like an owner-driven one: it rides the join burst,
/// its pushed snapshots move the client's replica, and no player can claim it out from under the server.
/// All game-free over Loopback.
/// </summary>
public class ServerOwnedTrainTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static CarDef[] Cars(int n)
    {
        var cars = new CarDef[n];
        for (int i = 0; i < n; i++) cars[i] = new CarDef(0, i == 0 ? "LocoDiesel" : "BoxcarBrown");
        return cars;
    }

    private static TrainsetSnapshot SnapshotAt(TrainsetDef def, float s)
    {
        var cars = new CarSnapshot[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
            cars[i] = CarSnapshot.Railed(new BogieState(1, s + i * 16f, 5f), new BogieState(1, s + i * 16f - 9f, 5f));
        return new TrainsetSnapshot(def.Id, def.Epoch, 0L, cars);
    }

    private static void Pump(NetServer server, NetClient c, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++) { server.Poll(); c.Poll(); }
    }

    [Fact]
    public void A_server_owned_train_reaches_a_client_and_its_snapshots_move_it()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        using var c = new NetClient(hub.Connect(out _), Identity, "Solo", clock, playerKey: "k");
        Pump(server, c);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(2));
        Pump(server, c);

        // Delivered like any consist — and marked server-owned (so it's not a claimable parked set).
        Assert.True(c.Trains.View.Sets.ContainsKey(def.Id));
        Assert.Equal(ServerTrains.ServerOwnerId, c.Trains.View.Sets[def.Id].OwnerId);

        server.Trains.PushServerSnapshot(SnapshotAt(def, 10f));
        Pump(server, c);
        Assert.True(c.Trains.View.LatestSnapshots.ContainsKey(def.Id));
        float first = c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S;

        server.Trains.PushServerSnapshot(SnapshotAt(def, 40f)); // the train advanced 30 m
        Pump(server, c);
        float second = c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S;
        Assert.True(second > first, "the pushed snapshot should move the client's replica forward");
    }

    [Fact]
    public void A_server_owned_train_rides_the_join_burst_for_a_newcomer()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(3)); // spawned BEFORE anyone connects
        server.Trains.PushServerSnapshot(SnapshotAt(def, 25f));

        using var c = new NetClient(hub.Connect(out _), Identity, "Late", clock, playerKey: "k2");
        Pump(server, c);

        Assert.True(c.Trains.View.Sets.ContainsKey(def.Id));           // def in the burst
        Assert.True(c.Trains.View.LatestSnapshots.ContainsKey(def.Id)); // baseline position in the burst
    }

    [Fact]
    public void A_player_cannot_claim_a_server_owned_train()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        using var c = new NetClient(hub.Connect(out _), Identity, "Solo", clock, playerKey: "k");
        Pump(server, c);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(1));
        Pump(server, c);

        c.Trains.RequestOwnership(def.Id); // a player tries to take it over
        Pump(server, c);

        // Refused: the server stays the owner, both server-side and in the client's mirror.
        Assert.Equal(ServerTrains.ServerOwnerId, server.Trains.Registry.Sets[def.Id].OwnerId);
        Assert.Equal(ServerTrains.ServerOwnerId, c.Trains.View.Sets[def.Id].OwnerId);
    }
}
