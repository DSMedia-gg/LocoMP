using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// U3 persistent bans (M5.5): a ban of an identity-bearing peer is recorded against the SteamId64 and
/// outlives the session — the store round-trips its file, the join gate refuses the identity, the
/// merged ban list surfaces both stores in one (id, name) shape, and unban-by-id routes across the
/// disjoint id ranges. Anonymous links (UDP/Loopback) keep exactly the session-only behaviour.
/// </summary>
public class PersistentBanTests
{
    private const ulong HostId = 76561197960100001;
    private const ulong GuestId = 76561197960100002;
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static string TempBanFile() =>
        Path.Combine(Path.GetTempPath(), "locomp-test-" + Guid.NewGuid().ToString("N"), "bans.txt");

    private static void Cleanup(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // ── The store on its own ──

    [Fact]
    public void Records_survive_a_reopen()
    {
        string path = TempBanFile();
        try
        {
            var store = new BanStore(path);
            Assert.True(store.Add(GuestId, "Griefer"));
            Assert.True(store.Add(HostId, "Also Banned"));
            Assert.False(store.Add(GuestId, "again")); // duplicate identity: no second record

            var reopened = new BanStore(path);
            Assert.True(reopened.IsBanned(GuestId));
            Assert.True(reopened.IsBanned(HostId));
            Assert.Equal(2, reopened.Entries.Count);
            PersistentBan first = reopened.Entries[0];
            Assert.Equal(BanStore.PersistentIdFloor, first.Id);
            Assert.Equal(GuestId, first.SteamId);
            Assert.Equal("Griefer", first.Name);
            Assert.True(first.BannedAtUtc <= DateTime.UtcNow);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Ids_mint_in_the_persistent_range_and_never_reuse_after_removal()
    {
        string path = TempBanFile();
        try
        {
            var store = new BanStore(path);
            store.Add(1UL, "a");
            store.Add(2UL, "b");
            Assert.True(store.RemoveById(BanStore.PersistentIdFloor)); // lift "a"
            Assert.False(store.RemoveById(BanStore.PersistentIdFloor)); // second lift: nothing there
            Assert.False(store.IsBanned(1UL));

            // Reopen and ban again: the id counter continues past every id ever written, so a stale
            // list view can never lift the WRONG ban with a remembered id.
            var reopened = new BanStore(path);
            reopened.Add(3UL, "c");
            Assert.Equal(BanStore.PersistentIdFloor + 2, reopened.Entries.Single(e => e.SteamId == 3UL).Id);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Load_skips_garbage_lines_and_sanitises_names()
    {
        string path = TempBanFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, new[]
            {
                "# comment survives",
                "not\ta\tvalid\tline?",                                   // unparseable id — skipped
                "5\t42\t2026-08-08T00:00:00Z\tsession-range id",          // id below the floor — skipped
                $"{BanStore.PersistentIdFloor}\t42\t2026-08-08T00:00:00Z\tKeeper",
                $"{BanStore.PersistentIdFloor + 1}\t42\twhenever\tduplicate steamid", // dup — skipped
                $"{BanStore.PersistentIdFloor + 7}\t43\tnot-a-date\t  ",  // bad date + blank name: kept, defaulted
            });

            var store = new BanStore(path);
            Assert.Equal(2, store.Entries.Count);
            Assert.Equal("Keeper", store.Entries[0].Name);
            Assert.Equal("(unknown)", store.Entries[1].Name);

            // A hand-edit can't smuggle separators back in either.
            store.Add(44UL, "Tab\tNew\nline");
            Assert.Equal("Tab New line", new BanStore(path).Entries.Single(e => e.SteamId == 44UL).Name);
        }
        finally { Cleanup(path); }
    }

    // ── The whole stack ──

    private static void Pump(NetServer server, params NetClient[] clients)
    {
        for (int i = 0; i < 15; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void Banning_a_relay_peer_persists_the_identity_and_blocks_its_rejoin()
    {
        string path = TempBanFile();
        try
        {
            var store = new BanStore(path);
            var hub = new LoopbackNetwork();
            var net = new FakeRelayNetwork(HostId);
            var clock = new ManualClock();
            using var server = new NetServer(
                new CompositeTransport(hub.Server, SteamRelayTransport.Server(net.Host)),
                new ServerConfig(Identity, banStore: store), clock);
            var admitted = new List<(int Id, string Name)>();
            server.PlayerAdmitted += p => admitted.Add((p.Id, p.Name));

            // Host joins over Loopback first (owner, anonymous link) — the guest over the relay.
            using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
            Pump(server, host);
            using var guest = new NetClient(SteamRelayTransport.Client(net.Connect(GuestId)),
                Identity, "Guest", clock, playerKey: "kG");
            Pump(server, host, guest);
            Assert.True(guest.Joined);

            host.Ban(admitted.Single(p => p.Name == "Guest").Id);
            Pump(server, host, guest);

            Assert.True(store.IsBanned(GuestId));       // persisted against the identity…
            Assert.False(store.IsBanned(HostId));
            Assert.False(guest.Joined);                  // …and the live peer is gone

            // A brand-new connection presenting a FRESH key but the same SteamId64: the join gate
            // refuses it — the whole point of U3 (the key re-rolls, the identity doesn't).
            string? reject = null;
            using var again = new NetClient(SteamRelayTransport.Client(net.Connect(GuestId)),
                Identity, "Guest2", clock, playerKey: "kG2");
            again.Rejected += r => reject = r;
            Pump(server, host, again);

            Assert.False(again.Joined);
            Assert.Equal("you are banned from this server", reject);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void The_merged_list_surfaces_both_stores_and_unban_routes_by_id_range()
    {
        string path = TempBanFile();
        try
        {
            var store = new BanStore(path);
            var hub = new LoopbackNetwork();
            var net = new FakeRelayNetwork(HostId);
            var clock = new ManualClock();
            using var server = new NetServer(
                new CompositeTransport(hub.Server, SteamRelayTransport.Server(net.Host)),
                new ServerConfig(Identity, banStore: store), clock);
            var admitted = new List<(int Id, string Name)>();
            server.PlayerAdmitted += p => admitted.Add((p.Id, p.Name));

            using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
            Pump(server, host);
            using var guest = new NetClient(SteamRelayTransport.Client(net.Connect(GuestId)),
                Identity, "Guest", clock, playerKey: "kG");
            Pump(server, host, guest);

            host.Ban(admitted.Single(p => p.Name == "Guest").Id);
            Pump(server, host, guest);

            // One human, two entries: the session ban (key-scoped, small id) and the persistent one
            // (identity-scoped, floor-range id) — same flat (id, name) shape on the wire.
            IReadOnlyList<SessionBan>? list = null;
            host.BanListReceived += l => list = l;
            host.RequestBanList();
            Pump(server, host);
            Assert.NotNull(list);
            Assert.Equal(2, list!.Count);
            SessionBan sessionEntry = list.Single(b => b.Id < BanStore.PersistentIdFloor);
            SessionBan persistentEntry = list.Single(b => b.Id >= BanStore.PersistentIdFloor);
            Assert.Equal("Guest", sessionEntry.Name);
            Assert.Equal("Guest", persistentEntry.Name);

            // Unban the PERSISTENT entry over the wire: the store forgets the identity, the session
            // ban (which dies with the server anyway) is untouched, and the reply refreshes the list.
            host.Unban(persistentEntry.Id);
            Pump(server, host);
            Assert.False(store.IsBanned(GuestId));
            Assert.Single(list!);
            Assert.Equal(sessionEntry.Id, list![0].Id);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void A_ban_on_an_anonymous_link_stays_session_only()
    {
        // Negative control: same server, same store — but the victim arrives over Loopback, which
        // carries no identity. Nothing may reach the persistent store (a key is not an identity).
        string path = TempBanFile();
        try
        {
            var store = new BanStore(path);
            var hub = new LoopbackNetwork();
            var clock = new ManualClock();
            using var server = new NetServer(hub.Server, new ServerConfig(Identity, banStore: store), clock);
            var admitted = new List<(int Id, string Name)>();
            server.PlayerAdmitted += p => admitted.Add((p.Id, p.Name));

            using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
            Pump(server, host);
            using var guest = new NetClient(hub.Connect(out _), Identity, "Guest", clock, playerKey: "kG");
            Pump(server, host, guest);

            host.Ban(admitted.Single(p => p.Name == "Guest").Id);
            Pump(server, host, guest);

            Assert.Single(server.SessionBans);
            Assert.Empty(store.Entries);
            Assert.False(File.Exists(path)); // never even written
        }
        finally { Cleanup(path); }
    }
}
