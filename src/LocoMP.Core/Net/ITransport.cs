using System;

namespace LocoMP.Core.Net;

/// <summary>
/// Delivery guarantees, mirroring the incumbent's proven channel split (03 §5). Transactions/economy
/// go reliable-ordered; pose snapshots go sequenced-unreliable; chat/UI go reliable-unordered.
/// </summary>
public enum DeliveryMethod
{
    ReliableOrdered = 0,
    SequencedUnreliable = 1,
    ReliableUnordered = 2,
}

/// <summary>
/// The transport port (hexagonal seam, 03 §2). Core owns this abstraction; concrete adapters
/// (LiteNetLib UDP, Steam relay, in-process Loopback) live in LocoMP.Transport. Core never depends
/// on any networking library — only on this interface — so the same logic runs over real UDP in a
/// session and over Loopback in a single-process test (03 §11).
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>Raised when a payload arrives from a peer: (peerId, payload).</summary>
    event Action<int, byte[]>? Received;

    /// <summary>
    /// Raised when a peer link is established: (peerId). On the client this fires once with the
    /// server's id (join handshake is sent in response); on the server, once per connecting client.
    /// </summary>
    event Action<int>? PeerConnected;

    /// <summary>Raised when a peer link drops (graceful or lost): (peerId). Drives roster eviction.</summary>
    event Action<int>? PeerDisconnected;

    /// <summary>Send a payload to a peer with the given delivery guarantee.</summary>
    void Send(int peerId, byte[] payload, DeliveryMethod delivery);

    /// <summary>Pump queued network events (Received / PeerConnected / PeerDisconnected). Once per tick.</summary>
    void Poll();
}
