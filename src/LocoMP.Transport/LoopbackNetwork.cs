using System;
using System.Collections.Generic;
using LocoMP.Core.Net;

namespace LocoMP.Transport;

/// <summary>
/// In-memory switchboard modelling ONE server endpoint serving N client endpoints — the way a single
/// LiteNetLib NetManager serves many peers (03 §11). Fully deterministic: sends and connect/disconnect
/// signals are queued and dispatched on each endpoint's <see cref="ITransport.Poll"/>, so join/leave
/// storm tests are reproducible with no threads or wall-clock. The host player uses this too
/// (server endpoint + one local client endpoint = "client #1", 03 §6).
/// </summary>
public sealed class LoopbackNetwork
{
    private const int ServerPeer = 0;
    private int _nextClientId = 1;
    private readonly Endpoint _server;
    private readonly Dictionary<int, Endpoint> _clients = new();

    public LoopbackNetwork() => _server = new Endpoint(this, ServerPeer);

    /// <summary>
    /// The server-side transport. <c>Send(clientId, …)</c> routes to that client; <c>Received</c>
    /// reports the sending client's id.
    /// </summary>
    public ITransport Server => _server;

    /// <summary>Number of client endpoints currently connected (test-visibility for leak checks).</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Create a client endpoint and queue PeerConnected on both ends (delivered on the next Poll of
    /// each side). Construct the client's session object BEFORE polling so it observes the connect.
    /// </summary>
    public ITransport Connect(out int clientId)
    {
        clientId = _nextClientId++;
        var ep = new Endpoint(this, clientId);
        _clients[clientId] = ep;
        _server.QueueConnect(clientId);
        ep.QueueConnect(ServerPeer);
        return ep;
    }

    /// <summary>Drop a client; queues PeerDisconnected on both ends. No-op if already gone.</summary>
    public void Disconnect(int clientId)
    {
        if (!_clients.TryGetValue(clientId, out Endpoint? ep)) return;
        _clients.Remove(clientId);
        _server.QueueDisconnect(clientId);
        ep.QueueDisconnect(ServerPeer);
    }

    private void Route(int fromId, int toId, byte[] payload)
    {
        if (toId == ServerPeer) { _server.QueueReceive(fromId, payload); return; }
        if (_clients.TryGetValue(toId, out Endpoint? ep)) ep.QueueReceive(fromId, payload);
        // A message to a departed client is silently dropped, exactly as UDP would.
    }

    private sealed class Endpoint : ITransport
    {
        private readonly LoopbackNetwork _net;
        private readonly int _id;
        private readonly Queue<Action> _events = new();

        public Endpoint(LoopbackNetwork net, int id)
        {
            _net = net;
            _id = id;
        }

        public event Action<int, byte[]>? Received;
        public event Action<int>? PeerConnected;
        public event Action<int>? PeerDisconnected;

        public void Send(int peerId, byte[] payload, DeliveryMethod delivery)
        {
            if (payload is null) throw new ArgumentNullException(nameof(payload));
            // Clone so a sender reusing its buffer can't retroactively change a queued message.
            _net.Route(_id, peerId, (byte[])payload.Clone());
        }

        public void Poll()
        {
            // Only drain what is queued NOW: a handler that queues more (e.g. connect → send join)
            // has its follow-up delivered on a later Poll, mirroring real network round-trips.
            int n = _events.Count;
            for (int i = 0; i < n; i++) _events.Dequeue().Invoke();
        }

        internal void QueueReceive(int fromId, byte[] payload) => _events.Enqueue(() => Received?.Invoke(fromId, payload));
        internal void QueueConnect(int peerId) => _events.Enqueue(() => PeerConnected?.Invoke(peerId));
        internal void QueueDisconnect(int peerId) => _events.Enqueue(() => PeerDisconnected?.Invoke(peerId));

        public void Dispose()
        {
            // Match UDP semantics: disposing your transport drops the link, and the OTHER side
            // observes PeerDisconnected (LiteNetLib does this on socket close). A client endpoint
            // deregisters from the hub; no-op if the hub already disconnected it.
            if (_id != ServerPeer) _net.Disconnect(_id);
            _events.Clear();
        }
    }
}
