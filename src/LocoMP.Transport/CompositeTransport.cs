using System;
using System.Collections.Generic;
using LocoMP.Core.Net;

namespace LocoMP.Transport;

/// <summary>
/// Multiplexes several inner transports into one <see cref="ITransport"/> with a unified peer-id
/// space. This is how the host serves everyone at once (03 §6): its own player connects over the
/// in-process Loopback hub while remote players arrive over LiteNetLib UDP — but each inner
/// transport numbers its peers independently (both start at 1), so the composite remaps every inner
/// peer to a fresh outer id. NetServer sees one flat roster and never knows which link a player is
/// on. Single-threaded like everything at this seam: events fire inside <see cref="Poll"/>.
/// </summary>
public sealed class CompositeTransport : ITransport
{
    private readonly ITransport[] _inners;
    private readonly Dictionary<int, (int inner, int innerId)> _byOuter = new();
    private readonly Dictionary<(int inner, int innerId), int> _byInner = new();
    private readonly Action<int>[] _onConnect;
    private readonly Action<int>[] _onDisconnect;
    private readonly Action<int, byte[]>[] _onReceive;
    private int _nextOuterId = 1;

    public event Action<int, byte[]>? Received;
    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    public CompositeTransport(params ITransport[] inners)
    {
        if (inners is null || inners.Length == 0) throw new ArgumentException("need at least one inner transport", nameof(inners));
        _inners = inners;
        _onConnect = new Action<int>[inners.Length];
        _onDisconnect = new Action<int>[inners.Length];
        _onReceive = new Action<int, byte[]>[inners.Length];

        for (int i = 0; i < inners.Length; i++)
        {
            int idx = i; // capture per-inner index, not the loop variable
            _onConnect[i] = innerId => OnInnerConnected(idx, innerId);
            _onDisconnect[i] = innerId => OnInnerDisconnected(idx, innerId);
            _onReceive[i] = (innerId, payload) => OnInnerReceived(idx, innerId, payload);
            inners[i].PeerConnected += _onConnect[i];
            inners[i].PeerDisconnected += _onDisconnect[i];
            inners[i].Received += _onReceive[i];
        }
    }

    public void Send(int peerId, byte[] payload, DeliveryMethod delivery)
    {
        if (_byOuter.TryGetValue(peerId, out var route))
            _inners[route.inner].Send(route.innerId, payload, delivery);
        // Unknown outer id (peer already gone): drop silently, like UDP.
    }

    public void Poll()
    {
        foreach (ITransport inner in _inners) inner.Poll();
    }

    private void OnInnerConnected(int inner, int innerId)
    {
        int outer = _nextOuterId++;
        _byOuter[outer] = (inner, innerId);
        _byInner[(inner, innerId)] = outer;
        PeerConnected?.Invoke(outer);
    }

    private void OnInnerDisconnected(int inner, int innerId)
    {
        if (!_byInner.TryGetValue((inner, innerId), out int outer)) return;
        _byInner.Remove((inner, innerId));
        _byOuter.Remove(outer);
        PeerDisconnected?.Invoke(outer);
    }

    private void OnInnerReceived(int inner, int innerId, byte[] payload)
    {
        if (_byInner.TryGetValue((inner, innerId), out int outer))
            Received?.Invoke(outer, payload);
        // A message from an unmapped peer (raced its own disconnect) is dropped.
    }

    /// <summary>Disposes every inner transport too — the composite owns its links.</summary>
    public void Dispose()
    {
        for (int i = 0; i < _inners.Length; i++)
        {
            _inners[i].PeerConnected -= _onConnect[i];
            _inners[i].PeerDisconnected -= _onDisconnect[i];
            _inners[i].Received -= _onReceive[i];
            _inners[i].Dispose();
        }
        _byOuter.Clear();
        _byInner.Clear();
    }
}
