using System;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using HarmonyLib;
using LocoMP.Core.Career;
using LocoMP.Core.Session;

namespace LocoMP.Shim;

/// <summary>
/// D13 host-native job capture: the host lets DV generate jobs normally (real cars, booklets, yard
/// logic) and mirrors every one onto the server board. `JobsManager.RegisterGeneratedJob` is the
/// single point each real job passes through; the Job's own lifecycle events drive the rest —
/// native take (booklet → validator) becomes an OPTIMISTIC server claim rolled back on refusal,
/// native completion (turn-in) becomes the payout report, native expiry retracts. The validator's
/// money printer is silenced in-session: the wage rides the policy layer into the LocoMP wallet,
/// never the game's. Scope note (M3.5a): only the HOST claims natively — remote players see
/// captured jobs as read-only until real-car replication lands; their panel hides the Claim button
/// for these.
/// </summary>
public sealed class JobCapture : IDisposable
{
    private static JobCapture? _active;

    private readonly NetClient _client;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Job> _gameJobsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _serverIdByGameId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _gameIdByServerId = new();
    private readonly List<Job> _subscribed = new();
    private bool _applying; // reentry guard around native operations we initiate ourselves

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(
            AccessTools.Method(typeof(JobsManager), nameof(JobsManager.RegisterGeneratedJob)),
            postfix: new HarmonyMethod(typeof(JobCapture), nameof(RegisteredPostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(JobsManager), nameof(JobsManager.TakeJob)),
            postfix: new HarmonyMethod(typeof(JobCapture), nameof(TakenPostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(MoneyPrinterJobValidator), nameof(MoneyPrinterJobValidator.PrintPayment)),
            prefix: new HarmonyMethod(typeof(JobCapture), nameof(PrintPaymentPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(JobValidator), nameof(JobValidator.ProcessJobOverview)),
            prefix: new HarmonyMethod(typeof(JobCapture), nameof(ProcessJobOverviewPrefix)));
        log("[career] host job capture installed (engages while hosting)");
    }

    /// <summary>
    /// D14 pre-gate: refuse a doomed take BEFORE the game consumes the overview. The optimistic
    /// claim's rollback (AbandonJob) is destructive — DV has no way to re-shelve a taken job, so a
    /// server refusal AFTER the native take costs the physical leaflet. Everything the server
    /// would refuse a solo host for is knowable from the client mirror (licenses live in the
    /// native check itself now, via LicenseSync), so refusing here keeps the leaflet in hand and
    /// the error sound tells the player the validator said no.
    /// </summary>
    private static bool ProcessJobOverviewPrefix(JobValidator __instance, JobOverview jobOverview)
    {
        JobCapture? active = _active;
        if (active == null) return true;
        string? refusal = active.WhyUnclaimable(jobOverview == null ? null : jobOverview.job);
        if (refusal == null) return true;

        active._log($"[career] take refused at the validator: {refusal} (leaflet kept)");
        active.TakeRefused?.Invoke(refusal);
        __instance.PlayErrorSound();
        return false;
    }

    private static void RegisteredPostfix(Job job) => _active?.OnGenerated(job);

    private static void TakenPostfix(Job job, bool takenViaLoadGame)
    {
        if (!takenViaLoadGame) _active?.OnTakenNatively(job);
    }

    /// <summary>In a session the wage is committed by the server into the LocoMP wallet — printing
    /// the game's cash on top would duplicate the payout into the host's SP economy (03 §7).</summary>
    private static bool PrintPaymentPrefix() => _active == null;

    /// <summary>Why the server would refuse claiming this job right now — null when it wouldn't.
    /// Only Available jobs are gated: anything else through the validator (turn-ins, reprints) is
    /// native flow this class hears about via the Job's own events.</summary>
    private string? WhyUnclaimable(Job? job)
    {
        if (job == null || _applying || job.State != JobState.Available) return null;
        if (!_serverIdByGameId.TryGetValue(job.ID, out int serverId))
            return $"{job.ID} is not on the multiplayer board yet — give it a second and try again";
        if (!_client.Career.Jobs.TryGetValue(serverId, out ClientJob? mirror)) return null;
        if (mirror.State == JobLifecycle.Available) return null;
        if (mirror.State == JobLifecycle.Claimed && mirror.ClaimantPeerId == _client.LocalId) return null;
        string who = mirror.ClaimantName.Length > 0 ? mirror.ClaimantName : "another player";
        return mirror.State == JobLifecycle.Claimed
            ? $"{job.ID} is already claimed by {who}"
            : $"{job.ID} is no longer available on the board";
    }

    /// <summary>A take was refused at the validator (pre-gate) — for the panel toast.</summary>
    public event Action<string>? TakeRefused;

    public JobCapture(NetClient client, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _log = log;
        _active = this;

        _client.Career.JobAdded += OnServerJobAdded;
        _client.Career.RequestRejected += OnServerRejected;
        _client.Accepted += OnAccepted;
        if (_client.Joined) SweepExistingJobs();
    }

    private void OnAccepted(int _) => SweepExistingJobs();

    /// <summary>Register every available job already in the world — pre-session generation, plus
    /// anything generated in the gap before our join was admitted (the postfix no-ops until then).</summary>
    private void SweepExistingJobs()
    {
        JobsManager? manager = JobsManager.Instance;
        if (manager == null) return;
        int offered = 0;
        foreach (Job job in manager.allJobs.ToList())
        {
            if (job == null || job.State != JobState.Available) continue;
            if (_serverIdByGameId.ContainsKey(job.ID)) continue;
            OnGenerated(job);
            offered++;
        }
        if (offered > 0) _log($"[career] offered {offered} existing world job(s) to the board");
    }

    private void OnGenerated(Job job)
    {
        if (job == null || job.State != JobState.Available) return;
        if (!_gameJobsById.ContainsKey(job.ID))
        {
            _gameJobsById[job.ID] = job;
            job.JobCompleted += OnNativeCompleted;
            job.JobAbandoned += OnNativeAbandoned;
            job.JobExpired += OnNativeExpired;
            _subscribed.Add(job);
        }
        if (_serverIdByGameId.ContainsKey(job.ID)) return; // re-generation echo — already on the board
        _client.Career.RegisterExternalJob(BuildProposal(job));
    }

    private static JobDef BuildProposal(Job job)
    {
        int carCount = 1;
        JobsManager? manager = JobsManager.Instance;
        if (manager != null && manager.jobToJobCars.TryGetValue(job, out HashSet<Car> cars))
            carCount = Math.Max(1, cars.Count);

        string[] licenses = JobLicenseType_v2.ToV2List(job.requiredLicenses).Select(v2 => v2.id).ToArray();
        long payoutCents = (long)(job.GetBasePaymentForTheJob() * 100f);
        string destination = job.chainData.chainDestinationYardId;
        var tasks = new[] { new JobTaskDef(JobTaskKind.Haul, destination) };
        return new JobDef(0, job.jobType.ToString(), job.chainData.chainOriginYardId, destination,
            "cars", carCount, payoutCents, licenses, tasks, job.ID);
    }

    // ── native lifecycle → server proposals ──

    /// <summary>The player put the overview through the validator: the native take already
    /// happened, so claim OPTIMISTICALLY — a refusal rolls the native job back below.</summary>
    private void OnTakenNatively(Job job)
    {
        if (_applying || job == null) return;
        if (!_serverIdByGameId.TryGetValue(job.ID, out int serverId))
        {
            _log($"[career] {job.ID} taken before its board registration committed — not claimable, abandoning natively");
            RunNative(() => JobsManager.Instance?.AbandonJob(job));
            return;
        }
        if (_client.Career.Jobs.TryGetValue(serverId, out ClientJob? mirror) &&
            mirror.State == JobLifecycle.Claimed && mirror.ClaimantPeerId == _client.LocalId)
        {
            return; // already ours server-side
        }
        _client.Career.ClaimJob(serverId);
    }

    /// <summary>Turn-in: the game's own task tree validated the work. Report our single Haul step;
    /// the server mints the payout into the claimant's policy-routed wallet.</summary>
    private void OnNativeCompleted(Job job)
    {
        if (!_serverIdByGameId.TryGetValue(job.ID, out int serverId)) return;
        _client.Career.ReportTask(serverId, 0);
        _log($"[career] {job.ID} completed natively — reporting for payout");
        Forget(job);
    }

    private void OnNativeAbandoned(Job job)
    {
        if (!_applying &&
            _serverIdByGameId.TryGetValue(job.ID, out int serverId) &&
            _client.Career.Jobs.TryGetValue(serverId, out ClientJob? mirror) &&
            mirror.State == JobLifecycle.Claimed && mirror.ClaimantPeerId == _client.LocalId)
        {
            _client.Career.AbandonJob(serverId);
        }
        Forget(job);
    }

    private void OnNativeExpired(Job job)
    {
        if (_serverIdByGameId.TryGetValue(job.ID, out int serverId) &&
            _client.Career.Jobs.TryGetValue(serverId, out ClientJob? mirror) &&
            mirror.State == JobLifecycle.Available)
        {
            _client.Career.RetractJob(serverId);
        }
        Forget(job);
    }

    // ── server commits → native follow-up ──

    private void OnServerJobAdded(ClientJob job)
    {
        if (job.Def.GameId.Length == 0) return;
        _serverIdByGameId[job.Def.GameId] = job.Def.Id;
        _gameIdByServerId[job.Def.Id] = job.Def.GameId;
    }

    /// <summary>Our optimistic native take lost — a true race the pre-gate couldn't see (its
    /// mirror was current at take time). AbandonJob is the game's only "untake" primitive and it
    /// is DESTRUCTIVE: the job is dead natively and the leaflet is gone, so the board entry is
    /// retracted too — leaving it would advertise a job no world can deliver (a ghost). The
    /// pre-gate exists precisely to make this path unreachable in normal play.</summary>
    private void OnServerRejected(string reason, int jobId)
    {
        if (jobId == 0 || !reason.StartsWith("claim:", StringComparison.Ordinal)) return;
        if (!_gameIdByServerId.TryGetValue(jobId, out string? gameId)) return;
        if (!_gameJobsById.TryGetValue(gameId, out Job? job)) return;
        if (job.State != JobState.InProgress) return; // nothing to roll back

        RunNative(() => JobsManager.Instance?.AbandonJob(job));
        _client.Career.RetractJob(jobId);
        _log($"[career] claim refused ({reason}) — {gameId} rolled back natively; the game cannot " +
             "re-shelve a taken job, so it is LOST and retracted from the board");
    }

    private void RunNative(Action action)
    {
        _applying = true;
        try { action(); }
        catch (Exception e) { _log("[career] native rollback failed: " + e.Message); }
        finally { _applying = false; }
    }

    private void Forget(Job job)
    {
        _gameJobsById.Remove(job.ID);
        // The server-id mapping stays: late JobState traffic for it must still resolve.
    }

    public void Dispose()
    {
        _active = null;
        _client.Career.JobAdded -= OnServerJobAdded;
        _client.Career.RequestRejected -= OnServerRejected;
        _client.Accepted -= OnAccepted;
        foreach (Job job in _subscribed)
        {
            job.JobCompleted -= OnNativeCompleted;
            job.JobAbandoned -= OnNativeAbandoned;
            job.JobExpired -= OnNativeExpired;
        }
        _subscribed.Clear();
        _gameJobsById.Clear();
    }
}
