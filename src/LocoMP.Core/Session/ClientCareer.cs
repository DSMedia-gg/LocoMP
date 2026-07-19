using System;
using System.Collections.Generic;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>A client's read-only view of one job on the board. Claimants are identified by session
/// peer id + display name (peer 0 with a name = claimant currently in their grace window).</summary>
public sealed class ClientJob
{
    internal ClientJob(JobDef def) => Def = def;

    public JobDef Def { get; }
    public JobLifecycle State { get; internal set; }
    public int ClaimantPeerId { get; internal set; }
    public string ClaimantName { get; internal set; } = string.Empty;
    public int NextTaskIndex { get; internal set; }
}

/// <summary>
/// The client's career subsystem, owned by <see cref="NetClient"/>: mirrors of the board, own
/// wallet, and own license scope on the receive side, and the propose calls (claim/report/abandon/
/// purchase) the Shim's job UI drives on the send side. Everything here is proposals and mirrors —
/// wallets and job states only ever change when the server says so (03 §3; 02 §4: economy deltas
/// are server-computed, never client-supplied).
/// </summary>
public sealed class ClientCareer
{
    private readonly ITransport _transport;
    private readonly Func<bool> _joined;
    private readonly HashSet<string> _licenses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _licenseCatalog = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ClientJob> _jobs = new();

    internal ClientCareer(ITransport transport, Func<bool> joined)
    {
        _transport = transport;
        _joined = joined;
    }

    /// <summary>The session's progression preset, learned from CareerState in the join burst.</summary>
    public ProgressionPreset Preset { get; private set; }

    /// <summary>Our effective wallet (own wallet per-player; the shared wallet in shared career).</summary>
    public long BalanceCents { get; private set; }

    /// <summary>Our effective license scope (own set per-player; the shared set in shared career).</summary>
    public IReadOnlyCollection<string> Licenses => _licenses;

    /// <summary>What the server sells: licenseId → price in cents (from the career burst).</summary>
    public IReadOnlyDictionary<string, long> LicenseCatalog => _licenseCatalog;

    /// <summary>The mirrored job board, keyed by job id. Completed jobs leave the board after
    /// <see cref="JobChanged"/> fires for them.</summary>
    public IReadOnlyDictionary<int, ClientJob> Jobs => _jobs;

    public event Action? CareerStateReceived;
    public event Action<ClientJob>? JobAdded;
    public event Action<ClientJob>? JobChanged;
    public event Action<long>? WalletChanged;                       // new effective balance
    public event Action<string>? LicenseGranted;                    // licenseId
    public event Action<EconomyEventKind, long, string>? EconomyEventReceived; // (kind, cents, reason)

    /// <summary>The server refused one of our proposals: (reason, jobId — 0 when not job-related).
    /// The job id lets an optimistic native claim roll itself back (D13).</summary>
    public event Action<string, int>? RequestRejected;

    /// <summary>World source only (M3.5c): the server asks whether a remotely claimed captured job
    /// is actually complete in the native world — answer with <see cref="SendCompleteReply"/>.</summary>
    public event Action<int>? CompleteQueryReceived; // (jobId)

    // ── send side (all silently no-op until joined, matching the other subsystems) ──

    public void ClaimJob(int jobId) => SendIdOnly(MessageType.JobClaimRequest, jobId);

    /// <summary>Report the job's next task step done. The index must be exactly the next one.</summary>
    public void ReportTask(int jobId, int taskIndex)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.JobTaskReport)
            .WriteVarUInt((uint)jobId)
            .WriteVarUInt((uint)taskIndex)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    public void AbandonJob(int jobId) => SendIdOnly(MessageType.JobAbandonRequest, jobId);

    /// <summary>World source only (D13): mirror a game-generated job onto the server board. The
    /// proposal's id is ignored — the commit comes back as JobCreated with the server's id and
    /// the same GameId for correlation.</summary>
    public void RegisterExternalJob(JobDef proposal)
    {
        if (!_joined()) return;
        var w = new PacketWriter(128).WriteByte((byte)MessageType.JobRegister);
        CareerCodec.WriteJobDef(w, proposal);
        _transport.Send(NetProtocol.ServerPeer, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>World source only (D13): drop an unclaimed job whose native counterpart expired.</summary>
    public void RetractJob(int jobId) => SendIdOnly(MessageType.JobRetract, jobId);

    public void PurchaseLicense(string licenseId)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.LicensePurchaseRequest)
            .WriteString(licenseId)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>World source only (D14/M3.5c): grant a license charge-free into a policy scope.
    /// <paramref name="targetPeerId"/> 0 = own scope (the native-purchase mirror); a connected
    /// peer id = the host-admin grant (fresh guests on a mature world can't afford the board's
    /// license gates — the host hands them what they need, transparently and logged).</summary>
    public void GrantExternalLicense(string licenseId, int targetPeerId = 0)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(24)
            .WriteByte((byte)MessageType.LicenseGrantExternal)
            .WriteString(licenseId)
            .WriteVarUInt((uint)targetPeerId)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>World source only (M3.5c): the native verdict on a CompleteQuery — did the game's
    /// own task tree accept this job as finished? On ok the server commits the deferred report and
    /// mints the claimant's payout; the reason is the claimant's toast on a refusal.</summary>
    public void SendCompleteReply(int jobId, bool ok, string reason)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.JobCompleteReply)
            .WriteVarUInt((uint)jobId)
            .WriteByte(ok ? (byte)1 : (byte)0)
            .WriteString(reason)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>World source only (D14): a native register finalized a purchase against the
    /// mirrored wallet; the server burns the amount from the policy wallet.</summary>
    public void ReportExternalFee(long amountCents, string label)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.FeeExternal)
            .WriteInt64(amountCents)
            .WriteString(label)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    // ── receive side ──

    internal bool TryHandle(MessageType type, PacketReader r)
    {
        switch (type)
        {
            case MessageType.CareerState:
            {
                Preset = (ProgressionPreset)r.ReadByte();
                BalanceCents = r.ReadInt64();
                _licenses.Clear();
                foreach (string lic in CareerCodec.ReadLicenses(r)) _licenses.Add(lic);
                int catalogCount = (int)r.ReadVarUInt();
                if (catalogCount > CareerCodec.MaxLicenses)
                    throw new System.IO.InvalidDataException($"catalog count {catalogCount} out of range");
                _licenseCatalog.Clear();
                for (int i = 0; i < catalogCount; i++)
                {
                    string id = r.ReadString();
                    _licenseCatalog[id] = r.ReadInt64();
                }
                CareerStateReceived?.Invoke();
                return true;
            }
            case MessageType.JobCreated:
            {
                JobDef def = CareerCodec.ReadJobDef(r);
                var job = new ClientJob(def);
                _jobs[def.Id] = job;
                JobAdded?.Invoke(job);
                return true;
            }
            case MessageType.JobState:
            {
                int jobId = (int)r.ReadVarUInt();
                var state = (JobLifecycle)r.ReadByte();
                int claimantPeer = (int)r.ReadVarUInt();
                string claimantName = r.ReadString();
                int nextTask = (int)r.ReadVarUInt();
                if (!_jobs.TryGetValue(jobId, out ClientJob? job)) return true; // unknown id — stale, drop
                job.State = state;
                job.ClaimantPeerId = claimantPeer;
                job.ClaimantName = claimantName;
                job.NextTaskIndex = nextTask;
                JobChanged?.Invoke(job);
                if (state == JobLifecycle.Completed || state == JobLifecycle.Expired) _jobs.Remove(jobId);
                return true;
            }
            case MessageType.WalletState:
            {
                _ = (EconomyScope)r.ReadByte(); // scope is implied by the preset; carried for clarity
                BalanceCents = r.ReadInt64();
                WalletChanged?.Invoke(BalanceCents);
                return true;
            }
            case MessageType.LicenseState:
            {
                _ = (EconomyScope)r.ReadByte();
                string licenseId = r.ReadString();
                _licenses.Add(licenseId);
                LicenseGranted?.Invoke(licenseId);
                return true;
            }
            case MessageType.EconomyEvent:
            {
                var kind = (EconomyEventKind)r.ReadByte();
                long amount = r.ReadInt64();
                string reason = r.ReadString();
                EconomyEventReceived?.Invoke(kind, amount, reason);
                return true;
            }
            case MessageType.CareerRejected:
            {
                string reason = r.ReadString();
                int jobId = (int)r.ReadVarUInt();
                RequestRejected?.Invoke(reason, jobId);
                return true;
            }
            case MessageType.JobCompleteRequest:
            {
                int jobId = (int)r.ReadVarUInt();
                CompleteQueryReceived?.Invoke(jobId);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Wipe the mirrors on disconnect (the next join's career burst rebuilds them).</summary>
    internal void Reset()
    {
        _licenses.Clear();
        _licenseCatalog.Clear();
        _jobs.Clear();
        BalanceCents = 0;
    }

    private void SendIdOnly(MessageType type, int id)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)type)
            .WriteVarUInt((uint)id)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }
}
