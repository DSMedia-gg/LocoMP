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
/// D24 (closes the FeeExternal gap): comms fees are honoured whoever executes. A routed action
/// whose executor is NOT the natively-billing world source (another guest's owned car — or ANY
/// owner on a dedicated server, which has no world source) is billed by the SERVER from its flat
/// fee table at route time, refuse-on-poor, and the command carries serverBilled so the executor
/// never bills on top. The embedded host keeps billing the richer native price itself.
/// </summary>
public class FeeRoutingTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static ServerConfig Config(bool hostBillsNatively)
    {
        var config = new ServerConfig(Identity);
        config.Career.StartingBalanceCents = 500_00;
        config.Career.AcceptExternalJobs = hostBillsNatively; // = an embedded host exists
        return config;
    }

    /// <summary>A (world source) + B + C joined; B registered and streamed one two-car set.</summary>
    private static (LoopbackNetwork hub, NetServer server, NetClient a, NetClient b, NetClient c, TrainsetDef set)
        Session(bool hostBillsNatively)
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, Config(hostBillsNatively), clock);
        var a = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        var b = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { a, b, c });
        Assert.True(a.Joined && b.Joined && c.Joined);

        b.Trains.RegisterTrainset(token: 1, new[] { new CarDef(0, "loco"), new CarDef(0, "boxcar") });
        Pump(server, new[] { a, b, c });
        TrainsetDef set = server.Trains.Registry.Sets.Values.Single();
        Assert.Equal(b.LocalId, set.OwnerId);
        return (hub, server, a, b, c, set);
    }

    [Fact]
    public void A_routed_action_on_a_guest_owned_car_bills_the_initiator_server_side()
    {
        var (_, server, a, b, c, set) = Session(hostBillsNatively: true);

        bool? billedFlag = null;
        int initiatorSeen = 0;
        b.Trains.CommsActionCommanded += (_, _, _, initiator, serverBilled) =>
        {
            initiatorSeen = initiator;
            billedFlag = serverBilled;
        };
        string wallet = server.Career.Registry.Policy.WalletAccountFor("kC");
        long before = server.Career.Registry.Ledger.BalanceOf(wallet);

        c.Trains.RequestCommsAction(CommsActionKind.Delete, set.Cars[0].Id, Pose.Identity);
        Pump(server, new[] { a, b, c });

        Assert.Equal(c.LocalId, initiatorSeen);
        Assert.True(billedFlag, "the executor must be told the server already billed");
        Assert.Equal(before - 100_00, server.Career.Registry.Ledger.BalanceOf(wallet));
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void A_routed_action_to_the_natively_billing_host_is_not_server_billed()
    {
        // The embedded host executes AND bills the native (distance-scaled, exemption-aware)
        // price itself — the server must not pre-bill or the initiator pays twice.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, Config(hostBillsNatively: true), clock);
        using var a = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        using var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { a, c });

        a.Trains.RegisterTrainset(token: 1, new[] { new CarDef(0, "loco") }); // world source owns it
        Pump(server, new[] { a, c });
        TrainsetDef set = server.Trains.Registry.Sets.Values.Single();

        bool? billedFlag = null;
        a.Trains.CommsActionCommanded += (_, _, _, _, serverBilled) => billedFlag = serverBilled;
        string wallet = server.Career.Registry.Policy.WalletAccountFor("kC");
        long before = server.Career.Registry.Ledger.BalanceOf(wallet);

        c.Trains.RequestCommsAction(CommsActionKind.Delete, set.Cars[0].Id, Pose.Identity);
        Pump(server, new[] { a, c });

        Assert.False(billedFlag, "the natively-billing world source bills, not the server");
        Assert.Equal(before, server.Career.Registry.Ledger.BalanceOf(wallet));
    }

    [Fact]
    public void On_a_dedicated_server_even_the_first_joiner_is_not_a_native_biller()
    {
        // The dedicated "first admitted" is just a player (no embedded host world) — routed
        // actions on THEIR cars must server-bill too, closing the R4 "guest fees all waived" gap.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, Config(hostBillsNatively: false), clock);
        using var first = new NetClient(hub.Connect(out _), Identity, "First", clock, playerKey: "kF");
        using var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { first, c });

        first.Trains.RegisterTrainset(token: 1, new[] { new CarDef(0, "loco") });
        Pump(server, new[] { first, c });
        TrainsetDef set = server.Trains.Registry.Sets.Values.Single();

        bool? billedFlag = null;
        first.Trains.CommsActionCommanded += (_, _, _, _, serverBilled) => billedFlag = serverBilled;
        string wallet = server.Career.Registry.Policy.WalletAccountFor("kC");
        long before = server.Career.Registry.Ledger.BalanceOf(wallet);

        c.Trains.RequestCommsAction(CommsActionKind.Rerail, set.Cars[0].Id, new Pose(1f, 2f, 3f, 0f, 0f, 0f, 1f));
        Pump(server, new[] { first, c });

        Assert.True(billedFlag);
        Assert.Equal(before - 500_00, server.Career.Registry.Ledger.BalanceOf(wallet));
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void An_unaffordable_routed_action_refuses_and_never_routes()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var config = Config(hostBillsNatively: true);
        config.Career.StartingBalanceCents = 50_00; // less than the $100 delete
        using var server = new NetServer(hub.Server, config, clock);
        using var a = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        using var b = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { a, b, c });
        b.Trains.RegisterTrainset(token: 1, new[] { new CarDef(0, "loco") });
        Pump(server, new[] { a, b, c });
        TrainsetDef set = server.Trains.Registry.Sets.Values.Single();

        bool commanded = false;
        b.Trains.CommsActionCommanded += (_, _, _, _, _) => commanded = true;
        var rejects = new List<string>();
        server.Trains.ProposalRejected += (_, reason) => rejects.Add(reason);
        string wallet = server.Career.Registry.Policy.WalletAccountFor("kC");
        long before = server.Career.Registry.Ledger.BalanceOf(wallet);

        c.Trains.RequestCommsAction(CommsActionKind.Delete, set.Cars[0].Id, Pose.Identity);
        Pump(server, new[] { a, b, c });

        Assert.False(commanded, "an unaffordable action must never reach the executor");
        Assert.Contains(rejects, r => r.StartsWith("comms:"));
        Assert.Equal(before, server.Career.Registry.Ledger.BalanceOf(wallet)); // never an overdraft
    }
}
