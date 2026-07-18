using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M2 integration over REAL LiteNetLib UDP on localhost (M1.2 precedent): register → snapshot relay →
/// couple → stale-stamp drop → re-baseline, on the same sockets a live session uses. One deliberate
/// difference from the Loopback fuzz: transactions (reliable-ordered) and snapshots (sequenced-
/// unreliable) ride DIFFERENT UDP channels with no cross-channel ordering, so the stale snapshot is
/// fired only after the merge has fully converged — deterministic staleness, no race on the assert.
/// The in-flight race case is the fuzz harness's job; this proves the wire.
/// </summary>
public class TrainUdpIntegrationTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");
    private const string Key = "locomp-test";

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

    private static CarDef[] Specs(params string[] kinds) => kinds.Select(k => new CarDef(0, k)).ToArray();

    private static TrainsetSnapshot RailedSnapshot(int trainsetId, uint epoch, int carCount) =>
        new(trainsetId, epoch, 0L, Enumerable.Range(0, carCount)
            .Select(i => CarSnapshot.Railed(new BogieState(1, i * 20f, 3f), new BogieState(1, i * 20f - 8f, 3f)))
            .ToArray());

    [Fact]
    public void Train_sync_register_relay_couple_and_stale_drop_all_work_over_udp()
    {
        var clock = new ManualClock();
        using var serverT = LiteNetLibTransport.StartServer(0, Key);
        using var server = new NetServer(serverT, new ServerConfig(Identity), clock);

        using var aT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key);
        using var a = new NetClient(aT, Identity, "Alice", clock);
        using var bT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key);
        using var b = new NetClient(bT, Identity, "Bob", clock);

        Action[] pumps = { server.Poll, a.Poll, b.Poll };
        Assert.True(SpinUntil(() => a.Joined && b.Joined, 5000, pumps), "both clients should join over UDP");

        // Nothing stale exists yet — any epoch-mismatched application would be an invariant breach.
        int badApplies = 0;
        b.Trains.View.SnapshotApplied += s =>
        {
            if (!b.Trains.View.Sets.TryGetValue(s.TrainsetId, out TrainsetDef? d) || d.Epoch != s.Epoch) badApplies++;
        };

        // A registers a consist; both mirrors converge on it.
        TrainsetDef? setA = null;
        a.Trains.TrainsetRegistered += (_, def) => setA = def;
        a.Trains.RegisterTrainset(1, Specs("loco", "boxcar"));
        Assert.True(SpinUntil(() => setA != null && b.Trains.View.Sets.ContainsKey(setA.Id), 5000, pumps),
            "registration should commit and mirror over UDP");

        // Owner snapshot relays to B on the sequenced-unreliable channel.
        a.Trains.SendSnapshot(RailedSnapshot(setA!.Id, setA.Epoch, 2));
        Assert.True(SpinUntil(() => b.Trains.View.AppliedSnapshots >= 1, 5000, pumps),
            "owner snapshot should relay and apply over UDP");

        // B registers its own consist; A couples them once both are settled.
        TrainsetDef? setB = null;
        b.Trains.TrainsetRegistered += (_, def) => setB = def;
        b.Trains.RegisterTrainset(2, Specs("tanker"));
        Assert.True(SpinUntil(() => setB != null && a.Trains.View.Sets.ContainsKey(setB.Id), 5000, pumps),
            "second registration should mirror over UDP");
        clock.Advance(3000);

        a.Trains.ProposeCouple(setA.Cars[1].Id, CoupleEnd.Rear, setB!.Cars[0].Id, CoupleEnd.Front, relV: 1f);
        Assert.True(SpinUntil(
                () => a.Trains.View.Sets.Count == 1 && b.Trains.View.Sets.Count == 1 &&
                      !b.Trains.View.Sets.ContainsKey(setA.Id) && !b.Trains.View.Sets.ContainsKey(setB.Id),
                5000, pumps),
            "the merge transaction should retire both parents on every mirror");
        TrainsetDef merged = server.Trains.Registry.Sets.Values.Single();
        Assert.Equal(3, merged.Cars.Count);

        // NOW deterministically stale: the pre-merge stamp must be refused at the server's door.
        long appliedBefore = b.Trains.View.AppliedSnapshots;
        a.Trains.SendSnapshot(RailedSnapshot(setA.Id, setA.Epoch, 2));
        Assert.True(SpinUntil(() => server.Trains.StaleSnapshotsDropped >= 1, 5000, pumps),
            "the retired stamp should be dropped by the admission check");

        // And the owner's first snapshot at the new epoch re-baselines B.
        a.Trains.SendSnapshot(RailedSnapshot(merged.Id, merged.Epoch, merged.Cars.Count));
        Assert.True(SpinUntil(() => b.Trains.View.AppliedSnapshots > appliedBefore, 5000, pumps),
            "the new-epoch snapshot should apply cleanly");
        Assert.Equal(merged.Epoch, b.Trains.View.LatestSnapshots[merged.Id].Epoch);
        Assert.Equal(0, badApplies);
    }
}
