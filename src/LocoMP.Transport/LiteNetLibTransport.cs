using System;
using System.Collections.Generic;
using LiteNetLib;
using LocoMP.Core.Net;
using LocoMP.Core.Session;

// LiteNetLib defines its OWN DeliveryMethod enum which collides with LocoMP.Core.Net.DeliveryMethod once
// `using LiteNetLib;` is in scope. The alias pins the unqualified name to Core's enum; LiteNetLib's is
// always written out in full (LiteNetLib.DeliveryMethod), and the adapter maps Core→LiteNetLib in one
// place (Map). The collision never leaks past this file.
using DeliveryMethod = LocoMP.Core.Net.DeliveryMethod;

namespace LocoMP.Transport;

/// <summary>
/// LiteNetLib 1.3.5 UDP transport implementing the <see cref="ITransport"/> seam (03 §2). One instance
/// wraps one <see cref="NetManager"/> in a fixed role — server (listens, accepts by connect key, assigns
/// peer ids 1..N) or client (connects to one server, which is peer <see cref="NetProtocol.ServerPeer"/>).
/// The Core session stack above this is identical to what runs over the Loopback hub — swapping the
/// transport is invisible above the seam. Events are raised on the <see cref="Poll"/> thread, so the
/// id/peer maps need no locking.
/// </summary>
public sealed class LiteNetLibTransport : ITransport
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly bool _isServer;
    private readonly string _connectKey;
    private readonly Dictionary<int, NetPeer> _peersById = new();
    private readonly Dictionary<NetPeer, int> _idByPeer = new();
    private int _nextPeerId = 1;

    public event Action<int, byte[]>? Received;
    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    private LiteNetLibTransport(bool isServer, string connectKey)
    {
        _isServer = isServer;
        _connectKey = connectKey ?? throw new ArgumentNullException(nameof(connectKey));
        _net = new NetManager(_listener)
        {
            // LiteNetLib's 5 s default evicted a joined game client mid save-load freeze
            // (observed 2026-07-19: evicted as id 2, re-handshook as id 3). Loading hitches are
            // normal for DV; a genuinely dead peer just lingers 10 s longer, which the server's
            // park-on-disconnect + career grace absorb by design.
            DisconnectTimeout = 15000,
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    /// <summary>Bind and listen. Pass port 0 to let the OS pick; read the chosen port from <see cref="Port"/>.</summary>
    public static LiteNetLibTransport StartServer(int port, string connectKey)
    {
        var t = new LiteNetLibTransport(isServer: true, connectKey);
        if (!t._net.Start(port))
        {
            t.Dispose();
            throw new InvalidOperationException($"LiteNetLib failed to bind UDP port {port}.");
        }
        return t;
    }

    /// <summary>Start a client socket and initiate a connection to the server.</summary>
    public static LiteNetLibTransport ConnectClient(string host, int port, string connectKey)
    {
        var t = new LiteNetLibTransport(isServer: false, connectKey);
        if (!t._net.Start())
        {
            t.Dispose();
            throw new InvalidOperationException("LiteNetLib failed to start the client socket.");
        }
        t._net.Connect(host, port, connectKey);
        return t;
    }

    /// <summary>The actually-bound local port (useful when StartServer was given port 0).</summary>
    public int Port => _net.LocalPort;

    public void Send(int peerId, byte[] payload, DeliveryMethod delivery)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (_peersById.TryGetValue(peerId, out NetPeer? peer))
            peer.Send(payload, Map(delivery));
        // No peer for that id (already dropped / not yet connected): silently ignore, as UDP would.
    }

    public void Poll() => _net.PollEvents();

    private void OnConnectionRequest(ConnectionRequest request)
    {
        // Only the server admits connections, and only with the right key. Clients reject any inbound.
        if (_isServer) request.AcceptIfKey(_connectKey);
        else request.Reject();
    }

    private void OnPeerConnected(NetPeer peer)
    {
        int id = _isServer ? _nextPeerId++ : NetProtocol.ServerPeer;
        _peersById[id] = peer;
        _idByPeer[peer] = id;
        PeerConnected?.Invoke(id);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (!_idByPeer.TryGetValue(peer, out int id)) return;
        _idByPeer.Remove(peer);
        _peersById.Remove(id);
        PeerDisconnected?.Invoke(id);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, LiteNetLib.DeliveryMethod deliveryMethod)
    {
        if (_idByPeer.TryGetValue(peer, out int id))
        {
            byte[] data = reader.GetRemainingBytes(); // owned copy — safe to hand to Core
            Received?.Invoke(id, data);
        }
        reader.Recycle(); // no AutoRecycle in 1.3.5 — return the pooled reader ourselves
    }

    private static LiteNetLib.DeliveryMethod Map(DeliveryMethod delivery) => delivery switch
    {
        DeliveryMethod.ReliableOrdered => LiteNetLib.DeliveryMethod.ReliableOrdered,
        DeliveryMethod.SequencedUnreliable => LiteNetLib.DeliveryMethod.Sequenced, // unreliable, latest wins
        DeliveryMethod.ReliableUnordered => LiteNetLib.DeliveryMethod.ReliableUnordered,
        _ => LiteNetLib.DeliveryMethod.ReliableOrdered,
    };

    public void Dispose()
    {
        _listener.ConnectionRequestEvent -= OnConnectionRequest;
        _listener.PeerConnectedEvent -= OnPeerConnected;
        _listener.PeerDisconnectedEvent -= OnPeerDisconnected;
        _listener.NetworkReceiveEvent -= OnNetworkReceive;
        _net.Stop();
        _peersById.Clear();
        _idByPeer.Clear();
    }
}
