namespace LocoMP.Core.Net;

/// <summary>
/// Cumulative application-payload traffic counters for a transport, since it was created (M5.2 diagnostics).
/// These count the BYTES OF PAYLOAD handed across the <see cref="ITransport"/> seam — not wire bytes, which
/// carry per-transport framing (UDP + LiteNetLib headers, or none at all for the in-process Loopback). That
/// keeps the number consistent and testable regardless of the underlying link; it is a "how much is the
/// session moving" gauge, not an exact-on-the-wire meter.
/// </summary>
public readonly struct TransportStats
{
    public TransportStats(long bytesSent, long bytesReceived, long messagesSent, long messagesReceived)
    {
        BytesSent = bytesSent;
        BytesReceived = bytesReceived;
        MessagesSent = messagesSent;
        MessagesReceived = messagesReceived;
    }

    public long BytesSent { get; }
    public long BytesReceived { get; }
    public long MessagesSent { get; }
    public long MessagesReceived { get; }

    /// <summary>The empty snapshot — a transport that has moved nothing.</summary>
    public static TransportStats Zero => new(0, 0, 0, 0);
}
