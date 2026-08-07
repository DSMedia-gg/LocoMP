using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.2 remote-admin queries: an admin asks the server for a diagnostics snapshot or the ban list and
/// gets a reply over the wire (a promoted admin on a dedicated server, where host-mode's in-process read
/// isn't available). Covers the codec round-trip and the authorised request/reply over Loopback.
/// </summary>
public class AdminQueryTests
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
    public void Diagnostics_survive_a_codec_round_trip()
    {
        // Every numeric field distinct so a write/read field-order swap can't hide behind equal values.
        var d = new ServerDiagnostics(players: 3, queued: 2, trainsets: 5, jobs: 8, items: 13,
            staleSnapshotsDropped: 4200, moneyConservationHolds: true, itemConservationHolds: false,
            joinsPaused: true, admins: 6, bannedKeys: 7, interestEnabled: true,
            bytesSent: 111, bytesReceived: 222, messagesSent: 33, messagesReceived: 44);

        var w = new PacketWriter(48);
        AdminCodec.WriteDiagnostics(w, d);
        ServerDiagnostics r = AdminCodec.ReadDiagnostics(new PacketReader(w.ToArray()));

        Assert.Equal(d.Players, r.Players);
        Assert.Equal(d.Queued, r.Queued);
        Assert.Equal(d.Trainsets, r.Trainsets);
        Assert.Equal(d.Jobs, r.Jobs);
        Assert.Equal(d.Items, r.Items);
        Assert.Equal(d.StaleSnapshotsDropped, r.StaleSnapshotsDropped);
        Assert.Equal(d.Admins, r.Admins);
        Assert.Equal(d.BannedKeys, r.BannedKeys);
        Assert.Equal(d.MoneyConservationHolds, r.MoneyConservationHolds);
        Assert.Equal(d.ItemConservationHolds, r.ItemConservationHolds);
        Assert.Equal(d.JoinsPaused, r.JoinsPaused);
        Assert.Equal(d.InterestEnabled, r.InterestEnabled);
        Assert.Equal(d.BytesSent, r.BytesSent);
        Assert.Equal(d.BytesReceived, r.BytesReceived);
        Assert.Equal(d.MessagesSent, r.MessagesSent);
        Assert.Equal(d.MessagesReceived, r.MessagesReceived);
    }

    [Fact]
    public void The_ban_list_survives_a_codec_round_trip()
    {
        // v20 (R4-A): entries are (opaque id, display name) — keys never ride this message.
        var bans = new[] { new SessionBan(1, "Alice"), new SessionBan(2, "Bob"), new SessionBan(7, "Carol") };
        var w = new PacketWriter(32);
        AdminCodec.WriteBanList(w, bans);
        IReadOnlyList<SessionBan> back = AdminCodec.ReadBanList(new PacketReader(w.ToArray()));
        Assert.Equal(bans.Select(b => (b.Id, b.Name)), back.Select(b => (b.Id, b.Name)));
    }

    [Fact]
    public void An_admin_receives_a_diagnostics_snapshot_on_request()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        ServerDiagnostics? got = null;
        host.DiagnosticsReceived += d => got = d;
        host.RequestDiagnostics();
        Pump(server, new[] { host, bob });

        Assert.NotNull(got);
        Assert.Equal(2, got!.Value.Players);
        Assert.Equal(1, got!.Value.Admins);   // the owner
    }

    [Fact]
    public void A_non_admin_gets_no_diagnostics_only_a_rejection()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        bool gotDiag = false;
        var notices = new List<AdminNoticeKind>();
        bob.DiagnosticsReceived += _ => gotDiag = true;
        bob.AdminNotice += (k, _) => notices.Add(k);

        bob.RequestDiagnostics();               // Bob is a plain player
        Pump(server, new[] { host, bob });

        Assert.False(gotDiag);
        Assert.Contains(AdminNoticeKind.Rejected, notices);
    }

    [Fact]
    public void An_admin_receives_the_ban_list_on_request()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        host.Ban(bobId);
        Pump(server, new[] { host, bob });

        IReadOnlyList<SessionBan>? list = null;
        host.BanListReceived += l => list = l;
        host.RequestBanList();
        Pump(server, new[] { host });

        Assert.NotNull(list);
        SessionBan entry = Assert.Single(list!);
        // v20 (R4-A): entries carry the display NAME and an opaque id — never the key. The key is
        // a credential (F7 takeover, D3 wallet); v19 leaked it to every admin.
        Assert.Equal("Bob", entry.Name);
        Assert.True(entry.Id > 0);
        Assert.DoesNotContain(list!, b => b.Name.Contains("kB"));
    }

    [Fact]
    public void An_unban_by_entry_id_lifts_the_ban_and_pushes_the_refreshed_list()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        host.Ban(bobId);
        Pump(server, new[] { host, bob });
        SessionBan entry = Assert.Single(server.Moderation.Bans);

        var pushes = new List<IReadOnlyList<SessionBan>>();
        host.BanListReceived += l => pushes.Add(l);
        var notices = new List<AdminNoticeKind>();
        host.AdminNotice += (k, _) => notices.Add(k);

        // A wrong id refuses by name and lifts nothing.
        host.Unban(entry.Id + 99);
        Pump(server, new[] { host });
        Assert.Contains(AdminNoticeKind.Rejected, notices);
        Assert.True(server.Moderation.IsBanned("kB"));

        // The right id lifts it AND the refreshed (now empty) list arrives unasked.
        host.Unban(entry.Id);
        Pump(server, new[] { host });
        Assert.False(server.Moderation.IsBanned("kB"));
        Assert.Empty(pushes[pushes.Count - 1]);

        // The unbanned key can join again.
        using var bob2 = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob2 });
        Assert.True(bob2.Joined);
    }

    [Fact]
    public void An_admin_save_now_raises_the_server_save_hook()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        int saves = 0;
        server.SaveRequested += () => saves++;

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });

        host.SaveNow();
        Pump(server, new[] { host });
        Assert.Equal(1, saves);
    }

    [Fact]
    public void A_non_admin_save_now_does_nothing()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);
        int saves = 0;
        server.SaveRequested += () => saves++;

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        bob.SaveNow();                          // Bob is a plain player
        Pump(server, new[] { host, bob });
        Assert.Equal(0, saves);
    }
}
