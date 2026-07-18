using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>
/// The authoritative session server (03 §1 — server owns truth). Runs headless in the dedicated
/// process and, in host-mode, in the host's game connected over Loopback (host = client #1). It
/// admits players after the handshake (03 §10), keeps the roster, relays pose, and evicts on
/// disconnect. It touches nothing game-specific, so the whole join lifecycle is fuzzed over Loopback
/// with no game running (03 §11, hard rule 8).
/// </summary>
public sealed class NetServer : IDisposable
{
    private readonly ITransport _transport;
    private readonly ServerConfig _config;
    private readonly IClock _clock;
    private readonly Dictionary<int, PlayerState> _players = new();

    public NetServer(ITransport transport, ServerConfig config, IClock clock, ServerSaveData? restore = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        Trains = new ServerTrains(_transport, _clock, () => _players.Keys);
        Career = new ServerCareer(_transport, _clock, _config.Career, () => _players.Keys, restore?.Career);
        if (restore != null) Trains.Restore(restore.Trains);

        _transport.Received += OnReceived;
        _transport.PeerDisconnected += OnPeerDisconnected;
    }

    /// <summary>The current roster, keyed by player id. Read-only view for the host UI / tests.</summary>
    public IReadOnlyDictionary<int, PlayerState> Players => _players;

    /// <summary>The train subsystem (M2): authoritative trainsets, junctions, turntables, grants.</summary>
    public ServerTrains Trains { get; }

    /// <summary>The career subsystem (M3): jobs, wallets, licenses, reconnect grace.</summary>
    public ServerCareer Career { get; }

    public int PlayerCount => _players.Count;

    /// <summary>Raised when a player passes the handshake and is added to the roster.</summary>
    public event Action<PlayerState>? PlayerAdmitted;

    /// <summary>Raised (with the player id) after a player is removed and PlayerLeft has been broadcast.</summary>
    public event Action<int>? PlayerRemoved;

    /// <summary>Pump the transport, then advance time-driven career state (claim TTLs, grace
    /// expiries, board refill) — cheap when nothing is due.</summary>
    public void Poll()
    {
        _transport.Poll();
        Career.Tick();
    }

    /// <summary>Snapshot everything persistence v1 stores (03 §7): career + world. Serialize with
    /// SaveCodec; restore by passing the result to the constructor of the next server.</summary>
    public ServerSaveData CaptureSave() => new(Career.Registry.Capture(), Trains.Capture());

    /// <summary>Push the authoritative clock to every admitted player (call on a slow cadence).</summary>
    public void BroadcastTime()
    {
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.TimeSync)
            .WriteInt64(_clock.NowMs)
            .ToArray();
        foreach (int id in _players.Keys)
            _transport.Send(id, payload, DeliveryMethod.ReliableUnordered);
    }

    private void OnReceived(int peerId, byte[] payload)
    {
        var r = new PacketReader(payload);
        MessageType type;
        try { type = (MessageType)r.ReadByte(); }
        catch { return; } // empty packet — ignore

        try
        {
            switch (type)
            {
                case MessageType.JoinRequest: HandleJoin(peerId, r); break;
                case MessageType.PlayerPose: HandlePose(peerId, r); break;
                case MessageType.Leave: Remove(peerId); break;
                default:
                    // Subsystem traffic is only heard from ADMITTED peers — everything else is ignored.
                    if (_players.ContainsKey(peerId) && !Trains.TryHandle(peerId, type, r, payload))
                        Career.TryHandle(peerId, type, r);
                    break;
            }
        }
        catch (Exception)
        {
            // Malformed message from a peer must never crash the server (03 §9). Drop it; a future
            // hardening pass can disconnect repeat offenders once rate-limiting lands.
        }
    }

    private void HandleJoin(int peerId, PacketReader r)
    {
        if (_players.ContainsKey(peerId)) return; // duplicate join on an admitted peer — ignore

        int protocol = (int)r.ReadVarUInt();
        string build = r.ReadString();
        string modVersion = r.ReadString();
        string modListHash = r.ReadString();
        string password = r.ReadString();
        string name = r.ReadString();
        // v3 field, read defensively: a v2 client's packet ends here, and it deserves the exact
        // "protocol mismatch" reject below rather than a silent drop on a truncated read.
        string playerKey = r.AtEnd ? string.Empty : r.ReadString();

        var client = new HandshakeRequest(protocol, build, modVersion, modListHash);
        HandshakeResult compat = VersionHandshake.Check(client, _config.Expected);
        if (!compat.Compatible) { Reject(peerId, compat.Reason ?? "incompatible"); return; }

        if (!string.IsNullOrEmpty(_config.Password) &&
            !string.Equals(password, _config.Password, StringComparison.Ordinal))
        {
            Reject(peerId, "incorrect password");
            return;
        }

        if (_players.Count >= _config.MaxPlayers) { Reject(peerId, "server full"); return; }

        // The stable key is the profile/reconnect identity (M3): it must exist, stay out of the
        // reserved '@' account namespace, and not already be online (a live duplicate would let a
        // second connection hijack the profile mid-session).
        if (playerKey.Length == 0 || playerKey.Length > 64 || playerKey[0] == '@')
        {
            Reject(peerId, "invalid player key");
            return;
        }
        if (Career.IsKeyOnline(playerKey)) { Reject(peerId, "player key already in session"); return; }

        var state = new PlayerState(peerId, name, Pose.Identity);
        _players[peerId] = state;

        SendAccepted(peerId, state);                       // newcomer learns id + time + roster
        BroadcastPlayerJoined(state, exceptPeer: peerId);  // everyone else learns the newcomer
        Trains.OnPlayerAdmitted(peerId);                   // world burst: trainsets/junctions/grants
        Career.OnPlayerAdmitted(peerId, playerKey, name);  // career burst: your career + the board
        PlayerAdmitted?.Invoke(state);
    }

    private void SendAccepted(int peerId, PlayerState newcomer)
    {
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.JoinAccepted)
            .WriteVarUInt((uint)peerId)     // your assigned id
            .WriteInt64(_clock.NowMs);      // server time, for the client's offset

        // Roster excluding the newcomer (they already know themselves).
        var others = new List<PlayerState>(_players.Count);
        foreach (var kv in _players)
            if (kv.Key != peerId) others.Add(kv.Value);

        w.WriteVarUInt((uint)others.Count);
        foreach (var p in others) PresenceCodec.WritePlayer(w, p);

        _transport.Send(peerId, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    private void Reject(int peerId, string reason)
    {
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.JoinRejected)
            .WriteString(reason)
            .ToArray();
        _transport.Send(peerId, payload, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastPlayerJoined(PlayerState state, int exceptPeer)
    {
        var w = new PacketWriter(48).WriteByte((byte)MessageType.PlayerJoined);
        PresenceCodec.WritePlayer(w, state);
        byte[] payload = w.ToArray();
        foreach (int id in _players.Keys)
            if (id != exceptPeer) _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }

    private void HandlePose(int peerId, PacketReader r)
    {
        if (!_players.TryGetValue(peerId, out PlayerState? state)) return; // pose before join — ignore

        _ = r.ReadVarUInt();               // client-supplied id — ignored; server stamps its own (authority)
        Pose pose = PresenceCodec.ReadPose(r);
        state.Pose = pose;

        var w = new PacketWriter(40)
            .WriteByte((byte)MessageType.PlayerPose)
            .WriteVarUInt((uint)peerId);   // authoritative id
        PresenceCodec.WritePose(w, pose);
        byte[] payload = w.ToArray();
        foreach (int id in _players.Keys)
            if (id != peerId) _transport.Send(id, payload, DeliveryMethod.SequencedUnreliable);
    }

    private void OnPeerDisconnected(int peerId) => Remove(peerId);

    private void Remove(int peerId)
    {
        if (!_players.Remove(peerId)) return; // never joined, or already removed — no double broadcast

        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.PlayerLeft)
            .WriteVarUInt((uint)peerId)
            .ToArray();
        foreach (int id in _players.Keys)
            _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);

        Trains.OnPlayerRemoved(peerId);                    // park their consists, free their grants
        Career.OnPlayerRemoved(peerId);                    // start their reconnect-grace hold
        PlayerRemoved?.Invoke(peerId);
    }

    public void Dispose()
    {
        _transport.Received -= OnReceived;
        _transport.PeerDisconnected -= OnPeerDisconnected;
        _players.Clear();
    }
}
