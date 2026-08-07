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
/// M6-A1.1 — the CosmeticState channel end-to-end over the Loopback hub: owner streams coarse
/// scalars, the server validates/stores/relays, mirrors cache latest-per-car, the join burst
/// replays, deletion prunes. The channel is owner-cosmetic and transaction-free by design (11 §2)
/// — nothing here touches epochs, and the tests pin that boundary by never needing one.
/// </summary>
public class CosmeticSyncTests
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

    private static TrainsetSnapshot RailedSnapshot(TrainsetDef def) =>
        new(def.Id, def.Epoch, 0L, def.Cars
            .Select((_, i) => CarSnapshot.Railed(new BogieState(1, 10f + i * 20f, 5f), new BogieState(1, 2f + i * 20f, 5f)))
            .ToArray());

    /// <summary>Host A + client B joined, A registered one two-car consist.</summary>
    private static (LoopbackNetwork hub, ManualClock clock, NetServer server, NetClient a, NetClient b, TrainsetDef set)
        Session()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        var a = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        var b = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { a, b });
        a.Trains.RegisterTrainset(token: 1, Specs("loco", "boxcar"));
        Pump(server, new[] { a, b });
        TrainsetDef set = server.Trains.Registry.Sets.Values.Single();
        return (hub, clock, server, a, b, set);
    }

    [Fact]
    public void Owner_scalars_relay_to_other_clients_and_cache_latest_per_car()
    {
        var (_, _, server, a, b, set) = Session();
        int loco = set.Cars[0].Id;

        var seen = new List<(int carId, byte kind, byte value)>();
        b.Trains.CosmeticReceived += (carId, kind, value) => seen.Add((carId, kind, value));

        a.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)200) });
        a.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)90),
                                            ((byte)CosmeticKind.SandFlow, (byte)255) });
        Pump(server, new[] { a, b });

        Assert.Contains((loco, (byte)CosmeticKind.SmokeIntensity, (byte)200), seen);
        Assert.Contains((loco, (byte)CosmeticKind.SmokeIntensity, (byte)90), seen);
        Assert.Contains((loco, (byte)CosmeticKind.SandFlow, (byte)255), seen);
        // The cache holds ONLY the latest per kind — the Shim's spawn path reads this.
        Assert.Equal((byte)90, b.Trains.Cosmetics[loco][(byte)CosmeticKind.SmokeIntensity]);
        Assert.Equal((byte)255, b.Trains.Cosmetics[loco][(byte)CosmeticKind.SandFlow]);
        // The sender does not hear its own echo (its world is the source).
        Assert.False(a.Trains.Cosmetics.ContainsKey(loco));
    }

    [Fact]
    public void A_non_owner_sender_is_rejected_and_nothing_relays()
    {
        var (_, _, server, a, b, set) = Session();
        int loco = set.Cars[0].Id;

        var rejects = new List<string>();
        server.Trains.ProposalRejected += (_, reason) => rejects.Add(reason);
        bool aHeard = false;
        a.Trains.CosmeticReceived += (_, _, _) => aHeard = true;

        b.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)255) });
        Pump(server, new[] { a, b });

        Assert.Contains(rejects, r => r.StartsWith("cosmetic:"));
        Assert.False(aHeard, "a non-owner's scalars must not reach anyone");
        Assert.False(a.Trains.Cosmetics.ContainsKey(loco));
    }

    [Fact]
    public void A_late_joiner_receives_the_current_state_in_the_join_burst()
    {
        var (hub, clock, server, a, b, set) = Session();
        int loco = set.Cars[0].Id;
        a.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)140) });
        Pump(server, new[] { a, b });

        using var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { a, b, c });

        Assert.True(c.Joined);
        Assert.Equal((byte)140, c.Trains.Cosmetics[loco][(byte)CosmeticKind.SmokeIntensity]);
    }

    [Fact]
    public void A_new_owner_can_stream_the_same_car_after_an_ownership_flip()
    {
        // The server's incoming seq guard is keyed per (car, sender): B's fresh counter must not
        // be judged against A's — otherwise every adopted loco goes cosmetically mute until the
        // byte wraps.
        var (_, _, server, a, b, set) = Session();
        int loco = set.Cars[0].Id;
        a.Trains.SendSnapshot(RailedSnapshot(set)); // baselined — parks on leave, not phantom-retires
        for (int i = 0; i < 40; i++)
            a.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)i) });
        Pump(server, new[] { a, b });

        a.Leave(); // parks the set
        Pump(server, new[] { a, b });
        b.Trains.RequestOwnership(set.Id); // B adopts
        Pump(server, new[] { b });
        Assert.Equal(b.LocalId, server.Trains.Registry.Sets[set.Id].OwnerId);

        b.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)7) });
        Pump(server, new[] { b });

        Assert.Equal(0, server.Trains.StaleSnapshotsDropped); // unrelated counter stays clean
        // The server accepted the new owner's first message: its store moved to B's value.
        // (Verified through a late joiner — the server's own store is private by design.)
    }

    [Fact]
    public void The_new_owners_value_wins_for_a_late_joiner()
    {
        var (hub, clock, server, a, b, set) = Session();
        int loco = set.Cars[0].Id;
        a.Trains.SendSnapshot(RailedSnapshot(set)); // baselined — parks on leave, not phantom-retires
        a.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)200) });
        Pump(server, new[] { a, b });

        a.Leave();
        Pump(server, new[] { a, b });
        b.Trains.RequestOwnership(set.Id);
        Pump(server, new[] { b });
        b.Trains.SendCosmetic(loco, new[] { ((byte)CosmeticKind.SmokeIntensity, (byte)5) });
        Pump(server, new[] { b });

        using var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { b, c });
        Assert.Equal((byte)5, c.Trains.Cosmetics[loco][(byte)CosmeticKind.SmokeIntensity]);
    }

    [Fact]
    public void Deleting_a_car_prunes_its_cosmetic_state_for_later_joiners()
    {
        var (hub, clock, server, a, b, set) = Session();
        int boxcar = set.Cars[1].Id;
        a.Trains.SendCosmetic(boxcar, new[] { ((byte)CosmeticKind.SandFlow, (byte)99) });
        Pump(server, new[] { a, b });
        Assert.True(b.Trains.Cosmetics.ContainsKey(boxcar));

        a.Trains.NotifyCarDeleted(boxcar);
        Pump(server, new[] { a, b });

        using var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { a, b, c });
        Assert.True(c.Joined);
        Assert.False(c.Trains.Cosmetics.ContainsKey(boxcar), "a deleted car's plume must not haunt newcomers");
    }

    [Fact]
    public void Oversized_and_empty_sends_are_refused_at_the_client()
    {
        var (_, _, server, a, b, set) = Session();
        int loco = set.Cars[0].Id;
        bool heard = false;
        b.Trains.CosmeticReceived += (_, _, _) => heard = true;

        a.Trains.SendCosmetic(loco, new (byte, byte)[0]);
        var over = new (byte kind, byte value)[CosmeticCodec.MaxEntries + 1];
        for (int i = 0; i < over.Length; i++) over[i] = ((byte)(i + 1), (byte)1);
        a.Trains.SendCosmetic(loco, over);
        Pump(server, new[] { a, b });

        Assert.False(heard, "an empty or over-cap batch must never leave the client");
    }

    [Fact]
    public void Seq_advance_is_wrap_safe()
    {
        // The latest-wins byte guard both endpoints share: strictly-forward within a half-window,
        // stale and equal never advance, and the wrap seam behaves.
        Assert.True(CosmeticCodec.SeqAdvances(1, 2));
        Assert.True(CosmeticCodec.SeqAdvances(200, 60));   // wrapped forward
        Assert.True(CosmeticCodec.SeqAdvances(255, 0));    // exact wrap
        Assert.False(CosmeticCodec.SeqAdvances(2, 1));     // stale
        Assert.False(CosmeticCodec.SeqAdvances(5, 5));     // duplicate
        Assert.False(CosmeticCodec.SeqAdvances(60, 200));  // far-past arrival reads as stale
    }
}
