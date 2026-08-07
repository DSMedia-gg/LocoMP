using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LocoMP.Transport;
using Steamworks;
using Steamworks.Data;

namespace LocoMP.Net;

/// <summary>
/// The Facepunch binding behind <see cref="IRelaySocket"/> (M5.5, O6/D4): a listen socket or one
/// outbound connection on Valve's SDR, riding the game's OWN initialized Steam client — DV's
/// <c>DVSteamworks</c> calls <c>SteamClient.Init(588030)</c> at scene load (decompile-proven) and we
/// never init or shut it down. All transport behaviour lives game-free in
/// <see cref="SteamRelayTransport"/>; this class only forwards events and bytes.
///
/// <para><b>Threading.</b> DV inits Facepunch with async callbacks, so connection-status changes
/// arrive on a background thread; message receives arrive synchronously inside our own
/// <c>Receive()</c> call. EVERYTHING funnels through one lock-guarded FIFO drained in
/// <see cref="Poll"/> — a single ordered stream on the polling thread, which is what the seam
/// contract promises (and why a Connected can never be overtaken by that link's first message).</para>
/// </summary>
internal sealed class FacepunchRelaySocket : IRelaySocket, ISocketManager, IConnectionManager
{
    /// <summary>The session's virtual port on the relay. One well-known value — the address of a
    /// LocoMP session IS the host's SteamId64; the port only namespaces us from other P2P uses of
    /// DV's AppId.</summary>
    public const int VirtualPort = 33417;

    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private readonly Dictionary<uint, FacepunchRelayPeer> _peersByConn = new();
    private SocketManager? _listen;          // host role
    private ConnectionManager? _outbound;    // client role
    private FacepunchRelayPeer? _serverPeer; // client role: the single far end
    private bool _disposed;

    public event Action<IRelayPeer>? Connecting;
    public event Action<IRelayPeer>? Connected;
    public event Action<IRelayPeer>? Disconnected;
    public event Action<IRelayPeer, byte[]>? Message;

    private FacepunchRelaySocket() { }

    private static void RequireSteam()
    {
        if (!SteamClient.IsValid)
            throw new InvalidOperationException("Steam is not available (the game runs without it)");
    }

    /// <summary>Open the host's listen socket on the relay. Throws when Steam isn't up — the caller
    /// logs and hosts UDP-only; a session must never fail to start over the optional link.</summary>
    public static FacepunchRelaySocket Host()
    {
        RequireSteam();
        var s = new FacepunchRelaySocket();
        s._listen = SteamNetworkingSockets.CreateRelaySocket(VirtualPort, s);
        return s;
    }

    /// <summary>Dial a host by SteamId64 over the relay.</summary>
    public static FacepunchRelaySocket Connect(ulong hostSteamId)
    {
        RequireSteam();
        var s = new FacepunchRelaySocket();
        // The far end's identity is the address we dialed — known before any callback fires.
        s._serverPeer = new FacepunchRelayPeer(s, hostSteamId);
        s._outbound = SteamNetworkingSockets.ConnectRelay(hostSteamId, VirtualPort, s);
        s._serverPeer.Bind(s._outbound.Connection);
        return s;
    }

    public void Poll()
    {
        if (_disposed) return;
        // Receives first: they enqueue through the same FIFO the background statuses use, so a
        // status queued earlier always drains before the messages that followed it.
        _listen?.Receive();
        _outbound?.Receive();

        // Drain what exists NOW (bounded — events raised by handlers wait a frame, like every
        // adapter at this seam).
        int n;
        lock (_gate) n = _pending.Count;
        for (int i = 0; i < n; i++)
        {
            Action deliver;
            lock (_gate)
            {
                if (_pending.Count == 0) break;
                deliver = _pending.Dequeue();
            }
            deliver();
        }
    }

    private void Enqueue(Action deliver)
    {
        lock (_gate)
        {
            if (!_disposed) _pending.Enqueue(deliver);
        }
    }

    // ── host role: ISocketManager (statuses on the Dispatch thread, messages inside Receive) ──

    void ISocketManager.OnConnecting(Connection connection, ConnectionInfo info)
    {
        var peer = new FacepunchRelayPeer(this, info.Identity.SteamId);
        peer.Bind(connection);
        lock (_gate) _peersByConn[connection.Id] = peer;
        Enqueue(() => Connecting?.Invoke(peer));
    }

    void ISocketManager.OnConnected(Connection connection, ConnectionInfo info)
    {
        if (TryPeer(connection.Id) is { } peer)
        {
            peer.Live = true;
            Enqueue(() => Connected?.Invoke(peer));
        }
    }

    void ISocketManager.OnDisconnected(Connection connection, ConnectionInfo info)
    {
        if (TryPeer(connection.Id) is { } peer)
        {
            peer.Live = false;
            lock (_gate) _peersByConn.Remove(connection.Id);
            Enqueue(() => Disconnected?.Invoke(peer));
        }
    }

    void ISocketManager.OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size,
        long messageNum, long recvTime, int channel)
    {
        if (TryPeer(connection.Id) is { } peer)
            Enqueue(CaptureMessage(peer, data, size));
    }

    // ── client role: IConnectionManager (same threads, one implicit connection) ──

    void IConnectionManager.OnConnecting(ConnectionInfo info) { } // our own dial — nothing to decide

    void IConnectionManager.OnConnected(ConnectionInfo info)
    {
        if (_serverPeer is { } peer)
        {
            peer.Live = true;
            Enqueue(() => Connected?.Invoke(peer));
        }
    }

    void IConnectionManager.OnDisconnected(ConnectionInfo info)
    {
        if (_serverPeer is { } peer)
        {
            peer.Live = false;
            Enqueue(() => Disconnected?.Invoke(peer));
        }
    }

    void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
    {
        if (_serverPeer is { } peer) Enqueue(CaptureMessage(peer, data, size));
    }

    private Action CaptureMessage(FacepunchRelayPeer peer, IntPtr data, int size)
    {
        // The native buffer is released the moment the callback returns — copy NOW, deliver later.
        byte[] copy = new byte[size];
        if (size > 0) Marshal.Copy(data, copy, 0, size);
        return () => Message?.Invoke(peer, copy);
    }

    private FacepunchRelayPeer? TryPeer(uint connectionId)
    {
        lock (_gate) return _peersByConn.TryGetValue(connectionId, out FacepunchRelayPeer? p) ? p : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) _pending.Clear();
        _listen?.Close();
        _listen = null;
        _outbound?.Close();
        _outbound = null;
        lock (_gate) _peersByConn.Clear();
        _serverPeer = null;
    }

    private sealed class FacepunchRelayPeer : IRelayPeer
    {
        private readonly FacepunchRelaySocket _owner;
        private Connection _conn;

        public FacepunchRelayPeer(FacepunchRelaySocket owner, ulong remoteId)
        {
            _owner = owner;
            RemoteId = remoteId;
        }

        internal void Bind(Connection conn) => _conn = conn;
        internal bool Live;

        public ulong RemoteId { get; }

        public void Accept() => _conn.Accept();

        public void Close() => _conn.Close();

        public void Send(byte[] payload, bool reliable) =>
            // NoNagle on the unreliable class: pose snapshots are latency-priced and already paced
            // by the send interval — matching the UDP adapter's send-now behaviour.
            _conn.SendMessage(payload, reliable ? SendType.Reliable : SendType.Unreliable | SendType.NoNagle);

        /// <summary>Valve's ping is the round trip already. Null until the link is live (QuickStatus
        /// on a half-open connection reads 0, which would render as a perfect link).</summary>
        public int? PingMs
        {
            get
            {
                if (!Live || _owner._disposed) return null;
                int ping = _conn.QuickStatus().Ping;
                return ping >= 0 ? ping : (int?)null;
            }
        }
    }
}
