using System;
using System.Collections.Generic;
using LocoMP.Core.Net;

namespace LocoMP.Core.Tests;

/// <summary>
/// A pass-through <see cref="ITransport"/> decorator that tallies the bytes and messages the SERVER
/// sends, bucketed by recipient peer id and delivery method. It changes no behaviour — every call is
/// forwarded to the inner transport — so a real session runs over it unmodified while we measure the
/// exact wire cost (03 §11 lets Core run headless over Loopback; this just weighs what crosses the seam).
///
/// Deterministic: the counts are a pure function of the messages the server chose to send, so the
/// size measurements (join-burst bytes, per-message relay sizes) are stable across machines and safe to
/// assert against the 02 §9 budgets. Wrap the server side (<c>hub.Server</c>) and the counts are
/// everything the server emits to clients.
/// </summary>
public sealed class CountingTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly Dictionary<int, long> _bytesTo = new();
    private readonly Dictionary<int, int> _msgsTo = new();
    private readonly long[] _bytesByDelivery = new long[3];

    public CountingTransport(ITransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        // Re-raise the inner transport's events to our own subscribers, unchanged.
        _inner.Received += (p, d) => Received?.Invoke(p, d);
        _inner.PeerConnected += p => PeerConnected?.Invoke(p);
        _inner.PeerDisconnected += p => PeerDisconnected?.Invoke(p);
    }

    public event Action<int, byte[]>? Received;
    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    /// <summary>Total bytes the server has sent to all peers since the last <see cref="Reset"/>.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>Total messages the server has sent to all peers since the last <see cref="Reset"/>.</summary>
    public int TotalMessages { get; private set; }

    public void Send(int peerId, byte[] payload, DeliveryMethod delivery)
    {
        int len = payload.Length;
        _bytesTo.TryGetValue(peerId, out long b);
        _bytesTo[peerId] = b + len;
        _msgsTo.TryGetValue(peerId, out int m);
        _msgsTo[peerId] = m + 1;
        _bytesByDelivery[(int)delivery] += len;
        TotalBytes += len;
        TotalMessages++;
        _inner.Send(peerId, payload, delivery);
    }

    public void Poll() => _inner.Poll();

    /// <summary>Zero every counter — call immediately before the interval you want to weigh.</summary>
    public void Reset()
    {
        _bytesTo.Clear();
        _msgsTo.Clear();
        Array.Clear(_bytesByDelivery, 0, _bytesByDelivery.Length);
        TotalBytes = 0;
        TotalMessages = 0;
    }

    public long BytesTo(int peerId) => _bytesTo.TryGetValue(peerId, out long b) ? b : 0;

    public int MessagesTo(int peerId) => _msgsTo.TryGetValue(peerId, out int m) ? m : 0;

    public long BytesByDelivery(DeliveryMethod delivery) => _bytesByDelivery[(int)delivery];

    public void Dispose() => _inner.Dispose();
}
