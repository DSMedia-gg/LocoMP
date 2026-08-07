using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// v18 coupler hardware (02 §1 P0 — hoses, anglecocks, MU cables): the registry's validation
/// matrix (the incumbent's phantom-MU bug class is the reason it exists), and the session wiring
/// over Loopback — commit/broadcast/join-burst, the redundancy absorber that soaks N clients all
/// reporting the same native auto-break, and pruning when a car leaves the world.
/// </summary>
public class CouplerHardwareTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static CarDef[] Specs(params string[] kinds) => kinds.Select(k => new CarDef(0, k)).ToArray();

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static CouplerHardwareReport Hose(int a, CoupleEnd ea, int b, CoupleEnd eb) =>
        new(CouplerHardwareKind.HoseConnect, a, ea, b, eb);

    // ── registry unit: the validation matrix ──

    [Fact]
    public void Adjacent_cars_connect_and_non_adjacent_are_refused()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("loco", "boxcar", "tanker"));
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id, car2 = set.Cars[2].Id;
        var hw = new CouplerHardwareRegistry();

        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _));

        Assert.Equal(HardwareApplyResult.Rejected,
            hw.TryApply(Hose(car0, CoupleEnd.Front, car2, CoupleEnd.Rear), registry, out string? reason));
        Assert.Contains("not adjacent", reason);
    }

    [Fact]
    public void An_occupied_end_refuses_and_an_identical_connect_is_redundant()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("loco", "boxcar", "tanker"));
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id, car2 = set.Cars[2].Id;
        var hw = new CouplerHardwareRegistry();

        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _));
        Assert.Equal(HardwareApplyResult.Redundant,
            hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _));
        // car1's FRONT is taken; hooking car2 onto it must refuse (phantom-MU class).
        Assert.Equal(HardwareApplyResult.Rejected,
            hw.TryApply(Hose(car2, CoupleEnd.Front, car1, CoupleEnd.Front), registry, out string? reason));
        Assert.Contains("already connected", reason);
    }

    [Fact]
    public void Disconnect_frees_both_ends_and_an_unknown_disconnect_is_redundant()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("loco", "boxcar"));
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;
        var hw = new CouplerHardwareRegistry();

        // The N-client auto-break race: the first disconnect commits, the rest absorb silently.
        Assert.Equal(HardwareApplyResult.Redundant,
            hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, car0, CoupleEnd.Rear), registry, out _));

        hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _);
        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, car0, CoupleEnd.Rear), registry, out _));
        Assert.Null(hw.HoseAt(car0, CoupleEnd.Rear));
        Assert.Null(hw.HoseAt(car1, CoupleEnd.Front));
        // Both ends free again: the reverse connect works.
        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(Hose(car1, CoupleEnd.Front, car0, CoupleEnd.Rear), registry, out _));
    }

    [Fact]
    public void Cocks_default_closed_and_only_changes_commit()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("boxcar"));
        int car = set.Cars[0].Id;
        var hw = new CouplerHardwareRegistry();

        Assert.False(hw.CockOpen(car, CoupleEnd.Front));
        Assert.Equal(HardwareApplyResult.Redundant,
            hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: false), registry, out _));
        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: true), registry, out _));
        Assert.True(hw.CockOpen(car, CoupleEnd.Front));
        Assert.Equal(HardwareApplyResult.Redundant,
            hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: true), registry, out _));
        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: false), registry, out _));
    }

    [Fact]
    public void A_cross_set_connect_is_accepted_on_existence()
    {
        // Two touching but unchained cuts — legal in DV (hoses are independent of the chain). The
        // server has no geometry check yet, so existence + free ends is the contract (class doc).
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef setA = registry.Register(1, Specs("loco"));
        TrainsetDef setB = registry.Register(2, Specs("boxcar"));
        var hw = new CouplerHardwareRegistry();

        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(Hose(setA.Cars[0].Id, CoupleEnd.Rear, setB.Cars[0].Id, CoupleEnd.Front), registry, out _));
    }

    [Fact]
    public void Unknown_cars_and_self_connects_are_refused()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("loco", "boxcar"));
        int car0 = set.Cars[0].Id;
        var hw = new CouplerHardwareRegistry();

        Assert.Equal(HardwareApplyResult.Rejected, hw.TryApply(Hose(9999, CoupleEnd.Rear, car0, CoupleEnd.Front), registry, out _));
        Assert.Equal(HardwareApplyResult.Rejected, hw.TryApply(Hose(car0, CoupleEnd.Rear, 9999, CoupleEnd.Front), registry, out _));
        Assert.Equal(HardwareApplyResult.Rejected, hw.TryApply(Hose(car0, CoupleEnd.Front, car0, CoupleEnd.Rear), registry, out string? reason));
        Assert.Contains("itself", reason);
    }

    [Fact]
    public void The_mu_layer_is_independent_of_the_hose_layer()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("loco", "loco"));
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;
        var hw = new CouplerHardwareRegistry();

        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _));
        // Same physical ends, different layer: the MU cable also hooks up.
        Assert.Equal(HardwareApplyResult.Committed,
            hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.MuConnect, car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _));
        Assert.NotNull(hw.HoseAt(car0, CoupleEnd.Rear));
        Assert.NotNull(hw.MuAt(car0, CoupleEnd.Rear));
    }

    [Fact]
    public void Pruning_a_car_drops_its_lines_and_enumeration_emits_pairs_once()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("loco", "boxcar"));
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;
        var hw = new CouplerHardwareRegistry();

        hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _);
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car0, CoupleEnd.Rear, flag: true), registry, out _);
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car1, CoupleEnd.Front, flag: true), registry, out _);

        List<CouplerHardwareReport> lines = hw.EnumerateState().ToList();
        Assert.Single(lines, l => l.Kind == CouplerHardwareKind.HoseConnect); // the pair, once
        Assert.Equal(2, lines.Count(l => l.Kind == CouplerHardwareKind.CockSet));

        hw.PruneCar(car0);
        Assert.Null(hw.HoseAt(car1, CoupleEnd.Front)); // the partner's side went too
        Assert.True(hw.CockOpen(car1, CoupleEnd.Front)); // the surviving car's own cock stays
        Assert.Single(hw.EnumerateState()); // just car1's open cock
    }

    // ── R4-K: deliberate absence is state (tombstones + tri-state cocks) ──

    [Fact]
    public void A_deliberate_part_leaves_tombstones_and_a_reconnect_retires_them()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("loco", "boxcar"));
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;
        var hw = new CouplerHardwareRegistry();

        // Never-connected is NOT parted — a redundant disconnect must not fabricate a tombstone.
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, car0, CoupleEnd.Rear), registry, out _);
        Assert.False(hw.HoseParted(car0, CoupleEnd.Rear));

        hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _);
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, car0, CoupleEnd.Rear), registry, out _);
        Assert.True(hw.HoseParted(car0, CoupleEnd.Rear));
        Assert.True(hw.HoseParted(car1, CoupleEnd.Front)); // both freed ends remember the part

        // The join burst replays the part: one HoseDisconnect line per tombstoned end.
        Assert.Equal(2, hw.EnumerateState().Count(l => l.Kind == CouplerHardwareKind.HoseDisconnect));

        // A live reconnect is the one thing that retires a tombstone.
        hw.TryApply(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front), registry, out _);
        Assert.False(hw.HoseParted(car0, CoupleEnd.Rear));
        Assert.False(hw.HoseParted(car1, CoupleEnd.Front));
        Assert.DoesNotContain(hw.EnumerateState(), l => l.Kind == CouplerHardwareKind.HoseDisconnect);
    }

    [Fact]
    public void An_explicitly_closed_cock_is_state_not_absence()
    {
        var registry = new TrainsetRegistry(new ManualClock());
        TrainsetDef set = registry.Register(1, Specs("boxcar"));
        int car = set.Cars[0].Id;
        var hw = new CouplerHardwareRegistry();

        // Default-closed stays invisible: a redundant close creates no record (a fresh replica's
        // spawn state must not enumerate).
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: false), registry, out _);
        Assert.False(hw.CockExplicitlyClosed(car, CoupleEnd.Front));
        Assert.Empty(hw.EnumerateState());

        // Open then deliberately close: that close is state and rides the join burst.
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: true), registry, out _);
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: false), registry, out _);
        Assert.True(hw.CockExplicitlyClosed(car, CoupleEnd.Front));
        CouplerHardwareReport line = Assert.Single(hw.EnumerateState());
        Assert.Equal(CouplerHardwareKind.CockSet, line.Kind);
        Assert.False(line.Flag);

        // Re-opening retires the explicit close.
        hw.TryApply(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car, CoupleEnd.Front, flag: true), registry, out _);
        Assert.False(hw.CockExplicitlyClosed(car, CoupleEnd.Front));
    }

    [Fact]
    public void The_client_mirror_records_a_burst_tombstone_line_and_pruning_drops_tombstones()
    {
        var hw = new CouplerHardwareRegistry();

        // A join-burst tombstone line names only its own end (CarB = 0) — the mirror must still
        // record the part so the Shim can impose it over a respawn re-rig.
        hw.ApplyCommitted(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, 7, CoupleEnd.Rear));
        Assert.True(hw.HoseParted(7, CoupleEnd.Rear));

        // A committed connect through the mirror path retires it.
        hw.ApplyCommitted(new CouplerHardwareReport(CouplerHardwareKind.HoseConnect, 7, CoupleEnd.Rear, 8, CoupleEnd.Front));
        Assert.False(hw.HoseParted(7, CoupleEnd.Rear));

        // And a despawn is not a part: pruning clears the DYING car's tombstones and explicit
        // closes; the partner's tombstone is its own record and survives on its own terms.
        hw.ApplyCommitted(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, 7, CoupleEnd.Rear));
        hw.ApplyCommitted(new CouplerHardwareReport(CouplerHardwareKind.CockSet, 7, CoupleEnd.Rear, flag: false));
        hw.PruneCar(7);
        Assert.False(hw.HoseParted(7, CoupleEnd.Rear));
        Assert.False(hw.CockExplicitlyClosed(7, CoupleEnd.Rear));
        Assert.True(hw.HoseParted(8, CoupleEnd.Front)); // the surviving end is still deliberately free
        hw.PruneCar(8);
        Assert.Empty(hw.EnumerateState());
    }

    [Fact]
    public void A_parted_hose_and_closed_cock_survive_a_rejoin_via_the_join_burst()
    {
        // The R4-K scenario end-to-end: part committed, the client leaves and rejoins — the fresh
        // mirror must KNOW the pair is parted (tombstone) and the cock explicitly closed, so the
        // Shim imposes that over the respawn re-rig instead of re-seeding the rig.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("loco", "boxcar"));
        Pump(server, new[] { owner });
        TrainsetDef set = owner.Trains.View.Sets.Values.Single();
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;

        owner.Trains.ReportCouplerHardware(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front));
        owner.Trains.ReportCouplerHardware(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car0, CoupleEnd.Rear, flag: true));
        Pump(server, new[] { owner });
        owner.Trains.ReportCouplerHardware(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, car0, CoupleEnd.Rear, car1, CoupleEnd.Front));
        owner.Trains.ReportCouplerHardware(new CouplerHardwareReport(CouplerHardwareKind.CockSet, car0, CoupleEnd.Rear, flag: false));
        Pump(server, new[] { owner });

        using var rejoiner = new NetClient(hub.Connect(out _), Identity, "Rejoiner", clock, playerKey: "kR");
        Pump(server, new[] { owner, rejoiner });

        Assert.True(rejoiner.Joined);
        Assert.Null(rejoiner.Trains.Hardware.HoseAt(car0, CoupleEnd.Rear));
        Assert.True(rejoiner.Trains.Hardware.HoseParted(car0, CoupleEnd.Rear));
        Assert.True(rejoiner.Trains.Hardware.HoseParted(car1, CoupleEnd.Front));
        Assert.False(rejoiner.Trains.Hardware.CockOpen(car0, CoupleEnd.Rear));
        Assert.True(rejoiner.Trains.Hardware.CockExplicitlyClosed(car0, CoupleEnd.Rear));
    }

    // ── session wiring over Loopback ──

    [Fact]
    public void A_commit_reaches_everyone_but_the_reporter_and_rides_the_join_burst()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("loco", "boxcar"));
        Pump(server, new[] { owner });
        TrainsetDef set = owner.Trains.View.Sets.Values.Single();
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { owner, bob });

        var bobSees = new List<CouplerHardwareReport>();
        var ownerSees = new List<CouplerHardwareReport>();
        bob.Trains.CouplerHardwareChanged += r => bobSees.Add(r);
        owner.Trains.CouplerHardwareChanged += r => ownerSees.Add(r);

        owner.Trains.ReportCouplerHardware(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front));
        Pump(server, new[] { owner, bob });

        CouplerHardwareReport seen = Assert.Single(bobSees);
        Assert.Equal(CouplerHardwareKind.HoseConnect, seen.Kind);
        Assert.Empty(ownerSees); // the reporter's world already did it — no echo
        Assert.Equal((car1, CoupleEnd.Front), bob.Trains.Hardware.HoseAt(car0, CoupleEnd.Rear));

        // Late joiner: the join burst rigs the mirror before any replica exists.
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { owner, bob, carol });
        Assert.Equal((car1, CoupleEnd.Front), carol.Trains.Hardware.HoseAt(car0, CoupleEnd.Rear));
    }

    [Fact]
    public void A_phantom_connect_is_refused_and_nothing_is_broadcast()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("loco", "boxcar", "tanker"));
        Pump(server, new[] { owner });
        TrainsetDef set = owner.Trains.View.Sets.Values.Single();

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { owner, bob });

        int bobSaw = 0;
        var refusals = new List<string>();
        bob.Trains.CouplerHardwareChanged += _ => bobSaw++;
        server.Trains.ProposalRejected += (_, reason) => refusals.Add(reason);

        // Ends of the same set two cars apart — the phantom-MU class.
        owner.Trains.ReportCouplerHardware(Hose(set.Cars[0].Id, CoupleEnd.Front, set.Cars[2].Id, CoupleEnd.Rear));
        Pump(server, new[] { owner, bob });

        Assert.Equal(0, bobSaw);
        Assert.Contains(refusals, r => r.Contains("not adjacent"));
    }

    [Fact]
    public void N_clients_reporting_the_same_auto_break_commit_once()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("loco", "boxcar"));
        Pump(server, new[] { owner });
        TrainsetDef set = owner.Trains.View.Sets.Values.Single();
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { owner, bob, carol });

        owner.Trains.ReportCouplerHardware(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front));
        Pump(server, new[] { owner, bob, carol });

        var carolSees = new List<CouplerHardwareReport>();
        carol.Trains.CouplerHardwareChanged += r => carolSees.Add(r);

        // The consist pulls apart: every client's native world breaks the hose and reports it.
        var breakReport = new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, car0, CoupleEnd.Rear, car1, CoupleEnd.Front);
        owner.Trains.ReportCouplerHardware(breakReport);
        bob.Trains.ReportCouplerHardware(breakReport);
        carol.Trains.ReportCouplerHardware(breakReport);
        Pump(server, new[] { owner, bob, carol });

        // Carol reported it herself AND hears the owner's commit relayed once — never a storm of N.
        Assert.Single(carolSees);
        Assert.Null(carol.Trains.Hardware.HoseAt(car0, CoupleEnd.Rear));
    }

    [Fact]
    public void A_deleted_cars_hardware_is_pruned_from_the_join_burst()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("loco", "boxcar"));
        Pump(server, new[] { owner });
        TrainsetDef set = owner.Trains.View.Sets.Values.Single();
        int car0 = set.Cars[0].Id, car1 = set.Cars[1].Id;

        owner.Trains.ReportCouplerHardware(Hose(car0, CoupleEnd.Rear, car1, CoupleEnd.Front));
        Pump(server, new[] { owner });

        owner.Trains.NotifyCarDeleted(car1);
        Pump(server, new[] { owner });

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { owner, bob });
        Assert.Null(bob.Trains.Hardware.HoseAt(car0, CoupleEnd.Rear)); // nothing stale replayed
    }
}
