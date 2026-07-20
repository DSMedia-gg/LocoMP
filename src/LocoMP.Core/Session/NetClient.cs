using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>
/// The client half of a session. On transport connect it sends the handshake; on acceptance it holds
/// its assigned id, an NTP-style server-time offset, and a live mirror of the OTHER players' state
/// (self is excluded — the frontend already knows itself). Game-free: the Shim feeds it local pose and
/// consumes <see cref="PlayerMoved"/> to drive remote avatars (M1.3).
/// </summary>
public sealed class NetClient : IDisposable
{
    private readonly ITransport _transport;
    private readonly HandshakeRequest _identity;
    private readonly string _displayName;
    private readonly string _password;
    private readonly IClock _clock;
    private readonly Dictionary<int, PlayerState> _players = new();

    public NetClient(ITransport transport, HandshakeRequest identity, string displayName, IClock clock,
        string? password = null, string? playerKey = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _password = password ?? string.Empty;
        PlayerKey = playerKey ?? Guid.NewGuid().ToString("N");

        Trains = new ClientTrains(_transport, () => Joined);
        Career = new ClientCareer(_transport, () => Joined);
        Items = new ClientItems(_transport, () => Joined);

        _transport.Received += OnReceived;
        _transport.PeerConnected += OnConnected;
        _transport.PeerDisconnected += OnDisconnected;
    }

    /// <summary>The train subsystem (M2): the mirrored trainset world + propose/stream calls.</summary>
    public ClientTrains Trains { get; }

    /// <summary>The career subsystem (M3): the mirrored board/wallet/licenses + propose calls.</summary>
    public ClientCareer Career { get; }

    /// <summary>The item subsystem (M4): the mirrored item set + pickup/drop/purchase propose calls.</summary>
    public ClientItems Items { get; }

    /// <summary>
    /// This player's stable identity (M3): the server keys their profile, wallet, and reconnect
    /// grace on it, so the frontend should PERSIST it and pass the same key every session — a
    /// fresh default (random) key is a fresh career. Never shown to other players.
    /// </summary>
    public string PlayerKey { get; }

    /// <summary>This client's server-assigned id once admitted; null until JoinAccepted arrives.</summary>
    public int? LocalId { get; private set; }

    public bool Joined => LocalId.HasValue;

    /// <summary>Set if the server refused the join; carries the exact reason (03 §10).</summary>
    public string? RejectReason { get; private set; }

    /// <summary>serverNow - localNow (ms). Add to the local clock to estimate server time (03 §5).</summary>
    public long ServerTimeOffsetMs { get; private set; }

    /// <summary>Estimated authoritative server time now, from the last sync.</summary>
    public long EstimatedServerTimeMs => _clock.NowMs + ServerTimeOffsetMs;

    /// <summary>The OTHER players in the session, keyed by id (excludes self).</summary>
    public IReadOnlyDictionary<int, PlayerState> Players => _players;

    public event Action<int>? Accepted;             // arg: local id
    public event Action<string>? Rejected;          // arg: reason
    public event Action<PlayerState>? PlayerJoined;
    public event Action<int>? PlayerLeft;
    public event Action<int, Pose>? PlayerMoved;    // args: id, new pose

    /// <summary>The server hid a remote player who left our spatial relevance set (D10). Unlike
    /// <see cref="PlayerLeft"/> the player is still in the session — the Shim should hide the avatar
    /// but keep its state; the next <see cref="PlayerMoved"/> for that id (when we near them again)
    /// re-shows it. Arg: the hidden player's id.</summary>
    public event Action<int>? PlayerHidden;

    /// <summary>The transport link to the server dropped AFTER we had been admitted (host died,
    /// eviction, network loss). Not raised for failed joins — those surface via timeout/Rejected.
    /// The mirrors are already reset when this fires; the frontend decides what "session lost"
    /// means for its world (the joined Shim must NOT silently unblock native saving, 03 §10).</summary>
    public event Action? Disconnected;

    public void Poll() => _transport.Poll();

    /// <summary>Announce the local player's pose to the server (sequenced-unreliable — latest wins).</summary>
    public void SendPose(Pose pose)
    {
        if (!Joined) return;
        var w = new PacketWriter(32)
            .WriteByte((byte)MessageType.PlayerPose)
            .WriteVarUInt((uint)LocalId!.Value);   // server ignores this and stamps authoritatively
        PresenceCodec.WritePose(w, pose);
        _transport.Send(NetProtocol.ServerPeer, w.ToArray(), DeliveryMethod.SequencedUnreliable);
    }

    /// <summary>Tell the server we are leaving (graceful). The transport disconnect is the hard fallback.</summary>
    public void Leave()
    {
        if (!Joined) return;
        byte[] payload = new PacketWriter(1).WriteByte((byte)MessageType.Leave).ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    private void OnConnected(int serverPeer)
    {
        byte[] payload = new PacketWriter(64)
            .WriteByte((byte)MessageType.JoinRequest)
            .WriteVarUInt((uint)_identity.ProtocolVersion)
            .WriteString(_identity.GameBuild)
            .WriteString(_identity.ModVersion)
            .WriteString(_identity.ModListHash)
            .WriteString(_password)
            .WriteString(_displayName)
            .WriteString(PlayerKey)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    private void OnDisconnected(int serverPeer)
    {
        bool wasJoined = LocalId.HasValue;
        LocalId = null;
        _players.Clear();
        Trains.Reset();
        Career.Reset();
        Items.Reset();
        if (wasJoined) Disconnected?.Invoke();
    }

    private void OnReceived(int fromPeer, byte[] payload)
    {
        var r = new PacketReader(payload);
        MessageType type;
        try { type = (MessageType)r.ReadByte(); }
        catch { return; }

        try
        {
            switch (type)
            {
                case MessageType.JoinAccepted: HandleAccepted(r); break;
                case MessageType.JoinRejected: HandleRejected(r); break;
                case MessageType.PlayerJoined: HandlePlayerJoined(r); break;
                case MessageType.PlayerLeft: HandlePlayerLeft(r); break;
                case MessageType.PlayerPose: HandlePlayerPose(r); break;
                case MessageType.TimeSync: HandleTimeSync(r); break;
                case MessageType.InterestHide: HandleInterestHide(r); break;
                default:
                    if (!Trains.TryHandle(type, r) && !Career.TryHandle(type, r)) Items.TryHandle(type, r);
                    break;
            }
        }
        catch (Exception)
        {
            // Drop malformed server packets rather than tearing down the client mid-session.
        }
    }

    private void HandleAccepted(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        long serverTime = r.ReadInt64();
        LocalId = id;
        ServerTimeOffsetMs = serverTime - _clock.NowMs;

        int count = (int)r.ReadVarUInt();
        _players.Clear();
        var roster = new List<PlayerState>(count);
        for (int i = 0; i < count; i++)
        {
            PlayerState p = PresenceCodec.ReadPlayer(r);
            _players[p.Id] = p;
            roster.Add(p);
        }

        Accepted?.Invoke(id);
        foreach (PlayerState p in roster) PlayerJoined?.Invoke(p);
    }

    private void HandleRejected(PacketReader r)
    {
        string reason = r.ReadString();
        RejectReason = reason;
        Rejected?.Invoke(reason);
    }

    private void HandlePlayerJoined(PacketReader r)
    {
        PlayerState p = PresenceCodec.ReadPlayer(r);
        if (LocalId.HasValue && p.Id == LocalId.Value) return; // never mirror self
        _players[p.Id] = p;
        PlayerJoined?.Invoke(p);
    }

    private void HandlePlayerLeft(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        if (_players.Remove(id)) PlayerLeft?.Invoke(id);
    }

    private void HandlePlayerPose(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        Pose pose = PresenceCodec.ReadPose(r);
        if (LocalId.HasValue && id == LocalId.Value) return; // ignore an echo of our own pose
        if (_players.TryGetValue(id, out PlayerState? p))
        {
            p.Pose = pose;
            PlayerMoved?.Invoke(id, pose);
        }
    }

    private void HandleTimeSync(PacketReader r)
    {
        long serverTime = r.ReadInt64();
        ServerTimeOffsetMs = serverTime - _clock.NowMs;
    }

    /// <summary>Hide a replica that left our relevance scope (D10). Burst 1 gates only players — an
    /// Item/Trainset hide is reserved for Burst 2 (routed to Items/Trains then), so its id is read past
    /// and ignored here for now. The roster entry is kept: a later pose re-shows the avatar.</summary>
    private void HandleInterestHide(PacketReader r)
    {
        var kind = (EntityKind)r.ReadByte();
        int id = (int)r.ReadVarUInt();
        if (kind == EntityKind.Player && _players.ContainsKey(id)) PlayerHidden?.Invoke(id);
    }

    public void Dispose()
    {
        _transport.Received -= OnReceived;
        _transport.PeerConnected -= OnConnected;
        _transport.PeerDisconnected -= OnDisconnected;
        _players.Clear();
    }
}
