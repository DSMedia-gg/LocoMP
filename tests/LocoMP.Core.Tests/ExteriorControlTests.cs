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
/// v18 exterior-hardware controls (02 §1 P0 — the handbrake) over the ControlState/ControlInput
/// machinery: ids ≥ <see cref="VirtualControlId.ExteriorFloor"/> are grant-exempt on the input path
/// (a handbrake wheel is worked from the ground — the acting client's physical reach is the gate),
/// route to the sim owner like any input, are server-committed directly on a PARKED set, and ride
/// the join burst exactly like cab-control state. Cab ids keep every existing gate — pinned here so
/// the carve-out can never widen silently.
/// </summary>
public class ExteriorControlTests
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

    [Fact]
    public void An_exterior_input_routes_to_the_owner_without_a_grant()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("boxcar"));
        Pump(server, new[] { owner });
        int carId = owner.Trains.View.Sets.Values.Single().Cars[0].Id;

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { owner, bob });

        var received = new List<(int carId, byte controlId, float value)>();
        owner.Trains.ControlInputReceived += (c, id, v) => received.Add((c, id, v));

        bob.Trains.SendControlInput(carId, VirtualControlId.Handbrake, 0.8f); // NO grant requested
        Pump(server, new[] { owner, bob });

        (int, byte, float) hit = Assert.Single(received);
        Assert.Equal((carId, VirtualControlId.Handbrake, 0.8f), hit);
    }

    [Fact]
    public void A_cab_input_without_a_grant_is_still_refused()
    {
        // The carve-out must not widen: id 1 (a real DV ControlType) keeps the grant gate.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("loco"));
        Pump(server, new[] { owner });
        int carId = owner.Trains.View.Sets.Values.Single().Cars[0].Id;

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { owner, bob });

        int inputs = 0;
        var refusals = new List<string>();
        owner.Trains.ControlInputReceived += (_, _, _) => inputs++;
        server.Trains.ProposalRejected += (_, reason) => refusals.Add(reason);

        bob.Trains.SendControlInput(carId, controlId: 1, 0.5f);
        Pump(server, new[] { owner, bob });

        Assert.Equal(0, inputs);
        Assert.Contains(refusals, r => r.Contains("no control grant"));
    }

    [Fact]
    public void A_parked_sets_exterior_input_is_server_committed_and_broadcast()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("boxcar"));
        Pump(server, new[] { owner });
        var set = owner.Trains.View.Sets.Values.Single();
        int carId = set.Cars[0].Id;

        // Stream one snapshot so the set is baselined, then park it (owner 0).
        owner.Trains.SendSnapshot(new TrainsetSnapshot(set.Id, set.Epoch, clock.NowMs,
            new[] { CarSnapshot.Railed(new BogieState(1, 5f, 0f), new BogieState(1, 13f, 0f)) }));
        Pump(server, new[] { owner });
        owner.Trains.ReleaseOwnership(set.Id);
        Pump(server, new[] { owner });
        Assert.Equal(0, server.Trains.Registry.Sets[set.Id].OwnerId);

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { owner, bob, carol });

        var carolSees = new List<(int carId, byte controlId, float value)>();
        carol.Trains.ControlStateReceived += (c, id, v) => carolSees.Add((c, id, v));

        bob.Trains.SendControlInput(carId, VirtualControlId.Handbrake, 1.0f); // secure the cut
        Pump(server, new[] { owner, bob, carol });

        Assert.Contains((carId, VirtualControlId.Handbrake, 1.0f), carolSees);

        // And it is join-burst state now: a late joiner's replicas read the set handbrake.
        using var dave = new NetClient(hub.Connect(out _), Identity, "Dave", clock, playerKey: "kD");
        var daveSees = new List<(int carId, byte controlId, float value)>();
        dave.Trains.ControlStateReceived += (c, id, v) => daveSees.Add((c, id, v));
        Pump(server, new[] { owner, bob, carol, dave });
        Assert.Contains((carId, VirtualControlId.Handbrake, 1.0f), daveSees);
    }

    [Fact]
    public void A_parked_sets_cab_input_stays_dropped()
    {
        // The parked commit is exterior-only: a cab lever on an ownerless set has no sim to apply
        // it, so nothing must be stored or broadcast (pinned so the parked path can never widen).
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("loco"));
        Pump(server, new[] { owner });
        var set = owner.Trains.View.Sets.Values.Single();
        int carId = set.Cars[0].Id;
        owner.Trains.SendSnapshot(new TrainsetSnapshot(set.Id, set.Epoch, clock.NowMs,
            new[] { CarSnapshot.Railed(new BogieState(1, 5f, 0f), new BogieState(1, 13f, 0f)) }));
        Pump(server, new[] { owner });
        owner.Trains.ReleaseOwnership(set.Id);
        Pump(server, new[] { owner });

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { owner, bob, carol });

        int carolSaw = 0;
        carol.Trains.ControlStateReceived += (_, _, _) => carolSaw++;

        bob.Trains.SendControlInput(carId, controlId: 1, 0.5f); // a THROTTLE on a parked set
        Pump(server, new[] { owner, bob, carol });

        Assert.Equal(0, carolSaw);
    }

    [Fact]
    public void An_owners_exterior_state_rides_the_join_burst()
    {
        // The owner path was already generic — this pins that id 200 rides it end to end.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var owner = new NetClient(hub.Connect(out _), Identity, "Owner", clock, playerKey: "kO");
        Pump(server, new[] { owner });
        owner.Trains.RegisterTrainset(token: 1, Specs("boxcar"));
        Pump(server, new[] { owner });
        int carId = owner.Trains.View.Sets.Values.Single().Cars[0].Id;

        owner.Trains.SendControlState(carId, VirtualControlId.Handbrake, 0.6f);
        Pump(server, new[] { owner });

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        var bobSees = new List<(int carId, byte controlId, float value)>();
        bob.Trains.ControlStateReceived += (c, id, v) => bobSees.Add((c, id, v));
        Pump(server, new[] { owner, bob });

        Assert.Contains((carId, VirtualControlId.Handbrake, 0.6f), bobSees);
    }
}
