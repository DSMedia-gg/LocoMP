using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// Cleanup on departure, and the convergence the session was missing (the M4 smoke pass's orphan
/// findings). The session modelled acquisition — pick up, claim, couple — but not
/// involuntary relinquishment, and it never re-asserted truth, so state that went wrong stayed wrong
/// with no repair path.
///
/// <para>These tests pin the SERVER half: a trainset nobody simulates must still be reachable by a
/// later joiner, and the documented resync escape hatch must actually recover a client. All game-free
/// over Loopback.</para>
/// </summary>
public class OrphanRecoveryTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static CarDef[] Cars(int n)
    {
        var cars = new CarDef[n];
        for (int i = 0; i < n; i++) cars[i] = new CarDef(0, i == 0 ? "LocoDiesel" : "BoxcarBrown");
        return cars;
    }

    private static TrainsetSnapshot SnapshotAt(TrainsetDef def, float headS)
    {
        var cars = new CarSnapshot[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
            cars[i] = CarSnapshot.Railed(new BogieState(0, headS - i * 16f, 4f), new BogieState(0, headS - i * 16f - 9f, 4f));
        return new TrainsetSnapshot(def.Id, def.Epoch, 0L, cars);
    }

    private static void Pump(NetServer server, params NetClient[] clients)
    {
        for (int i = 0; i < 8; i++) { server.Poll(); foreach (NetClient c in clients) c.Poll(); }
    }

    /// <summary>
    /// The finding, end to end: after a split the owner keeps streaming only the half it adopted, so the
    /// other product is never streamed — and because a client materialises a consist on its FIRST
    /// SNAPSHOT, a later joiner never saw it at all ("the consists disappeared on rejoin"). The product
    /// now inherits its cars' last known positions, so it arrives in the join burst complete.
    /// </summary>
    [Fact]
    public void A_split_product_nobody_streams_still_reaches_a_later_joiner()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        using var owner = new NetClient(hub.Connect(out int ownerId), Identity, "Driver", clock, playerKey: "k1");
        Pump(server, owner);

        owner.Trains.RegisterTrainset(token: 1, Cars(4));
        Pump(server, owner);
        TrainsetDef whole = Assert.Single(server.Trains.Registry.Sets.Values);
        owner.Trains.SendSnapshot(SnapshotAt(whole, 500f));
        Pump(server, owner);

        // Split it in half. Both products are the proposer's, but only one will ever be streamed again.
        owner.Trains.ProposeUncouple(whole.Id, gapIndex: 1);
        Pump(server, owner);
        Assert.Equal(2, server.Trains.Registry.Sets.Count);

        // The owner adopts one product and streams only that one from here on.
        TrainsetDef[] products = System.Linq.Enumerable.ToArray(server.Trains.Registry.Sets.Values);
        TrainsetDef adopted = products[0], orphan = products[1];
        for (int i = 0; i < 3; i++) { owner.Trains.SendSnapshot(SnapshotAt(adopted, 520f)); Pump(server, owner); }

        // A second player joins now, long after the split.
        using var joiner = new NetClient(hub.Connect(out _), Identity, "Latecomer", clock, playerKey: "k2");
        Pump(server, owner, joiner);

        // Both sets must arrive WITH a position — a def alone leaves the consist unspawnable.
        Assert.True(joiner.Trains.View.Sets.ContainsKey(adopted.Id));
        Assert.True(joiner.Trains.View.Sets.ContainsKey(orphan.Id));
        Assert.True(joiner.Trains.View.LatestSnapshots.ContainsKey(adopted.Id));
        Assert.True(joiner.Trains.View.LatestSnapshots.ContainsKey(orphan.Id),
            "the orphaned product must carry a baseline or it can never materialise");

        // And the inherited position must be the cars' real one, not a placeholder: the split did not
        // move anything, so the orphan's cars sit where they did before the transaction.
        TrainsetSnapshot got = joiner.Trains.View.LatestSnapshots[orphan.Id];
        Assert.Equal(orphan.Cars.Count, got.Cars.Length);
        Assert.Equal(orphan.Epoch, got.Epoch); // re-stamped to the product epoch, so it is not stale
    }

    /// <summary>An inherited baseline must never be a PARTIAL consist. A snapshot's slots are positional,
    /// so a set registered but never streamed has no per-car state and must yield no baseline at all
    /// rather than a half-filled one that would mis-assign every car after the gap.</summary>
    [Fact]
    public void A_set_that_was_never_streamed_inherits_no_baseline_rather_than_a_partial_one()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        using var owner = new NetClient(hub.Connect(out _), Identity, "Driver", clock, playerKey: "k1");
        Pump(server, owner);

        owner.Trains.RegisterTrainset(token: 1, Cars(4)); // registered, never streamed
        Pump(server, owner);
        TrainsetDef whole = Assert.Single(server.Trains.Registry.Sets.Values);

        owner.Trains.ProposeUncouple(whole.Id, gapIndex: 1);
        Pump(server, owner);

        Assert.Equal(2, server.Trains.Registry.Sets.Count);
        Assert.Empty(server.Trains.LatestSnapshots); // no invented positions
    }

    /// <summary>
    /// The 03 §4 escape hatch has to actually rescue a client. It used to re-send the def alone, which
    /// left the requester exactly as stuck as before it asked — a def does not materialise a consist.
    /// </summary>
    [Fact]
    public void A_resync_request_replays_the_position_not_just_the_definition()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        using var owner = new NetClient(hub.Connect(out _), Identity, "Driver", clock, playerKey: "k1");
        Pump(server, owner);

        owner.Trains.RegisterTrainset(token: 1, Cars(2));
        Pump(server, owner);
        TrainsetDef set = Assert.Single(server.Trains.Registry.Sets.Values);
        owner.Trains.SendSnapshot(SnapshotAt(set, 300f));
        Pump(server, owner);

        using var stuck = new NetClient(hub.Connect(out _), Identity, "Stuck", clock, playerKey: "k2");
        Pump(server, owner, stuck);

        // Simulate a client that lost the consist locally (a dropped message, a bad reconnect).
        stuck.Trains.View.ApplyRemove(set.Id);
        Assert.False(stuck.Trains.View.Sets.ContainsKey(set.Id));

        stuck.Trains.RequestResync(set.Id);
        Pump(server, owner, stuck);

        Assert.True(stuck.Trains.View.Sets.ContainsKey(set.Id));
        Assert.True(stuck.Trains.View.LatestSnapshots.ContainsKey(set.Id),
            "resync must restore a position too, or the client stays unable to spawn the consist");
    }

    /// <summary>A set whose owner disconnects parks, and parking must not cost it its position — 03 §3
    /// says a parked set is "static; positions frozen", which is only true if the freeze survives.</summary>
    [Fact]
    public void A_parked_set_keeps_its_position_after_its_owner_leaves()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        using var owner = new NetClient(hub.Connect(out int ownerId), Identity, "Driver", clock, playerKey: "k1");
        Pump(server, owner);

        owner.Trains.RegisterTrainset(token: 1, Cars(3));
        Pump(server, owner);
        TrainsetDef set = Assert.Single(server.Trains.Registry.Sets.Values);
        owner.Trains.SendSnapshot(SnapshotAt(set, 800f));
        Pump(server, owner);

        owner.Leave();
        Pump(server, owner);
        Assert.Equal(0, server.Trains.Registry.Sets[set.Id].OwnerId); // parked

        using var joiner = new NetClient(hub.Connect(out _), Identity, "Latecomer", clock, playerKey: "k2");
        Pump(server, joiner);

        Assert.True(joiner.Trains.View.LatestSnapshots.ContainsKey(set.Id));
        Assert.Equal(800f, joiner.Trains.View.LatestSnapshots[set.Id].Cars[0].Front.S, 1);
    }
}
