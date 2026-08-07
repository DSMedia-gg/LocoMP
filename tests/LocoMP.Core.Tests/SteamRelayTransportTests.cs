using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.5 Steam relay transport — the game-free half. Everything the Facepunch binding will drive is
/// pinned here through the <see cref="IRelaySocket"/> seam: role-correct peer-id assignment, the
/// admission (persistent-ban) door, the plain/sequenced framing, LiteNetLib-parity lifecycle edges,
/// stats/RTT/identity surfaces, and a whole NetServer↔NetClient session over the paired fake.
/// </summary>
public class SteamRelayTransportTests
{
    private const ulong HostId = 76561197960000001;
    private const ulong GuestId = 76561197960000002;

    // ── Lifecycle + admission ──

    [Fact]
    public void Server_accepts_a_connecting_peer_and_assigns_ids_from_one()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var connected = new List<int>();
        t.PeerConnected += connected.Add;

        var peer = new StubRelayPeer(GuestId);
        socket.RaiseConnecting(peer);
        Assert.True(peer.Accepted);
        Assert.False(peer.Closed);

        socket.RaiseConnected(peer);
        Assert.Equal(new[] { 1 }, connected);
    }

    [Fact]
    public void The_admission_predicate_refuses_at_the_door_without_raising_events()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket, admit: id => id != GuestId);
        var connected = new List<int>();
        t.PeerConnected += connected.Add;

        var banned = new StubRelayPeer(GuestId);
        socket.RaiseConnecting(banned);
        Assert.True(banned.Closed);
        Assert.False(banned.Accepted);
        Assert.Empty(connected);

        var fine = new StubRelayPeer(HostId);
        socket.RaiseConnecting(fine);
        Assert.True(fine.Accepted);
    }

    [Fact]
    public void A_client_maps_the_far_end_to_the_server_peer_id()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Client(socket);
        var connected = new List<int>();
        t.PeerConnected += connected.Add;

        var server = new StubRelayPeer(HostId);
        socket.RaiseConnected(server);
        Assert.Equal(new[] { NetProtocol.ServerPeer }, connected);
        Assert.Equal(HostId, t.IdentityOf(NetProtocol.ServerPeer));
    }

    [Fact]
    public void A_connect_attempt_that_dies_before_connecting_stays_silent()
    {
        // LiteNetLib parity: an unmapped peer's disconnect is nobody's business — the session layer's
        // join timeout owns that UX (and the mismatch screen its copy).
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Client(socket);
        var dropped = new List<int>();
        t.PeerDisconnected += dropped.Add;

        socket.RaiseDisconnected(new StubRelayPeer(HostId));
        Assert.Empty(dropped);
    }

    [Fact]
    public void Disconnect_closes_the_link_and_cleanup_rides_the_disconnected_event()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);
        var dropped = new List<int>();
        t.PeerDisconnected += dropped.Add;

        t.Disconnect(1);
        Assert.True(peer.Closed);
        Assert.Empty(dropped); // the far side / relay raises the event, not the close call

        socket.RaiseDisconnected(peer);
        Assert.Equal(new[] { 1 }, dropped);
        Assert.Null(t.IdentityOf(1));
        Assert.Null(t.RttMs(1));
    }

    // ── Framing ──

    [Fact]
    public void Reliable_classes_frame_with_the_plain_marker_and_ride_the_reliable_class()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);

        t.Send(1, new byte[] { 7, 8 }, DeliveryMethod.ReliableOrdered);
        t.Send(1, new byte[] { 9 }, DeliveryMethod.ReliableUnordered);

        Assert.Equal(2, peer.Sent.Count);
        Assert.Equal(new byte[] { 0, 7, 8 }, peer.Sent[0].Frame);
        Assert.True(peer.Sent[0].Reliable);
        Assert.Equal(new byte[] { 0, 9 }, peer.Sent[1].Frame);
        Assert.True(peer.Sent[1].Reliable);
        Assert.Equal(3, t.Stats.BytesSent);      // payload bytes, not wire bytes — adapter-wide convention
        Assert.Equal(2, t.Stats.MessagesSent);
    }

    [Fact]
    public void Sequenced_sends_carry_a_per_peer_wrapping_sequence_and_ride_unreliable()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);

        t.Send(1, new byte[] { 5 }, DeliveryMethod.SequencedUnreliable);
        t.Send(1, new byte[] { 6 }, DeliveryMethod.SequencedUnreliable);

        Assert.Equal(new byte[] { 1, 1, 0, 5 }, peer.Sent[0].Frame); // seq 1 first — 0 is never sent
        Assert.False(peer.Sent[0].Reliable);
        Assert.Equal(new byte[] { 1, 2, 0, 6 }, peer.Sent[1].Frame);
    }

    [Fact]
    public void Plain_receive_strips_the_marker_and_counts_payload_bytes()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);
        var got = new List<(int Peer, byte[] Payload)>();
        t.Received += (id, p) => got.Add((id, p));

        socket.RaiseMessage(peer, new byte[] { 0, 42, 43 });
        Assert.Single(got);
        Assert.Equal(1, got[0].Peer);
        Assert.Equal(new byte[] { 42, 43 }, got[0].Payload);
        Assert.Equal(2, t.Stats.BytesReceived);
        Assert.Equal(1, t.Stats.MessagesReceived);
    }

    [Fact]
    public void Stale_and_duplicate_sequenced_frames_are_dropped()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);
        var got = new List<byte>();
        t.Received += (_, p) => got.Add(p[0]);

        socket.RaiseMessage(peer, Seq(2, 20)); // newest first
        socket.RaiseMessage(peer, Seq(1, 10)); // late straggler — stale, must drop
        socket.RaiseMessage(peer, Seq(2, 21)); // duplicate seq — drop
        socket.RaiseMessage(peer, Seq(3, 30)); // fresh — delivered

        Assert.Equal(new byte[] { 20, 30 }, got.ToArray());
    }

    [Fact]
    public void The_sequence_guard_accepts_forward_progress_across_the_ushort_wrap()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);
        var got = new List<byte>();
        t.Received += (_, p) => got.Add(p[0]);

        socket.RaiseMessage(peer, Seq(65534, 1));
        socket.RaiseMessage(peer, Seq(65535, 2));
        socket.RaiseMessage(peer, Seq(1, 3));     // wrapped forward (skipping 0 like the sender does)
        socket.RaiseMessage(peer, Seq(65535, 4)); // pre-wrap straggler — stale now

        Assert.Equal(new byte[] { 1, 2, 3 }, got.ToArray());
    }

    [Fact]
    public void Sequence_state_dies_with_the_peer()
    {
        // A fresh link must never inherit the old link's high-water mark, or its first snapshots all
        // read as stale (a client reconnect reuses the ServerPeer id by construction).
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Client(socket);
        var got = new List<byte>();
        t.Received += (_, p) => got.Add(p[0]);

        var first = new StubRelayPeer(HostId);
        socket.RaiseConnected(first);
        socket.RaiseMessage(first, Seq(4000, 1));
        socket.RaiseDisconnected(first);

        var second = new StubRelayPeer(HostId);
        socket.RaiseConnected(second);
        socket.RaiseMessage(second, Seq(1, 2)); // would be "stale" against 4000 if state leaked

        Assert.Equal(new byte[] { 1, 2 }, got.ToArray());
    }

    [Fact]
    public void Garbage_frames_are_dropped_not_delivered()
    {
        var socket = new StubRelaySocket();
        using var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);
        int delivered = 0;
        t.Received += (_, _) => delivered++;

        socket.RaiseMessage(peer, new byte[0]);           // empty
        socket.RaiseMessage(peer, new byte[] { 9, 1 });   // unknown marker
        socket.RaiseMessage(peer, new byte[] { 1, 5 });   // sequenced but truncated before the sequence

        Assert.Equal(0, delivered);
        Assert.Equal(0, t.Stats.MessagesReceived);
    }

    // ── Surfaces ──

    [Fact]
    public void Identity_ping_and_dispose_ride_through_the_seam()
    {
        var socket = new StubRelaySocket();
        var t = SteamRelayTransport.Server(socket);
        var peer = Admit(socket, GuestId);
        peer.PingMs = 37;

        Assert.Equal(GuestId, t.IdentityOf(1));
        Assert.Equal(37, t.RttMs(1));
        Assert.Null(t.IdentityOf(2));

        t.Dispose();
        Assert.True(socket.Disposed);
    }

    [Fact]
    public void A_composite_routes_identity_to_the_link_the_peer_lives_on()
    {
        // The host serves Loopback + UDP + Steam at once; only the Steam peer has a SteamId64.
        var hub = new LoopbackNetwork();
        var relaySocket = new StubRelaySocket();
        using var composite = new CompositeTransport(hub.Server, SteamRelayTransport.Server(relaySocket));
        var connected = new List<int>();
        composite.PeerConnected += connected.Add;

        ITransport loopbackGuest = hub.Connect(out _);
        Admit(relaySocket, GuestId);
        composite.Poll();

        // Arrival order differs by link (the stub relay raises synchronously, Loopback on Poll) —
        // identify the links by their answer, which is the property under test.
        Assert.Equal(2, connected.Count);
        Assert.Single(connected, id => composite.IdentityOf(id) == GuestId);
        Assert.Single(connected, id => composite.IdentityOf(id) == null);
        loopbackGuest.Dispose();
    }

    // ── The whole stack over the paired fake ──

    [Fact]
    public void A_full_session_admits_a_client_over_the_relay_pair()
    {
        // Both ends of the relay path are this transport (the dedicated server is UDP-first), so the
        // paired fake proves the framing symmetric end to end: handshake, admission, and the join
        // burst all arrive intact. This is the relay-flavoured twin of the Loopback session tests.
        var identity = new HandshakeRequest(ProtocolVersion.Current, "B99.7", "0.0.2");
        var net = new FakeRelayNetwork(HostId);
        var clock = new ManualClock();
        using var server = new NetServer(SteamRelayTransport.Server(net.Host),
            new ServerConfig(identity), clock);

        using var guest = new NetClient(SteamRelayTransport.Client(net.Connect(GuestId)),
            identity, "Guest", clock, playerKey: "kG");

        for (int i = 0; i < 12; i++)
        {
            server.Poll();
            guest.Poll();
        }

        Assert.True(guest.Joined);
        Assert.Equal(1, server.PlayerCount);
    }

    // ── helpers ──

    private static StubRelayPeer Admit(StubRelaySocket socket, ulong steamId)
    {
        var peer = new StubRelayPeer(steamId);
        socket.RaiseConnecting(peer);
        socket.RaiseConnected(peer);
        return peer;
    }

    private static byte[] Seq(int seq, byte payload) =>
        new byte[] { 1, (byte)seq, (byte)(seq >> 8), payload };
}
