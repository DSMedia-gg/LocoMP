using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.2 roster status over the Loopback hub: every client mirrors each admitted player's role and
/// measured ping (self included), fed by the join burst, role changes, departures, and the slow
/// cadence — the backend the player list's badge/latency column renders from.
/// </summary>
public class RosterSyncTests
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

    [Fact]
    public void The_join_burst_carries_every_players_role_and_ping_self_included()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out int hostId), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        Assert.True(bob.JoinSettled);

        // Roles arrived inside the burst: the first admit is the owner, the newcomer a plain player.
        Assert.Equal(PlayerRole.Owner, bob.RoleOf(hostId));
        Assert.Equal(PlayerRole.Player, bob.RoleOf(bob.LocalId!.Value));
        Assert.Equal(PlayerRole.Owner, host.RoleOf(host.LocalId!.Value));

        // Self is in the roster status (unlike the Players mirror), and the loopback link's ping is
        // a KNOWN 0 ms — not the "unknown" null a transport with no measurement reports.
        Assert.True(bob.Roster.ContainsKey(bob.LocalId!.Value));
        Assert.Equal(0, bob.PingOf(hostId));
    }

    [Fact]
    public void A_promotion_and_demotion_update_every_mirror()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { host, bob, carol });
        Assert.Equal(PlayerRole.Player, carol.RoleOf(bobId));

        host.Promote(bobId);
        Pump(server, new[] { host, bob, carol });
        Assert.Equal(PlayerRole.Admin, carol.RoleOf(bobId));   // a third party sees the new badge
        Assert.Equal(PlayerRole.Admin, bob.RoleOf(bob.LocalId!.Value)); // and so does the promoted player

        host.Demote(bobId);
        Pump(server, new[] { host, bob, carol });
        Assert.Equal(PlayerRole.Player, carol.RoleOf(bobId));
    }

    [Fact]
    public void A_departed_player_is_pruned_from_the_roster()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        Assert.True(host.Roster.ContainsKey(bobId));

        bob.Leave();
        Pump(server, new[] { host, bob });
        Assert.False(host.Roster.ContainsKey(bobId), "PlayerLeft must prune the roster line immediately");
        Assert.True(host.Roster.ContainsKey(host.LocalId!.Value), "self stays on the roster");
    }

    [Fact]
    public void A_restated_roster_with_nothing_changed_does_not_refire()
    {
        // Negative control for the dedupe: the cadence re-push with identical entries (loopback pings
        // are a constant 0) must not repaint; a real role change afterwards must.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        int fired = 0;
        bob.RosterChanged += () => fired++;

        server.BroadcastRoster();                       // the periodic ping refresh, nothing moved
        Pump(server, new[] { host, bob });
        Assert.Equal(0, fired);

        host.Promote(bobId);                            // a genuine change fires exactly as before
        Pump(server, new[] { host, bob });
        Assert.True(fired > 0, "a role change must repaint the player list");
    }
}
