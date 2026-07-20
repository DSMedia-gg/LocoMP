using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// End-to-end train sync over the Loopback hub: the same NetServer/NetClient stack a live session
/// runs, exercising the M2 flows — register, snapshot relay with the 03 §4 admission check, couple/
/// uncouple/derail/rerail transactions, junctions (incl. the duplicate-coalesce rule), control
/// grants with input routing, and park-on-disconnect.
/// </summary>
public class TrainSessionTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static CarDef[] Specs(params string[] kinds) => kinds.Select(k => new CarDef(0, k)).ToArray();

    private static TrainsetSnapshot RailedSnapshot(TrainsetDef def, float s = 10f) =>
        new(def.Id, def.Epoch, 0L, def.Cars
            .Select((_, i) => CarSnapshot.Railed(new BogieState(1, s + i * 20f, 5f), new BogieState(1, s + i * 20f - 8f, 5f)))
            .ToArray());

    /// <summary>Host A + client B joined, A registered one two-car consist, clock past settling.</summary>
    private static (LoopbackNetwork hub, ManualClock clock, NetServer server, NetClient a, NetClient b, TrainsetDef set)
        SessionWithOneTrainset()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        ITransport ta = hub.Connect(out int _);
        var a = new NetClient(ta, Identity, "Alice", clock);
        ITransport tb = hub.Connect(out int _);
        var b = new NetClient(tb, Identity, "Bob", clock);
        Pump(server, new[] { a, b });

        a.Trains.RegisterTrainset(token: 1, Specs("loco", "boxcar"));
        Pump(server, new[] { a, b });
        clock.Advance(3000);

        TrainsetDef set = server.Trains.Registry.Sets.Values.Single();
        return (hub, clock, server, a, b, set);
    }

    [Fact]
    public void Registration_commits_on_the_server_and_mirrors_to_every_client()
    {
        var (_, _, server, a, b, _) = SessionWithOneTrainset();

        uint tokenSeen = 0;
        TrainsetDef? mine = null;
        a.Trains.TrainsetRegistered += (token, def) => { tokenSeen = token; mine = def; };
        a.Trains.RegisterTrainset(token: 42, Specs("flatbed"));
        Pump(server, new[] { a, b });

        Assert.Equal(42u, tokenSeen);                       // registrant correlates by token
        Assert.Equal("flatbed", mine!.Cars.Single().Kind);
        Assert.Equal(a.LocalId, mine.OwnerId);
        Assert.Equal(2, server.Trains.Registry.Sets.Count);
        Assert.Equal(2, a.Trains.View.Sets.Count);          // both clients mirror both trainsets
        Assert.Equal(2, b.Trains.View.Sets.Count);
    }

    [Fact]
    public void World_source_delete_notice_reforms_the_survivors_and_removes_the_car_everywhere()
    {
        var (_, _, server, a, b, set) = SessionWithOneTrainset(); // A owns a two-car set
        int deletedCar = set.Cars[1].Id;
        int keptCar = set.Cars[0].Id;

        a.Trains.NotifyCarDeleted(deletedCar);                 // comms-radio Clear on the host
        Pump(server, new[] { a, b });

        // The deleted car is gone from the registry; the survivor re-formed into a fresh set.
        var serverCars = server.Trains.Registry.Sets.Values.SelectMany(s => s.Cars).Select(c => c.Id).ToList();
        Assert.DoesNotContain(deletedCar, serverCars);
        Assert.Contains(keptCar, serverCars);
        // B's mirror agrees — the transaction retired the old set and spawned the survivor's.
        var bCars = b.Trains.View.Sets.Values.SelectMany(s => s.Cars).Select(c => c.Id).ToList();
        Assert.DoesNotContain(deletedCar, bCars);
        Assert.Contains(keptCar, bCars);
    }

    [Fact]
    public void Deleting_the_last_car_removes_the_whole_set_everywhere()
    {
        var (_, clock, server, a, b, _) = SessionWithOneTrainset();
        a.Trains.RegisterTrainset(token: 7, Specs("handcar")); // a lone car A owns
        Pump(server, new[] { a, b });
        clock.Advance(3000);
        TrainsetDef lone = server.Trains.Registry.Sets.Values.Single(s => s.Cars.Count == 1);

        a.Trains.NotifyCarDeleted(lone.Cars[0].Id);
        Pump(server, new[] { a, b });

        Assert.False(server.Trains.Registry.Sets.ContainsKey(lone.Id)); // set gone
        Assert.False(b.Trains.View.Sets.ContainsKey(lone.Id));          // TrainsetRemove despawned it on B
    }

    [Fact]
    public void A_non_owner_cannot_delete_a_car()
    {
        var (_, _, server, a, b, set) = SessionWithOneTrainset(); // A owns
        string? refusal = null;
        server.Trains.ProposalRejected += (_, reason) => refusal = reason;

        b.Trains.NotifyCarDeleted(set.Cars[0].Id); // B does not own it
        Pump(server, new[] { a, b });

        Assert.Contains("only the owner", refusal);
        Assert.Equal(2, server.Trains.Registry.Sets.Values.Single().Cars.Count); // untouched
    }

    [Fact]
    public void A_remote_comms_action_routes_to_the_cars_sim_owner_with_the_initiator()
    {
        var (_, _, server, a, b, set) = SessionWithOneTrainset(); // A owns the car
        CommsActionKind? kind = null;
        int carSeen = 0, initiator = 0;
        Pose destSeen = default;
        a.Trains.CommsActionCommanded += (k, car, dest, init) => { kind = k; carSeen = car; destSeen = dest; initiator = init; };

        b.Trains.RequestCommsAction(CommsActionKind.Rerail, set.Cars[0].Id, new Pose(1f, 2f, 3f, 0f, 0f, 0f, 1f));
        Pump(server, new[] { a, b });

        Assert.Equal(CommsActionKind.Rerail, kind);
        Assert.Equal(set.Cars[0].Id, carSeen);
        Assert.Equal(b.LocalId, initiator); // the owner charges the INITIATOR's wallet, not its own
        Assert.Equal(1f, destSeen.Px);
    }

    [Fact]
    public void Owner_snapshots_relay_to_others_and_non_owner_snapshots_are_dropped()
    {
        var (_, _, server, a, b, set) = SessionWithOneTrainset();

        int applied = 0;
        b.Trains.View.SnapshotApplied += _ => applied++;

        a.Trains.SendSnapshot(RailedSnapshot(set));         // A owns it — relays
        b.Trains.SendSnapshot(RailedSnapshot(set));         // B does not — server must drop
        Pump(server, new[] { a, b });

        Assert.Equal(1, applied);
        Assert.Equal(1L, b.Trains.View.AppliedSnapshots);
        Assert.Equal(1L, server.Trains.StaleSnapshotsDropped);
        Assert.Equal(5f, b.Trains.View.LatestSnapshots[set.Id].Cars[0].Front.V);
    }

    [Fact]
    public void Couple_retires_both_parents_everywhere_and_old_stamps_are_dead()
    {
        var (_, clock, server, a, b, setA) = SessionWithOneTrainset();

        b.Trains.RegisterTrainset(token: 2, Specs("tanker", "tanker"));
        Pump(server, new[] { a, b });
        clock.Advance(3000);
        TrainsetDef setB = server.Trains.Registry.Sets.Values.Single(s => s.Id != setA.Id);

        // A's rear car touched B's front car (A simulates the moving consist and reports it).
        a.Trains.ProposeCouple(setA.Cars[1].Id, CoupleEnd.Rear, setB.Cars[0].Id, CoupleEnd.Front, relV: 1.5f);
        Pump(server, new[] { a, b });

        TrainsetDef merged = server.Trains.Registry.Sets.Values.Single();
        Assert.Equal(2u, merged.Epoch);
        Assert.Equal(a.LocalId, merged.OwnerId);
        Assert.Equal("loco,boxcar,tanker,tanker", string.Join(",", merged.Cars.Select(c => c.Kind)));

        foreach (NetClient c in new[] { a, b })
        {
            Assert.False(c.Trains.View.Sets.ContainsKey(setA.Id));
            Assert.False(c.Trains.View.Sets.ContainsKey(setB.Id));
            Assert.Equal(merged.Id, c.Trains.View.Sets.Values.Single().Id);
        }

        // The race, replayed deliberately: B fires a snapshot still stamped with its retired set —
        // the server refuses it at the door (03 §4 step 3).
        long dropsBefore = server.Trains.StaleSnapshotsDropped;
        b.Trains.SendSnapshot(RailedSnapshot(setB));
        Pump(server, new[] { a, b });
        Assert.Equal(dropsBefore + 1, server.Trains.StaleSnapshotsDropped);

        // And the new owner's first snapshot at the new epoch re-baselines B cleanly.
        a.Trains.SendSnapshot(RailedSnapshot(merged));
        Pump(server, new[] { a, b });
        Assert.Equal(merged.Epoch, b.Trains.View.LatestSnapshots[merged.Id].Epoch);
    }

    [Fact]
    public void Derail_then_rerail_by_the_other_player_round_trips_the_flags()
    {
        var (_, _, server, a, b, set) = SessionWithOneTrainset();

        a.Trains.ReportDerail(set.Id, new[] { set.Cars[1].Id });
        Pump(server, new[] { a, b });

        Assert.True(b.Trains.View.Sets[set.Id].Cars[1].Derailed);
        Assert.Equal(2u, b.Trains.View.Sets[set.Id].Epoch);

        // Rerail is the comms-radio path: B may request it without owning the consist.
        b.Trains.RequestRerail(set.Id);
        Pump(server, new[] { a, b });

        Assert.All(a.Trains.View.Sets[set.Id].Cars, c => Assert.False(c.Derailed));
        Assert.Equal(3u, a.Trains.View.Sets[set.Id].Epoch);
    }

    [Fact]
    public void Junction_throws_commit_to_everyone_and_true_duplicates_coalesce()
    {
        var (_, _, server, a, b, _) = SessionWithOneTrainset();

        int aChanges = 0, bChanges = 0;
        a.Trains.JunctionChanged += (_, _) => aChanges++;
        b.Trains.JunctionChanged += (_, _) => bChanges++;

        a.Trains.ThrowJunction(junctionId: 5, branch: 1);
        a.Trains.ThrowJunction(junctionId: 5, branch: 1);   // hook double-fire → same state, coalesced
        Pump(server, new[] { a, b });

        Assert.Equal(1, aChanges);                          // the thrower gets the authoritative echo once
        Assert.Equal(1, bChanges);
        Assert.Equal(1, b.Trains.Junctions[5]);

        b.Trains.ThrowJunction(junctionId: 5, branch: 0);   // a DISTINCT throw always commits
        Pump(server, new[] { a, b });
        Assert.Equal(2, aChanges);
        Assert.Equal(0, a.Trains.Junctions[5]);
    }

    [Fact]
    public void A_late_joiner_receives_the_full_world_burst()
    {
        var (hub, clock, server, a, b, set) = SessionWithOneTrainset();

        a.Trains.ThrowJunction(3, 1);
        a.Trains.RotateTurntable(1, 45f);
        a.Trains.RequestControlGrant(set.Cars[0].Id);
        Pump(server, new[] { a, b });

        ITransport tc = hub.Connect(out int _);
        using var c = new NetClient(tc, Identity, "Cara", clock);
        Pump(server, new[] { a, b, c });

        Assert.True(c.Joined);
        Assert.Equal(set.Id, c.Trains.View.Sets.Values.Single().Id);
        Assert.Equal(1, c.Trains.Junctions[3]);
        Assert.Equal(45f, c.Trains.Turntables[1]);
        Assert.Equal(a.LocalId, c.Trains.Grants[set.Cars[0].Id]);
    }

    [Fact]
    public void Control_inputs_route_from_the_grant_holder_to_the_sim_owner()
    {
        var (_, _, server, a, b, set) = SessionWithOneTrainset();
        int cab = set.Cars[0].Id;

        // B (not the sim owner) takes the cab — the multi-crew shape (03 §3: grant ≠ ownership).
        b.Trains.RequestControlGrant(cab);
        Pump(server, new[] { a, b });
        Assert.Equal(b.LocalId, a.Trains.Grants[cab]);

        var received = new List<(int carId, byte controlId, float value)>();
        a.Trains.ControlInputReceived += (carId, controlId, value) => received.Add((carId, controlId, value));

        b.Trains.SendControlInput(cab, controlId: 2, value: 0.75f);
        Pump(server, new[] { a, b });

        Assert.Equal((cab, (byte)2, 0.75f), Assert.Single(received));

        // A holds no grant on that cab, so A's inputs are refused server-side.
        var rejections = new List<string>();
        server.Trains.ProposalRejected += (_, reason) => rejections.Add(reason);
        a.Trains.SendControlInput(cab, controlId: 2, value: 0.1f);
        Pump(server, new[] { a, b });
        Assert.Contains(rejections, r => r.Contains("no control grant"));

        // While B holds the grant, A cannot steal it — A just learns who has it.
        a.Trains.RequestControlGrant(cab);
        Pump(server, new[] { a, b });
        Assert.Equal(b.LocalId, a.Trains.Grants[cab]);

        b.Trains.ReleaseControlGrant(cab);
        Pump(server, new[] { a, b });
        Assert.False(a.Trains.Grants.ContainsKey(cab));
    }

    [Fact]
    public void A_leaving_owner_parks_their_consists_and_frees_their_grants()
    {
        var (_, _, server, a, b, set) = SessionWithOneTrainset();
        int aId = a.LocalId!.Value;

        a.Trains.RequestControlGrant(set.Cars[0].Id);
        Pump(server, new[] { a, b });

        int ownerEvents = 0;
        b.Trains.View.OwnerChanged += (id, owner) => { if (id == set.Id && owner == 0) ownerEvents++; };

        a.Leave();
        Pump(server, new[] { a, b });

        Assert.Equal(0, server.Trains.Registry.Sets[set.Id].OwnerId);
        Assert.Equal(1, ownerEvents);
        Assert.Equal(0, b.Trains.View.Sets[set.Id].OwnerId);
        Assert.False(server.Trains.Grants.ContainsKey(set.Cars[0].Id));
        Assert.False(b.Trains.Grants.ContainsKey(set.Cars[0].Id));
        Assert.Equal(aId, set.OwnerId); // (sanity: it was A's before)

        // B claims the parked consist and becomes its simulator.
        b.Trains.RequestOwnership(set.Id);
        Pump(server, new[] { a, b });
        Assert.Equal(b.LocalId, b.Trains.View.Sets[set.Id].OwnerId);
        Assert.Equal(b.LocalId, server.Trains.Registry.Sets[set.Id].OwnerId);
    }
}
