using System.Collections.Generic;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Core.World;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// Interest management on RAILED TRAINS (D10 Burst 2) — the ~96% of steady-state bandwidth the perf
/// baseline measured (docs/PERF-BASELINE.md §3). The mechanism shipped in Burst 1 against player
/// poses; what makes trains work is placing a spline-space bogie in the world through the topology's
/// per-edge geometry, so these tests are as much about the geometry contract as the filter.
///
/// <para>Every test here is game-free over Loopback: a synthetic straight-line world where edge s
/// maps to world X, so "1200 m along edge 0" is literally x = 1200 and distances are readable.</para>
/// </summary>
public class TrainInterestTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    /// <summary>One 4 km edge running due east from the origin: world X == spline s, so a bogie at
    /// s = 3000 sits 3 km from a player standing at the origin. Junction-free — irrelevant here.</summary>
    private static WorldTopology StraightWorld() => new(
        "B99.7",
        new[] { new TrackEdge(0, 4000f, nodeA: 1, nodeB: 2, new WorldPoint(0, 0, 0), new WorldPoint(4000, 0, 0)) },
        new JunctionDef[0]);

    /// <summary>The same world with the geometry stripped — a v1 <c>.lmpw</c>, which is what every
    /// extraction predating Burst 2 looks like.</summary>
    private static WorldTopology GeometryFreeWorld() => new(
        "B99.7",
        new[] { new TrackEdge(0, 4000f, nodeA: 1, nodeB: 2) },
        new JunctionDef[0]);

    private static InterestConfig Filtering() => new()
    {
        Enabled = true,
        FilterPlayers = false,   // trains only — this is the Burst 2 surface
        FilterTrains = true,
        EnterRadiusM = 500f,
        LeaveRadiusM = 750f,
        RecomputeIntervalMs = 1, // recompute every pumped tick (the clock advances each round)
    };

    private static CarDef[] Cars(int n)
    {
        var cars = new CarDef[n];
        for (int i = 0; i < n; i++) cars[i] = new CarDef(0, i == 0 ? "LocoDiesel" : "BoxcarBrown");
        return cars;
    }

    /// <summary>A snapshot putting the consist's head at <paramref name="s"/> metres along edge 0 —
    /// i.e. at world x = s in <see cref="StraightWorld"/>.</summary>
    private static TrainsetSnapshot SnapshotAt(TrainsetDef def, float s)
    {
        var cars = new CarSnapshot[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
            cars[i] = CarSnapshot.Railed(new BogieState(0, s - i * 16f, 5f), new BogieState(0, s - i * 16f - 9f, 5f));
        return new TrainsetSnapshot(def.Id, def.Epoch, 0L, cars);
    }

    private static Pose At(float x, float z) => new(x, 0f, z, 0f, 0f, 0f, 1f);

    private static void Pump(ManualClock clock, NetServer server, NetClient c, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++) { clock.NowMs += 50; server.Poll(); c.Poll(); }
    }

    private static EntityKey Train(int id) => new(EntityKind.Trainset, id);

    // ── the headline: a distant train stops streaming ──

    /// <summary>
    /// The whole point of Burst 2. A player stands at the origin; a server-driven consist runs away
    /// down the line. Once it passes the leave radius the server stops relaying its snapshots — the
    /// client's replica goes quiet and is told to hide — and when it comes back, the stream resumes
    /// from a full replay rather than a position with no train under it.
    /// </summary>
    [Fact]
    public void A_distant_train_stops_streaming_and_resumes_on_approach()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()),
                                         clock, restore: null, topology: StraightWorld());
        using var c = new NetClient(hub.Connect(out int cId), Identity, "Watcher", clock, playerKey: "k");
        Pump(clock, server, c);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(3));
        server.Trains.PushServerSnapshot(SnapshotAt(def, 100f)); // right next to the watcher
        c.SendPose(At(0f, 0f));                                  // ...who anchors at the origin
        Pump(clock, server, c);

        int hidden = 0;
        c.TrainsetHidden += id => { if (id == def.Id) hidden++; };

        Assert.True(server.Interest.IsRelevant(cId, Train(def.Id)));
        Assert.True(c.Trains.View.LatestSnapshots.ContainsKey(def.Id));

        // The train runs 3 km down the line — well past the 750 m leave radius.
        for (int s = 200; s <= 3000; s += 200)
        {
            server.Trains.PushServerSnapshot(SnapshotAt(def, s));
            Pump(clock, server, c, rounds: 2);
        }

        Assert.False(server.Interest.IsRelevant(cId, Train(def.Id)));
        Assert.Equal(1, hidden); // hidden exactly once — the hysteresis band must not flicker

        // It keeps driving out there, and NONE of that traffic reaches the watcher. This is the
        // bandwidth saving, asserted as an observable: the client's last known position goes stale.
        float stale = c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S;
        for (int s = 3200; s <= 3800; s += 200)
        {
            server.Trains.PushServerSnapshot(SnapshotAt(def, s));
            Pump(clock, server, c, rounds: 2);
        }
        Assert.Equal(stale, c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S);

        // It comes home: the stream resumes and the replica catches up to reality.
        for (int s = 2900; s >= 100; s -= 200)
        {
            server.Trains.PushServerSnapshot(SnapshotAt(def, s));
            Pump(clock, server, c, rounds: 2);
        }
        Assert.True(server.Interest.IsRelevant(cId, Train(def.Id)));
        Assert.Equal(100f, c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S, 1);
    }

    /// <summary>
    /// Re-entering scope must deliver the consist's DEFINITION, not just its next position. The client
    /// tears the replica down on a hide, so a bare snapshot would describe a train it no longer has —
    /// which is why enter replays create + snapshot + controls (the join burst, narrowed to one set).
    /// </summary>
    [Fact]
    public void Re_entering_scope_replays_the_full_trainset_state()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()),
                                         clock, restore: null, topology: StraightWorld());
        using var c = new NetClient(hub.Connect(out int cId), Identity, "Watcher", clock, playerKey: "k");
        Pump(clock, server, c);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(2));
        server.Trains.PushServerSnapshot(SnapshotAt(def, 100f));
        c.SendPose(At(0f, 0f));
        Pump(clock, server, c);

        // Simulate the Shim's reaction to a hide: drop the local replica entirely.
        c.TrainsetHidden += id => c.Trains.View.ApplyRemove(id);

        for (int s = 200; s <= 3000; s += 200)
        {
            server.Trains.PushServerSnapshot(SnapshotAt(def, s));
            Pump(clock, server, c, rounds: 2);
        }
        Assert.False(c.Trains.View.Sets.ContainsKey(def.Id)); // gone locally, as a Shim would have it

        for (int s = 2900; s >= 100; s -= 200)
        {
            server.Trains.PushServerSnapshot(SnapshotAt(def, s));
            Pump(clock, server, c, rounds: 2);
        }

        // The replay rebuilt it from nothing: right id, right car count, right position.
        Assert.True(c.Trains.View.Sets.ContainsKey(def.Id));
        Assert.Equal(2, c.Trains.View.Sets[def.Id].Cars.Count);
        Assert.Equal(100f, c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S, 1);
    }

    // ── the fail-open guarantees: what must NEVER be hidden ──

    /// <summary>
    /// Without world geometry a train has no anchor, so the filter must switch itself OFF rather than
    /// hide everything. This is the back-compat path for every <c>.lmpw</c> extracted before Burst 2 —
    /// the session degrades to the old broadcast behaviour, which is merely expensive, not broken.
    /// </summary>
    [Fact]
    public void Without_world_geometry_train_filtering_is_suppressed_not_guessed()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()),
                                         clock, restore: null, topology: GeometryFreeWorld());
        using var c = new NetClient(hub.Connect(out int cId), Identity, "Watcher", clock, playerKey: "k");
        Pump(clock, server, c);

        Assert.Contains(EntityKind.Trainset, server.Interest.Suppressed);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(2));
        c.SendPose(At(0f, 0f));
        for (int s = 100; s <= 3800; s += 300)
        {
            server.Trains.PushServerSnapshot(SnapshotAt(def, s));
            Pump(clock, server, c, rounds: 2);
        }

        // Absurdly far, yet still streaming — exactly as it behaved before Burst 2.
        Assert.True(server.Interest.IsRelevant(cId, Train(def.Id)));
        Assert.Equal(3700f, c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S, 1);
    }

    /// <summary>No topology at all (a host that couldn't read the track network, or a bare server) is
    /// the same story as a geometry-free one: suppressed, never guessed.</summary>
    [Fact]
    public void Without_any_topology_train_filtering_is_suppressed()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()), clock);
        using var c = new NetClient(hub.Connect(out int cId), Identity, "Watcher", clock, playerKey: "k");
        Pump(clock, server, c);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(2));
        c.SendPose(At(0f, 0f));
        server.Trains.PushServerSnapshot(SnapshotAt(def, 3900f));
        Pump(clock, server, c);

        Assert.Contains(EntityKind.Trainset, server.Interest.Suppressed);
        Assert.True(server.Interest.IsRelevant(cId, Train(def.Id)));
    }

    /// <summary>
    /// A consist whose snapshot sits on an edge the topology doesn't contain cannot be placed — and an
    /// unplaceable entity must be relayed to everyone, not silently hidden from everyone. This is the
    /// one way the filter could fail CLOSED, so it is pinned: mismatched data costs bandwidth, never
    /// visibility.
    /// </summary>
    [Fact]
    public void A_train_on_an_unknown_edge_stays_visible_to_everyone()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()),
                                         clock, restore: null, topology: StraightWorld());
        using var c = new NetClient(hub.Connect(out int cId), Identity, "Watcher", clock, playerKey: "k");
        Pump(clock, server, c);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(2));
        c.SendPose(At(0f, 0f));

        // Edge 42 doesn't exist in this world (a topology from a different extraction, say). Car count
        // must match the def — a snapshot's car order IS its car identity (03 §4).
        var cars = new[]
        {
            CarSnapshot.Railed(new BogieState(42, 10f, 5f), new BogieState(42, 1f, 5f)),
            CarSnapshot.Railed(new BogieState(42, -6f, 5f), new BogieState(42, -15f, 5f)),
        };
        for (int i = 0; i < 4; i++)
        {
            server.Trains.PushServerSnapshot(new TrainsetSnapshot(def.Id, def.Epoch, 0L, cars));
            Pump(clock, server, c, rounds: 2);
        }

        Assert.True(server.Interest.IsRelevant(cId, Train(def.Id)));
        Assert.True(c.Trains.View.LatestSnapshots.ContainsKey(def.Id));
    }

    /// <summary>
    /// A player is never told to hide a consist they SIMULATE. Their cars are real and local — a host's
    /// own native trainsets above all — so a hide would have the Shim tear down objects it is
    /// authoritative for. Ownership beats distance, always.
    /// </summary>
    [Fact]
    public void An_owner_is_never_told_to_hide_its_own_trainset()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, interest: Filtering()),
                                         clock, restore: null, topology: StraightWorld());
        using var owner = new NetClient(hub.Connect(out int ownerId), Identity, "Driver", clock, playerKey: "k1");
        Pump(clock, server, owner);

        owner.Trains.RegisterTrainset(token: 1, Cars(2));
        Pump(clock, server, owner);
        TrainsetDef def = Assert.Single(server.Trains.Registry.Sets.Values);
        Assert.Equal(ownerId, def.OwnerId);

        bool told = false;
        owner.TrainsetHidden += _ => told = true;

        // The owner stands at the origin while their own consist is parked 3 km away — the exact shape
        // of a host whose native cars sit in a distant yard.
        owner.SendPose(At(0f, 0f));
        owner.Trains.SendSnapshot(SnapshotAt(def, 3000f));
        Pump(clock, server, owner, rounds: 12);

        Assert.False(told, "an owner must never be told to hide a consist it simulates");
    }

    // ── the untouched default ──

    /// <summary>With interest disabled (the default) nothing changes, geometry or not — the whole
    /// feature stays opt-in.</summary>
    [Fact]
    public void Disabled_interest_streams_distant_trains_exactly_as_before()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock,
                                         restore: null, topology: StraightWorld());
        using var c = new NetClient(hub.Connect(out int cId), Identity, "Watcher", clock, playerKey: "k");
        Pump(clock, server, c);

        TrainsetDef def = server.Trains.SpawnServerOwned(Cars(2));
        c.SendPose(At(0f, 0f));
        server.Trains.PushServerSnapshot(SnapshotAt(def, 3900f)); // 3.9 km away
        Pump(clock, server, c);

        Assert.True(server.Interest.IsRelevant(cId, Train(def.Id)));
        Assert.Equal(3900f, c.Trains.View.LatestSnapshots[def.Id].Cars[0].Front.S, 1);
    }
}
