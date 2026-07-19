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
/// never the game's.
///
/// M3.5c remote claim parity: a REMOTE player's board claim is mirrored by taking the job natively
/// on their behalf (<c>JobsManager.TakeJob</c> is public and DV's "taken" is global world state —
/// no booklet prints, no player context needed); their completion report arrives back as a
/// CompleteQuery the host answers from <c>TryToCompleteAJob</c> — the game's own task tree is the
/// validator, wherever the claimant is. A released external claim kills the job everywhere
/// (abandoned ≠ available in DV), so the board and the world can never disagree about liveness.
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
        _client.Career.JobChanged += OnServerJobChanged;
        _client.Career.RequestRejected += OnServerRejected;
        _client.Career.CompleteQueryReceived += OnCompleteQuery;
        _client.Accepted += OnAccepted;
        if (_client.Joined) SweepExistingJobs();
    }

    private bool _sweepDone; // the live world's jobs are known — board reconciliation may run

    private void OnAccepted(int _) => SweepExistingJobs();

    /// <summary>Register every available job already in the world — pre-session generation, plus
    /// anything generated in the gap before our join was admitted (the postfix no-ops until then).</summary>
    private void SweepExistingJobs()
    {
        JobsManager? manager = JobsManager.Instance;
        if (manager == null) return;
        _sweepDone = true;
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
        // The task param carries the REAL route (tracks, load/unload steps) read from the native
        // task tree — the info the printed booklet would show. Without it a remote claimant (who
        // gets no booklet until M4's item sync) cannot know which track completes the job.
        string route = DescribeTaskTree(job);
        var tasks = new[] { new JobTaskDef(JobTaskKind.Haul, route.Length > 0 ? route : destination) };
        return new JobDef(0, job.jobType.ToString(), job.chainData.chainOriginYardId, destination,
            "cars", carCount, payoutCents, licenses, tasks, job.ID);
    }

    /// <summary>The booklet's essence as one line: every leaf task in order, with its real track
    /// ids ("load @ SM-A1-L, move → SM-B4-O"). TaskData is the game's own uniform flattening —
    /// one recursive walk covers every job type, sequential and parallel alike.</summary>
    private static string DescribeTaskTree(Job job)
    {
        try
        {
            var steps = new List<string>();
            foreach (Task task in job.tasks) CollectSteps(task.GetTaskData(), steps);
            string route = string.Join(", ", steps.ToArray());
            return route.Length > 500 ? route.Substring(0, 500) + "…" : route;
        }
        catch
        {
            return ""; // the yard-level destination still rides the def — degrade, don't fail
        }
    }

    private static void CollectSteps(TaskData? data, List<string> steps)
    {
        if (data == null) return;
        if (data.nestedTasks != null && data.nestedTasks.Count > 0)
        {
            // nestedTasks holds live Task objects (not TaskData) — flatten each via GetTaskData.
            foreach (Task nested in data.nestedTasks) CollectSteps(nested.GetTaskData(), steps);
            return;
        }
        switch (data.type)
        {
            case TaskType.Transport:
                steps.Add($"move{CarSpan(data.cars)} → {TrackName(data.destinationTrack)}");
                break;
            case TaskType.Warehouse:
                string verb = data.warehouseTaskType == WarehouseTaskType.Loading ? "load" : "unload";
                steps.Add($"{verb}{CarSpan(data.cars)} @ {TrackName(data.destinationTrack ?? data.startTrack)}");
                break;
        }
    }

    /// <summary>The step's exact cars as "[N× first … last]" — the warehouse machine services THE
    /// job's specific cars, not any empty car of the right type, so without these ids a helper
    /// assembles the wrong consist and gets "no loadable trains" (run-A finding).</summary>
    private static string CarSpan(List<Car>? cars)
    {
        try
        {
            if (cars == null || cars.Count == 0) return "";
            string first = cars[0] != null ? cars[0].ID : "?";
            if (cars.Count == 1) return $" [{first}]";
            string last = cars[cars.Count - 1] != null ? cars[cars.Count - 1].ID : "?";
            return $" [{cars.Count}× {first} … {last}]";
        }
        catch
        {
            return "";
        }
    }

    private static string TrackName(Track? track)
    {
        try { return track != null && track.ID != null ? track.ID.FullDisplayID : "?"; }
        catch { return "?"; }
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

    /// <summary>Turn-in: the game's own task tree validated the work. When WE hold the claim,
    /// report our single Haul step and the server mints the payout. When a REMOTE player holds it,
    /// this completion happened inside <see cref="OnCompleteQuery"/>'s validation call — the reply
    /// carries the verdict, so reporting here would only earn a "not claimed by you" rejection.</summary>
    private void OnNativeCompleted(Job job)
    {
        if (_serverIdByGameId.TryGetValue(job.ID, out int serverId))
        {
            bool mine = !_client.Career.Jobs.TryGetValue(serverId, out ClientJob? mirror) ||
                        mirror.State != JobLifecycle.Claimed || mirror.ClaimantPeerId == _client.LocalId;
            if (mine)
            {
                _client.Career.ReportTask(serverId, 0);
                _log($"[career] {job.ID} completed natively — reporting for payout");
            }
            else
            {
                _log($"[career] {job.ID} completed natively for a remote claimant — verdict rides the reply");
            }
        }
        Forget(job);
    }

    /// <summary>M3.5c: mirror board-side lifecycle onto the native world. A remote claim takes the
    /// job natively on the claimant's behalf (stops the host double-taking it and starts DV's own
    /// bookkeeping); a released external claim (abandon/grace/TTL — broadcast as Expired) kills
    /// the native job, because DV cannot re-shelve a taken job.</summary>
    private void OnServerJobChanged(ClientJob job)
    {
        if (job.Def.GameId.Length == 0) return;
        if (!_gameJobsById.TryGetValue(job.Def.GameId, out Job? native) || native == null)
        {
            // Should be unreachable now that ghosts are reconciled away — if a claim still lands
            // on one, say so loudly instead of silently skipping the native take.
            if (job.State == JobLifecycle.Claimed && job.ClaimantPeerId != _client.LocalId)
                _log($"[career] WARNING: {job.Def.GameId} claimed by {job.ClaimantName} but has no " +
                     "native job in this world — a GHOST claim; completion will be refused");
            return;
        }

        if (job.State == JobLifecycle.Claimed &&
            job.ClaimantPeerId != 0 && job.ClaimantPeerId != _client.LocalId &&
            native.State == JobState.Available)
        {
            string who = job.ClaimantName.Length > 0 ? job.ClaimantName : $"player {job.ClaimantPeerId}";
            RunNative(() => JobsManager.Instance?.TakeJob(native, false));
            _log($"[career] {job.Def.GameId} claimed by {who} — taken natively on their behalf");
        }
        else if (job.State == JobLifecycle.Expired && native.State == JobState.InProgress)
        {
            RunNative(() => JobsManager.Instance?.AbandonJob(native));
            _log($"[career] {job.Def.GameId} released — abandoned natively (a taken job cannot return to the world)");
        }
    }

    /// <summary>M3.5c: the server asks whether a remotely claimed job is really finished. The
    /// game's own completion check answers — <c>TryToCompleteAJob</c> walks the task tree and, on
    /// success, completes the job for real (payout stays silenced by the money-printer prefix;
    /// the wage is minted server-side into the claimant's policy wallet).</summary>
    private void OnCompleteQuery(int serverJobId)
    {
        if (!_gameIdByServerId.TryGetValue(serverJobId, out string? gameId) ||
            !_gameJobsById.TryGetValue(gameId, out Job? native) || native == null)
        {
            _client.Career.SendCompleteReply(serverJobId, false, "job not found in the host world");
            return;
        }
        if (native.State != JobState.InProgress)
        {
            _client.Career.SendCompleteReply(serverJobId, native.State == JobState.Completed,
                $"job is {native.State} in the host world");
            return;
        }

        JobState verdict = JobState.InProgress;
        RunNative(() =>
        {
            JobsManager? manager = JobsManager.Instance;
            if (manager != null) verdict = manager.TryToCompleteAJob(native);
        });
        bool ok = verdict == JobState.Completed;
        _client.Career.SendCompleteReply(serverJobId, ok, ok ? "" : "the cars are not delivered yet");
        _log($"[career] {gameId}: remote completion check → {(ok ? "COMPLETE — claimant gets paid" : "not finished")}");
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
        // Claimed or not: a natively dead job can never complete, so the board entry goes —
        // the server ends any claim honestly (the claimant gets an explicit toast). Far-station
        // jobs expire under remote claimants because the world's lifecycle follows the HOST's
        // presence (run-A finding); the dedicated server (M6) is the real fix for that.
        if (_serverIdByGameId.TryGetValue(job.ID, out int serverId) &&
            _client.Career.Jobs.ContainsKey(serverId))
        {
            _client.Career.RetractJob(serverId);
            _log($"[career] {job.ID} expired natively — retracting from the board (any claim ends)");
        }
        Forget(job);
    }

    // ── server commits → native follow-up ──

    private void OnServerJobAdded(ClientJob job)
    {
        if (job.Def.GameId.Length == 0) return;
        _serverIdByGameId[job.Def.GameId] = job.Def.Id;
        _gameIdByServerId[job.Def.Id] = job.Def.GameId;

        // Reconcile a RESUMED board against the live world (M3.5c run-A finding): a saved
        // external job with no native counterpart here is a ghost — claimable, backed by
        // nothing. Retract it before anyone wastes a claim. Only meaningful once the sweep has
        // seen the live world; the saves no longer persist available externals, so this is
        // strictly a cleaner for boards written before that change (and a belt-and-braces
        // guard after it).
        if (_sweepDone && job.State == JobLifecycle.Available && !_gameJobsById.ContainsKey(job.Def.GameId))
        {
            _client.Career.RetractJob(job.Def.Id);
            _log($"[career] {job.Def.GameId} is on the saved board but not in this world — retracting (ghost)");
        }
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
        _client.Career.JobChanged -= OnServerJobChanged;
        _client.Career.RequestRejected -= OnServerRejected;
        _client.Career.CompleteQueryReceived -= OnCompleteQuery;
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
