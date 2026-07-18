using System;
using System.Collections.Generic;
using LocoMP.Core.Net;

namespace LocoMP.Transport;

/// <summary>
/// In-memory transport pair for single-process test harnesses (03 §11, hard rule 8 — the daily rig).
/// Deterministic by design: messages are delivered on <see cref="Poll"/>, never on a background
/// thread, so transaction-fuzz and join-storm tests stay reproducible.
/// </summary>
public sealed class LoopbackTransport : ITransport
{
    private readonly Queue<(int peerId, byte[] payload)> _inbox = new();
    private LoopbackTransport? _peer;

    public event Action<int, byte[]>? Received;

    // The 1:1 pair models an already-established link (host = client #1). It never raises connect/
    // disconnect itself; the multi-peer LoopbackNetwork drives the join/leave lifecycle for N clients.
#pragma warning disable CS0067
    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;
#pragma warning restore CS0067

    private LoopbackTransport() { }

    /// <summary>Create two linked endpoints; a Send on one is delivered to the other on its next Poll.</summary>
    public static (LoopbackTransport a, LoopbackTransport b) CreatePair()
    {
        var a = new LoopbackTransport();
        var b = new LoopbackTransport();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public void Send(int peerId, byte[] payload, DeliveryMethod delivery)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        // Copy so the sender mutating its buffer can't retroactively change what the peer receives.
        _peer?._inbox.Enqueue((peerId, (byte[])payload.Clone()));
    }

    public void Poll()
    {
        while (_inbox.Count > 0)
        {
            var (peerId, payload) = _inbox.Dequeue();
            Received?.Invoke(peerId, payload);
        }
    }

    public void Dispose()
    {
        _inbox.Clear();
        _peer = null;
    }
}
