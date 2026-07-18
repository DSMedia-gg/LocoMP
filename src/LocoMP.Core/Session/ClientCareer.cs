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

    /// <summary>The mirrored job board, keyed by job id. Completed jobs leave the board after
    /// <see cref="JobChanged"/> fires for them.</summary>
    public IReadOnlyDictionary<int, ClientJob> Jobs => _jobs;

    public event Action? CareerStateReceived;
    public event Action<ClientJob>? JobAdded;
    public event Action<ClientJob>? JobChanged;
    public event Action<long>? WalletChanged;                       // new effective balance
    public event Action<string>? LicenseGranted;                    // licenseId
    public event Action<EconomyEventKind, long, string>? EconomyEventReceived; // (kind, cents, reason)

    /// <summary>The server refused one of our proposals; carries the exact reason (03 §10 spirit).</summary>
    public event Action<string>? RequestRejected;

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

    public void PurchaseLicense(string licenseId)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.LicensePurchaseRequest)
            .WriteString(licenseId)
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
                if (state == JobLifecycle.Completed) _jobs.Remove(jobId);
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
                RequestRejected?.Invoke(r.ReadString());
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
