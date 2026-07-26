using System;
using System.Collections.Generic;
using System.IO;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Core.World;
using LocoMP.Server;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The M6-B soak exit ("the dedicated server survives a long unattended run without a restart"), as a
/// deterministic accelerated test. A real overnight bot-soak is confidence; THIS is the proof — over
/// Loopback + <see cref="ManualClock"/>, hundreds of waves of player churn, server-owned trains being
/// borrowed/driven/released (and abandoned mid-drive by a disconnect), a job board refilling, and item
/// spawn/despawn churn — all in milliseconds, so it runs in CI (hard rule 8). The point is the standing
/// invariants: after <b>every</b> wave the money + item conservation oracles hold, the epoch invariant
/// never trips, and nothing accumulates (players return to empty, trainsets never multiply). A leak or a
/// broken invariant fails a specific round, not "sometime in the night".
/// </summary>
public class SoakTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "99-build2702", "0.0.2");

    private static void Advance(NetServer server, IReadOnlyList<NetClient> clients,
                                IReadOnlyList<ServerKinematicTrain> trains, ManualClock clock, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (ServerKinematicTrain t in trains) t.Tick(0.1);
            foreach (NetClient c in clients) c.Poll();
            clock.Advance(50);
        }
    }

    private static TrainsetSnapshot SnapshotAt(TrainsetDef def, float s)
    {
        var cars = new CarSnapshot[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
            cars[i] = CarSnapshot.Railed(new BogieState(1, s + i * 16f, 5f), new BogieState(1, s + i * 16f - 9f, 5f));
        return new TrainsetSnapshot(def.Id, def.Epoch, 0L, cars);
    }

    [Fact]
    public void An_accelerated_soak_holds_every_conservation_invariant_and_leaks_nothing()
    {
        // A real extracted topology ships under tests/data — drive the server trains along the real map.
        string? worldPath = new ServerOptions().ResolveWorldFile();
        Assert.NotNull(worldPath);
        WorldTopology topo = TopologyCodec.Read(File.ReadAllBytes(worldPath!));

        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var config = new ServerConfig(Identity, maxPlayers: 16,
            career: DefaultCareer.Build(ProgressionPreset.PerPlayer));
        using var server = new NetServer(hub.Server, config, clock);

        const int serverTrains = 4;
        var trains = new List<ServerKinematicTrain>();
        for (int i = 0; i < serverTrains; i++)
            trains.Add(new ServerKinematicTrain(server.Trains, topo, carCount: 3, speed: 10, seed: i + 1));

        var worldItemIds = new List<int>(); // live world items, for despawn churn (counts fall as well as rise)

        const int rounds = 200;
        for (int round = 0; round < rounds; round++)
        {
            // ── a wave of players storms in ──
            int waveSize = 2 + (round % 4); // 2..5
            var clients = new List<NetClient>(waveSize);
            var ids = new List<int>(waveSize);
            for (int k = 0; k < waveSize; k++)
            {
                ITransport t = hub.Connect(out int id);
                clients.Add(new NetClient(t, Identity, $"P{round}-{k}", clock, playerKey: $"key-{round}-{k}"));
                ids.Add(id);
            }
            Advance(server, clients, trains, clock, rounds: 8);
            Assert.Equal(waveSize, server.PlayerCount);

            // ── one player borrows a server train and drives it; half the time they release it, half
            //    the time they just disconnect while owning (exercising reclaim-on-disconnect under load) ──
            NetClient borrower = clients[0];
            int setId = trains[round % serverTrains].TrainsetId;
            borrower.Trains.RequestOwnership(setId);
            Advance(server, clients, trains, clock, 6);
            bool released = false;
            if (borrower.LocalId is int me && server.Trains.Registry.Sets[setId].OwnerId == me)
            {
                borrower.Trains.SendSnapshot(SnapshotAt(server.Trains.Registry.Sets[setId], 20f));
                Advance(server, clients, trains, clock, 4);
                borrower.Trains.SendSnapshot(SnapshotAt(server.Trains.Registry.Sets[setId], 65f)); // advanced 45 m
                Advance(server, clients, trains, clock, 4);
                if (round % 2 == 0) { borrower.Trains.ReleaseOwnership(setId); released = true; Advance(server, clients, trains, clock, 4); }
            }

            // ── item churn at the authoritative registry (server commits): spawn one, retire the oldest
            //    once the world holds a score of them, so the live count oscillates instead of only growing ──
            worldItemIds.Add(server.Items.Registry.SpawnInWorld($"Lantern{round}", LocoMP.Core.Presence.Pose.Identity, "").Def.Id);
            if (worldItemIds.Count > 20)
            {
                int oldest = worldItemIds[0]; worldItemIds.RemoveAt(0);
                Assert.True(server.Items.Registry.TryDespawn(oldest, out _, out _), "an owned world item should despawn");
            }

            // ── the standing invariants, asserted EVERY round ──
            Assert.True(server.Career.Registry.Ledger.ConservationHolds, $"money conservation broke at round {round}");
            Assert.True(server.Items.Registry.ItemConservationHolds, $"item conservation broke at round {round}");
            Assert.Equal(serverTrains, server.Trains.Registry.Sets.Count);      // sets never leak or multiply
            Assert.Equal(0, server.Trains.StaleSnapshotsDropped);              // the M2 epoch invariant never trips

            // ── a mid-soak save/restore round-trip: the save path stays consistent under live load ──
            if (round == rounds / 2)
            {
                ServerSaveData rt = SaveCodec.Read(SaveCodec.Write(server.CaptureSave()));
                Assert.Equal(server.Career.Registry.Jobs.Count, rt.Career.Jobs.Count);
                Assert.Equal(server.Trains.Registry.Sets.Count, rt.Trains.Sets.Count);
            }

            // ── the wave storms out (some borrowers still own a train — reclaim must fire) ──
            foreach (int id in ids) hub.Disconnect(id);
            Advance(server, Array.Empty<NetClient>(), trains, clock, 6);
            foreach (NetClient c in clients) c.Dispose();

            Assert.Equal(0, server.PlayerCount);                               // roster returns to empty — no player leak
            Assert.Equal(0, hub.ClientCount);                                  // no hub endpoint left dangling
            Assert.Equal(serverTrains, server.Trains.Registry.Sets.Count);     // trains survive the churn intact
            // Whether released or reclaimed-on-disconnect, every train is back under the server and driving.
            Assert.Equal(ServerTrains.ServerOwnerId, server.Trains.Registry.Sets[setId].OwnerId);
            Assert.True(server.Trains.IsServerDriven(setId), released ? "released train resumes" : "reclaimed train resumes");

            clock.Advance(1000); // simulate a second of wall-time between waves (board refill, grace/TTL windows fire)
        }

        // After all that churn a fresh joiner still converges to the full world.
        ITransport lt = hub.Connect(out _);
        using var late = new NetClient(lt, Identity, "Late", clock, playerKey: "late");
        Advance(server, new[] { late }, trains, clock, 12);
        Assert.True(late.Joined, "a late joiner still completes the handshake after the soak");
        Assert.True(late.Career.Jobs.Count > 0, "the job board is still populated after the soak");
        foreach (ServerKinematicTrain t in trains)
            Assert.True(late.Trains.View.Sets.ContainsKey(t.TrainsetId), "every server train is still present for a late joiner");

        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
        Assert.True(server.Items.Registry.ItemConservationHolds);
    }
}
