using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.2 session-control settings (v18 arc): the OWNER — and only the owner — can change the session
/// password, the player cap, and the autosave cadence mid-session. Present players are never
/// touched: a password change only gates future joins, a cap cut never evicts, and a cap raise
/// admits the D18 queue without anyone re-joining. The Autosaver's live retune is pinned at the
/// unit level (re-scheduled from now — no immediate thrash-save, no stale deadline).
/// </summary>
public class SessionControlTests
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
    public void The_owner_changes_the_password_and_only_future_joins_check_it()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, password: "old"), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, password: "old", playerKey: "kH");
        Pump(server, new[] { host });
        Assert.True(host.Joined);

        host.SetSessionPassword("new");
        Pump(server, new[] { host });
        Assert.True(host.Joined); // the present player is untouched

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, password: "old", playerKey: "kB");
        Pump(server, new[] { host, bob });
        Assert.False(bob.Joined); // the old password died with the change

        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, password: "new", playerKey: "kC");
        Pump(server, new[] { host, bob, carol });
        Assert.True(carol.Joined);
    }

    [Fact]
    public void A_promoted_admin_is_still_refused_the_session_settings()
    {
        // Admins moderate players; only the OWNER reconfigures the session — pinned so the
        // owner-only gate can never quietly widen to mere admins.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        host.Promote(bobId);
        Pump(server, new[] { host, bob });

        var notices = new List<(AdminNoticeKind kind, string arg)>();
        bob.AdminNotice += (k, a) => notices.Add((k, a));

        bob.SetSessionPassword("hijacked");
        bob.SetMaxPlayers(2);
        bob.SetAutosaveInterval(30);
        Pump(server, new[] { host, bob });

        Assert.Equal(3, notices.FindAll(n => n.kind == AdminNoticeKind.Rejected && n.arg == "owner only").Count);

        // And the password really didn't change: an open-config join still works.
        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { host, bob, carol });
        Assert.True(carol.Joined);
    }

    [Fact]
    public void Raising_the_cap_admits_the_queue_without_a_rejoin()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });

        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        var queuePositions = new List<int>();
        bob.QueueChanged += (pos, _) => queuePositions.Add(pos);
        Pump(server, new[] { host, bob });
        Assert.False(bob.Joined);
        Assert.Contains(1, queuePositions); // waiting at the head (D18)

        host.SetMaxPlayers(2);
        Pump(server, new[] { host, bob });
        Assert.True(bob.Joined); // the raise pumped the queue — no rejoin needed
        Assert.Equal(2, server.PlayerCount);
    }

    [Fact]
    public void Lowering_the_cap_never_evicts_but_holds_new_joins()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 8), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        Assert.Equal(2, server.PlayerCount);

        host.SetMaxPlayers(1);
        Pump(server, new[] { host, bob });
        Assert.True(bob.Joined); // present players stay
        Assert.Equal(2, server.PlayerCount);

        using var carol = new NetClient(hub.Connect(out _), Identity, "Carol", clock, playerKey: "kC");
        Pump(server, new[] { host, bob, carol });
        Assert.False(carol.Joined); // but the door is now narrower — she waits in the queue
    }

    [Fact]
    public void The_autosave_retune_reaches_the_owning_process_and_bad_values_are_refused()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });

        var requested = new List<int>();
        server.AutosaveIntervalRequested += s => requested.Add(s);
        var notices = new List<(AdminNoticeKind kind, string arg)>();
        host.AdminNotice += (k, a) => notices.Add((k, a));

        host.SetAutosaveInterval(30);
        Pump(server, new[] { host });
        Assert.Equal(new[] { 30 }, requested);
        Assert.Contains(notices, n => n.kind == AdminNoticeKind.SettingChanged && n.arg.Contains("30"));

        host.SetAutosaveInterval(2); // sub-5 s = disk-thrash foot-gun, refused
        Pump(server, new[] { host });
        Assert.Equal(new[] { 30 }, requested); // nothing new reached the process
        Assert.Contains(notices, n => n.kind == AdminNoticeKind.Rejected);
    }

    [Fact]
    public void The_autosaver_retune_reschedules_from_now_without_a_thrash_save()
    {
        var clock = new ManualClock();
        var storage = new CountingStorage();
        var saver = new Autosaver(clock, intervalMs: 100_000, storage, () => new byte[] { 1 });

        clock.Advance(10_000);
        saver.SetInterval(5_000);
        saver.Tick();
        Assert.Equal(0, storage.Saves); // shortening never fires an immediate save

        clock.Advance(5_000);
        saver.Tick();
        Assert.Equal(1, storage.Saves); // the new cadence took effect from the retune

        saver.SetInterval(50_000);
        clock.Advance(10_000);
        saver.Tick();
        Assert.Equal(1, storage.Saves); // lengthening left no stale short deadline behind
    }

    private sealed class CountingStorage : ISaveStorage
    {
        public int Saves;
        public void Save(byte[] data) => Saves++;
        public byte[]? TryLoad() => null;
    }
}
