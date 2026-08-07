using System;
using System.Collections.Generic;
using LocoMP.Transport;

namespace LocoMP.Core.Tests;

/// <summary>
/// Hand-driven <see cref="IRelaySocket"/> for unit tests: the test raises the seam events itself and
/// inspects what the transport told each peer. No pairing, no queues — pure white-box control over
/// framing, sequencing, and lifecycle edges.
/// </summary>
internal sealed class StubRelaySocket : IRelaySocket
{
    public bool Disposed;
    public int PollCount;

    public event Action<IRelayPeer>? Connecting;
    public event Action<IRelayPeer>? Connected;
    public event Action<IRelayPeer>? Disconnected;
    public event Action<IRelayPeer, byte[]>? Message;

    public void RaiseConnecting(IRelayPeer peer) => Connecting?.Invoke(peer);
    public void RaiseConnected(IRelayPeer peer) => Connected?.Invoke(peer);
    public void RaiseDisconnected(IRelayPeer peer) => Disconnected?.Invoke(peer);
    public void RaiseMessage(IRelayPeer peer, byte[] frame) => Message?.Invoke(peer, frame);

    public void Poll() => PollCount++;
    public void Dispose() => Disposed = true;
}

internal sealed class StubRelayPeer : IRelayPeer
{
    public StubRelayPeer(ulong remoteId) => RemoteId = remoteId;

    public ulong RemoteId { get; }
    public bool Accepted;
    public bool Closed;
    public int? PingMs { get; set; }
    public readonly List<(byte[] Frame, bool Reliable)> Sent = new();

    public void Accept() => Accepted = true;
    public void Close() => Closed = true;
    public void Send(byte[] payload, bool reliable) => Sent.Add(((byte[])payload.Clone(), reliable));
}

/// <summary>
/// An in-memory relay "network": one listening host socket plus any number of client sockets, with
/// the real event choreography — Connecting on the host first, Connected on both sides only after
/// Accept, Disconnected everywhere relevant, messages delivered on the FAR side's Poll. This is the
/// integration-grade double: a whole NetServer/NetClient session runs over it, proving the framing is
/// symmetric end to end (the relay path is always LocoMP↔LocoMP, so both ends are this transport).
/// </summary>
internal sealed class FakeRelayNetwork
{
    public FakeRelaySocket Host { get; }

    public FakeRelayNetwork(ulong hostSteamId)
    {
        HostSteamId = hostSteamId;
        Host = new FakeRelaySocket();
    }

    public ulong HostSteamId { get; }

    /// <summary>A client dials the host. The host socket sees Connecting on its next Poll; nothing
    /// reaches the client until the host side accepts (or refuses, which surfaces as Disconnected).</summary>
    public FakeRelaySocket Connect(ulong clientSteamId)
    {
        var client = new FakeRelaySocket();
        var link = new Link();
        link.HostSide = new FakeRelayPeer(clientSteamId, Host, link);
        link.ClientSide = new FakeRelayPeer(HostSteamId, client, link);
        Host.Enqueue(() => Host.RaiseConnecting(link.HostSide!));
        return client;
    }

    internal sealed class Link
    {
        public FakeRelayPeer? HostSide;
        public FakeRelayPeer? ClientSide;
        public bool Accepted;
        public bool Closed;
    }

    internal sealed class FakeRelayPeer : IRelayPeer
    {
        private readonly FakeRelaySocket _owner; // the socket whose events this peer appears in
        private readonly Link _link;

        public FakeRelayPeer(ulong remoteId, FakeRelaySocket owner, Link link)
        {
            RemoteId = remoteId;
            _owner = owner;
            _link = link;
        }

        public ulong RemoteId { get; }
        public int? PingMs { get; set; } = 0; // relay links always measure; tests overwrite at will

        private FakeRelayPeer Partner => ReferenceEquals(_link.HostSide, this) ? _link.ClientSide! : _link.HostSide!;

        public void Accept()
        {
            if (_link.Accepted || _link.Closed) return;
            _link.Accepted = true;
            _owner.Enqueue(() => _owner.RaiseConnected(this));
            FakeRelayPeer partner = Partner;
            partner._owner.Enqueue(() => partner._owner.RaiseConnected(partner));
        }

        public void Close()
        {
            if (_link.Closed) return;
            bool wasAccepted = _link.Accepted;
            _link.Closed = true;
            FakeRelayPeer partner = Partner;
            // A refusal at the connecting stage: the dialing side learns its attempt died; the
            // refusing side never raised Connected, so its transport stays silent (unmapped peer).
            partner._owner.Enqueue(() => partner._owner.RaiseDisconnected(partner));
            if (wasAccepted) _owner.Enqueue(() => _owner.RaiseDisconnected(this));
        }

        public void Send(byte[] payload, bool reliable)
        {
            if (!_link.Accepted || _link.Closed) return;
            byte[] copy = (byte[])payload.Clone();
            FakeRelayPeer partner = Partner;
            partner._owner.Enqueue(() => partner._owner.RaiseMessage(partner, copy));
        }
    }

    internal sealed class FakeRelaySocket : IRelaySocket
    {
        private readonly Queue<Action> _pending = new();
        public bool Disposed;

        public event Action<IRelayPeer>? Connecting;
        public event Action<IRelayPeer>? Connected;
        public event Action<IRelayPeer>? Disconnected;
        public event Action<IRelayPeer, byte[]>? Message;

        internal void Enqueue(Action deliver) => _pending.Enqueue(deliver);
        internal void RaiseConnecting(IRelayPeer p) => Connecting?.Invoke(p);
        internal void RaiseConnected(IRelayPeer p) => Connected?.Invoke(p);
        internal void RaiseDisconnected(IRelayPeer p) => Disconnected?.Invoke(p);
        internal void RaiseMessage(IRelayPeer p, byte[] frame) => Message?.Invoke(p, frame);

        public void Poll()
        {
            // Drain what exists NOW; deliveries queued by these handlers wait for the next Poll,
            // like a real socket's per-tick pump.
            int n = _pending.Count;
            for (int i = 0; i < n && _pending.Count > 0; i++) _pending.Dequeue()();
        }

        public void Dispose() => Disposed = true;
    }
}
