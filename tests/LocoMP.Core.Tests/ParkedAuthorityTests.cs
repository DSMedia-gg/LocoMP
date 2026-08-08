using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// D21: the parked-authority verb family (the Round 2 Part 4 finding — once a driver disconnects
/// after a split, NO verb worked on the leftovers: radio delete refused, rerail refused, no claim
/// verb in-game). Comms actions on a parked target no longer dead-end at "nobody can act on it":
/// DELETE commits server-side as a retire transaction (the CarDeleteNotice path), and RERAIL is
/// claim-then-execute — ownership transfers to the requester and the command routes back to them
/// as the new owner-executor, with the owner flip broadcast FIRST on the same ordered channel.
/// All game-free over Loopback.
/// </summary>
public class ParkedAuthorityTests
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

    /// <summary>Register a consist, split it, let the owner leave — the gauntlet's exact orphan
    /// shape (both products parked, owner 0), settle window cleared.</summary>
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

        // Stream a baseline so the split PRODUCTS inherit real car positions — a physically-driven
        // consist, the gauntlet's actual shape. A set that never streams a position now RETIRES on
        // owner-leave (the phantom-orphan fix), which is NOT what this family is about.
        owner.Trains.SendSnapshot(new TrainsetSnapshot(whole.Id, whole.Epoch, clock.NowMs, whole.Cars
            .Select((_, i) => CarSnapshot.Railed(new BogieState(1, 100f + i * 20f, 5f),
                                                 new BogieState(1, 100f + i * 20f - 8f, 5f))).ToArray()));
        Pump(server, owner, guest);

        owner.Trains.ProposeUncouple(whole.Id, gapIndex: 1);
        Pump(server, owner, guest);
        Assert.Equal(2, server.Trains.Registry.Sets.Count);

        owner.Leave();
        Pump(server, owner, guest);
        owner.Dispose();

        TrainsetDef[] sets = server.Trains.Registry.Sets.Values.OrderBy(s => s.Id).ToArray();
        Assert.All(sets, s => Assert.Equal(0, s.OwnerId));
        clock.Advance(3000);
        return (sets[0], sets[1]);
    }

    [Fact]
    public void Radio_delete_on_a_parked_set_retires_the_car_for_everyone()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef tail) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        int doomed = head.Cars[0].Id;
        g.Trains.RequestCommsAction(CommsActionKind.Delete, doomed, Pose.Identity);
        Pump(server, g);

        // The car is in no set anywhere; the survivor re-formed as a fresh parked product.
        Assert.DoesNotContain(head.Id, server.Trains.Registry.Sets.Keys);
        Assert.DoesNotContain(server.Trains.Registry.Sets.Values,
            s => s.Cars.Any(c => c.Id == doomed));
        TrainsetDef product = server.Trains.Registry.Sets.Values.Single(s => s.Id != tail.Id);
        Assert.Equal(0, product.OwnerId);                     // retire never assigns a simulator
        Assert.Single(product.Cars);
        Assert.Equal(head.Cars[1].Id, product.Cars[0].Id);

        // The retire broadcast reached the room like any transaction.
        Assert.False(g.Trains.View.Sets.ContainsKey(head.Id));
        Assert.True(g.Trains.View.Sets.ContainsKey(product.Id));
    }

    [Fact]
    public void Radio_delete_of_a_parked_sets_last_car_removes_the_set()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        // Split the 2-car head into singles first, so a delete hits a last-car set.
        g.Trains.RequestUncouple(head.Cars[0].Id, CoupleEnd.Rear, head.Cars[1].Id);
        Pump(server, g);
        TrainsetDef single = server.Trains.Registry.Sets.Values
            .Single(s => s.Cars.Count == 1 && s.Cars[0].Id == head.Cars[0].Id);

        g.Trains.RequestCommsAction(CommsActionKind.Delete, head.Cars[0].Id, Pose.Identity);
        Pump(server, g);

        Assert.DoesNotContain(single.Id, server.Trains.Registry.Sets.Keys);
        Assert.False(g.Trains.View.Sets.ContainsKey(single.Id)); // TrainsetRemove reached the room
    }

    [Fact]
    public void Parked_rerail_claims_the_wreck_for_the_requester_and_routes_the_command_back()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        var commands = new List<(CommsActionKind kind, int carId, Pose dest, int initiator)>();
        var ownerAtCommand = new List<int>(); // the ordering contract: owner flip BEFORE command
        g.Trains.CommsActionCommanded += (kind, carId, dest, initiator, _) =>
        {
            commands.Add((kind, carId, dest, initiator));
            ownerAtCommand.Add(g.Trains.View.Sets[head.Id].OwnerId);
        };

        var dest = new Pose(10f, 2f, 30f, 0f, 0f, 0f, 1f);
        g.Trains.RequestCommsAction(CommsActionKind.Rerail, head.Cars[0].Id, dest);
        Pump(server, g);

        // The server transferred ownership to the requester (the D21 claim verb)...
        Assert.Equal(g.LocalId, server.Trains.Registry.Sets[head.Id].OwnerId);
        // ...and routed the command back to them as the new owner-executor.
        (CommsActionKind kind, int carId, Pose routedDest, int initiator) = Assert.Single(commands);
        Assert.Equal(CommsActionKind.Rerail, kind);
        Assert.Equal(head.Cars[0].Id, carId);
        Assert.Equal(dest, routedDest);
        Assert.Equal(g.LocalId, initiator);                   // the fee lands on themselves
        Assert.Equal(g.LocalId, Assert.Single(ownerAtCommand)); // adoption strictly precedes execution

        // Ownership is not a membership change: same epoch, snapshots admissible immediately.
        Assert.Equal(head.Epoch, server.Trains.Registry.Sets[head.Id].Epoch);
    }

    [Fact]
    public void A_parked_radio_delete_bills_the_initiator_from_the_server_fee_table()
    {
        // R4-M: the scan-assist made the ROUTED delete the normal path, and that path was free —
        // the $100 only ever rode the (now bypassed) adopt-then-delete native fee. The server now
        // bills its own table when IT executes the retire.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        string wallet = server.Career.Registry.Policy.WalletAccountFor("kGuest");
        long before = server.Career.Registry.Ledger.BalanceOf(wallet);
        long burnedBefore = server.Career.Registry.Ledger.TotalBurned;

        g.Trains.RequestCommsAction(CommsActionKind.Delete, head.Cars[0].Id, Pose.Identity);
        Pump(server, g);

        Assert.DoesNotContain(server.Trains.Registry.Sets.Values, s => s.Cars.Any(c => c.Id == head.Cars[0].Id));
        Assert.Equal(before - 100_00, server.Career.Registry.Ledger.BalanceOf(wallet));
        Assert.Equal(burnedBefore + 100_00, server.Career.Registry.Ledger.TotalBurned);
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void A_parked_rerail_bills_the_flat_fee_at_claim_time()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        string wallet = server.Career.Registry.Policy.WalletAccountFor("kGuest");
        long before = server.Career.Registry.Ledger.BalanceOf(wallet);

        g.Trains.RequestCommsAction(CommsActionKind.Rerail, head.Cars[0].Id, new Pose(10f, 2f, 30f, 0f, 0f, 0f, 1f));
        Pump(server, g);

        Assert.Equal(g.LocalId, server.Trains.Registry.Sets[head.Id].OwnerId); // claim went through
        Assert.Equal(before - 500_00, server.Career.Registry.Ledger.BalanceOf(wallet));
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void An_unaffordable_parked_action_is_refused_and_touches_nothing()
    {
        // Fee gates FIRST: a wallet that cannot pay gets a named refusal, keeps its money, and the
        // world does not change — no free deletes, no free claims, never an overdraft.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var config = new ServerConfig(Identity, career: new CareerConfig { StartingBalanceCents = 50_00 });
        using var server = new NetServer(hub.Server, config, clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        var refusals = new List<string>();
        server.Trains.ProposalRejected += (_, reason) => refusals.Add(reason);
        string wallet = server.Career.Registry.Policy.WalletAccountFor("kGuest");

        g.Trains.RequestCommsAction(CommsActionKind.Delete, head.Cars[0].Id, Pose.Identity);
        g.Trains.RequestCommsAction(CommsActionKind.Rerail, head.Cars[1].Id, Pose.Identity);
        Pump(server, g);

        Assert.Equal(2, refusals.Count(r => r.Contains("insufficient funds")));
        Assert.Contains(server.Trains.Registry.Sets.Values, s => s.Cars.Any(c => c.Id == head.Cars[0].Id));
        Assert.Equal(0, server.Trains.Registry.Sets[head.Id].OwnerId); // no claim happened
        Assert.Equal(50_00, server.Career.Registry.Ledger.BalanceOf(wallet));
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void Parked_comms_refusals_are_named_not_silent()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        var refusals = new List<string>();
        server.Trains.ProposalRejected += (_, reason) => refusals.Add(reason);

        // An action kind the parked branch does not support (summon) refuses by name.
        g.Trains.RequestCommsAction((CommsActionKind)7, head.Cars[0].Id, Pose.Identity);
        Pump(server, g);

        Assert.Contains(refusals, r => r.Contains("unsupported action"));
        Assert.Equal(2, server.Trains.Registry.Sets.Count);   // nothing moved, nothing claimed
        Assert.All(server.Trains.Registry.Sets.Values, s => Assert.Equal(0, s.OwnerId));
    }

    [Fact]
    public void A_comms_fee_charge_reaches_the_initiators_wallet_display()
    {
        // R5-3: every Round 5 fee billed correctly server-side and the CLIENT never heard — the
        // wallet display only read the server at join mount. A charge must push WalletState (the
        // mirror's refresh trigger) and an ExternalFee economy event to the payer.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        long before = g.Career.BalanceCents;
        var events = new List<(EconomyEventKind kind, long amount)>();
        g.Career.EconomyEventReceived += (kind, amount, _) => events.Add((kind, amount));

        g.Trains.RequestCommsAction(CommsActionKind.Delete, head.Cars[0].Id, Pose.Identity);
        Pump(server, g);

        Assert.Equal(before - 100_00, g.Career.BalanceCents); // the CLIENT's number moved, not just the ledger
        Assert.Contains(events, e => e.kind == EconomyEventKind.ExternalFee && e.amount == 100_00);
    }

    [Fact]
    public void A_comms_refusal_reaches_the_initiating_client()
    {
        // R5-10: the trains-side ProposalRejected event had NO subscriber — 9 insufficient-funds
        // refusals died server-side while the player kept pressing delete. The refusal must ride
        // CareerRejected to the initiator.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var config = new ServerConfig(Identity, career: new CareerConfig { StartingBalanceCents = 50_00 });
        using var server = new NetServer(hub.Server, config, clock);
        (TrainsetDef head, TrainsetDef _) = ParkedSplit(hub, clock, server, out NetClient guest);
        using NetClient g = guest;

        var rejects = new List<string>();
        g.Career.RequestRejected += (reason, _) => rejects.Add(reason);

        g.Trains.RequestCommsAction(CommsActionKind.Delete, head.Cars[0].Id, Pose.Identity);
        Pump(server, g);

        Assert.Contains(rejects, r => r.Contains("insufficient funds"));
        Assert.Equal(50_00, g.Career.BalanceCents); // nothing charged, and the client knows it
    }

    [Fact]
    public void A_routed_action_on_your_own_car_is_refused_by_name()
    {
        // R5-10: a client that ROUTES a request believes it is not the owner — the old bare return
        // hid that desync from both sides. Now the mismatch is named to the initiator.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        var owner = new NetClient(hub.Connect(out _), Identity, "Driver", clock, playerKey: "kOwner");
        Pump(server, owner);
        using NetClient o = owner;
        o.Trains.RegisterTrainset(token: 9, Cars(2));
        Pump(server, o);
        TrainsetDef set = Assert.Single(server.Trains.Registry.Sets.Values);

        var rejects = new List<string>();
        o.Career.RequestRejected += (reason, _) => rejects.Add(reason);

        o.Trains.RequestCommsAction(CommsActionKind.Delete, set.Cars[0].Id, Pose.Identity);
        Pump(server, o);

        Assert.Contains(rejects, r => r.Contains("you own car"));
        Assert.Contains(set.Id, (IDictionary<int, TrainsetDef>)server.Trains.Registry.Sets); // nothing deleted
    }
}
