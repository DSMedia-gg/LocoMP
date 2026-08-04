using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// D18 (join admission at capacity) + F7 (credentialed key takeover). A full server holds a
/// VALIDATED joiner in a FIFO line, reports the live position, and admits through the ordinary
/// path when a slot frees; a key that is already online is a reconnect credential, not a lockout.
/// All game-free over the Loopback hub.
/// </summary>
public class AdmissionQueueTests
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
    public void Queue_is_fifo_and_positions_update_as_the_line_moves()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using var alice = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice });
        Assert.True(alice.Joined);

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        var carolQueue = new List<(int pos, int total)>();
        carol.QueueChanged += (p, t) => carolQueue.Add((p, t));
        Pump(server, new[] { alice, bob, carol });

        Assert.Equal(1, bob.QueuePosition);
        Assert.Equal(2, carol.QueuePosition);
        Assert.Equal(2, carol.QueueTotal);
        Assert.Equal(2, server.QueuedCount);

        // The slot frees: the HEAD is admitted, everyone behind moves up and hears about it.
        alice.Leave();
        Pump(server, new[] { alice, bob, carol });

        Assert.True(bob.JoinSettled);
        Assert.Equal(0, bob.QueuePosition);
        Assert.Equal(1, carol.QueuePosition);
        Assert.Equal(1, carol.QueueTotal);
        Assert.Contains((2, 2), carolQueue);
        Assert.Contains((1, 1), carolQueue);
    }

    [Fact]
    public void A_queued_peer_abandoning_shortens_the_line_without_freeing_a_slot()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using var alice = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice });

        var bobTransport = hub.Connect(out int bobId);
        var bob = new NetClient(bobTransport, Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { alice, bob, carol });
        Assert.Equal(2, carol.QueuePosition);

        // Bob's link dies while waiting (give-up = Leave, or a crash — same transport signal).
        bob.Dispose();
        bobTransport.Dispose();
        Pump(server, new[] { alice, carol });

        Assert.Equal(1, server.PlayerCount);      // nobody new was admitted
        Assert.Equal(1, server.QueuedCount);
        Assert.Equal(1, carol.QueuePosition);     // but the line moved up
        Assert.False(carol.Joined);
    }

    [Fact]
    public void Invalid_credentials_still_reject_instantly_when_full()
    {
        // A joiner who could NEVER be admitted deserves the exact reject at the door, not a queue
        // slot that resolves to it later — validation order is part of the D18 contract.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, password: "hunter2", maxPlayers: 1), clock);

        using var alice = new NetClient(hub.Connect(out _), Identity, "Alice", clock, password: "hunter2", playerKey: "kA");
        Pump(server, new[] { alice });
        Assert.True(alice.Joined);

        using var wrongPass = new NetClient(hub.Connect(out _), Identity, "Mallory", clock, password: "nope", playerKey: "kM");
        using var badKey = new NetClient(hub.Connect(out _), Identity, "Kevin", clock, password: "hunter2", playerKey: "@reserved");
        Pump(server, new[] { alice, wrongPass, badKey });

        Assert.Equal(RejectKind.Password, wrongPass.RejectDetail!.Value.Kind);
        Assert.Equal(RejectKind.InvalidKey, badKey.RejectDetail!.Value.Kind);
        Assert.Equal(0, server.QueuedCount);
    }

    [Fact]
    public void Beyond_the_queue_cap_the_server_full_reject_remains()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        var all = new List<NetClient>();
        try
        {
            var first = new NetClient(hub.Connect(out _), Identity, "P0", clock, playerKey: "k0");
            all.Add(first);
            Pump(server, all);
            Assert.True(first.Joined);

            // Fill the queue to its bound, then one more.
            for (int i = 1; i <= 33; i++)
                all.Add(new NetClient(hub.Connect(out _), Identity, $"P{i}", clock, playerKey: $"k{i}"));
            Pump(server, all);

            Assert.Equal(32, server.QueuedCount);
            NetClient overflow = all[^1];
            Assert.False(overflow.Joined);
            Assert.Equal(RejectKind.ServerFull, overflow.RejectDetail!.Value.Kind);
        }
        finally
        {
            foreach (NetClient c in all) c.Dispose();
        }
    }

    [Fact]
    public void A_same_key_join_evicts_the_zombie_and_takes_its_slot_over_the_queue()
    {
        // F7: the key is a client-held secret, so presenting one that is already online is THIS
        // player reconnecting past their dead peer. The freed slot is a handover — a waiting
        // stranger must not slip into it.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using var alice1 = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice1 });
        Assert.True(alice1.Joined);

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { alice1, bob });
        Assert.Equal(1, bob.QueuePosition);

        bool alice1Dropped = false;
        alice1.Disconnected += () => alice1Dropped = true;

        using var alice2 = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice1, bob, alice2 });

        Assert.True(alice2.JoinSettled, "the reconnect must be admitted immediately");
        Assert.True(alice1Dropped, "the zombie peer must be force-dropped");
        Assert.False(bob.Joined);
        Assert.Equal(1, bob.QueuePosition);       // the handover never advanced the line
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void A_key_already_waiting_keeps_its_place_on_reconnect()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using var alice = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice });

        using var bob1 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { alice, bob1, carol });
        Assert.Equal(1, bob1.QueuePosition);
        Assert.Equal(2, carol.QueuePosition);

        // Bob's game hitches and reconnects fresh while still waiting: the new link REPLACES the
        // entry in place — a flaky joiner does not go to the back of the line.
        using var bob2 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { alice, bob1, carol, bob2 });

        Assert.Equal(1, bob2.QueuePosition);
        Assert.Equal(2, carol.QueuePosition);
        Assert.Equal(2, server.QueuedCount);

        alice.Leave();
        Pump(server, new[] { alice, carol, bob2 });
        Assert.True(bob2.JoinSettled, "the replaced entry must be the one admitted");
        Assert.False(carol.Joined);
    }

    [Fact]
    public void Takeover_preserves_the_career_across_the_reconnect()
    {
        // The whole point of F7: the fast reconnect lands back in the SAME career (wallet and all),
        // exactly as the grace path would have delivered eventually.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var alice1 = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice1 });
        Assert.True(alice1.Joined);
        long balance = server.Career.Registry.BalanceFor("kA");

        using var alice2 = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "kA");
        Pump(server, new[] { alice1, alice2 });

        Assert.True(alice2.JoinSettled);
        Assert.Equal(1, server.PlayerCount);
        Assert.Equal(balance, server.Career.Registry.BalanceFor("kA"));
        Assert.Equal(balance, alice2.Career.BalanceCents);
    }
}
