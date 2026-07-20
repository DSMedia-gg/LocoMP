using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// End-to-end item sync over the Loopback hub — the same NetServer/NetClient stack a live session
/// runs. Covers the M4 win condition (a CLIENT buys an item and the cash leaves the RIGHT wallet),
/// the buy → drop → pickup lifecycle mirrored to everyone, host-captured world items, pickup
/// exclusivity + proximity, and the cold-restart tail: world items AND per-player inventory resume.
/// </summary>
public class ItemSessionTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static CareerConfig Career(ProgressionPreset preset = ProgressionPreset.PerPlayer, long start = 500_00) => new()
    {
        Preset = preset,
        StartingBalanceCents = start,
        ClaimTtlMs = 60_000,
        ReconnectGraceMs = 10_000,
    };

    private static ItemConfig Items(float pickupRadius = 0f) => new()
    {
        ShopPrices = new Dictionary<string, long> { ["lantern"] = 50_00, ["crate"] = 200_00 },
        PickupRadiusM = pickupRadius,
        AcceptExternalItems = true,
    };

    private static Pose At(float x, float z) => new(x, 0f, z, 0f, 0f, 0f, 1f);

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static (LoopbackNetwork hub, ManualClock clock, NetServer server, NetClient a, NetClient b)
        Session(ProgressionPreset preset = ProgressionPreset.PerPlayer, long start = 500_00, float pickupRadius = 0f)
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server,
            new ServerConfig(Identity, career: Career(preset, start), items: Items(pickupRadius)), clock);
        var a = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "key-alice");
        var b = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "key-bob");
        Pump(server, new[] { a, b });
        return (hub, clock, server, a, b);
    }

    [Fact]
    public void Client_purchase_mints_the_item_and_burns_from_the_buyers_own_wallet()
    {
        var (_, _, server, a, b) = Session(); // a is the world source; b is a plain client

        b.Items.Purchase("lantern");
        Pump(server, new[] { a, b });

        ClientItem mine = b.Items.Items.Values.Single();
        Assert.Equal("lantern", mine.Def.PrefabName);
        Assert.Equal(ItemLocationKind.Possessed, mine.Location);
        Assert.Equal(b.LocalId, mine.OwnerPeerId);
        Assert.Equal(450_00, b.Career.BalanceCents);        // the buyer paid...
        Assert.Equal(500_00, a.Career.BalanceCents);        // ...not the host (the incumbent's gap)
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);

        // Everyone else sees the same item, owned by the buyer's peer.
        ClientItem seen = a.Items.Items[mine.Def.Id];
        Assert.Equal(ItemLocationKind.Possessed, seen.Location);
        Assert.Equal(b.LocalId, seen.OwnerPeerId);
        Assert.Equal("Bob", seen.OwnerName);
    }

    [Fact]
    public void Join_burst_delivers_the_shop_catalog_to_every_client()
    {
        var (_, _, _, a, b) = Session(); // Items() sells lantern @ 50.00 and crate @ 200.00

        foreach (NetClient c in new[] { a, b })
        {
            Assert.Equal(2, c.Items.ShopCatalog.Count);
            Assert.Equal(50_00, c.Items.ShopCatalog["lantern"]);
            Assert.Equal(200_00, c.Items.ShopCatalog["crate"]);
        }
    }

    [Fact]
    public void Purchase_is_refused_on_insufficient_funds_and_mints_nothing()
    {
        var (_, _, server, a, b) = Session(start: 10_00);
        string? refusal = null;
        b.Items.RequestRejected += (reason, _) => refusal = reason;

        b.Items.Purchase("lantern"); // 50.00 > 10.00
        Pump(server, new[] { a, b });

        Assert.Contains("insufficient funds", refusal);
        Assert.Empty(b.Items.Items);
        Assert.Equal(10_00, b.Career.BalanceCents);
        Assert.Empty(server.Items.Registry.Items);
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void Purchase_of_an_unlisted_prefab_is_refused()
    {
        var (_, _, server, a, b) = Session();
        string? refusal = null;
        b.Items.RequestRejected += (reason, _) => refusal = reason;

        b.Items.Purchase("locomotive");
        Pump(server, new[] { a, b });

        Assert.Contains("not for sale", refusal);
        Assert.Empty(b.Items.Items);
        Assert.Equal(500_00, b.Career.BalanceCents);
    }

    [Fact]
    public void Buy_drop_pickup_mirrors_the_whole_lifecycle_to_everyone()
    {
        var (_, _, server, a, b) = Session();

        b.Items.Purchase("lantern");
        Pump(server, new[] { a, b });
        int id = b.Items.Items.Keys.Single();

        b.Items.RequestDrop(id, At(50, 60));
        Pump(server, new[] { a, b });
        Assert.Equal(ItemLocationKind.World, a.Items.Items[id].Location);
        Assert.Equal(At(50, 60), a.Items.Items[id].WorldPose);

        a.Items.RequestPickup(id);
        Pump(server, new[] { a, b });
        // Now Alice holds Bob's dropped lantern — server-authoritative, mirrored on both clients.
        Assert.Equal(ItemLocationKind.Possessed, b.Items.Items[id].Location);
        Assert.Equal(a.LocalId, b.Items.Items[id].OwnerPeerId);
        Assert.Equal(a.LocalId, a.Items.Items[id].OwnerPeerId);
        Assert.True(server.Items.Registry.ItemConservationHolds);
    }

    [Fact]
    public void Pickup_is_exclusive_across_clients()
    {
        var (_, _, server, a, b) = Session();
        a.Items.RegisterWorldItem("crate", At(0, 0), "", token: 1); // a is the world source
        Pump(server, new[] { a, b });
        int id = a.Items.Items.Values.Single().Def.Id;

        b.Items.RequestPickup(id);
        Pump(server, new[] { a, b });
        Assert.Equal(b.LocalId, a.Items.Items[id].OwnerPeerId); // Bob got it

        string? refusal = null;
        a.Items.RequestRejected += (reason, _) => refusal = reason;
        a.Items.RequestPickup(id); // Alice too late
        Pump(server, new[] { a, b });
        Assert.Contains("already held", refusal);
        Assert.Equal(b.LocalId, a.Items.Items[id].OwnerPeerId); // still Bob's
    }

    [Fact]
    public void A_locked_essential_is_visible_to_all_but_pickup_is_refused()
    {
        var (_, _, server, a, b) = Session();
        a.Items.RegisterWorldItem("Map", At(5, 5), "", token: 3, locked: true); // a's personal essential, set down
        Pump(server, new[] { a, b });
        int id = a.Items.Items.Values.Single().Def.Id;

        // Everyone sees it, flagged locked (the Shim renders the replica non-grabbable off this).
        Assert.True(a.Items.Items[id].WorldLocked);
        Assert.True(b.Items.Items[id].WorldLocked);

        // Look, but don't touch: a remote's pickup is refused and the item never moves.
        string? refusal = null;
        b.Items.RequestRejected += (reason, _) => refusal = reason;
        b.Items.RequestPickup(id);
        Pump(server, new[] { a, b });
        Assert.Contains("personal item", refusal);
        Assert.Equal(ItemLocationKind.World, a.Items.Items[id].Location);
        Assert.Equal(0, a.Items.Items[id].OwnerPeerId);
    }

    [Fact]
    public void Only_the_world_source_registers_world_items()
    {
        var (_, _, server, a, b) = Session();

        string? refusal = null;
        b.Items.RequestRejected += (reason, _) => refusal = reason;
        b.Items.RegisterWorldItem("crate", At(1, 1), "", token: 9); // Bob is not the world source
        Pump(server, new[] { a, b });
        Assert.Contains("only the world source", refusal);
        Assert.Empty(server.Items.Registry.Items);

        uint echoed = 0;
        a.Items.RegisterAccepted += (token, _) => echoed = token;
        a.Items.RegisterWorldItem("crate", At(1, 1), "sealed", token: 7);
        Pump(server, new[] { a, b });
        Assert.Equal(7u, echoed); // the registrant's correlation token comes back
        int id = a.Items.Items.Values.Single().Def.Id;
        Assert.Equal(ItemLocationKind.World, b.Items.Items[id].Location); // everyone sees it
        Assert.Equal("sealed", b.Items.Items[id].Def.State);
    }

    [Fact]
    public void World_source_despawn_removes_the_item_everywhere()
    {
        var (_, _, server, a, b) = Session();
        a.Items.RegisterWorldItem("crate", At(2, 2), "", token: 1);
        Pump(server, new[] { a, b });
        int id = a.Items.Items.Values.Single().Def.Id;

        a.Items.DespawnItem(id);
        Pump(server, new[] { a, b });
        Assert.False(a.Items.Items.ContainsKey(id));
        Assert.False(b.Items.Items.ContainsKey(id));
        Assert.False(server.Items.Registry.Items.ContainsKey(id));
    }

    [Fact]
    public void Pickup_is_refused_beyond_the_configured_radius()
    {
        var (_, _, server, a, b) = Session(pickupRadius: 5f);
        a.Items.RegisterWorldItem("crate", At(1000, 1000), "", token: 1);
        Pump(server, new[] { a, b });
        int id = a.Items.Items.Values.Single().Def.Id;

        string? refusal = null;
        b.Items.RequestRejected += (reason, _) => refusal = reason; // Bob is at the origin
        b.Items.RequestPickup(id);
        Pump(server, new[] { a, b });

        Assert.Contains("m away", refusal);
        Assert.Equal(ItemLocationKind.World, b.Items.Items[id].Location); // never picked up
    }

    [Fact]
    public void Join_burst_delivers_world_items_and_inventory_to_a_newcomer()
    {
        var (hub, clock, server, a, b) = Session();
        a.Items.RegisterWorldItem("crate", At(3, 4), "sealed", token: 1);
        b.Items.Purchase("lantern");
        Pump(server, new[] { a, b });

        var c = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "key-carol");
        Pump(server, new[] { a, b, c });

        Assert.Equal(2, c.Items.Items.Count);
        Assert.Contains(c.Items.Items.Values, i => i.Def.PrefabName == "crate" && i.Location == ItemLocationKind.World);
        Assert.Contains(c.Items.Items.Values, i => i.Def.PrefabName == "lantern"
            && i.Location == ItemLocationKind.Possessed && i.OwnerPeerId == b.LocalId);
    }

    [Fact]
    public void Cold_restart_resumes_world_items_and_per_player_inventory()
    {
        var (_, clock, server, a, b) = Session();
        a.Items.RegisterWorldItem("crate", At(100, 200), "sealed", token: 1);
        b.Items.Purchase("lantern");
        Pump(server, new[] { a, b });
        int crateId = a.Items.Items.Values.Single(i => i.Def.PrefabName == "crate").Def.Id;
        int lanternId = b.Items.Items.Values.Single(i => i.Def.PrefabName == "lantern").Def.Id;

        byte[] bytes = SaveCodec.Write(server.CaptureSave());
        a.Leave();
        b.Leave();
        Pump(server, new[] { a, b });
        server.Dispose();

        // A fresh server process, restored from the save (07 §M3 cold restart).
        var hub2 = new LoopbackNetwork();
        var server2 = new NetServer(hub2.Server,
            new ServerConfig(Identity, career: Career(), items: Items()), clock, SaveCodec.Read(bytes));
        var a2 = new NetClient(hub2.Connect(out _), Identity, "Alice", clock, playerKey: "key-alice");
        var b2 = new NetClient(hub2.Connect(out _), Identity, "Bob", clock, playerKey: "key-bob");
        Pump(server2, new[] { a2, b2 });

        // The world crate is back at its pose...
        ClientItem crate = a2.Items.Items[crateId];
        Assert.Equal(ItemLocationKind.World, crate.Location);
        Assert.Equal(At(100, 200), crate.WorldPose);
        Assert.Equal("sealed", crate.Def.State);

        // ...and Bob's inventory rebinds to him on rejoin (the 02 §5 win-condition tail).
        ClientItem lantern = b2.Items.Items[lanternId];
        Assert.Equal(ItemLocationKind.Possessed, lantern.Location);
        Assert.Equal(b2.LocalId, lantern.OwnerPeerId);
        Assert.Equal(450_00, b2.Career.BalanceCents); // the wallet came back too
    }

    [Fact]
    public void Shared_career_pools_purchased_inventory()
    {
        var (_, _, server, a, b) = Session(ProgressionPreset.SharedCareer);

        b.Items.Purchase("lantern");
        Pump(server, new[] { a, b });
        int id = b.Items.Items.Keys.Single();

        // The item is communal (shared scope) — Alice may drop what Bob bought.
        a.Items.RequestDrop(id, At(7, 7));
        Pump(server, new[] { a, b });
        Assert.Equal(ItemLocationKind.World, b.Items.Items[id].Location);
        Assert.Equal(At(7, 7), b.Items.Items[id].WorldPose);
        Assert.True(server.Items.Registry.ItemConservationHolds);
    }
}
