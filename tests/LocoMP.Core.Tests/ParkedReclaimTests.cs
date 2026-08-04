using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// F8 (v14): parked-set reclaim. The gauntlet's concrete finding — an ownerless split-product could
/// never be re-coupled, because chain requests route to a sim owner that does not exist and the
/// ownership rule refuses every proposal. Parked truth is SERVER truth, so the server now commits
/// the couple/uncouple itself when there is nobody to route to; a request that CAN route to the
/// other side's live owner still does. All game-free over Loopback.
/// </summary>
public class ParkedReclaimTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static CarDef[] Cars(int n)
    {
        var cars = new CarDef[n];
        for (int i = 0; i < n; i++) cars[i] = new CarDef(0, i == 0 ? "LocoDiesel" : "BoxcarBrown");
        return cars;
    }

    private static void Pump(NetServer server, params NetClient[] clients)
    {
        for (int i = 0; i < 8; i++) { server.Poll(); foreach (NetClient c in clients) c.Poll(); }
    }

    /// <summary>Register a consist, split it, then let the owner leave — the exact shape that
    /// produced the gauntlet's un-recouplable orphans (both products parked, owner 0).</summary>
    private static (TrainsetDef head, TrainsetDef tail) ParkedSplit(
        LoopbackNetwork hub, ManualClock clock, NetServer server, out NetClient guest)
    {
        var owner = new NetClient(hub.Connect(out _), Identity, "Driver", clock, playerKey: "kOwner");
        guest = new NetClient(hub.Connect(out _), Identity, "Guest", clock, playerKey: "kGuest");
        Pump(server, owner, guest);
        Assert.True(guest.Joined);

        owner.Trains.RegisterTrainset(token: 1, Cars(4));
        Pump(server, owner, guest);
        TrainsetDef whole = Assert.Single(server.Trains.Registry.Sets.Values);

        owner.Trains.ProposeUncouple(whole.Id, gapIndex: 1);
        Pump(server, owner, guest);
        Assert.Equal(2, server.Trains.Registry.Sets.Count);

        owner.Leave();
        Pump(server, owner, guest);
        owner.Dispose();

        TrainsetDef[] sets = server.Trains.Registry.Sets.Values.OrderBy(s => s.Id).ToArray();
        Assert.All(sets, s => Assert.Equal(0, s.OwnerId)); // both parked — the F8 precondition
        clock.Advance(3000);                               // clear the settle window (default 2000 ms)
        return (sets[0], sets[1]);
    }

    [Fact]
    public void Two_parked_sets_recouple_by_any_players_request()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef tail) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        int carA = head.Cars[^1].Id; // the two cars whose couplers physically touch
        int carB = tail.Cars[0].Id;
        g.Trains.RequestCouple(carA, CoupleEnd.Rear, carB, CoupleEnd.Front);
        Pump(server, g);

        TrainsetDef product = Assert.Single(server.Trains.Registry.Sets.Values);
        Assert.Equal(0, product.OwnerId); // reclaim changes membership, never who simulates
        Assert.Equal(head.Cars.Select(c => c.Id).Concat(tail.Cars.Select(c => c.Id)),
                     product.Cars.Select(c => c.Id)); // the touched cars meet in the middle
        Assert.True(product.Epoch > head.Epoch && product.Epoch > tail.Epoch);
        Assert.True(g.Trains.View.Sets.ContainsKey(product.Id), "the merge must broadcast like any transaction");
    }

    [Fact]
    public void A_parked_set_uncouples_via_the_partner_car_id()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef tail) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        // Uncouple inside the 2-car head: the car-relative end cannot name the gap (Core is
        // orientation-blind) — the partner id can, orientation-free.
        int carId = head.Cars[0].Id;
        int partner = head.Cars[1].Id;
        g.Trains.RequestUncouple(carId, CoupleEnd.Rear, partner);
        Pump(server, g);

        Assert.Equal(3, server.Trains.Registry.Sets.Count);
        Assert.DoesNotContain(head.Id, server.Trains.Registry.Sets.Keys); // retired by the split
        Assert.All(server.Trains.Registry.Sets.Values, s => Assert.Equal(0, s.OwnerId));
        Assert.Contains(server.Trains.Registry.Sets.Values, s => s.Cars.Count == 1 && s.Cars[0].Id == carId);
    }

    [Fact]
    public void Reclaim_refusals_are_named_not_silent()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef tail) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        var refusals = new List<string>();
        server.Trains.ProposalRejected += (_, reason) => refusals.Add(reason);

        // A v13-shaped request (no partner) cannot name an interior gap — refused, not guessed.
        g.Trains.RequestUncouple(head.Cars[0].Id, CoupleEnd.Rear);
        // Non-adjacent cars can't be a physical chain.
        g.Trains.RequestUncouple(head.Cars[0].Id, CoupleEnd.Rear, tail.Cars[^1].Id);
        Pump(server, g);

        Assert.Contains(refusals, r => r.Contains("no partner car"));
        Assert.Contains(refusals, r => r.Contains("not in trainset") || r.Contains("not adjacent"));
        Assert.Equal(2, server.Trains.Registry.Sets.Count); // nothing moved
    }

    [Fact]
    public void A_freshly_committed_product_still_settles_before_reclaim()
    {
        // The settle guard is the anti-storm brake: a reclaim landing inside another transaction's
        // settle window is refused exactly like a player proposal would be.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        var owner = new NetClient(hub.Connect(out _), Identity, "Driver", clock, playerKey: "kOwner");
        var guest = new NetClient(hub.Connect(out _), Identity, "Guest", clock, playerKey: "kGuest");
        Pump(server, owner, guest);
        owner.Trains.RegisterTrainset(token: 1, Cars(4));
        Pump(server, owner, guest);
        TrainsetDef whole = Assert.Single(server.Trains.Registry.Sets.Values);
        owner.Trains.ProposeUncouple(whole.Id, gapIndex: 1);
        Pump(server, owner, guest);
        owner.Leave();
        Pump(server, owner, guest);
        owner.Dispose();

        var refusals = new List<string>();
        server.Trains.ProposalRejected += (_, reason) => refusals.Add(reason);

        TrainsetDef[] sets = server.Trains.Registry.Sets.Values.OrderBy(s => s.Id).ToArray();
        guest.Trains.RequestCouple(sets[0].Cars[^1].Id, CoupleEnd.Rear, sets[1].Cars[0].Id, CoupleEnd.Front);
        Pump(server, guest); // NO clock advance — still inside the settle window

        Assert.Contains(refusals, r => r.Contains("settling"));
        Assert.Equal(2, server.Trains.Registry.Sets.Count);
        guest.Dispose();
    }

    [Fact]
    public void A_parked_couple_routes_to_the_other_sides_live_owner_when_one_exists()
    {
        // Mixed case: one side parked, the other simulated — the couple is ONE physical act, so
        // the live owner performs it natively (the ordinary request path, just resolved from the
        // other car). No server commit happens here.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        var parker = new NetClient(hub.Connect(out _), Identity, "Parker", clock, playerKey: "kP");
        using var driver = new NetClient(hub.Connect(out _), Identity, "Driver", clock, playerKey: "kD");
        using var guest = new NetClient(hub.Connect(out _), Identity, "Guest", clock, playerKey: "kG");
        Pump(server, parker, driver, guest);

        parker.Trains.RegisterTrainset(token: 1, Cars(2));
        driver.Trains.RegisterTrainset(token: 2, Cars(2));
        Pump(server, parker, driver, guest);
        parker.Leave();
        Pump(server, parker, driver, guest);
        parker.Dispose();
        clock.Advance(3000);

        TrainsetDef parked = server.Trains.Registry.Sets.Values.Single(s => s.OwnerId == 0);
        TrainsetDef owned = server.Trains.Registry.Sets.Values.Single(s => s.OwnerId != 0);

        var routed = new List<(int carA, int carB)>();
        driver.Trains.CoupleRequested += (carA, _, carB, _) => routed.Add((carA, carB));

        guest.Trains.RequestCouple(parked.Cars[^1].Id, CoupleEnd.Rear, owned.Cars[0].Id, CoupleEnd.Front);
        Pump(server, driver, guest);

        Assert.Single(routed);
        Assert.Equal((parked.Cars[^1].Id, owned.Cars[0].Id), routed[0]);
        Assert.Equal(2, server.Trains.Registry.Sets.Count); // routed, not committed
    }
}
