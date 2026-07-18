namespace LocoMP.Core.Session;

/// <summary>Session-layer wire constants shared by <see cref="NetServer"/> and <see cref="NetClient"/>.</summary>
public static class NetProtocol
{
    /// <summary>
    /// The peer id a client uses to address the server. A client has exactly one peer (the server),
    /// so every client Send targets this. Client ids assigned by the server start at 1, keeping 0
    /// reserved for "the server" and unambiguous on both transports (Loopback hub and LiteNetLib).
    /// </summary>
    public const int ServerPeer = 0;
}
