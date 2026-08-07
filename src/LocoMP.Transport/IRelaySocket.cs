using System;

namespace LocoMP.Transport;

/// <summary>
/// One end of a relay link as the game-free transport logic sees it (M5.5, O6/D4). The game-side
/// binding wraps a Facepunch connection riding the game's OWN initialized Steam client (never
/// <c>SteamClient.Init</c> — research §3.6); tests wrap an in-memory fake. The binding must raise the
/// SAME instance for a given connection across Connecting/Connected/Disconnected/Message so the
/// transport can key its peer maps on reference identity.
/// </summary>
public interface IRelayPeer
{
    /// <summary>The far end's SteamId64, authenticated by Valve's relay before the link opens. This is
    /// what makes the Steam path the U3 ban identity: unlike a self-declared player key, it cannot be
    /// re-rolled by reconnecting.</summary>
    ulong RemoteId { get; }

    /// <summary>Admit a connecting peer (server side, from the <see cref="IRelaySocket.Connecting"/>
    /// decision point). No-op once the link is past connecting.</summary>
    void Accept();

    /// <summary>Close the link (either the connecting-stage refusal or a live-link drop). Both sides
    /// observe the ordinary <see cref="IRelaySocket.Disconnected"/>.</summary>
    void Close();

    /// <summary>Send one framed message. <paramref name="reliable"/> maps to Steam's reliable class
    /// (which is also ordered); unreliable maps to unreliable-no-nagle so pose snapshots leave now.</summary>
    void Send(byte[] payload, bool reliable);

    /// <summary>Measured round-trip time in ms (Valve's ping IS the RTT, unlike LiteNetLib's one-way
    /// smoothed ping), or null while unknown.</summary>
    int? PingMs { get; }
}

/// <summary>
/// A relay socket in a fixed role — server (listens on a virtual port, peers arrive) or client (one
/// outbound connection, the peer is the server). The seam exists so ALL transport logic — peer-id
/// assignment, framing, sequencing, stats — lives game-free in <see cref="SteamRelayTransport"/> and
/// runs in the ordinary test suite; the Facepunch binding is a thin event forwarder.
///
/// <para><b>Threading contract:</b> every event fires inside <see cref="Poll"/>, on the polling
/// thread. DV initializes Facepunch with async callbacks (decompile-verified: <c>DVSteamworks</c>
/// calls <c>SteamClient.Init(588030)</c> with the default <c>asyncCallbacks: true</c>), so
/// connection-status changes arrive on a background thread — the binding queues them and drains the
/// queue in Poll. Message receives are pumped synchronously by Poll itself.</para>
/// </summary>
public interface IRelaySocket : IDisposable
{
    /// <summary>Server side only: a peer wants in. The subscriber decides — <see cref="IRelayPeer.Accept"/>
    /// or <see cref="IRelayPeer.Close"/>. With no subscriber the socket accepts (the transport always
    /// subscribes; the default only matters for a bare socket).</summary>
    event Action<IRelayPeer>? Connecting;

    /// <summary>The link is live (both roles). Server: once per admitted peer. Client: once, for the server.</summary>
    event Action<IRelayPeer>? Connected;

    /// <summary>The link dropped — graceful, lost, or a connect attempt that never completed (in which
    /// case <see cref="Connected"/> never fired for that peer and the transport ignores it, matching
    /// the LiteNetLib adapter's unmapped-peer silence).</summary>
    event Action<IRelayPeer>? Disconnected;

    /// <summary>A message arrived from a live peer. The payload is an owned copy, safe to hand upward.</summary>
    event Action<IRelayPeer, byte[]>? Message;

    /// <summary>Drain queued status events, then pump receives. Once per tick, single-threaded.</summary>
    void Poll();
}
