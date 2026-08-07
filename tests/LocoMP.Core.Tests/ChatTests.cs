using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.4 chat over the Loopback hub: player lines echo to everyone (sender included) with the
/// server-stamped name, the system feed announces joins/departures BY KIND (only the server knows a
/// kick from a leave), sanitisation and the per-peer rate limit gate what commits, and the client
/// backlog stays capped and session-scoped.
/// </summary>
public class ChatTests
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
    public void A_players_line_echoes_to_everyone_including_the_sender()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        bob.SendChat("hello there");
        Pump(server, new[] { host, bob });

        ChatEntry heard = host.ChatLog.Last();
        Assert.Equal(ChatMessageKind.Player, heard.Kind);
        Assert.Equal(bobId, heard.SenderId);
        Assert.Equal("Bob", heard.SenderName);          // server-stamped, not sender-supplied
        Assert.Equal("hello there", heard.Text);

        // The sender renders from the server's echo — their own line is in their own log.
        ChatEntry echo = bob.ChatLog.Last();
        Assert.Equal(ChatMessageKind.Player, echo.Kind);
        Assert.Equal("hello there", echo.Text);
    }

    [Fact]
    public void The_system_feed_announces_joins_and_departures()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        ChatEntry joined = Assert.Single(host.ChatLog, e => e.Kind == ChatMessageKind.Joined);
        Assert.Equal("Bob", joined.SenderName);
        // The newcomer knows they joined — their own admission is not in their log.
        Assert.DoesNotContain(bob.ChatLog, e => e.Kind == ChatMessageKind.Joined);

        bob.Leave();
        Pump(server, new[] { host, bob });
        ChatEntry left = Assert.Single(host.ChatLog, e => e.Kind == ChatMessageKind.Left);
        Assert.Equal("Bob", left.SenderName);
    }

    [Fact]
    public void A_kick_and_a_ban_are_announced_by_kind_not_as_a_plain_leave()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out int carolId), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { host, bob, carol });

        host.Kick(bobId);
        Pump(server, new[] { host, bob, carol });
        ChatEntry kicked = Assert.Single(carol.ChatLog, e => e.Kind == ChatMessageKind.Kicked);
        Assert.Equal("Bob", kicked.SenderName);

        host.Ban(carolId);
        Pump(server, new[] { host, bob, carol });
        ChatEntry banned = Assert.Single(host.ChatLog, e => e.Kind == ChatMessageKind.Banned);
        Assert.Equal("Carol", banned.SenderName);
        // Negative control: a moderated departure must NOT also produce the generic "left" line.
        Assert.DoesNotContain(host.ChatLog, e => e.Kind == ChatMessageKind.Left);
    }

    [Fact]
    public void A_takeover_reconnect_never_says_the_player_left()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        // Same key from a fresh connection = credentialed takeover (F7): the zombie is evicted
        // silently — the player never left, their link did — and the re-admission announces itself.
        using var bob2 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob, bob2 });

        Assert.True(bob2.JoinSettled);
        Assert.DoesNotContain(host.ChatLog, e => e.Kind == ChatMessageKind.Left);
        Assert.Equal(2, host.ChatLog.Count(e => e.Kind == ChatMessageKind.Joined));
    }

    [Fact]
    public void Sanitisation_trims_flattens_control_characters_and_truncates()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        bob.SendChat("  a\nb\t ");
        Pump(server, new[] { host, bob });
        Assert.Equal("a b", host.ChatLog.Last().Text);

        bob.SendChat(new string('x', 300));
        Pump(server, new[] { host, bob });
        Assert.Equal(new string('x', ChatPolicy.MaxLength), host.ChatLog.Last().Text);
    }

    [Fact]
    public void An_empty_or_whitespace_line_is_never_committed()
    {
        // Negative control for sanitisation: nothing worth saying, nothing on the wire.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        ITransport bobTransport = hub.Connect(out _);
        using var bob = new NetClient(bobTransport, Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        int before = host.ChatLog.Count;

        bob.SendChat("   ");
        // SendChat itself refuses whitespace client-side; go around it to prove the SERVER also
        // refuses (defence in depth — a foreign client won't be polite).
        byte[] raw = new PacketWriter(8)
            .WriteByte((byte)MessageType.ChatSend)
            .WriteString(" \t \n ")
            .ToArray();
        bobTransport.Send(NetProtocol.ServerPeer, raw, DeliveryMethod.ReliableUnordered);
        Pump(server, new[] { host, bob });

        Assert.Equal(before, host.ChatLog.Count);
    }

    [Fact]
    public void A_paste_bomb_is_rate_limited_with_one_cooldown_gated_warning()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        int heardBefore = host.ChatLog.Count;

        // Seven instant lines: the burst allows exactly BurstSize, the rest die silently — except
        // ONE warning to the sender (the cooldown keeps the warning from becoming its own spam).
        for (int i = 0; i < ChatPolicy.BurstSize + 2; i++) bob.SendChat($"line {i}");
        Pump(server, new[] { host, bob });

        Assert.Equal(ChatPolicy.BurstSize, host.ChatLog.Count - heardBefore);
        Assert.Equal(1, bob.ChatLog.Count(e => e.Kind == ChatMessageKind.Server));

        // Refill restores service: one token per RefillMs, so after a full refill interval the
        // next line commits again.
        clock.Advance(ChatPolicy.RefillMs + 1);
        bob.SendChat("back again");
        Pump(server, new[] { host, bob });
        Assert.Equal("back again", host.ChatLog.Last().Text);
    }

    [Fact]
    public void The_server_can_say_and_an_empty_say_is_a_no_op()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });

        ChatEntry? observed = null;
        server.Chat += e => observed = e;

        server.BroadcastServerChat("restart in 5 minutes");
        Pump(server, new[] { host });
        ChatEntry said = host.ChatLog.Last();
        Assert.Equal(ChatMessageKind.Server, said.Kind);
        Assert.Equal(0, said.SenderId);
        Assert.Equal("restart in 5 minutes", said.Text);
        Assert.Equal("restart in 5 minutes", observed?.Text); // the console log feed saw it too

        int before = host.ChatLog.Count;
        server.BroadcastServerChat("   ");                    // negative control: nothing to say
        Pump(server, new[] { host });
        Assert.Equal(before, host.ChatLog.Count);
    }

    [Fact]
    public void A_stranger_who_never_joined_cannot_chat()
    {
        // Admitted-only: a connected-but-never-admitted transport sends a raw chat line and the
        // session never hears it (the same stranger discipline as every other subsystem).
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });

        ITransport stranger = hub.Connect(out _);
        byte[] raw = new PacketWriter(16)
            .WriteByte((byte)MessageType.ChatSend)
            .WriteString("let me in")
            .ToArray();
        stranger.Send(NetProtocol.ServerPeer, raw, DeliveryMethod.ReliableUnordered);
        Pump(server, new[] { host });

        Assert.DoesNotContain(host.ChatLog, e => e.Text == "let me in");
        stranger.Dispose();
    }

    [Fact]
    public void The_backlog_is_capped_and_dies_with_the_session()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        // Server "say" bypasses the rate limit, so flooding the cap is cheap.
        for (int i = 0; i < NetClient.ChatLogCapacity + 20; i++)
            server.BroadcastServerChat($"line {i}");
        Pump(server, new[] { host, bob });

        Assert.Equal(NetClient.ChatLogCapacity, bob.ChatLog.Count);
        Assert.Equal("line 20", bob.ChatLog[0].Text);           // oldest fell off, order preserved

        // A real link teardown (kick closes the socket) clears the backlog with the session.
        host.Kick(bobId);
        Pump(server, new[] { host, bob });
        Assert.Empty(bob.ChatLog);                              // chat is session-scoped
    }
}
