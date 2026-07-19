using System;
using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>
/// The server's career subsystem (02 §4/§6), owned by <see cref="NetServer"/> beside
/// <see cref="ServerTrains"/>. Maps session peer ids onto stable player keys — the wire NEVER
/// carries another player's key, because within the grace window the key is the reclaim credential
/// (07 §M3) — validates claim/report/abandon/purchase proposals against <see cref="Career"/>, and
/// broadcasts committed state. All career traffic is reliable-ordered: it is transactions, not
/// telemetry (03 §5 delivery tiers).
/// </summary>
public sealed class ServerCareer
{
    private readonly ITransport _transport;
    private readonly CareerConfig _config;
    private readonly Func<IEnumerable<int>> _connectedIds;
    private readonly Func<int, Pose?> _poseOf;
    private readonly Dictionary<int, string> _keyByPeer = new();
    private readonly Dictionary<string, int> _peerByKey = new(StringComparer.Ordinal);

    // The first admitted peer is the world source (host-embedded: the host's loopback client
    // always joins first, before the UDP port has anyone). Only it may register/retract external
    // jobs (D13). Dedicated-server mode never sets AcceptExternalJobs, so this never matters there.
    private int _worldSourcePeer;

    /// <summary>The session's world source (first admitted peer — the host's loopback client). The
    /// item subsystem shares this gate for host-captured world items (D13 posture). 0 before anyone
    /// joins.</summary>
    internal int WorldSourcePeer => _worldSourcePeer;

    // Deferred completion reports on captured jobs (M3.5c): a remote claimant's "done" is not
    // trusted — the world source's game validates its own task tree and answers. Keyed by job id;
    // claimants are held by KEY (they may disconnect while the query is in flight).
    private sealed class PendingComplete
    {
        public PendingComplete(string claimantKey, int taskIndex, long deadlineMs)
        {
            ClaimantKey = claimantKey;
            TaskIndex = taskIndex;
            DeadlineMs = deadlineMs;
        }

        public string ClaimantKey { get; }
        public int TaskIndex { get; }
        public long DeadlineMs { get; }
    }

    private const long CompleteQueryTimeoutMs = 15_000;
    private readonly Dictionary<int, PendingComplete> _pendingCompletes = new();
    private readonly IClock _clock;

    internal ServerCareer(ITransport transport, IClock clock, CareerConfig config,
        Func<IEnumerable<int>> connectedIds, CareerSaveData? restore, Func<int, Pose?> poseOf)
    {
        _transport = transport;
        _config = config;
        _connectedIds = connectedIds;
        _poseOf = poseOf;
        _clock = clock;
        Registry = new CareerRegistry(config, clock, restore);
    }

    /// <summary>The authoritative career state. Exposed for the host UI, admin, and tests.</summary>
    public CareerRegistry Registry { get; }

    private bool _autoGrantHostLicenses;

    /// <summary>D15's auto-grant half: when on, joining players inherit the world source's current
    /// license scope on admit (riding the career burst), and licenses that newly enter the world
    /// source's scope mid-session propagate to every connected player live. Flipping it ON
    /// mid-session sweeps immediately, so the checkbox works whenever it's ticked. Grants are
    /// charge-free — progression is shared, money is not. The host UI sets this in-proc; the
    /// dedicated server leaves it off.</summary>
    public bool AutoGrantHostLicenses
    {
        get => _autoGrantHostLicenses;
        set
        {
            bool turnedOn = value && !_autoGrantHostLicenses;
            _autoGrantHostLicenses = value;
            if (turnedOn && KeyOf(_worldSourcePeer) is string hostKey)
                foreach (string lic in Registry.LicensesFor(hostKey).ToArray())
                    PropagateHostLicense(lic);
        }
    }

    /// <summary>A career proposal failed validation: (peerId, reason). Also sent to the requester
    /// as CareerRejected — "missing license: hazmat" is UX, not just a host log line.</summary>
    public event Action<int, string>? RequestRejected;

    internal bool IsKeyOnline(string playerKey) => Registry.IsOnline(playerKey);

    /// <summary>The stable key behind a connected peer (null before admission/after removal).</summary>
    public string? KeyOf(int peerId) => _keyByPeer.TryGetValue(peerId, out string? key) ? key : null;

    /// <summary>The connected peer behind a stable key, or 0 when the holder is offline (in their
    /// grace window). The item subsystem uses this to resolve a possession's scope to a wire peer id
    /// — keys never leave the server (07 §M3).</summary>
    internal int PeerOf(string playerKey) => _peerByKey.TryGetValue(playerKey, out int peer) ? peer : 0;

    /// <summary>The display name last seen for a key ("" if unknown) — the holder label items ride
    /// on the wire with, beside <see cref="PeerOf"/>.</summary>
    internal string NameOf(string playerKey) =>
        Registry.Profiles.TryGetValue(playerKey, out PlayerProfile? p) ? p.Name : string.Empty;

    /// <summary>Charge a shop purchase against a player's policy wallet (M4), broadcasting the new
    /// balance + an economy event on success. Overdraft-refused (the ledger never goes negative), so
    /// a client can't buy what it can't afford — the win-condition invariant: the cash leaves the
    /// RIGHT wallet. The item mint is the caller's job, ordered AFTER this so money and item move
    /// together.</summary>
    internal bool TryChargeShopPurchase(int peerId, long amountCents, string label, out string? reason)
    {
        reason = null;
        if (KeyOf(peerId) is not string key) { reason = "not in session"; return false; }
        if (!Registry.TryChargeExternalFee(key, amountCents, out reason)) return false;
        SendWalletUpdate(key);
        SendEconomyEvent(key, EconomyEventKind.ShopPurchase, amountCents, label);
        return true;
    }

    /// <summary>Career burst for a newly admitted player: their career state, then the whole board.
    /// Runs after the trains burst, all reliable-ordered (03 §10). Reconnect within grace lands
    /// here too — Connect cancels the hold and the untouched claims simply stream back out.</summary>
    internal void OnPlayerAdmitted(int peerId, string playerKey, string name)
    {
        if (_worldSourcePeer == 0) _worldSourcePeer = peerId;
        _keyByPeer[peerId] = playerKey;
        _peerByKey[playerKey] = peerId;
        Registry.Connect(playerKey, name);

        // D15 auto-grant, join half: copy the world source's license scope onto the newcomer
        // BEFORE the burst below is built, so the licenses arrive inside their CareerState
        // instead of trickling in as N separate updates. Idempotent into a set — rejoins re-copy.
        if (AutoGrantHostLicenses && peerId != _worldSourcePeer &&
            KeyOf(_worldSourcePeer) is string hostKey && hostKey != playerKey)
        {
            foreach (string lic in Registry.LicensesFor(hostKey).ToArray())
                Registry.TryGrantExternal(playerKey, lic, out _, out _);
        }

        _transport.Send(peerId, BuildCareerState(playerKey), DeliveryMethod.ReliableOrdered);
        foreach (JobRecord job in Registry.Jobs.Values)
        {
            _transport.Send(peerId, BuildJobCreated(job.Def), DeliveryMethod.ReliableOrdered);
            if (job.State != JobLifecycle.Available)
                _transport.Send(peerId, BuildJobState(job), DeliveryMethod.ReliableOrdered);
        }

        // Rebind broadcast: this key's held claims resolve to a live peer again — everyone else's
        // board still shows the offline claimant (peer 0) from the disconnect.
        foreach (JobRecord job in Registry.Jobs.Values)
            if (job.State == JobLifecycle.Claimed && job.ClaimantKey == playerKey)
                Broadcast(BuildJobState(job));
    }

    /// <summary>A player left: drop the peer mapping and start their reconnect-grace hold.</summary>
    internal void OnPlayerRemoved(int peerId)
    {
        if (!_keyByPeer.TryGetValue(peerId, out string? key)) return;
        _keyByPeer.Remove(peerId);
        _peerByKey.Remove(key);
        Registry.Disconnect(key);

        // Their claims are HELD (grace), but the peer id behind them just died — tell the room the
        // claimant is offline (peer 0, name kept) so nobody addresses a stale id.
        foreach (JobRecord job in Registry.Jobs.Values)
            if (job.State == JobLifecycle.Claimed && job.ClaimantKey == key)
                Broadcast(BuildJobState(job));
    }

    /// <summary>Handle a career message from an ADMITTED peer. Returns false for non-career types.</summary>
    internal bool TryHandle(int peerId, MessageType type, PacketReader r)
    {
        switch (type)
        {
            case MessageType.JobClaimRequest: HandleClaim(peerId, r); return true;
            case MessageType.JobTaskReport: HandleTaskReport(peerId, r); return true;
            case MessageType.JobAbandonRequest: HandleAbandon(peerId, r); return true;
            case MessageType.LicensePurchaseRequest: HandlePurchase(peerId, r); return true;
            case MessageType.JobRegister: HandleJobRegister(peerId, r); return true;
            case MessageType.JobRetract: HandleJobRetract(peerId, r); return true;
            case MessageType.LicenseGrantExternal: HandleLicenseGrantExternal(peerId, r); return true;
            case MessageType.FeeExternal: HandleFeeExternal(peerId, r); return true;
            case MessageType.JobCompleteReply: HandleCompleteReply(peerId, r); return true;
            default: return false;
        }
    }

    /// <summary>Advance time-driven state (claim TTLs, grace expiries, board refill) and broadcast
    /// whatever committed. Called from NetServer.Poll — cheap when nothing is due.</summary>
    internal void Tick()
    {
        CareerTick tick = Registry.Tick();
        foreach (JobRecord job in tick.GeneratedJobs)
            Broadcast(BuildJobCreated(job.Def));
        foreach (JobRecord job in tick.ReleasedJobs)
        {
            _pendingCompletes.Remove(job.Def.Id); // a released claim voids any in-flight verification
            Broadcast(BuildJobState(job));
        }

        if (_pendingCompletes.Count > 0)
        {
            long now = _clock.NowMs;
            List<int>? lapsed = null;
            foreach (KeyValuePair<int, PendingComplete> kv in _pendingCompletes)
                if (now >= kv.Value.DeadlineMs) (lapsed ??= new List<int>()).Add(kv.Key);
            if (lapsed != null)
            {
                foreach (int jobId in lapsed)
                {
                    PendingComplete pending = _pendingCompletes[jobId];
                    _pendingCompletes.Remove(jobId);
                    if (_peerByKey.TryGetValue(pending.ClaimantKey, out int peer))
                        Reject(peer, "task: the host did not confirm delivery in time — try again", jobId);
                }
            }
        }
    }

    // ── handlers ──

    private void HandleClaim(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        if (KeyOf(peerId) is not string key) return;

        if (Registry.TryClaim(key, jobId, out JobRecord? job, out string? reason))
            Broadcast(BuildJobState(job!));
        else
            Reject(peerId, $"claim: {reason}", jobId);
    }

    private void HandleJobRegister(int peerId, PacketReader r)
    {
        JobDef proposal = CareerCodec.ReadJobDef(r);
        if (peerId != _worldSourcePeer)
        {
            Reject(peerId, "register: only the world source registers jobs");
            return;
        }
        if (Registry.TryRegisterExternal(proposal, out JobRecord? job, out string? reason))
            Broadcast(BuildJobCreated(job!.Def));
        else
            Reject(peerId, $"register: {reason}");
    }

    private void HandleJobRetract(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        if (peerId != _worldSourcePeer)
        {
            Reject(peerId, "retract: only the world source retracts jobs", jobId);
            return;
        }
        // Capture the claimant BEFORE the retract clears it: a claim dying under someone (the
        // native job expired at a far station) deserves an explicit toast, not just a vanished
        // MY JOB row.
        string? claimantKey = Registry.Jobs.TryGetValue(jobId, out JobRecord? held) ? held.ClaimantKey : null;

        if (Registry.TryRetract(jobId, out JobRecord? job, out string? reason))
        {
            _pendingCompletes.Remove(jobId);
            Broadcast(BuildJobState(job!));
            if (claimantKey != null && _peerByKey.TryGetValue(claimantKey, out int claimantPeer))
                Reject(claimantPeer, "the host world expired this job — claim released", jobId);
        }
        else
        {
            Reject(peerId, $"retract: {reason}", jobId);
        }
    }

    private void HandleTaskReport(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        int taskIndex = (int)r.ReadVarUInt();
        if (KeyOf(peerId) is not string key) return;

        // 02 §4: task transitions validated from owner-reported world state — and the claimant's
        // presence pose IS that state. Runs before the registry so order errors still get their
        // exact registry reason when the reporter is standing in the right place.
        if (!TaskLocationOk(peerId, jobId, taskIndex, out string? whereReason))
        {
            Reject(peerId, $"task: {whereReason}", jobId);
            return;
        }

        // Captured jobs reported by anyone but the world source defer to NATIVE validation
        // (M3.5c): the host's game owns the task tree, so the report becomes a query and the
        // commit waits for the verdict. The world source's own reports keep the direct path —
        // they only ever arrive AFTER the game validated the turn-in (JobCapture's flow).
        if (Registry.Jobs.TryGetValue(jobId, out JobRecord? record) &&
            record.Def.GameId.Length > 0 && peerId != _worldSourcePeer)
        {
            if (record.State != JobLifecycle.Claimed || record.ClaimantKey != key)
            {
                Reject(peerId, $"task: job {jobId} is not claimed by you", jobId);
                return;
            }
            if (taskIndex != record.NextTaskIndex)
            {
                Reject(peerId, $"task: task {taskIndex} out of order (expected {record.NextTaskIndex})", jobId);
                return;
            }
            // Re-reports while a query is in flight just refresh the deadline — reliable-ordered
            // transport means the world source will answer the first one soon regardless.
            _pendingCompletes[jobId] = new PendingComplete(key, taskIndex, _clock.NowMs + CompleteQueryTimeoutMs);
            byte[] query = new PacketWriter(8)
                .WriteByte((byte)MessageType.JobCompleteRequest)
                .WriteVarUInt((uint)jobId)
                .ToArray();
            _transport.Send(_worldSourcePeer, query, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (Registry.TryReportTask(key, jobId, taskIndex, out JobRecord? job, out bool completed,
                out long payout, out string? reason))
        {
            Broadcast(BuildJobState(job!));
            if (completed)
            {
                SendWalletUpdate(key);
                SendEconomyEvent(key, EconomyEventKind.JobPayout, payout, $"job {jobId} delivered");
            }
        }
        else
        {
            Reject(peerId, $"task: {reason}", jobId);
        }
    }

    /// <summary>The world source's native verdict on a deferred completion (M3.5c). On ok the
    /// stashed report commits exactly like a direct one — payout to whoever holds the claim (by
    /// KEY, so a claimant mid-reconnect still gets paid). On a refusal the claimant gets the
    /// native reason as their toast.</summary>
    private void HandleCompleteReply(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        bool ok = r.ReadByte() != 0;
        string verdict = r.ReadString();
        if (peerId != _worldSourcePeer)
        {
            Reject(peerId, "complete: only the world source verifies captured jobs", jobId);
            return;
        }
        if (!_pendingCompletes.TryGetValue(jobId, out PendingComplete? pending)) return; // lapsed or voided
        _pendingCompletes.Remove(jobId);

        if (!ok)
        {
            if (_peerByKey.TryGetValue(pending.ClaimantKey, out int claimantPeer))
                Reject(claimantPeer, $"task: {(verdict.Length > 0 ? verdict : "the host world says this job is not finished")}", jobId);
            return;
        }

        if (Registry.TryReportTask(pending.ClaimantKey, jobId, pending.TaskIndex,
                out JobRecord? job, out bool completed, out long payout, out string? reason))
        {
            Broadcast(BuildJobState(job!));
            if (completed)
            {
                SendWalletUpdate(pending.ClaimantKey);
                SendEconomyEvent(pending.ClaimantKey, EconomyEventKind.JobPayout, payout, $"job {jobId} delivered");
            }
        }
        else if (_peerByKey.TryGetValue(pending.ClaimantKey, out int claimantPeer))
        {
            Reject(claimantPeer, $"task: {reason}", jobId);
        }
    }

    private void HandleAbandon(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        if (KeyOf(peerId) is not string key) return;

        if (Registry.TryAbandon(key, jobId, out JobRecord? job, out string? reason))
        {
            _pendingCompletes.Remove(jobId); // an abandon voids any in-flight verification
            Broadcast(BuildJobState(job!));
        }
        else
        {
            Reject(peerId, $"abandon: {reason}", jobId);
        }
    }

    private void HandlePurchase(int peerId, PacketReader r)
    {
        string licenseId = r.ReadString();
        if (KeyOf(peerId) is not string key) return;

        if (Registry.TryPurchaseLicense(key, licenseId, out long price, out string? reason))
        {
            SendLicenseUpdate(key, licenseId);
            SendWalletUpdate(key);
            SendEconomyEvent(key, EconomyEventKind.LicenseFee, price, $"license {licenseId}");
            // The world source buying from the PANEL shop skips the native-mirror echo path (the
            // reverse-applied native grant comes back idempotent), so auto-grant triggers here.
            if (peerId == _worldSourcePeer) PropagateHostLicense(licenseId);
        }
        else
        {
            Reject(peerId, $"purchase: {reason}");
        }
    }

    /// <summary>D14/M3.5c: a charge-free license grant from the world source — either the mirror
    /// of a native purchase on the host (target 0 = own scope; the register's payment arrives as
    /// FeeExternal) or a host-admin grant to a connected player (target = their peer id).
    /// Idempotent grants that change nothing are silently fine: they're echoes of our own
    /// server-side grants landing in the native LicenseManager and coming back around.</summary>
    private void HandleLicenseGrantExternal(int peerId, PacketReader r)
    {
        string licenseId = r.ReadString();
        int targetPeer = (int)r.ReadVarUInt();
        if (peerId != _worldSourcePeer || KeyOf(peerId) is not string senderKey)
        {
            Reject(peerId, "grant: only the world source grants licenses");
            return;
        }
        string? targetKey = targetPeer == 0 ? senderKey : KeyOf(targetPeer);
        if (targetKey is null)
        {
            Reject(peerId, $"grant: player {targetPeer} is not connected");
            return;
        }
        // D15 gate: a grant to ANOTHER player shares the host's progression — it never mints
        // beyond it. Self-grants stay scope-agnostic: they mirror native acquisitions, where the
        // game is the authority on what exists (D14).
        if (!string.Equals(targetKey, senderKey, StringComparison.Ordinal) &&
            !Registry.LicensesFor(senderKey).Contains(licenseId))
        {
            Reject(peerId, $"grant: you do not hold {licenseId} — grants share progression, they never mint it");
            return;
        }
        if (Registry.TryGrantExternal(targetKey, licenseId, out bool newlyGranted, out string? reason))
        {
            if (newlyGranted)
            {
                SendLicenseUpdate(targetKey, licenseId);
                // A license newly entering the world source's own scope is the auto-grant trigger
                // (native purchase or acquisition mirrored up through this same message).
                if (string.Equals(targetKey, senderKey, StringComparison.Ordinal))
                    PropagateHostLicense(licenseId);
            }
        }
        else
        {
            Reject(peerId, $"grant: {reason}");
        }
    }

    /// <summary>D15 auto-grant, live half: a license just entered the world source's scope — copy
    /// it to every other connected player, charge-free. No-op when the toggle is off, and in the
    /// shared preset (one scope means nothing is ever newly granted here). Players inside their
    /// reconnect grace catch up on the admit-time copy instead.</summary>
    private void PropagateHostLicense(string licenseId)
    {
        if (!AutoGrantHostLicenses) return;
        if (KeyOf(_worldSourcePeer) is not string hostKey) return;
        foreach (KeyValuePair<int, string> kv in _keyByPeer)
        {
            if (string.Equals(kv.Value, hostKey, StringComparison.Ordinal)) continue;
            if (Registry.TryGrantExternal(kv.Value, licenseId, out bool newly, out _) && newly)
                SendLicenseUpdate(kv.Value, licenseId);
        }
    }

    /// <summary>D14: a native register finalized a purchase against the mirrored wallet — burn it
    /// through the policy layer. A refusal here means the mirror drifted from the ledger; the
    /// rejection tells the Shim to resynchronize loudly rather than let them diverge further.</summary>
    private void HandleFeeExternal(int peerId, PacketReader r)
    {
        long amountCents = r.ReadInt64();
        string label = r.ReadString();
        if (KeyOf(peerId) is not string key) return;
        if (peerId != _worldSourcePeer)
        {
            Reject(peerId, "fee: only the world source reports native fees");
            return;
        }
        if (Registry.TryChargeExternalFee(key, amountCents, out string? reason))
        {
            SendWalletUpdate(key);
            SendEconomyEvent(key, EconomyEventKind.ExternalFee, amountCents, label);
        }
        else
        {
            Reject(peerId, $"fee: {reason}");
        }
    }

    /// <summary>The proximity gate: with a radius configured and a location known for the task's
    /// station, the reporter's last pose must be within it (horizontal). Missing data — no radius,
    /// unknown station, no pose yet, or an id the registry will reject anyway — passes through so
    /// the check can only ever ADD a refusal, never mask a better one.</summary>
    private bool TaskLocationOk(int peerId, int jobId, int taskIndex, out string? reason)
    {
        reason = null;
        float radius = _config.TaskProximityRadiusM;
        if (radius <= 0) return true;
        if (!Registry.Jobs.TryGetValue(jobId, out JobRecord? job)) return true;
        // Host-captured jobs (D13) are validated by the GAME's own task tree — the report arrives
        // from the world source at turn-in, wherever the validator happens to stand.
        if (job.Def.GameId.Length > 0) return true;
        if (taskIndex < 0 || taskIndex >= job.Def.Tasks.Count) return true;
        JobTaskDef task = job.Def.Tasks[taskIndex];
        if (!_config.StationLocations.TryGetValue(task.Param, out StationLocation loc)) return true;
        if (_poseOf(peerId) is not Pose pose) return true;

        float dx = pose.Px - loc.X;
        float dz = pose.Pz - loc.Z;
        if (dx * dx + dz * dz <= radius * radius) return true;

        double away = Math.Sqrt(dx * dx + dz * dz);
        reason = $"you must be at {task.Param} to report this step ({away:F0} m away)";
        return false;
    }

    // ── routed sends (the policy decides who a wallet/license change is FOR) ──

    private void SendWalletUpdate(string playerKey)
    {
        long balance = Registry.BalanceFor(playerKey);
        if (Registry.Policy.Preset == ProgressionPreset.SharedCareer)
            Broadcast(BuildWalletState(EconomyScope.Shared, balance));
        else if (_peerByKey.TryGetValue(playerKey, out int peer))
            _transport.Send(peer, BuildWalletState(EconomyScope.Personal, balance), DeliveryMethod.ReliableOrdered);
    }

    private void SendLicenseUpdate(string playerKey, string licenseId)
    {
        if (Registry.Policy.LicensesShared)
            Broadcast(BuildLicenseState(EconomyScope.Shared, licenseId));
        else if (_peerByKey.TryGetValue(playerKey, out int peer))
            _transport.Send(peer, BuildLicenseState(EconomyScope.Personal, licenseId), DeliveryMethod.ReliableOrdered);
    }

    private void SendEconomyEvent(string playerKey, EconomyEventKind kind, long amountCents, string reason)
    {
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.EconomyEvent)
            .WriteByte((byte)kind)
            .WriteInt64(amountCents)
            .WriteString(reason)
            .ToArray();
        if (Registry.Policy.Preset == ProgressionPreset.SharedCareer)
            Broadcast(payload);
        else if (_peerByKey.TryGetValue(playerKey, out int peer))
            _transport.Send(peer, payload, DeliveryMethod.ReliableOrdered);
    }

    private void Reject(int peerId, string reason, int jobId = 0)
    {
        RequestRejected?.Invoke(peerId, reason);
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.CareerRejected)
            .WriteString(reason)
            .WriteVarUInt((uint)jobId)
            .ToArray();
        _transport.Send(peerId, payload, DeliveryMethod.ReliableOrdered);
    }

    // ── packet builders ──

    private byte[] BuildCareerState(string playerKey)
    {
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.CareerState)
            .WriteByte((byte)Registry.Policy.Preset)
            .WriteInt64(Registry.BalanceFor(playerKey));
        CareerCodec.WriteLicenses(w, Registry.LicensesFor(playerKey));
        // The purchasable-license catalog: clients can't render a shop from gate failures alone.
        w.WriteVarUInt((uint)_config.LicensePrices.Count);
        foreach (KeyValuePair<string, long> kv in _config.LicensePrices)
        {
            w.WriteString(kv.Key);
            w.WriteInt64(kv.Value);
        }
        return w.ToArray();
    }

    private static byte[] BuildJobCreated(JobDef def)
    {
        var w = new PacketWriter(128).WriteByte((byte)MessageType.JobCreated);
        CareerCodec.WriteJobDef(w, def);
        return w.ToArray();
    }

    /// <summary>The claimant travels as (session peer id, display name): 0 + the name while the
    /// claimant is inside their grace window, 0 + "" when unclaimed. Keys never leave the server.</summary>
    private byte[] BuildJobState(JobRecord job)
    {
        int claimantPeer = 0;
        string claimantName = string.Empty;
        if (job.ClaimantKey is string key)
        {
            if (_peerByKey.TryGetValue(key, out int peer)) claimantPeer = peer;
            if (Registry.Profiles.TryGetValue(key, out PlayerProfile? profile)) claimantName = profile.Name;
        }
        return new PacketWriter(32)
            .WriteByte((byte)MessageType.JobState)
            .WriteVarUInt((uint)job.Def.Id)
            .WriteByte((byte)job.State)
            .WriteVarUInt((uint)claimantPeer)
            .WriteString(claimantName)
            .WriteVarUInt((uint)job.NextTaskIndex)
            .ToArray();
    }

    private static byte[] BuildWalletState(EconomyScope scope, long balanceCents) =>
        new PacketWriter(12)
            .WriteByte((byte)MessageType.WalletState)
            .WriteByte((byte)scope)
            .WriteInt64(balanceCents)
            .ToArray();

    private static byte[] BuildLicenseState(EconomyScope scope, string licenseId) =>
        new PacketWriter(16)
            .WriteByte((byte)MessageType.LicenseState)
            .WriteByte((byte)scope)
            .WriteString(licenseId)
            .ToArray();

    private void Broadcast(byte[] payload)
    {
        foreach (int id in _connectedIds())
            _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }
}
