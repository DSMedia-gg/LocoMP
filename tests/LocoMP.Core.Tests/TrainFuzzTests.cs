using System;
using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The M2 exit criterion (07 §M2): 1,000 random couple/uncouple/derail/rerail transactions with
/// ZERO stale-snapshot applications. Every transaction is chased by a deliberately stale snapshot
/// (the old stamp, fired down the same link right behind the proposal — the incumbent's race,
/// replayed on purpose 1,000 times), and an independent per-client shadow of (id → epoch) built
/// from definition traffic is the oracle: if any applied snapshot ever mismatches the shadow, the
/// 03 §4 invariant is broken. The final state must also converge: every client's mirror equal to
/// the server's registry, with no car created or lost.
/// </summary>
public class TrainFuzzTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, NetClient[] clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static TrainsetSnapshot RailedSnapshot(int trainsetId, uint epoch, int carCount) =>
        new(trainsetId, epoch, 0L, Enumerable.Range(0, carCount)
            .Select(i => CarSnapshot.Railed(new BogieState(1, i * 20f, 3f), new BogieState(1, i * 20f - 8f, 3f)))
            .ToArray());

    [Fact]
    public void One_thousand_random_transactions_produce_zero_stale_snapshot_applications()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        var clients = new NetClient[3];
        for (int i = 0; i < clients.Length; i++)
            clients[i] = new NetClient(hub.Connect(out int _), Identity, $"P{i}", clock);
        Pump(server, clients);
        Dictionary<int, NetClient> byPlayerId = clients.ToDictionary(c => c.LocalId!.Value);

        // The oracle: per client, an independent (trainsetId → epoch) shadow maintained ONLY from
        // definition traffic. Any applied snapshot that disagrees with it is an invariant breach.
        int oracleViolations = 0;
        foreach (NetClient c in clients)
        {
            var shadow = new Dictionary<int, uint>();
            TrainsetView view = c.Trains.View;
            view.TrainsetAdded += d => shadow[d.Id] = d.Epoch;
            view.TrainsetRemoved += id => shadow.Remove(id);
            view.TransactionApplied += txn =>
            {
                foreach (int id in txn.RetiredIds) shadow.Remove(id);
                foreach (TrainsetDef p in txn.Products) shadow[p.Id] = p.Epoch;
            };
            view.SnapshotApplied += s =>
            {
                if (!shadow.TryGetValue(s.TrainsetId, out uint epoch) || epoch != s.Epoch) oracleViolations++;
            };
        }

        var rejections = new List<string>();
        server.Trains.ProposalRejected += (peer, reason) => rejections.Add($"{peer}: {reason}");

        // Seed world: each player registers two consists of 3–5 cars.
        var rng = new Random(12345);
        foreach (NetClient c in clients)
        {
            for (int k = 0; k < 2; k++)
            {
                int cars = rng.Next(3, 6);
                c.Trains.RegisterTrainset((uint)(k + 1),
                    Enumerable.Range(0, cars).Select(i => new CarDef(0, $"car{i}")).ToArray());
            }
        }
        Pump(server, clients);

        TrainsetRegistry registry = server.Trains.Registry;
        var initialCarIds = registry.Sets.Values.SelectMany(s => s.Cars).Select(c => c.Id).OrderBy(id => id).ToArray();
        Assert.Equal(6, registry.Sets.Count);

        long expectedApplies = 0;
        long expectedServerDrops = 0;
        long clientStaleInjections = 0;
        int commits = 0;

        for (int iter = 0; commits < 1000 && iter < 5000; iter++)
        {
            clock.Advance(2500); // clear the settle window for anything minted last round

            // 1. Every owner streams a CURRENT snapshot for each consist it simulates; all of them
            //    must reach and apply on the other two clients.
            foreach (TrainsetDef set in registry.Sets.Values.ToList())
            {
                byPlayerId[set.OwnerId].Trains.SendSnapshot(RailedSnapshot(set.Id, set.Epoch, set.Cars.Count));
                expectedApplies += clients.Length - 1;
            }
            Pump(server, clients);

            // 2. One random valid transaction, proposed by the owning client...
            List<TrainsetDef> sets = registry.Sets.Values.ToList();
            List<TrainsetDef> clean = sets.Where(s => s.Cars.All(c => !c.Derailed)).ToList();
            List<TrainsetDef> splittable = sets.Where(s => s.Cars.Count >= 2).ToList();
            List<TrainsetDef> derailable = sets.Where(s => s.Cars.Any(c => !c.Derailed)).ToList();
            List<TrainsetDef> rerailable = sets.Where(s => s.Cars.Any(c => c.Derailed)).ToList();

            var ops = new List<Action>();
            if (clean.Count >= 2)
                ops.Add(() =>
                {
                    TrainsetDef s1 = clean[rng.Next(clean.Count)];
                    TrainsetDef s2 = clean.Where(s => s.Id != s1.Id).ElementAt(rng.Next(clean.Count - 1));
                    var endA = (CoupleEnd)rng.Next(2);
                    var endB = (CoupleEnd)rng.Next(2);
                    int carA = endA == CoupleEnd.Front ? s1.Cars[0].Id : s1.Cars[s1.Cars.Count - 1].Id;
                    int carB = endB == CoupleEnd.Front ? s2.Cars[0].Id : s2.Cars[s2.Cars.Count - 1].Id;
                    ClientTrains proposer = byPlayerId[s1.OwnerId].Trains;
                    proposer.ProposeCouple(carA, endA, carB, endB, relV: 1f);
                    proposer.SendSnapshot(RailedSnapshot(s1.Id, s1.Epoch, s1.Cars.Count)); // the chase
                });
            if (splittable.Count > 0)
                ops.Add(() =>
                {
                    TrainsetDef s1 = splittable[rng.Next(splittable.Count)];
                    ClientTrains proposer = byPlayerId[s1.OwnerId].Trains;
                    proposer.ProposeUncouple(s1.Id, rng.Next(s1.Cars.Count - 1));
                    proposer.SendSnapshot(RailedSnapshot(s1.Id, s1.Epoch, s1.Cars.Count));
                });
            if (derailable.Count > 0)
                ops.Add(() =>
                {
                    TrainsetDef s1 = derailable[rng.Next(derailable.Count)];
                    CarDef[] railed = s1.Cars.Where(c => !c.Derailed).ToArray();
                    ClientTrains proposer = byPlayerId[s1.OwnerId].Trains;
                    proposer.ReportDerail(s1.Id, new[] { railed[rng.Next(railed.Length)].Id });
                    proposer.SendSnapshot(RailedSnapshot(s1.Id, s1.Epoch, s1.Cars.Count));
                });
            if (rerailable.Count > 0)
                ops.Add(() =>
                {
                    TrainsetDef s1 = rerailable[rng.Next(rerailable.Count)];
                    ClientTrains proposer = byPlayerId[s1.OwnerId].Trains;
                    proposer.RequestRerail(s1.Id);
                    proposer.SendSnapshot(RailedSnapshot(s1.Id, s1.Epoch, s1.Cars.Count));
                });

            ops[rng.Next(ops.Count)]();
            commits++;
            // ...every proposal is chased (same link, so strictly after it) by a snapshot bearing
            // the PRE-transaction stamp. The server must refuse each one at the door.
            expectedServerDrops++;
            Pump(server, clients);

            // 3. Periodically inject a stale snapshot directly into a client mirror — the receive-
            //    side half of the discard rule.
            if (iter % 10 == 0)
            {
                TrainsetView view = clients[0].Trains.View;
                TrainsetDef live = view.Sets.Values.First();
                bool applied = view.TryApplySnapshot(RailedSnapshot(live.Id, live.Epoch + 7, live.Cars.Count));
                Assert.False(applied);
                clientStaleInjections++;
            }
        }

        Pump(server, clients, rounds: 10);

        // The exit criterion, measured: 1,000 committed transactions, zero stale applications.
        Assert.Equal(1000, commits);
        Assert.Empty(rejections);
        Assert.Equal(0, oracleViolations);
        Assert.Equal(expectedServerDrops, server.Trains.StaleSnapshotsDropped);
        Assert.Equal(clientStaleInjections, clients[0].Trains.View.StaleSnapshotsDiscarded);
        Assert.All(clients.Skip(1), c => Assert.Equal(0L, c.Trains.View.StaleSnapshotsDiscarded));

        // Every VALID snapshot applied — the invariant discards stale ones, never live ones.
        Assert.Equal(expectedApplies, clients.Sum(c => c.Trains.View.AppliedSnapshots));

        // Convergence: all three mirrors are exactly the server's registry.
        foreach (NetClient c in clients)
        {
            TrainsetView view = c.Trains.View;
            Assert.Equal(registry.Sets.Count, view.Sets.Count);
            foreach (TrainsetDef truth in registry.Sets.Values)
            {
                TrainsetDef mirror = view.Sets[truth.Id];
                Assert.Equal(truth.Epoch, mirror.Epoch);
                Assert.Equal(truth.OwnerId, mirror.OwnerId);
                Assert.Equal(truth.Cars.Select(x => (x.Id, x.Kind, x.Derailed)),
                             mirror.Cars.Select(x => (x.Id, x.Kind, x.Derailed)));
            }
        }

        // No car was created or destroyed across 1,000 membership changes.
        Assert.Equal(initialCarIds,
            registry.Sets.Values.SelectMany(s => s.Cars).Select(x => x.Id).OrderBy(id => id).ToArray());
    }

    // ── the receive-side discard rule in isolation ──

    [Fact]
    public void A_view_discards_unknown_wrong_epoch_and_malformed_snapshots_only()
    {
        var view = new TrainsetView();
        var def = new TrainsetDef(5, epoch: 3, ownerId: 1, new[] { new CarDef(1, "loco"), new CarDef(2, "boxcar") });
        view.ApplyCreate(def);

        Assert.False(view.TryApplySnapshot(RailedSnapshot(99, 3, 2)));  // unknown trainset
        Assert.False(view.TryApplySnapshot(RailedSnapshot(5, 2, 2)));   // stale epoch
        Assert.False(view.TryApplySnapshot(RailedSnapshot(5, 4, 2)));   // FUTURE epoch (channel race) — also refused
        Assert.False(view.TryApplySnapshot(RailedSnapshot(5, 3, 7)));   // car count mismatch
        Assert.True(view.TryApplySnapshot(RailedSnapshot(5, 3, 2)));    // current — applies

        Assert.Equal(4L, view.StaleSnapshotsDiscarded);
        Assert.Equal(1L, view.AppliedSnapshots);
    }

    [Fact]
    public void A_transaction_atomically_retires_parents_and_rebaselines_kinematics()
    {
        var view = new TrainsetView();
        var a = new TrainsetDef(1, 1, 1, new[] { new CarDef(1, "loco") });
        var b = new TrainsetDef(2, 1, 2, new[] { new CarDef(2, "boxcar") });
        view.ApplyCreate(a);
        view.ApplyCreate(b);
        Assert.True(view.TryApplySnapshot(RailedSnapshot(1, 1, 1)));

        var merged = new TrainsetDef(3, 2, 1, new[] { new CarDef(1, "loco"), new CarDef(2, "boxcar") });
        view.ApplyTransaction(new TrainsetTransaction(TrainsetTransactionType.Merge, new[] { 1, 2 }, new[] { merged }));

        Assert.Equal(new[] { 3 }, view.Sets.Keys.ToArray());
        Assert.Empty(view.LatestSnapshots);                             // old kinematics are gone with the epoch
        Assert.False(view.TryApplySnapshot(RailedSnapshot(1, 1, 1)));   // the retired stamp is dead
        Assert.True(view.TryApplySnapshot(RailedSnapshot(3, 2, 2)));    // the product re-baselines
    }
}
