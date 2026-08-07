using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Session;

namespace LocoMP.Transport;

/// <summary>
/// Steam relay transport implementing the <see cref="ITransport"/> seam (M5.5, O6/D4: friend joins
/// with no port forwarding, riding Valve's SDR through the game's own Steam client). All logic lives
/// here, game-free, driven through <see cref="IRelaySocket"/>; the Facepunch binding contributes no
/// behaviour beyond event forwarding. Same fixed-role model as the LiteNetLib adapter: a server
/// instance assigns peer ids 1..N, a client instance talks to peer <see cref="NetProtocol.ServerPeer"/>.
///
/// <para><b>Framing.</b> Steam sockets expose two delivery classes (reliable — which is also ordered —
/// and unreliable), so Core's three-way <see cref="DeliveryMethod"/> maps as: both reliable classes →
/// Steam reliable (ordered delivery trivially satisfies reliable-unordered); SequencedUnreliable →
/// Steam unreliable plus a transport-level latest-wins guard, because Valve's unreliable class may
/// deliver out of order and LiteNetLib's Sequenced (which the whole pose stream is tuned against)
/// drops stale packets. Wire: <c>[0x00][payload]</c> for plain, <c>[0x01][seq lo][seq hi][payload]</c>
/// for sequenced — one wrapping ushort sequence per peer per direction, exactly LiteNetLib's
/// single-channel semantics. Both ends of this frame are always this class (the dedicated server is
/// UDP-first by design — research §3.6 caveat — so a relay link is only ever LocoMP↔LocoMP).</para>
///
/// <para><b>Identity.</b> Implements <see cref="IPeerIdentity"/>: every relay peer carries a
/// Valve-authenticated SteamId64, which is the U3 persistent-ban identity. A server instance can also
/// refuse at the door via the <c>admit</c> predicate — the one check that runs BEFORE the connection
/// opens, so a persistently banned SteamId never reaches the session handshake.</para>
/// </summary>
public sealed class SteamRelayTransport : ITransport, IPeerIdentity
{
    private const byte MarkerPlain = 0;     // reliable-ordered AND reliable-unordered
    private const byte MarkerSequenced = 1; // + ushort little-endian sequence, drop-stale

    private readonly IRelaySocket _socket;
    private readonly bool _isServer;
    private readonly Func<ulong, bool>? _admit;
    private readonly Dictionary<int, IRelayPeer> _peersById = new();
    private readonly Dictionary<IRelayPeer, int> _idByPeer = new();
    private readonly Dictionary<int, ushort> _seqOut = new();
    private readonly Dictionary<int, ushort> _seqInLast = new(); // only ids that received ≥1 sequenced frame
    private int _nextPeerId = 1;
    private long _bytesSent, _bytesReceived, _messagesSent, _messagesReceived;

    public event Action<int, byte[]>? Received;
    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    private SteamRelayTransport(IRelaySocket socket, bool isServer, Func<ulong, bool>? admit)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _isServer = isServer;
        _admit = admit;
        _socket.Connecting += OnConnecting;
        _socket.Connected += OnConnected;
        _socket.Disconnected += OnDisconnected;
        _socket.Message += OnMessage;
    }

    /// <summary>Serve over a listening relay socket. <paramref name="admit"/> (optional) is consulted
    /// with each connecting peer's SteamId64 BEFORE the link opens — false refuses at the relay layer
    /// (the U3 persistent-ban door); everything else is admitted and gated by the session handshake,
    /// which unlike the UDP path can always answer with a structured reject (no connect-key filter).</summary>
    public static SteamRelayTransport Server(IRelaySocket socket, Func<ulong, bool>? admit = null) =>
        new(socket, isServer: true, admit);

    /// <summary>Wrap an outbound relay connection; the far end becomes peer
    /// <see cref="NetProtocol.ServerPeer"/> when the link opens.</summary>
    public static SteamRelayTransport Client(IRelaySocket socket) =>
        new(socket, isServer: false, admit: null);

    public void Send(int peerId, byte[] payload, DeliveryMethod delivery)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (!_peersById.TryGetValue(peerId, out IRelayPeer? peer)) return; // departed peer: drop, as UDP would

        byte[] frame;
        if (delivery == DeliveryMethod.SequencedUnreliable)
        {
            _seqOut.TryGetValue(peerId, out ushort seq);
            seq++; // first frame carries 1; 0 is never sent, so the receive guard's "no state" is unambiguous
            _seqOut[peerId] = seq;
            frame = new byte[payload.Length + 3];
            frame[0] = MarkerSequenced;
            frame[1] = (byte)seq;
            frame[2] = (byte)(seq >> 8);
            Buffer.BlockCopy(payload, 0, frame, 3, payload.Length);
        }
        else
        {
            frame = new byte[payload.Length + 1];
            frame[0] = MarkerPlain;
            Buffer.BlockCopy(payload, 0, frame, 1, payload.Length);
        }

        peer.Send(frame, reliable: delivery != DeliveryMethod.SequencedUnreliable);
        _bytesSent += payload.Length; // payload bytes, matching the LiteNetLib adapter's counters
        _messagesSent++;
    }

    public void Poll() => _socket.Poll();

    public TransportStats Stats => new(_bytesSent, _bytesReceived, _messagesSent, _messagesReceived);

    /// <summary>RTT straight from the link — Valve's ping is already round-trip (no ×2, unlike the
    /// LiteNetLib adapter whose NetPeer.Ping is smoothed one-way).</summary>
    public int? RttMs(int peerId) =>
        _peersById.TryGetValue(peerId, out IRelayPeer? peer) ? peer.PingMs : null;

    public ulong? IdentityOf(int peerId) =>
        _peersById.TryGetValue(peerId, out IRelayPeer? peer) ? peer.RemoteId : (ulong?)null;

    /// <summary>Force-drop a peer (F7 eviction). The relay raises the ordinary Disconnected for both
    /// sides, so the id maps clean up through the standard handler on a later Poll.</summary>
    public void Disconnect(int peerId)
    {
        if (_peersById.TryGetValue(peerId, out IRelayPeer? peer)) peer.Close();
    }

    private void OnConnecting(IRelayPeer peer)
    {
        // Client role: the only outbound link is ours; nothing to decide. Server role: the admission
        // predicate is the persistent-ban door — everything else defers to the session handshake.
        if (!_isServer) return;
        if (_admit is null || _admit(peer.RemoteId)) peer.Accept();
        else peer.Close();
    }

    private void OnConnected(IRelayPeer peer)
    {
        if (_idByPeer.ContainsKey(peer)) return; // a double-Connected from a misbehaving binding
        int id = _isServer ? _nextPeerId++ : NetProtocol.ServerPeer;
        _peersById[id] = peer;
        _idByPeer[peer] = id;
        PeerConnected?.Invoke(id);
    }

    private void OnDisconnected(IRelayPeer peer)
    {
        // A connect attempt that died before Connected was never mapped — ignore it, exactly like the
        // LiteNetLib adapter's unmapped-peer silence. The session layer's join timeout owns that UX.
        if (!_idByPeer.TryGetValue(peer, out int id)) return;
        _idByPeer.Remove(peer);
        _peersById.Remove(id);
        _seqOut.Remove(id);
        _seqInLast.Remove(id);
        PeerDisconnected?.Invoke(id);
    }

    private void OnMessage(IRelayPeer peer, byte[] frame)
    {
        if (!_idByPeer.TryGetValue(peer, out int id)) return; // raced its own disconnect
        if (frame.Length < 1) return;                          // not ours — drop, like UDP garbage

        int offset;
        if (frame[0] == MarkerPlain)
        {
            offset = 1;
        }
        else if (frame[0] == MarkerSequenced)
        {
            if (frame.Length < 3) return;
            ushort seq = (ushort)(frame[1] | (frame[2] << 8));
            if (_seqInLast.TryGetValue(id, out ushort last))
            {
                // Serial-number arithmetic: accept only frames newer than the last delivered, across
                // the ushort wrap. Equal or older (≥ half-window behind) = stale, drop — LiteNetLib
                // Sequenced semantics, which the pose stream's latest-wins tuning assumes.
                ushort ahead = (ushort)(seq - last);
                if (ahead == 0 || ahead >= 0x8000) return;
            }
            _seqInLast[id] = seq;
            offset = 3;
        }
        else
        {
            return; // unknown marker: a foreign or corrupt frame, never deliverable
        }

        byte[] payload = new byte[frame.Length - offset];
        Buffer.BlockCopy(frame, offset, payload, 0, payload.Length);
        _bytesReceived += payload.Length;
        _messagesReceived++;
        Received?.Invoke(id, payload);
    }

    public void Dispose()
    {
        _socket.Connecting -= OnConnecting;
        _socket.Connected -= OnConnected;
        _socket.Disconnected -= OnDisconnected;
        _socket.Message -= OnMessage;
        _socket.Dispose();
        _peersById.Clear();
        _idByPeer.Clear();
        _seqOut.Clear();
        _seqInLast.Clear();
    }
}
