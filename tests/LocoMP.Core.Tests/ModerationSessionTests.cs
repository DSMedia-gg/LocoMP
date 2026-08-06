using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.2 host moderation, end-to-end over the Loopback hub: admin roles, kick, session ban, promote,
/// and the pause-joins gate. The first joiner is the owner (host); authority is enforced server-side, so
/// these drive the real AdminAction wire and assert the roster / reject / notice consequences.
/// </summary>
public class ModerationSessionTests
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
    public void The_owner_can_kick_a_player_and_the_slot_is_freed()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        Assert.Equal(2, server.PlayerCount);

        bool bobDropped = false;
        bob.Disconnected += () => bobDropped = true;
        var bobNotices = new List<AdminNoticeKind>();
        bob.AdminNotice += (k, _) => bobNotices.Add(k);

        host.Kick(bobId);
        Pump(server, new[] { host, bob });

        Assert.Equal(1, server.PlayerCount);
        Assert.True(bobDropped, "the kicked peer must be disconnected");
        Assert.Contains(AdminNoticeKind.Kicked, bobNotices);
    }

    [Fact]
    public void A_kick_is_not_a_ban_the_player_may_rejoin_immediately()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob1 = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob1 });

        host.Kick(bobId);
        Pump(server, new[] { host, bob1 });
        Assert.Equal(1, server.PlayerCount);

        using var bob2 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob2 });
        Assert.True(bob2.Joined, "a kick without a ban lets the same key back in");
    }

    [Fact]
    public void A_banned_key_is_refused_on_rejoin_with_the_Banned_reject()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob1 = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob1 });

        host.Ban(bobId);
        Pump(server, new[] { host, bob1 });
        Assert.Equal(1, server.PlayerCount);
        Assert.True(server.Moderation.IsBanned("kB"));

        using var bob2 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob2 });
        Assert.False(bob2.Joined);
        Assert.Equal(RejectKind.Banned, bob2.RejectDetail!.Value.Kind);
    }

    [Fact]
    public void Unban_lets_a_banned_key_back_in()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob1 = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob1 });
        host.Ban(bobId);
        Pump(server, new[] { host, bob1 });

        host.Unban("kB");
        Pump(server, new[] { host });
        Assert.False(server.Moderation.IsBanned("kB"));

        using var bob2 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob2 });
        Assert.True(bob2.Joined);
    }

    [Fact]
    public void A_non_admin_cannot_moderate()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out int carolId), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { host, bob, carol });
        Assert.Equal(3, server.PlayerCount);

        var bobNotices = new List<AdminNoticeKind>();
        bob.AdminNotice += (k, _) => bobNotices.Add(k);

        bob.Kick(carolId);                              // Bob is a plain player
        Pump(server, new[] { host, bob, carol });

        Assert.Equal(3, server.PlayerCount);            // nobody removed
        Assert.Contains(AdminNoticeKind.Rejected, bobNotices);
    }

    [Fact]
    public void The_owner_cannot_be_kicked_by_a_promoted_admin()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out int hostId), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        host.Promote(bobId);
        Pump(server, new[] { host, bob });
        Assert.True(server.Moderation.IsAdmin("kB"));

        var bobNotices = new List<AdminNoticeKind>();
        bob.AdminNotice += (k, _) => bobNotices.Add(k);
        bob.Kick(hostId);                               // a promoted admin targets the owner
        Pump(server, new[] { host, bob });

        Assert.Equal(2, server.PlayerCount);            // the owner is immune
        Assert.Contains(AdminNoticeKind.Rejected, bobNotices);
    }

    [Fact]
    public void A_promoted_admin_can_kick_another_player()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out int carolId), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { host, bob, carol });

        host.Promote(bobId);
        Pump(server, new[] { host, bob, carol });

        bool carolDropped = false;
        carol.Disconnected += () => carolDropped = true;
        bob.Kick(carolId);
        Pump(server, new[] { host, bob, carol });

        Assert.True(carolDropped);
        Assert.Equal(2, server.PlayerCount);
    }

    [Fact]
    public void Only_the_owner_may_promote()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        using var carol = new NetClient(hub.Connect(out int carolId), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { host, bob, carol });

        bob.Promote(carolId);                           // Bob is not the owner
        Pump(server, new[] { host, bob, carol });
        Assert.False(server.Moderation.IsAdmin("kC"));
    }

    [Fact]
    public void Pausing_joins_refuses_a_new_joiner_and_resuming_lets_them_in()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });

        host.PauseJoins();
        Pump(server, new[] { host });
        Assert.True(server.Moderation.JoinsPaused);

        using var dave1 = new NetClient(hub.Connect(out _), Identity, "Dave", clock, playerKey: "kD");
        Pump(server, new[] { host, dave1 });
        Assert.False(dave1.Joined);
        Assert.Equal(RejectKind.JoinsPaused, dave1.RejectDetail!.Value.Kind);

        host.ResumeJoins();
        Pump(server, new[] { host });
        Assert.False(server.Moderation.JoinsPaused);

        using var dave2 = new NetClient(hub.Connect(out _), Identity, "Dave", clock, playerKey: "kD");
        Pump(server, new[] { host, dave2 });
        Assert.True(dave2.Joined);
    }

    [Fact]
    public void Pausing_joins_still_lets_a_present_player_reconnect()
    {
        // Pause is a "new joins" gate — a player already represented reconnects via the takeover path,
        // which resolves before the pause check.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob1 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob1 });

        host.PauseJoins();
        Pump(server, new[] { host, bob1 });

        // Bob's link reconnects (same key) while joins are paused — a takeover, not a new join.
        using var bob2 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob1, bob2 });
        Assert.True(bob2.JoinSettled, "a present player's reconnect must survive a joins-pause");
        Assert.Equal(2, server.PlayerCount);
    }
}
