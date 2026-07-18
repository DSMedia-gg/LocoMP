using System;
using LocoMP.Core.Net;

// Note: LiteNetLib types are fully-qualified below. LiteNetLib defines its OWN DeliveryMethod enum,
// which would collide with LocoMP.Core.Net.DeliveryMethod if we imported the namespace. The M1 send
// path will map Core's DeliveryMethod onto LiteNetLib's explicitly — the adapter translates, never leaks.

namespace LocoMP.Transport;

/// <summary>
/// LiteNetLib UDP transport. M0 skeleton: this proves the pinned LiteNetLib 1.3.5 dependency resolves
/// and fits the <see cref="ITransport"/> seam. Connection setup, the reliability channels, and the
/// send path are wired in M1 (07 §M1 — Presence).
/// </summary>
public sealed class LiteNetLibTransport : ITransport
{
    private readonly LiteNetLib.EventBasedNetListener _listener = new();
    private readonly LiteNetLib.NetManager _net;

#pragma warning disable CS0067 // Received is raised once the receive path lands in M1.
    public event Action<int, byte[]>? Received;
#pragma warning restore CS0067

    public LiteNetLibTransport()
    {
        _net = new LiteNetLib.NetManager(_listener);
    }

    public void Send(int peerId, byte[] payload, DeliveryMethod delivery) =>
        throw new NotImplementedException("LiteNetLib send path is implemented in M1 (Presence).");

    public void Poll() => _net.PollEvents();

    public void Dispose() => _net.Stop();
}
