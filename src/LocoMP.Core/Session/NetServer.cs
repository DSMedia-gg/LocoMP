using System;
using System.Collections.Generic;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Trains;
using LocoMP.Core.World;

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
    private readonly ServerModeration _moderation = new();
    private readonly ChatPolicy _chat;

    // The admission queue (D18): joiners who passed EVERY validation but capacity, in arrival order.
    // A queued peer is connected but NOT admitted — no roster entry, no subsystem state, its non-join
    // traffic ignored like any stranger's. Abandon = its transport dropping (or a Leave); admission =
    // the ordinary admit path when a slot frees. Bounded so a connect-storm can't grow server state
    // without limit — beyond the cap the instant ServerFull reject remains.
    private readonly List<QueuedJoin> _queue = new();
    private const int MaxQueuedJoins = 32;

    private sealed class QueuedJoin
    {
        public QueuedJoin(int peerId, string name, string playerKey)
        {
            PeerId = peerId;
            Name = name;
            PlayerKey = playerKey;
        }

        public int PeerId { get; set; } // set: a same-key reconnect replaces the peer, keeps the spot
        public string Name { get; set; }
        public string PlayerKey { get; }
    }

    // Peers that have sent at least one real pose. Until a peer is here its position is unknown, so
    // interest treats it fail-open (an unposed observer sees everything; an unposed player-entity is
    // never hidden). Cleared on disconnect.
    private readonly HashSet<int> _posed = new();
    private readonly InterestManager _interest;
    private readonly WorldTopology? _topology;
    private long _lastInterestMs;

    /// <param name="topology">The extracted rail network (D10 Burst 2). Supplied, and carrying world
    /// geometry (a codec-v2 <c>.lmpw</c>), it lets the server place railed trains in the world and gate
    /// their snapshot streams by relevance — the ~96% of steady-state bandwidth the perf baseline
    /// measured. Null or geometry-free ⇒ train interest is suppressed and behaviour is unchanged.</param>
    public NetServer(ITransport transport, ServerConfig config, IClock clock, ServerSaveData? restore = null,
                     WorldTopology? topology = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _topology = topology;
        _chat = new ChatPolicy(_clock);

        Func<int, Pose?> poseOf = id => _players.TryGetValue(id, out PlayerState? p) ? p.Pose : (Pose?)null;
        WorldTime = new WorldClock(_clock);
        Trains = new ServerTrains(_transport, _clock, () => _players.Keys);
        Career = new ServerCareer(_transport, _clock, _config.Career, () => _players.Keys, restore?.Career, poseOf);
        Trains.BindCareer(Career, _config.CommsFees); // parked-set comms fees bill the initiator (R4-M)
        // R5-10: comms proposal refusals must reach the PLAYER, not just a server-side event —
        // Round 5 found this event had no subscriber, so every refusal died silently while the
        // initiator kept paying/retrying. CareerRejected is the wire the client already surfaces.
        Trains.ProposalRejected += (peer, why) => Career.RejectExternal(peer, why);
        Items = new ServerItems(_transport, () => _players.Keys, _config.Items, poseOf, Career, restore?.Items);
        if (restore != null) Trains.Restore(restore.Trains);

        // Interest management (D10). Its observer position is pose-gated (fail-open until a peer moves),
        // separate from the career/item proximity poseOf above; enter/leave turn into pose gating +
        // hide packets here. Burst 1 gated players; Burst 2 adds railed trains via TrainEntities.
        _interest = new InterestManager(
            _config.Interest,
            () => _players.Keys,
            id => (_posed.Contains(id) && _players.TryGetValue(id, out PlayerState? p))
                ? (p.Pose.Px, p.Pose.Pz)
                : ((float, float)?)null,
            OnInterestEnter,
            OnInterestLeave,
            TrainEntities);

        // With NO placeable geometry anywhere a train has no anchor, so the filter would hide every
        // consist rather than the distant ones — fail open loudly instead (D10 Burst 2). Partial
        // geometry is fine (F4): a train on a bare edge simply isn't yielded by TrainEntities, so
        // it never lands in _placed and IsRelevant fails open to everyone for that one set.
        if (_topology is null || !_topology.HasGeometry) _interest.SuppressKind(EntityKind.Trainset);
        Trains.BindInterest(_interest);

        _transport.Received += OnReceived;
        _transport.PeerDisconnected += OnPeerDisconnected;
    }

    /// <summary>The current roster, keyed by player id. Read-only view for the host UI / tests.</summary>
    public IReadOnlyDictionary<int, PlayerState> Players => _players;

    /// <summary>The authoritative world time-of-day (v18, 02 §3). Host-embedded: anchored by the
    /// world source's reports. Dedicated: anchor it once at startup via <see cref="CommitWorldTime"/>
    /// and it flows. Frontends restate it on the <see cref="BroadcastTime"/> cadence.</summary>
    public WorldClock WorldTime { get; }

    /// <summary>The train subsystem (M2): authoritative trainsets, junctions, turntables, grants.</summary>
    public ServerTrains Trains { get; }

    /// <summary>The career subsystem (M3): jobs, wallets, licenses, reconnect grace.</summary>
    public ServerCareer Career { get; }

    /// <summary>The item subsystem (M4): world items, per-player inventory, shop purchases.</summary>
    public ServerItems Items { get; }

    /// <summary>The interest-management subsystem (D10): per-client spatial relevance. Exposed for the
    /// host UI, admin, and tests. Inert unless <see cref="ServerConfig.Interest"/> is enabled.</summary>
    public InterestManager Interest => _interest;

    /// <summary>Session moderation (M5.2): admin roles, session bans, pause-joins. The host UI and the
    /// dedicated-server console read/drive this; the join gate and admin-action handler enforce it.</summary>
    public ServerModeration Moderation => _moderation;

    public int PlayerCount => _players.Count;

    /// <summary>How many validated joiners are waiting for a slot (D18) — host UI / test visibility.</summary>
    public int QueuedCount => _queue.Count;

    /// <summary>Raised when a player passes the handshake and is added to the roster.</summary>
    public event Action<PlayerState>? PlayerAdmitted;

    /// <summary>Raised (with the player id) after a player is removed and PlayerLeft has been broadcast.</summary>
    public event Action<int>? PlayerRemoved;

    /// <summary>Raised with the player key when a pristine profile is evicted at grace lapse (D20) —
    /// host UI / server-console visibility; the eviction itself is invisible to the player.</summary>
    public event Action<string>? ProfileEvicted;

    /// <summary>Raised when an admin requests "Save now" (M5.2). The server has already authorised it; the
    /// subscriber (host game / dedicated-server process) performs the actual save — <see cref="CaptureSave"/>
    /// + write — since the file IO lives outside game-free Core.</summary>
    public event Action? SaveRequested;

    /// <summary>Raised with the new interval in SECONDS when the owner retunes the autosave cadence
    /// (M5.2 session control). Already authorised + validated; the host / dedicated process applies it
    /// to its <c>Autosaver</c> — the file IO layer stays outside game-free Core.</summary>
    public event Action<int>? AutosaveIntervalRequested;

    /// <summary>Change the session password mid-session (M5.2 session control; host UI calls this
    /// directly, a remote owner arrives via <see cref="AdminActionKind.SetPassword"/>). Empty/null =
    /// open. Present players are untouched — only future joins check it. The broadcast never carries
    /// the password itself.</summary>
    public void SetSessionPassword(string? password)
    {
        _config.OverridePassword(string.IsNullOrEmpty(password) ? null : password);
        BroadcastAdminNotice(AdminNoticeKind.SettingChanged,
            string.IsNullOrEmpty(password) ? "session password removed" : "session password changed");
    }

    /// <summary>Change the player cap live (M5.2 session control). Raising it pumps the admission
    /// queue immediately (D18 waiters admit without re-joining); lowering it never evicts — present
    /// players stay, only new admissions are held. False = out of range (1..99).</summary>
    public bool SetMaxPlayers(int maxPlayers)
    {
        if (maxPlayers < 1 || maxPlayers > 99) return false;
        bool raised = maxPlayers > _config.MaxPlayers;
        _config.OverrideMaxPlayers(maxPlayers);
        BroadcastAdminNotice(AdminNoticeKind.SettingChanged, $"max players: {maxPlayers}");
        if (raised) PumpQueue();
        return true;
    }

    /// <summary>Retune the autosave cadence (M5.2 session control): validate, announce, and raise
    /// <see cref="AutosaveIntervalRequested"/> for the owning process. False = out of range
    /// (5 s..1 h — a sub-5 s autosave is a disk-thrash foot-gun, not a setting).</summary>
    public bool SetAutosaveIntervalSeconds(int seconds)
    {
        if (seconds < 5 || seconds > 3600) return false;
        AutosaveIntervalRequested?.Invoke(seconds);
        BroadcastAdminNotice(AdminNoticeKind.SettingChanged, $"autosave every {seconds}s");
        return true;
    }

    /// <summary>Pump the transport, then advance time-driven career state (claim TTLs, grace
    /// expiries, board refill) — cheap when nothing is due.</summary>
    public void Poll()
    {
        _transport.Poll();
        CareerTick tick = Career.Tick();
        // Grace has ONE clock (the career's); a lapse fans out to every subsystem holding state
        // for the departed player — their minted item possessions release to the world. The
        // possession check runs BEFORE the release (D20: a holder of anything is not pristine;
        // afterwards everyone holds nothing), then a pristine profile evicts — invisible to the
        // player, and the only thing standing between join churn and unbounded heap + save growth.
        foreach (string key in tick.LapsedPlayers)
        {
            bool possessed = Items.HasPossessions(key);
            Items.OnGraceLapsed(key);
            if (!possessed && Career.Registry.TryEvictPristine(key))
                ProfileEvicted?.Invoke(key);
        }

        // Re-evaluate spatial relevance as players move (D10) — throttled, and a no-op while disabled.
        // The hot relay reads the cached relevance set, so recomputing a few times a second is ample.
        if (_config.Interest.Enabled)
        {
            long now = _clock.NowMs;
            if (now - _lastInterestMs >= _config.Interest.RecomputeIntervalMs)
            {
                _lastInterestMs = now;
                _interest.Recompute();
            }
        }
    }

    /// <summary>Snapshot everything persistence v1 stores (03 §7): career + world. Serialize with
    /// SaveCodec; restore by passing the result to the constructor of the next server.</summary>
    public ServerSaveData CaptureSave() => new(Career.Registry.Capture(), Trains.Capture(), Items.Capture());

    /// <summary>A cheap health snapshot for the M5.2 Diagnostics panel — the counters the server already
    /// tracks, aggregated. Bandwidth / per-peer latency are transport-level and not included here (see
    /// <see cref="ServerDiagnostics"/>).</summary>
    public ServerDiagnostics CaptureDiagnostics()
    {
        TransportStats t = _transport.Stats;
        return new(
            _players.Count,
            _queue.Count,
            Trains.Registry.Sets.Count,
            Career.Registry.Jobs.Count,
            Items.Registry.Items.Count,
            Trains.StaleSnapshotsDropped,
            Career.Registry.Ledger.ConservationHolds,
            Items.Registry.ItemConservationHolds,
            _moderation.JoinsPaused,
            _moderation.Admins.Count,
            _moderation.BannedKeys.Count,
            _config.Interest.Enabled,
            t.BytesSent, t.BytesReceived, t.MessagesSent, t.MessagesReceived);
    }

    /// <summary>Round-trip time to a player in ms, or null if unknown (M5.2 — the roster's per-player ping).
    /// Live and per-peer, so it is NOT part of the <see cref="CaptureDiagnostics"/> snapshot.</summary>
    public int? RttMs(int peerId) => _transport.RttMs(peerId);

    /// <summary>A player's session role (M5.2 roster) — owner beats admin beats player. Resolved through
    /// the peer's stable key into the moderation state; only this peer-id projection ever goes on the
    /// wire (keys are client-held secrets). A peer without a key (never admitted) reads Player.</summary>
    public PlayerRole RoleOf(int peerId)
    {
        string? key = Career.KeyOf(peerId);
        if (key is null) return PlayerRole.Player;
        if (_moderation.IsOwner(key)) return PlayerRole.Owner;
        return _moderation.IsAdmin(key) ? PlayerRole.Admin : PlayerRole.Player;
    }

    /// <summary>Push the live roster status — every admitted player's role + measured ping, self
    /// included — to every admitted player (M5.2: the player list's roles/latency column). Full-state
    /// and idempotent: the server sends it in the join burst and on role changes, the frontends call it
    /// on a slow cadence for the ping refresh (the TimeSync tick is fine), and clients dedupe
    /// no-change restatements.</summary>
    public void BroadcastRoster()
    {
        if (_players.Count == 0) return;
        var w = new PacketWriter(8 + _players.Count * 8)
            .WriteByte((byte)MessageType.RosterStatus)
            .WriteVarUInt((uint)_players.Count);
        foreach (int id in _players.Keys)
            PresenceCodec.WriteStatus(w, new PlayerStatus(id, RoleOf(id), RttMs(id)));
        byte[] payload = w.ToArray();
        foreach (int id in _players.Keys)
            _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Every chat line the server commits — player messages and system events alike (M5.4).
    /// The dedicated-server console logs from this; sender-only service notices (the rate-limit
    /// warning) are private and never raised here.</summary>
    public event Action<ChatEntry>? Chat;

    /// <summary>Say something as THE SERVER (M5.4 — the dedicated console's <c>say</c>; a host talks
    /// as a player through their own client instead). Sanitised like player chat but never
    /// rate-limited; empty after sanitisation is a no-op.</summary>
    public void BroadcastServerChat(string text)
    {
        string clean = ChatPolicy.Sanitize(text);
        if (clean.Length == 0) return;
        BroadcastChat(new ChatEntry(ChatMessageKind.Server, 0, string.Empty, clean));
    }

    /// <summary>Commit one chat line: broadcast to every admitted player (minus an optional
    /// exception) and raise <see cref="Chat"/> for the server process's own log.</summary>
    private void BroadcastChat(ChatEntry entry, int exceptPeer = 0)
    {
        byte[] payload = BuildChatMessage(entry);
        foreach (int id in _players.Keys)
            if (id != exceptPeer) _transport.Send(id, payload, DeliveryMethod.ReliableUnordered);
        Chat?.Invoke(entry);
    }

    private static byte[] BuildChatMessage(ChatEntry entry) => new PacketWriter(32)
        .WriteByte((byte)MessageType.ChatMessage)
        .WriteByte((byte)entry.Kind)
        .WriteVarUInt((uint)entry.SenderId)
        .WriteString(entry.SenderName)
        .WriteString(entry.Text)
        .ToArray();

    /// <summary>A player's chat line (M5.4). Sanitise, charge the sender's bucket, stamp the sender
    /// authoritatively, and echo to EVERYONE — the sender renders from the echo, so what they see is
    /// exactly what was committed (truncation included). A refused line dies silently except for a
    /// cooldown-gated warning to the sender alone.</summary>
    private void HandleChatSend(int peerId, PacketReader r)
    {
        if (!_players.TryGetValue(peerId, out PlayerState? sender)) return;
        string clean = ChatPolicy.Sanitize(r.ReadString());
        if (clean.Length == 0) return;

        if (!_chat.TryCharge(peerId, out bool warn))
        {
            if (warn)
                _transport.Send(peerId, BuildChatMessage(new ChatEntry(
                    ChatMessageKind.Server, 0, string.Empty, "you're sending messages too quickly")),
                    DeliveryMethod.ReliableUnordered);
            return;
        }

        BroadcastChat(new ChatEntry(ChatMessageKind.Player, peerId, sender.Name, clean));
    }

    /// <summary>Announce a clean session end (M5.2 Save &amp; Stop / dedicated shutdown) to every
    /// admitted AND queued peer, so their UI can say "the host ended the session" instead of inferring
    /// a dead link. Announce-only by design: state teardown stays with the caller's ordinary
    /// save-then-dispose path (saving FIRST keeps the final save identical to a plain leave), and the
    /// links are deliberately left up so the notice can outrun the transport teardown — dispose the
    /// transport a moment later to actually drop them.</summary>
    public void AnnounceSessionEnd(string reason)
    {
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.AdminNotice)
            .WriteByte((byte)AdminNoticeKind.SessionEnded)
            .WriteString(reason ?? string.Empty)
            .ToArray();
        foreach (int id in _players.Keys) _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
        foreach (QueuedJoin q in _queue) _transport.Send(q.PeerId, payload, DeliveryMethod.ReliableOrdered);
    }

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

    /// <summary>Is the shared world paused (D19 — the host's native ESC pause, made a session
    /// state)? Always false on a dedicated server: nothing ever calls the setter there.</summary>
    public bool WorldPaused { get; private set; }

    /// <summary>D19: the HOST process reports its native pause state (host-embedded mode only — a
    /// dedicated server has no host to pause and never calls this). The world source's sim is the
    /// authoritative stream, so its pause becomes an acknowledged session state: every peer gets a
    /// WorldPauseState (a joiner mid-pause gets it in the join burst), and the world CLOCK freezes
    /// with it — a paused host's sky stops, so the flowing restatement must stop too.</summary>
    public void SetWorldPaused(bool paused, string reason)
    {
        if (WorldPaused == paused) return;
        WorldPaused = paused;
        _worldPauseReason = reason ?? string.Empty;
        if (paused) WorldTime.Freeze();
        else WorldTime.Unfreeze();
        Career.Registry.JobClockPaused = paused; // D23: bonus windows freeze with the world
        byte[] payload = BuildWorldPauseState();
        foreach (int id in _players.Keys)
            _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }

    private string _worldPauseReason = string.Empty;

    private byte[] BuildWorldPauseState() => new PacketWriter(16)
        .WriteByte((byte)MessageType.WorldPauseState)
        .WriteByte(WorldPaused ? (byte)1 : (byte)0)
        .WriteString(_worldPauseReason)
        .ToArray();

    /// <summary>Anchor the world time-of-day from the SERVER PROCESS (a dedicated server's startup /
    /// console — the world-source report path is <see cref="MessageType.WorldTimeReport"/>) and push
    /// it to everyone. Returns false when the values are rejected (non-positive day length).</summary>
    public bool CommitWorldTime(double oaDate, float dayLengthMinutes)
    {
        if (!WorldTime.Set(oaDate, dayLengthMinutes)) return false;
        BroadcastWorldTime();
        return true;
    }

    /// <summary>Restate the current world time to every admitted player (v18 — call on the
    /// <see cref="BroadcastTime"/> cadence). The clock FLOWS between anchors, so a restatement is
    /// always current; a no-op until something has anchored it.</summary>
    public void BroadcastWorldTime()
    {
        if (!WorldTime.HasValue) return;
        byte[] payload = BuildWorldTimeState();
        foreach (int id in _players.Keys)
            _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>The world source reported its sky (heartbeat or a jump — sleep/fast-travel). Only the
    /// world source is believed: any other peer's local skip must NOT move the shared sun — their sky
    /// snaps back on the next restatement instead (02 §3: skips are host/server-approved).</summary>
    private void HandleWorldTimeReport(int peerId, PacketReader r)
    {
        double oaDate = r.ReadDouble();
        float dayLengthMinutes = r.ReadSingle();
        if (peerId != Career.WorldSourcePeer) return;
        if (!WorldTime.Set(oaDate, dayLengthMinutes)) return;
        byte[] payload = BuildWorldTimeState();
        foreach (int id in _players.Keys)
            if (id != peerId) _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }

    private byte[] BuildWorldTimeState() => new PacketWriter(16)
        .WriteByte((byte)MessageType.WorldTimeState)
        .WriteDouble(WorldTime.CurrentOa)
        .WriteSingle(WorldTime.DayLengthMinutes)
        .ToArray();

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
                case MessageType.AdminAction:
                    if (_players.ContainsKey(peerId)) HandleAdminAction(peerId, r);
                    break;
                case MessageType.WorldTimeReport:
                    if (_players.ContainsKey(peerId)) HandleWorldTimeReport(peerId, r);
                    break;
                case MessageType.ChatSend: HandleChatSend(peerId, r); break;
                default:
                    // Subsystem traffic is only heard from ADMITTED peers — everything else is
                    // ignored. Try each subsystem in turn; the first to claim the type wins.
                    if (_players.ContainsKey(peerId) &&
                        !Trains.TryHandle(peerId, type, r, payload) &&
                        !Career.TryHandle(peerId, type, r))
                        Items.TryHandle(peerId, type, r);
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
        if (FindQueued(peerId) is QueuedJoin already)
        {
            SendQueued(already);                  // duplicate join from a queued peer — restate position
            return;
        }

        // FROZEN prefix (F3): the connect key no longer embeds the protocol version, so peers on
        // ANY protocol reach this read. The varuint protocol version must stay the first field
        // forever — it is what lets a mismatched peer be rejected BY NAME instead of timing out.
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
        if (!compat.Compatible)
        {
            Reject(peerId, compat.Kind, compat.Reason ?? "incompatible", compat.ClientHas, compat.ServerNeeds);
            return;
        }

        if (!string.IsNullOrEmpty(_config.Password) &&
            !string.Equals(password, _config.Password, StringComparison.Ordinal))
        {
            Reject(peerId, RejectKind.Password, "incorrect password");
            return;
        }

        // The stable key is the profile/reconnect identity (M3): it must exist and stay out of the
        // reserved '@' account namespace. Validated BEFORE capacity (D18): a joiner who could never
        // be admitted deserves the exact reject now, not a queue slot that resolves to it later.
        if (playerKey.Length == 0 || playerKey.Length > 64 || playerKey[0] == '@')
        {
            Reject(peerId, RejectKind.InvalidKey, "invalid player key");
            return;
        }

        // Session ban (M5.2): the key is the credential (F7), so a ban keyed on it holds across a
        // reconnect within the session. Checked BEFORE takeover/capacity — a banned key never evicts a
        // live peer, never takes a slot; it is refused at the door. The owner can never be banned, so
        // the host can't lock itself out. (Session-scoped, U3: the ban dies with the server.)
        if (_moderation.IsBanned(playerKey))
        {
            Reject(peerId, RejectKind.Banned, "you are banned from this session");
            return;
        }

        // Persistent ban (M5.5, U3): keyed on the link's authenticated SteamId64 — the identity a
        // player cannot re-roll, unlike the key. Only identity-bearing links (the Steam relay) can
        // match; UDP/Loopback peers pass exactly as before. The relay's admission door refuses most
        // banned ids before the socket even opens — this gate covers the peer that connected before
        // the ban landed and is now re-presenting a join.
        if (_config.BanStore != null && _transport is IPeerIdentity identityLink &&
            identityLink.IdentityOf(peerId) is ulong joinSteamId && _config.BanStore.IsBanned(joinSteamId))
        {
            Reject(peerId, RejectKind.Banned, "you are banned from this server");
            return;
        }

        // A reconnect of a player already ONLINE is a takeover, not a new join (used by the pause gate
        // below). Captured before the takeover block evicts the zombie and clears the online flag.
        bool keyWasOnline = Career.IsKeyOnline(playerKey);

        // Credentialed takeover (F7): the key is a client-held secret (never shown to other players)
        // and the password was just checked, so presenting a key that is ALREADY ONLINE is this
        // player reconnecting past their own zombie peer — evict it through the ordinary removal
        // path (its state releases exactly as a transport timeout would have, just now) and let the
        // newcomer take the freed slot. The old lockout ("player key already in session") made a
        // fast reconnect wait out the transport timeout, 15 s to minutes in the gauntlet.
        if (keyWasOnline)
        {
            int zombie = Career.PeerOf(playerKey);
            if (zombie == 0)
            {
                // Online with no live peer mapping should not happen (grace removes the online
                // flag) — defensive: nothing to evict, fall through to the normal admit.
            }
            else if (zombie == peerId)
            {
                return; // raced our own admission — never evict the very connection joining
            }
            else
            {
                // pump: false — the freed slot is a HANDOVER to this reconnecting player, not a
                // vacancy for the queue; the capacity check below admits them into it. No farewell
                // line: the player never left, their zombie link did.
                Remove(zombie, pump: false, farewell: null);
                _transport.Disconnect(zombie);
            }
        }
        // A same-key entry already WAITING keeps its place in line: replace the dead connection
        // behind the entry (a flaky joiner should not go to the back for reconnecting).
        if (FindQueuedByKey(playerKey) is QueuedJoin waiting)
        {
            int stale = waiting.PeerId;
            waiting.PeerId = peerId;
            waiting.Name = name;
            _transport.Disconnect(stale);
            SendQueued(waiting);
            return;
        }

        // Pause-new-joins (M5.2): a host "hold the door" toggle. Exempts a player already represented in
        // the session — a same-key-queued reconnect returned above, and an online takeover (keyWasOnline)
        // is re-entry, not a new admission — so only a genuinely NEW joiner is refused, and it's a
        // transient state, not a rejection of the player.
        if (_moderation.JoinsPaused && !keyWasOnline)
        {
            Reject(peerId, RejectKind.JoinsPaused, "the host has paused new joins");
            return;
        }

        if (_players.Count >= _config.MaxPlayers)
        {
            // The admission queue (D18): validated joiners wait for a slot instead of bouncing off
            // an instant reject; the loading cover shows the live position. Bounded — past the cap
            // the old behaviour remains.
            if (_queue.Count >= MaxQueuedJoins)
            {
                Reject(peerId, RejectKind.ServerFull, "server full");
                return;
            }
            var entry = new QueuedJoin(peerId, name, playerKey);
            _queue.Add(entry);
            // Everyone in line, not just the newcomer: positions are unchanged for the others but
            // their TOTAL grew, and a queued cover showing "1 of 1" while someone waits behind is
            // stale UI (Round 2 live finding — the remove/pump paths already re-broadcast all).
            foreach (QueuedJoin q in _queue) SendQueued(q);
            return;
        }

        Admit(peerId, name, playerKey);
    }

    /// <summary>The admit path proper — everything after validation: roster, interest, the join
    /// burst, the sentinel. One code path whether the joiner walked straight in or waited (D18).</summary>
    private void Admit(int peerId, string name, string playerKey)
    {
        var state = new PlayerState(peerId, name, Pose.Identity);
        _players[peerId] = state;
        _moderation.EnsureOwner(playerKey);                // first admit = session owner/admin (M5.2)
        _interest.AddClient(peerId);                       // relevance tracking (fail-open until it moves)

        SendAccepted(peerId, state);                       // newcomer learns id + time + roster
        BroadcastPlayerJoined(state, exceptPeer: peerId);  // everyone else learns the newcomer
        Trains.OnPlayerAdmitted(peerId);                   // world burst: trainsets/junctions/grants
        if (WorldTime.HasValue)                            // the shared sun (v18) rides the world burst
            _transport.Send(peerId, BuildWorldTimeState(), DeliveryMethod.ReliableOrdered);
        if (WorldPaused)                                   // a joiner mid-pause must freeze too (D19)
            _transport.Send(peerId, BuildWorldPauseState(), DeliveryMethod.ReliableOrdered);
        Career.OnPlayerAdmitted(peerId, playerKey, name);  // career burst: your career + the board
        Items.OnPlayerAdmitted(peerId);                    // item burst: AFTER career maps peer↔key
        BroadcastChat(new ChatEntry(ChatMessageKind.Joined, peerId, name, string.Empty),
            exceptPeer: peerId);                           // system feed: everyone else's chat log (M5.4)
        BroadcastRoster();                                 // roles+ping ride the burst for the newcomer
                                                           // AND update everyone else's player list
        SendJoinBurstComplete(peerId);                     // MUST be the last send of the burst — the
                                                           // ordered channel makes it a true barrier
        PlayerAdmitted?.Invoke(state);
    }

    private QueuedJoin? FindQueued(int peerId)
    {
        foreach (QueuedJoin q in _queue)
            if (q.PeerId == peerId) return q;
        return null;
    }

    private QueuedJoin? FindQueuedByKey(string playerKey)
    {
        foreach (QueuedJoin q in _queue)
            if (string.Equals(q.PlayerKey, playerKey, StringComparison.Ordinal)) return q;
        return null;
    }

    private void SendQueued(QueuedJoin entry)
    {
        byte[] payload = new PacketWriter(4)
            .WriteByte((byte)MessageType.JoinQueued)
            .WriteVarUInt((uint)(_queue.IndexOf(entry) + 1)) // 1-based position
            .WriteVarUInt((uint)_queue.Count)
            .ToArray();
        _transport.Send(entry.PeerId, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Admit from the head while slots are free, then restate every waiter's position —
    /// called whenever a slot frees or the line shortens. Admission re-checks only what TIME can
    /// invalidate: the key coming online while waiting (someone took it over) turns that entry
    /// into the DuplicateKey reject it would have received at the door.</summary>
    private void PumpQueue()
    {
        bool changed = false;
        while (_queue.Count > 0 && _players.Count < _config.MaxPlayers)
        {
            QueuedJoin head = _queue[0];
            _queue.RemoveAt(0);
            changed = true;
            if (Career.IsKeyOnline(head.PlayerKey) && Career.PeerOf(head.PlayerKey) != 0)
            {
                Reject(head.PeerId, RejectKind.DuplicateKey, "player key already in session");
                continue;
            }
            Admit(head.PeerId, head.Name, head.PlayerKey);
        }
        if (changed)
            foreach (QueuedJoin q in _queue) SendQueued(q);
    }

    private void SendJoinBurstComplete(int peerId)
    {
        byte[] payload = new PacketWriter(1)
            .WriteByte((byte)MessageType.JoinBurstComplete)
            .ToArray();
        _transport.Send(peerId, payload, DeliveryMethod.ReliableOrdered);
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

    private void Reject(int peerId, RejectKind kind, string reason, string? clientHas = null, string? serverNeeds = null)
    {
        // The structured fields are APPENDED after the reason (v13): an older client reads the reason
        // and stops, so the reject stays human-readable across versions — the one message where
        // cross-version legibility matters, since it exists to explain a version mismatch.
        byte[] payload = new PacketWriter(48)
            .WriteByte((byte)MessageType.JoinRejected)
            .WriteString(reason)
            .WriteByte((byte)kind)
            .WriteString(clientHas ?? string.Empty)
            .WriteString(serverNeeds ?? string.Empty)
            .ToArray();
        _transport.Send(peerId, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>A host/admin moderation request (M5.2). Wire: [kind][targetPeerId][targetKey]. Authority
    /// is server-side: the sender must be an admin, the owner is immune to eviction/demotion, and
    /// promote/demote are owner-only. Peer-targeted verbs (kick/ban/promote/demote) name a live roster id;
    /// unban names an offline key. Every refusal comes back as an <see cref="AdminNoticeKind.Rejected"/>
    /// notice so the acting UI can explain it.</summary>
    private void HandleAdminAction(int peerId, PacketReader r)
    {
        var kind = (AdminActionKind)r.ReadByte();
        int targetPeerId = (int)r.ReadVarUInt();
        string targetKey = r.ReadString();

        string? senderKey = Career.KeyOf(peerId);
        if (senderKey is null || !_moderation.IsAdmin(senderKey))
        {
            SendAdminNotice(peerId, AdminNoticeKind.Rejected, "not allowed");
            return;
        }

        // A peer-targeted verb resolves its victim key from the live roster; a stranger/queued/absent
        // target has no key and the verb is refused by name.
        string? victimKey = targetPeerId != 0 && _players.ContainsKey(targetPeerId)
            ? Career.KeyOf(targetPeerId)
            : null;

        switch (kind)
        {
            case AdminActionKind.Kick:
            case AdminActionKind.Ban:
                if (victimKey is null) { SendAdminNotice(peerId, AdminNoticeKind.Rejected, "no such player"); return; }
                if (_moderation.IsOwner(victimKey)) { SendAdminNotice(peerId, AdminNoticeKind.Rejected, "cannot target the owner"); return; }
                // Record the ban BEFORE eviction so an instantaneous rejoin race is already refused.
                // The display name rides into the ban entry — it is what every unban surface lists (R4-A).
                if (kind == AdminActionKind.Ban)
                {
                    string victimName = _players.TryGetValue(targetPeerId, out PlayerState? victim) ? victim.Name : "";
                    _moderation.Ban(victimKey, victimName);
                    // U3 (M5.5): with an authenticated identity on the link, the ban also outlives the
                    // session — recorded against the SteamId64, surfaced as its own (persistent-range)
                    // entry beside the session one. Anonymous links get the session ban only.
                    if (_config.BanStore != null && _transport is IPeerIdentity banIdentity &&
                        banIdentity.IdentityOf(targetPeerId) is ulong victimSteamId)
                        _config.BanStore.Add(victimSteamId, victimName);
                }
                SendAdminNotice(targetPeerId,
                    kind == AdminActionKind.Ban ? AdminNoticeKind.Banned : AdminNoticeKind.Kicked, "");
                // A genuine vacancy: Remove pumps the queue (unlike an F7 handover). Then close the
                // socket. The farewell kind tells every bystander's chat WHY (M5.4) — only the server
                // knows; the wire's PlayerLeft is uniform.
                Remove(targetPeerId, farewell: kind == AdminActionKind.Ban
                    ? ChatMessageKind.Banned : ChatMessageKind.Kicked);
                _transport.Disconnect(targetPeerId);
                break;

            case AdminActionKind.Promote:
            case AdminActionKind.Demote:
                if (!_moderation.IsOwner(senderKey)) { SendAdminNotice(peerId, AdminNoticeKind.Rejected, "owner only"); return; }
                if (victimKey is null) { SendAdminNotice(peerId, AdminNoticeKind.Rejected, "no such player"); return; }
                bool changed = kind == AdminActionKind.Promote ? _moderation.Promote(victimKey) : _moderation.Demote(victimKey);
                if (changed)
                {
                    SendAdminNotice(targetPeerId, AdminNoticeKind.RoleChanged,
                        kind == AdminActionKind.Promote ? "admin" : "player");
                    BroadcastRoster();                     // everyone's player list shows the new badge
                }
                break;

            case AdminActionKind.PauseJoins:
            case AdminActionKind.ResumeJoins:
                bool pause = kind == AdminActionKind.PauseJoins;
                _moderation.JoinsPaused = pause;
                BroadcastAdminNotice(pause ? AdminNoticeKind.JoinsPaused : AdminNoticeKind.JoinsResumed, "");
                break;

            case AdminActionKind.Unban:
                // Offline target, named by its opaque ban-entry ID (v20, R4-A — keys never ride the
                // wire; the ban-list view only ever held ids+names). Session and persistent ids live
                // in disjoint ranges (BanStore.PersistentIdFloor), so the id routes itself. The
                // refreshed list goes straight back so the acting admin's view updates without a
                // second query.
                if (!Unban(targetPeerId))
                {
                    SendAdminNotice(peerId, AdminNoticeKind.Rejected, "no such ban");
                    return;
                }
                SendAdminBanList(peerId);
                break;

            case AdminActionKind.RequestDiagnostics:
                SendAdminDiagnostics(peerId);
                break;

            case AdminActionKind.RequestBanList:
                SendAdminBanList(peerId);
                break;

            case AdminActionKind.SaveNow:
                SaveRequested?.Invoke(); // authorised; the host/server process does the write
                break;

            // Session-control settings (v18 arc): OWNER-only — admins moderate players, the owner
            // reconfigures the session. The new value rides the targetKey string.
            case AdminActionKind.SetPassword:
                if (!_moderation.IsOwner(senderKey)) { SendAdminNotice(peerId, AdminNoticeKind.Rejected, "owner only"); return; }
                SetSessionPassword(targetKey);
                break;

            case AdminActionKind.SetMaxPlayers:
                if (!_moderation.IsOwner(senderKey)) { SendAdminNotice(peerId, AdminNoticeKind.Rejected, "owner only"); return; }
                if (!int.TryParse(targetKey, out int cap) || !SetMaxPlayers(cap))
                    SendAdminNotice(peerId, AdminNoticeKind.Rejected, "max players must be 1-99");
                break;

            case AdminActionKind.SetAutosaveInterval:
                if (!_moderation.IsOwner(senderKey)) { SendAdminNotice(peerId, AdminNoticeKind.Rejected, "owner only"); return; }
                if (!int.TryParse(targetKey, out int seconds) || !SetAutosaveIntervalSeconds(seconds))
                    SendAdminNotice(peerId, AdminNoticeKind.Rejected, "autosave interval must be 5-3600 seconds");
                break;
        }
    }

    private void SendAdminDiagnostics(int peerId)
    {
        var w = new PacketWriter(48).WriteByte((byte)MessageType.AdminDiagnostics);
        AdminCodec.WriteDiagnostics(w, CaptureDiagnostics());
        _transport.Send(peerId, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    private void SendAdminBanList(int peerId)
    {
        var w = new PacketWriter(48).WriteByte((byte)MessageType.AdminBanList);
        AdminCodec.WriteBanList(w, AllBans); // ids + names only — never keys, never SteamIds (R4-A)
        _transport.Send(peerId, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>The surfaced session-ban entries (id + name) — session-scoped, dies with the server.</summary>
    public IReadOnlyCollection<SessionBan> SessionBans => _moderation.Bans;

    /// <summary>Every surfaced ban entry — session bans first, then persistent (U3) ones projected
    /// into the same (id, name) shape. Ids ≥ <see cref="BanStore.PersistentIdFloor"/> are the
    /// persistent ones; the wire and every list view stay a flat (id, name) list either way. This is
    /// the host UI's and the dedicated console's view; remote admins get it via RequestBanList.</summary>
    public IReadOnlyCollection<SessionBan> AllBans
    {
        get
        {
            if (_config.BanStore is null) return _moderation.Bans;
            var merged = new List<SessionBan>(_moderation.Bans);
            foreach (PersistentBan b in _config.BanStore.Entries) merged.Add(new SessionBan(b.Id, b.Name));
            return merged;
        }
    }

    /// <summary>Lift a ban by its surfaced entry id — session or persistent, the disjoint id ranges
    /// route it. The dedicated console's unban (`unban &lt;id&gt;`) and the host UI's button both land
    /// here or on the wire verb.</summary>
    public bool Unban(int id) => _moderation.UnbanById(id) || _config.BanStore?.RemoveById(id) == true;

    private void SendAdminNotice(int peerId, AdminNoticeKind kind, string arg)
    {
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.AdminNotice)
            .WriteByte((byte)kind)
            .WriteString(arg)
            .ToArray();
        _transport.Send(peerId, payload, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastAdminNotice(AdminNoticeKind kind, string arg)
    {
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.AdminNotice)
            .WriteByte((byte)kind)
            .WriteString(arg)
            .ToArray();
        foreach (int id in _players.Keys) _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
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
        _posed.Add(peerId);                // this peer now has a real position (enables interest trimming)

        var w = new PacketWriter(40)
            .WriteByte((byte)MessageType.PlayerPose)
            .WriteVarUInt((uint)peerId);   // authoritative id
        PresenceCodec.WritePose(w, pose);
        byte[] payload = w.ToArray();
        // Relay only to clients for whom this player is in relevance scope (D10). Off by default and
        // fail-open, so this is the plain all-except-sender broadcast until interest is enabled.
        var senderKey = new EntityKey(EntityKind.Player, peerId);
        foreach (int id in _players.Keys)
            if (id != peerId && _interest.IsRelevant(id, senderKey))
                _transport.Send(id, payload, DeliveryMethod.SequencedUnreliable);
    }

    private void OnPeerDisconnected(int peerId) => Remove(peerId);

    /// <param name="farewell">The system chat line the departure earns (M5.4): Left for an ordinary
    /// leave/drop, Kicked/Banned from the moderation path, null for the F7 takeover eviction — the
    /// player is right there reconnecting, so a "left" line would be a lie.</param>
    private void Remove(int peerId, bool pump = true, ChatMessageKind? farewell = ChatMessageKind.Left)
    {
        // A QUEUED peer leaving (transport drop or Leave) just leaves the line — it was never
        // admitted, so there is no roster entry or subsystem state to release. Everyone behind
        // moves up a place and hears about it.
        if (FindQueued(peerId) is QueuedJoin queued)
        {
            _queue.Remove(queued);
            foreach (QueuedJoin q in _queue) SendQueued(q);
            return;
        }

        // Capture where they were last seen BEFORE the player record dies — the item release
        // transaction drops a departing holder's possessions at this pose. Gated on _posed so a
        // default (never-streamed) pose is null here, not a phantom position at the origin.
        Pose? lastPose = _posed.Contains(peerId) && _players.TryGetValue(peerId, out PlayerState? last)
            ? last.Pose : (Pose?)null;
        string? name = _players.TryGetValue(peerId, out PlayerState? leaving) ? leaving.Name : null;

        if (!_players.Remove(peerId)) return; // never joined, or already removed — no double broadcast

        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.PlayerLeft)
            .WriteVarUInt((uint)peerId)
            .ToArray();
        foreach (int id in _players.Keys)
            _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);

        if (farewell.HasValue)                             // system feed: why they departed (M5.4)
            BroadcastChat(new ChatEntry(farewell.Value, peerId, name ?? string.Empty, string.Empty));
        _chat.Forget(peerId);                              // peer ids are transport-scoped, may be reused

        Trains.OnPlayerRemoved(peerId);                    // park their consists, free their grants
        Items.OnPlayerRemoved(peerId, lastPose);           // release/retain their held items — BEFORE
        Career.OnPlayerRemoved(peerId);                    // career drops the peer↔key map it reads

        _posed.Remove(peerId);
        _interest.RemoveClient(peerId);                    // stop tracking them as an observer
        _interest.ForgetEntity(new EntityKey(EntityKind.Player, peerId)); // and drop them from others'
                                                           // scopes (PlayerLeft above already hid them)
        PlayerRemoved?.Invoke(peerId);

        if (pump) PumpQueue(); // a real slot freed — the line advances (D18)
    }

    /// <summary>
    /// Where each railed trainset is, in the absolute world frame — the anchors interest management
    /// distance-tests (D10 Burst 2). Yields nothing when there is no usable geometry, which combined
    /// with the constructor's <c>SuppressKind</c> means the train filter is fully inert on a v1
    /// topology.
    ///
    /// <para>A consist is anchored by its FIRST car: the whole set shares one relevance decision, so
    /// a 30-car train is never half-streamed. The head can be up to a train-length from the tail, but
    /// that is metres against a radius of hundreds — and the hysteresis band absorbs it.</para>
    /// </summary>
    private IEnumerable<SpatialEntity> TrainEntities()
    {
        if (_topology is null || !_topology.HasGeometry) yield break;

        foreach (KeyValuePair<int, TrainsetSnapshot> kv in Trains.LatestSnapshots)
        {
            CarSnapshot head = kv.Value.Cars[0]; // a snapshot always carries >= 1 car (ctor-enforced)
            if (head.Derailed)
            {
                // A derailed car left the rails, so its spline state is meaningless — but it carries a
                // real 6-DOF pose, which is already the frame we want.
                yield return new SpatialEntity(new EntityKey(EntityKind.Trainset, kv.Key), head.Pose.Px, head.Pose.Pz);
                continue;
            }
            if (_topology.TryEdgeWorldPoint(head.Front.EdgeId, head.Front.S, out WorldPoint p))
                yield return new SpatialEntity(new EntityKey(EntityKind.Trainset, kv.Key), p.X, p.Z);
            // Unknown or geometry-free edge (F4: B99.7 has bare special-track edges even in a good
            // extract): yield nothing — the set then isn't in _placed after the next Recompute, and
            // IsRelevant FAILS OPEN for it (relevant to everyone, streamed to everyone). Broadcast
            // for one set beats wrongly hiding it; the build-match guard at load time still keeps a
            // foreign topology from mis-placing everything.
        }
    }

    /// <summary>An entity newly in a client's relevance scope (D10). A player needs no packet — its
    /// identity is already global (PlayerJoined) and the next relayed pose, now permitted by
    /// <see cref="InterestManager.IsRelevant"/>, re-shows the avatar. A TRAINSET does need one: the
    /// client tore its replica down on the hide, so replay the def + last position + controls before
    /// its stream resumes (Burst 2).</summary>
    private void OnInterestEnter(int peerId, EntityKey key)
    {
        if (key.Kind == EntityKind.Trainset) Trains.ReplayTrainset(peerId, key.Id);
    }

    /// <summary>An entity left a client's relevance scope: tell just that client to hide the replica
    /// (a presence hint — the entity still exists). Reliable-ordered: a hide must not be dropped.</summary>
    private void OnInterestLeave(int peerId, EntityKey key)
    {
        // Never tell a player to hide a consist THEY simulate. Their cars are real, local and
        // physical — a host's own native trainsets most of all — and a hide would have the Shim tear
        // down the very objects it is authoritative for. Distance is irrelevant to ownership.
        if (key.Kind == EntityKind.Trainset
            && Trains.Registry.Sets.TryGetValue(key.Id, out TrainsetDef? owned)
            && owned.OwnerId == peerId)
            return;

        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.InterestHide)
            .WriteByte((byte)key.Kind)
            .WriteVarUInt((uint)key.Id)
            .ToArray();
        _transport.Send(peerId, payload, DeliveryMethod.ReliableOrdered);
    }

    public void Dispose()
    {
        _transport.Received -= OnReceived;
        _transport.PeerDisconnected -= OnPeerDisconnected;
        _players.Clear();
        _posed.Clear();
        _queue.Clear();
    }
}
