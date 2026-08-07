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
    private readonly Dictionary<int, PlayerStatus> _roster = new();

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

    /// <summary>The structured refusal (M5.1): kind + have/need. Set before <see cref="Rejected"/>
    /// fires; null until a reject arrives. Against a pre-v13 server the kind reads
    /// <see cref="RejectKind.Other"/> and only the reason is populated.</summary>
    public RejectInfo? RejectDetail { get; private set; }

    /// <summary>Where the join burst is (M5.1) — see <see cref="JoinStage"/> for what each stage
    /// proves. Monotonic while connected; resets to None on disconnect.</summary>
    public JoinStage Stage { get; private set; }

    /// <summary>The join burst is fully delivered (the server's sentinel arrived). THE completion
    /// signal for a join readiness gate — gates clear on this, never on a timer or inferred stage.</summary>
    public bool JoinSettled => Joined && Stage == JoinStage.Complete;

    /// <summary>Our place in the server's admission queue, 1-based (D18); 0 = not queued. Non-zero
    /// only while the server is full and we are waiting — the stage stays
    /// <see cref="JoinStage.Connecting"/> throughout, and admission arrives as an ordinary accept.</summary>
    public int QueuePosition { get; private set; }

    /// <summary>How many joiners are waiting in total (self included); 0 when not queued.</summary>
    public int QueueTotal { get; private set; }

    /// <summary>serverNow - localNow (ms). Add to the local clock to estimate server time (03 §5).</summary>
    public long ServerTimeOffsetMs { get; private set; }

    /// <summary>Estimated authoritative server time now, from the last sync.</summary>
    public long EstimatedServerTimeMs => _clock.NowMs + ServerTimeOffsetMs;

    /// <summary>The OTHER players in the session, keyed by id (excludes self).</summary>
    public IReadOnlyDictionary<int, PlayerState> Players => _players;

    /// <summary>The live roster status (M5.2): role + measured server ping per admitted player, SELF
    /// INCLUDED (unlike <see cref="Players"/>) — the player list renders from this plus the name
    /// mirror. Fed by the server's <see cref="Protocol.MessageType.RosterStatus"/> pushes (join burst,
    /// role changes, slow cadence); entries drop with PlayerLeft; empty until the join burst.</summary>
    public IReadOnlyDictionary<int, PlayerStatus> Roster => _roster;

    /// <summary>A player's session role from the last roster status (Player when unknown).</summary>
    public PlayerRole RoleOf(int id) => _roster.TryGetValue(id, out PlayerStatus s) ? s.Role : PlayerRole.Player;

    /// <summary>A player's measured server round-trip in ms from the last roster status (null = unknown).</summary>
    public int? PingOf(int id) => _roster.TryGetValue(id, out PlayerStatus s) ? s.PingMs : null;

    public event Action<int>? Accepted;             // arg: local id
    public event Action<string>? Rejected;          // arg: reason (read RejectDetail for structure)

    /// <summary>The join burst progressed (M5.1). Fires on every stage transition, including the
    /// reset to None on disconnect; drives the loading interstitial's stage display.</summary>
    public event Action<JoinStage>? JoinStageChanged;

    /// <summary>Queue state changed (D18): (position, total) — position 0 = no longer queued
    /// (admitted, or the link dropped). Drives the interstitial's "waiting for a slot" line.</summary>
    public event Action<int, int>? QueueChanged;
    public event Action<PlayerState>? PlayerJoined;
    public event Action<int>? PlayerLeft;
    public event Action<int, Pose>? PlayerMoved;    // args: id, new pose

    /// <summary>The roster status changed — membership, a role, or a ping moved (M5.2). Repaint the
    /// player list; no-change restatements are deduped and do not fire this.</summary>
    public event Action? RosterChanged;

    /// <summary>The server hid a remote player who left our spatial relevance set (D10). Unlike
    /// <see cref="PlayerLeft"/> the player is still in the session — the Shim should hide the avatar
    /// but keep its state; the next <see cref="PlayerMoved"/> for that id (when we near them again)
    /// re-shows it. Arg: the hidden player's id.</summary>
    public event Action<int>? PlayerHidden;

    /// <summary>The server hid a trainset that left our spatial relevance set (D10 Burst 2) — it has
    /// stopped streaming that consist's snapshots to us. Unlike a TrainsetRemove the set still exists:
    /// the Shim should despawn the replica GameObjects (they would otherwise sit frozen forever) but
    /// must NOT treat it as deleted. On re-approach the server replays the def, the last snapshot and
    /// the cab controls, so the replica rebuilds from that burst. Arg: the trainset id.</summary>
    public event Action<int>? TrainsetHidden;

    /// <summary>The transport link to the server dropped AFTER we had been admitted (host died,
    /// eviction, network loss). Not raised for failed joins — those surface via timeout/Rejected.
    /// The mirrors are already reset when this fires; the frontend decides what "session lost"
    /// means for its world (the joined Shim must NOT silently unblock native saving, 03 §10).</summary>
    public event Action? Disconnected;

    /// <summary>server → client moderation consequence (M5.2): kicked/banned reason, joins paused/resumed,
    /// a role change, or an action rejection. Args: kind, string argument (meaning depends on kind).</summary>
    public event Action<AdminNoticeKind, string>? AdminNotice;

    /// <summary>server → admin: a diagnostics snapshot, in reply to <see cref="RequestDiagnostics"/> (M5.2).</summary>
    public event Action<ServerDiagnostics>? DiagnosticsReceived;

    /// <summary>server → admin: the session ban list, in reply to <see cref="RequestBanList"/> (M5.2).</summary>
    public event Action<IReadOnlyList<string>>? BanListReceived;

    /// <summary>The committed world time-of-day arrived (v18, 02 §3): (OADate, dayLengthMinutes).
    /// The value is CURRENT as of its send (the server flows its clock), so the Shim corrects the
    /// local sky only past a drift threshold — both skies advance at the same rate.</summary>
    public event Action<double, float>? WorldTimeChanged;

    /// <summary>The last received world time as an OADate (0 = none yet), current AS OF RECEIPT —
    /// project it forward before comparing (see <see cref="WorldTimeChanged"/>).</summary>
    public double WorldTimeOa { get; private set; }

    /// <summary>The session's world day length in real minutes (0 = unknown).</summary>
    public float WorldDayLengthMinutes { get; private set; }

    /// <summary>The shared world's pause state changed (D19 — the host paused/resumed via its own
    /// native pause menu): (paused, display reason). The Shim freezes/unfreezes the local sim via
    /// DV's own pause-request system and shows the "host paused" surface.</summary>
    public event Action<bool, string>? WorldPauseChanged;

    /// <summary>Is the shared world paused right now (D19)? Join-burst-fed, so it is correct from
    /// the first frame of a session joined mid-pause.</summary>
    public bool WorldPaused { get; private set; }

    /// <summary>The display reason from the last pause notice (empty when unpaused).</summary>
    public string WorldPauseReason { get; private set; } = string.Empty;

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

    /// <summary>Send a moderation action to the server (M5.2). Authority is server-side — a non-admin's
    /// request comes back as an <see cref="AdminNotice"/> of kind Rejected. Peer-targeted verbs
    /// (kick/ban/promote/demote) pass <paramref name="targetPeerId"/>; unban passes
    /// <paramref name="targetKey"/>; pause/resume need neither.</summary>
    public void SendAdminAction(AdminActionKind kind, int targetPeerId = 0, string targetKey = "")
    {
        if (!Joined) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.AdminAction)
            .WriteByte((byte)kind)
            .WriteVarUInt((uint)targetPeerId)
            .WriteString(targetKey ?? string.Empty)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Kick a player by roster id (they may reconnect immediately).</summary>
    public void Kick(int targetPeerId) => SendAdminAction(AdminActionKind.Kick, targetPeerId);

    /// <summary>Kick and session-ban a player by roster id.</summary>
    public void Ban(int targetPeerId) => SendAdminAction(AdminActionKind.Ban, targetPeerId);

    /// <summary>Grant admin to a player by roster id (owner only).</summary>
    public void Promote(int targetPeerId) => SendAdminAction(AdminActionKind.Promote, targetPeerId);

    /// <summary>Revoke admin from a player by roster id (owner only).</summary>
    public void Demote(int targetPeerId) => SendAdminAction(AdminActionKind.Demote, targetPeerId);

    /// <summary>Stop admitting new players.</summary>
    public void PauseJoins() => SendAdminAction(AdminActionKind.PauseJoins);

    /// <summary>Resume admitting new players.</summary>
    public void ResumeJoins() => SendAdminAction(AdminActionKind.ResumeJoins);

    /// <summary>Lift a session ban on a key (from the ban-list view).</summary>
    public void Unban(string targetKey) => SendAdminAction(AdminActionKind.Unban, 0, targetKey);

    /// <summary>Ask the server for a fresh diagnostics snapshot (reply via <see cref="DiagnosticsReceived"/>).</summary>
    public void RequestDiagnostics() => SendAdminAction(AdminActionKind.RequestDiagnostics);

    /// <summary>Ask the server for the current session ban list (reply via <see cref="BanListReceived"/>).</summary>
    public void RequestBanList() => SendAdminAction(AdminActionKind.RequestBanList);

    /// <summary>Ask the server to save the world now (M5.2 "Save now").</summary>
    public void SaveNow() => SendAdminAction(AdminActionKind.SaveNow);

    /// <summary>Owner only: change the session password mid-session (empty = open). Present players
    /// are untouched; only future joins check it (M5.2 session control).</summary>
    public void SetSessionPassword(string password) => SendAdminAction(AdminActionKind.SetPassword, 0, password ?? "");

    /// <summary>Owner only: change the player cap live (1-99). Raising it admits queued waiters
    /// immediately; lowering it never evicts anyone (M5.2 session control).</summary>
    public void SetMaxPlayers(int maxPlayers) =>
        SendAdminAction(AdminActionKind.SetMaxPlayers, 0, maxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Owner only: retune the autosave cadence live, in seconds (5-3600) (M5.2 session control).</summary>
    public void SetAutosaveInterval(int seconds) =>
        SendAdminAction(AdminActionKind.SetAutosaveInterval, 0, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Advance the join stage, forward only — later traffic of an earlier family (ordinary
    /// in-session career/item messages) must never regress the display, and nothing but the server's
    /// sentinel may reach Complete.</summary>
    private void AdvanceStage(JoinStage stage)
    {
        if (stage <= Stage) return;
        Stage = stage;
        JoinStageChanged?.Invoke(stage);
    }

    private void OnConnected(int serverPeer)
    {
        AdvanceStage(JoinStage.Connecting);
        // The JoinRequest PREFIX (type byte + varuint protocol version) is FROZEN across protocol
        // versions (F3): the connect key is version-free, so a mismatched server must always be
        // able to read this far and answer with the exact protocol-mismatch reject.
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
        ClearQueue();
        _players.Clear();
        _roster.Clear();   // silent — Disconnected below already tells the frontend everything is gone
        WorldTimeOa = 0;   // the next session's sun re-anchors from its own join burst
        WorldDayLengthMinutes = 0;
        if (WorldPaused)
        {
            // The link died mid-pause: the frontend must not stay frozen forever — surface the
            // unpause so the Shim releases its pause request along with the session teardown.
            WorldPaused = false;
            WorldPauseReason = string.Empty;
            WorldPauseChanged?.Invoke(false, string.Empty);
        }
        Trains.Reset();
        Career.Reset();
        Items.Reset();
        if (Stage != JoinStage.None)
        {
            Stage = JoinStage.None;               // deliberate regression — the one non-monotonic move
            JoinStageChanged?.Invoke(JoinStage.None);
        }
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
                case MessageType.JoinQueued: HandleQueued(r); break;
                case MessageType.PlayerJoined: HandlePlayerJoined(r); break;
                case MessageType.PlayerLeft: HandlePlayerLeft(r); break;
                case MessageType.PlayerPose: HandlePlayerPose(r); break;
                case MessageType.TimeSync: HandleTimeSync(r); break;
                case MessageType.WorldTimeState: HandleWorldTimeState(r); break;
                case MessageType.WorldPauseState: HandleWorldPauseState(r); break;
                case MessageType.InterestHide: HandleInterestHide(r); break;
                case MessageType.JoinBurstComplete: AdvanceStage(JoinStage.Complete); break;
                case MessageType.AdminNotice: HandleAdminNotice(r); break;
                case MessageType.AdminDiagnostics: DiagnosticsReceived?.Invoke(AdminCodec.ReadDiagnostics(r)); break;
                case MessageType.AdminBanList: BanListReceived?.Invoke(AdminCodec.ReadBanList(r)); break;
                case MessageType.RosterStatus: HandleRosterStatus(r); break;
                default:
                    // Stage inference (M5.1): the burst is sent world → career → items on one ordered
                    // channel, so the first message a later family claims proves every earlier family
                    // fully arrived. AdvanceStage's monotonic guard makes this free after the burst.
                    if (Trains.TryHandle(type, r)) { }
                    else if (Career.TryHandle(type, r)) AdvanceStage(JoinStage.Career);
                    else if (Items.TryHandle(type, r)) AdvanceStage(JoinStage.Items);
                    break;
            }
        }
        catch (Exception)
        {
            // Drop malformed server packets rather than tearing down the client mid-session.
        }
    }

    private void HandleQueued(PacketReader r)
    {
        int position = (int)r.ReadVarUInt();
        int total = (int)r.ReadVarUInt();
        if (position == QueuePosition && total == QueueTotal) return; // restated, nothing moved
        QueuePosition = position;
        QueueTotal = total;
        QueueChanged?.Invoke(position, total);
    }

    /// <summary>Leave the queue state (admitted / link gone). Fires the 0 event only if we WERE queued.</summary>
    private void ClearQueue()
    {
        if (QueuePosition == 0) return;
        QueuePosition = 0;
        QueueTotal = 0;
        QueueChanged?.Invoke(0, 0);
    }

    private void HandleAccepted(PacketReader r)
    {
        ClearQueue(); // a queued join resolves into an ordinary admission
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

        AdvanceStage(JoinStage.World);   // admitted — the world burst follows on the same channel
        Accepted?.Invoke(id);
        foreach (PlayerState p in roster) PlayerJoined?.Invoke(p);
    }

    private void HandleRejected(PacketReader r)
    {
        ClearQueue(); // a queued join can resolve to a reject (e.g. key taken over while waiting)
        string reason = r.ReadString();
        // v13 appends kind/have/need after the reason; a pre-v13 server's packet ends here. Read the
        // tail defensively (the HandleJoin playerKey precedent) so both server generations reject legibly.
        var kind = RejectKind.Other;
        string? has = null, needs = null;
        if (!r.AtEnd)
        {
            kind = (RejectKind)r.ReadByte();
            string h = r.ReadString();
            string n = r.ReadString();
            has = h.Length > 0 ? h : null;
            needs = n.Length > 0 ? n : null;
        }
        RejectReason = reason;
        RejectDetail = new RejectInfo(kind, reason, has, needs);
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
        // The roster status only ever names live players, so prune here rather than waiting for the
        // next status push — the list must never show a departed player's stale role/ping line.
        bool pruned = _roster.Remove(id);
        if (_players.Remove(id)) PlayerLeft?.Invoke(id);
        if (pruned) RosterChanged?.Invoke();
    }

    /// <summary>Mirror the server's full-state roster status (M5.2). Fires <see cref="RosterChanged"/>
    /// only when something actually moved — the cadence re-push with identical pings (the loopback
    /// case, always 0 ms) must not repaint anything.</summary>
    private void HandleRosterStatus(PacketReader r)
    {
        int count = (int)r.ReadVarUInt();
        bool changed = count != _roster.Count;
        var next = new Dictionary<int, PlayerStatus>(count);
        for (int i = 0; i < count; i++)
        {
            PlayerStatus s = PresenceCodec.ReadStatus(r);
            next[s.Id] = s;
            if (!changed &&
                (!_roster.TryGetValue(s.Id, out PlayerStatus prev) || prev.Role != s.Role || prev.PingMs != s.PingMs))
                changed = true;
        }
        if (!changed) return;
        _roster.Clear();
        foreach (KeyValuePair<int, PlayerStatus> kv in next) _roster[kv.Key] = kv.Value;
        RosterChanged?.Invoke();
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

    private void HandleWorldTimeState(PacketReader r)
    {
        double oaDate = r.ReadDouble();
        float dayLengthMinutes = r.ReadSingle();
        WorldTimeOa = oaDate;
        WorldDayLengthMinutes = dayLengthMinutes;
        WorldTimeChanged?.Invoke(oaDate, dayLengthMinutes);
    }

    private void HandleWorldPauseState(PacketReader r)
    {
        bool paused = r.ReadByte() != 0;
        string reason = r.ReadString();
        if (WorldPaused == paused) return;
        WorldPaused = paused;
        WorldPauseReason = paused ? reason : string.Empty;
        WorldPauseChanged?.Invoke(paused, reason);
    }

    /// <summary>World source only (the server ignores anyone else): report the local sky as the
    /// session's time-of-day truth (v18) — on a slow heartbeat, and immediately on a time JUMP
    /// (sleep / fast travel) so every peer's sun moves together.</summary>
    public void SendWorldTimeReport(double oaDate, float dayLengthMinutes)
    {
        if (!Joined) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.WorldTimeReport)
            .WriteDouble(oaDate)
            .WriteSingle(dayLengthMinutes)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>A moderation consequence from the server (M5.2). Display-only — the authoritative effect
    /// already happened (we may be about to be disconnected on a kick/ban); the UI just explains it.</summary>
    private void HandleAdminNotice(PacketReader r)
    {
        var kind = (AdminNoticeKind)r.ReadByte();
        string arg = r.ReadString();
        AdminNotice?.Invoke(kind, arg);
    }

    /// <summary>Hide a replica that left our relevance scope (D10). A hide is a presence HINT, never an
    /// authoritative removal — the entity still exists on the server, so mirrors keep their state and a
    /// re-approach re-shows it (a player via the next pose; a trainset via the server's replay burst).
    /// Item hides are reserved for a later burst.</summary>
    private void HandleInterestHide(PacketReader r)
    {
        var kind = (EntityKind)r.ReadByte();
        int id = (int)r.ReadVarUInt();
        switch (kind)
        {
            case EntityKind.Player when _players.ContainsKey(id): PlayerHidden?.Invoke(id); break;
            case EntityKind.Trainset when Trains.View.Sets.ContainsKey(id): TrainsetHidden?.Invoke(id); break;
        }
    }

    public void Dispose()
    {
        _transport.Received -= OnReceived;
        _transport.PeerConnected -= OnConnected;
        _transport.PeerDisconnected -= OnDisconnected;
        _players.Clear();
    }
}
