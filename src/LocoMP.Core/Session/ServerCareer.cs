using System;
using System.Collections.Generic;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
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
    private readonly Func<IEnumerable<int>> _connectedIds;
    private readonly Dictionary<int, string> _keyByPeer = new();
    private readonly Dictionary<string, int> _peerByKey = new(StringComparer.Ordinal);

    internal ServerCareer(ITransport transport, IClock clock, CareerConfig config,
        Func<IEnumerable<int>> connectedIds, CareerSaveData? restore)
    {
        _transport = transport;
        _connectedIds = connectedIds;
        Registry = new CareerRegistry(config, clock, restore);
    }

    /// <summary>The authoritative career state. Exposed for the host UI, admin, and tests.</summary>
    public CareerRegistry Registry { get; }

    /// <summary>A career proposal failed validation: (peerId, reason). Also sent to the requester
    /// as CareerRejected — "missing license: hazmat" is UX, not just a host log line.</summary>
    public event Action<int, string>? RequestRejected;

    internal bool IsKeyOnline(string playerKey) => Registry.IsOnline(playerKey);

    /// <summary>The stable key behind a connected peer (null before admission/after removal).</summary>
    public string? KeyOf(int peerId) => _keyByPeer.TryGetValue(peerId, out string? key) ? key : null;

    /// <summary>Career burst for a newly admitted player: their career state, then the whole board.
    /// Runs after the trains burst, all reliable-ordered (03 §10). Reconnect within grace lands
    /// here too — Connect cancels the hold and the untouched claims simply stream back out.</summary>
    internal void OnPlayerAdmitted(int peerId, string playerKey, string name)
    {
        _keyByPeer[peerId] = playerKey;
        _peerByKey[playerKey] = peerId;
        Registry.Connect(playerKey, name);

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
            Broadcast(BuildJobState(job));
    }

    // ── handlers ──

    private void HandleClaim(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        if (KeyOf(peerId) is not string key) return;

        if (Registry.TryClaim(key, jobId, out JobRecord? job, out string? reason))
            Broadcast(BuildJobState(job!));
        else
            Reject(peerId, $"claim: {reason}");
    }

    private void HandleTaskReport(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        int taskIndex = (int)r.ReadVarUInt();
        if (KeyOf(peerId) is not string key) return;

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
            Reject(peerId, $"task: {reason}");
        }
    }

    private void HandleAbandon(int peerId, PacketReader r)
    {
        int jobId = (int)r.ReadVarUInt();
        if (KeyOf(peerId) is not string key) return;

        if (Registry.TryAbandon(key, jobId, out JobRecord? job, out string? reason))
            Broadcast(BuildJobState(job!));
        else
            Reject(peerId, $"abandon: {reason}");
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
        }
        else
        {
            Reject(peerId, $"purchase: {reason}");
        }
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

    private void Reject(int peerId, string reason)
    {
        RequestRejected?.Invoke(peerId, reason);
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.CareerRejected)
            .WriteString(reason)
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
