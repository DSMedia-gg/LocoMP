using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocoMP.Core.Persistence;
using LocoMP.Core.Session;

namespace LocoMP.Core.Career;

/// <summary>Result of one career tick: the committed changes the session layer must broadcast.</summary>
public sealed class CareerTick
{
    /// <summary>Jobs the deterministic generator just added to the board.</summary>
    public List<JobRecord> GeneratedJobs { get; } = new();

    /// <summary>Claims released by TTL or reconnect-grace expiry — Available again, progress reset.</summary>
    public List<JobRecord> ReleasedJobs { get; } = new();
}

/// <summary>
/// The authoritative career aggregate (02 §4/§6): profiles, the money ledger, the license scopes,
/// and the job board, every touch routed through the <see cref="ProgressionPolicy"/>. This mirrors
/// <see cref="Trains.TrainsetRegistry"/>'s role for trains — ALL the career rules live here and
/// only here, game-free, so the whole economy is fuzzed headless against the conservation oracle
/// (03 §11). Money is only created by payouts and starting grants (mint) and only destroyed by
/// fees (burn); there is no client-supplied delta anywhere (03 §9).
/// </summary>
public sealed class CareerRegistry
{
    private readonly CareerConfig _config;
    private readonly IClock _clock;
    private readonly Dictionary<string, PlayerProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<int, JobRecord> _jobs = new();
    private readonly HashSet<string> _sharedLicenses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _online = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _graceUntilMs = new(StringComparer.Ordinal);
    private int _nextJobId = 1;
    private uint _rng;
    private bool _sharedGrantIssued;

    public CareerRegistry(CareerConfig config, IClock clock, CareerSaveData? restore = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Policy = new ProgressionPolicy(config.Preset);
        _rng = config.JobSeed == 0 ? 1u : config.JobSeed; // xorshift has a fixed point at 0
        if (restore != null) ApplyRestore(restore);
    }

    public ProgressionPolicy Policy { get; }
    public EconomyLedger Ledger { get; } = new();

    /// <summary>The live board: Available and Claimed jobs, keyed by job id.</summary>
    public IReadOnlyDictionary<int, JobRecord> Jobs => _jobs;

    public IReadOnlyDictionary<string, PlayerProfile> Profiles => _profiles;
    public IReadOnlyCollection<string> SharedLicenses => _sharedLicenses;

    public bool IsOnline(string playerKey) => _online.Contains(playerKey);

    public long BalanceFor(string playerKey) => Ledger.BalanceOf(Policy.WalletAccountFor(playerKey));

    /// <summary>The license scope this player's checks run against under the active preset.</summary>
    public IReadOnlyCollection<string> LicensesFor(string playerKey)
    {
        if (Policy.LicensesShared) return _sharedLicenses;
        return _profiles.TryGetValue(playerKey, out PlayerProfile? profile)
            ? profile.Licenses
            : (IReadOnlyCollection<string>)Array.Empty<string>();
    }

    // ── connection lifecycle (the session layer maps peer ids to keys; we never see peers) ──

    /// <summary>
    /// A player with this key connected. First sight creates the profile and mints the starting
    /// grant (per-player: one per player; shared: once ever for the shared wallet) — minted, not
    /// conjured, so the conservation invariant accounts for it. Reconnecting within grace simply
    /// cancels the hold: claims were never released, so restore is exact by construction.
    /// </summary>
    public PlayerProfile Connect(string playerKey, string name)
    {
        _graceUntilMs.Remove(playerKey);

        if (!_profiles.TryGetValue(playerKey, out PlayerProfile? profile))
        {
            profile = new PlayerProfile(playerKey, name);
            _profiles[playerKey] = profile;

            if (Policy.LicensesShared)
            {
                if (!_sharedGrantIssued)
                {
                    Ledger.Mint(ProgressionPolicy.SharedAccount, _config.StartingBalanceCents);
                    _sharedGrantIssued = true;
                }
            }
            else
            {
                Ledger.Mint(playerKey, _config.StartingBalanceCents);
            }
        }
        else
        {
            profile.Name = name;
        }

        // Starting licenses are a FLOOR applied on every connect (idempotent — the scope is a
        // set), not a one-time grant: a config that gains a baseline license later must not
        // strand existing profiles, and saves from before a baseline change heal on next join.
        ICollection<string> scope = Policy.LicensesShared ? _sharedLicenses : profile.Licenses;
        foreach (string lic in _config.StartingLicenses) scope.Add(lic);

        _online.Add(playerKey);
        return profile;
    }

    /// <summary>A player left: start the reconnect-grace hold (07 §M3 — their claims stay theirs
    /// until the hold lapses; <see cref="Tick"/> releases them afterwards).</summary>
    public void Disconnect(string playerKey)
    {
        if (!_online.Remove(playerKey)) return;
        _graceUntilMs[playerKey] = _clock.NowMs + _config.ReconnectGraceMs;
    }

    // ── the tick: expiries + deterministic board refill ──

    /// <summary>Advance time-driven career state. Cheap when nothing is due; call every poll.</summary>
    public CareerTick Tick()
    {
        var tick = new CareerTick();
        long now = _clock.NowMs;

        foreach (JobRecord job in _jobs.Values)
            if (job.State == JobLifecycle.Claimed && now >= job.ClaimExpiresAtMs)
                Release(job, tick);

        List<string>? lapsed = null;
        foreach (KeyValuePair<string, long> kv in _graceUntilMs)
            if (now >= kv.Value) (lapsed ??= new List<string>()).Add(kv.Key);
        if (lapsed != null)
        {
            foreach (string key in lapsed)
            {
                _graceUntilMs.Remove(key);
                foreach (JobRecord job in _jobs.Values)
                    if (job.State == JobLifecycle.Claimed && job.ClaimantKey == key)
                        Release(job, tick);
            }
        }

        if (_config.Stations.Count >= 2 && _config.JobTypes.Count > 0)
        {
            int available = 0;
            foreach (JobRecord job in _jobs.Values)
                if (job.State == JobLifecycle.Available) available++;
            while (available < _config.TargetAvailableJobs)
            {
                JobRecord job = GenerateJob();
                _jobs[job.Def.Id] = job;
                tick.GeneratedJobs.Add(job);
                available++;
            }
        }

        return tick;
    }

    // ── proposals (clients propose, the server commits — 03 §3) ──

    /// <summary>Validate + commit a claim: job available, licenses held, claim limit not hit.</summary>
    public bool TryClaim(string playerKey, int jobId, out JobRecord? job, out string? reason)
    {
        job = null;
        if (!_profiles.ContainsKey(playerKey)) { reason = "no profile"; return false; }
        if (!_jobs.TryGetValue(jobId, out JobRecord? record)) { reason = $"unknown job {jobId}"; return false; }
        if (record.State != JobLifecycle.Available) { reason = $"job {jobId} is not available"; return false; }

        IReadOnlyCollection<string> scope = LicensesFor(playerKey);
        foreach (string lic in record.Def.RequiredLicenses)
            if (!scope.Contains(lic)) { reason = $"missing license: {lic}"; return false; }

        int held = 0;
        foreach (JobRecord j in _jobs.Values)
            if (j.State == JobLifecycle.Claimed && j.ClaimantKey == playerKey) held++;
        if (held >= _config.MaxConcurrentClaims)
        {
            reason = $"claim limit reached ({_config.MaxConcurrentClaims})";
            return false;
        }

        record.State = JobLifecycle.Claimed;
        record.ClaimantKey = playerKey;
        record.NextTaskIndex = 0;
        record.ClaimExpiresAtMs = _clock.NowMs + _config.ClaimTtlMs;
        job = record;
        reason = null;
        return true;
    }

    /// <summary>
    /// Validate + commit one task step. Steps are strictly sequential — the reported index must be
    /// exactly the next one (reliable-ordered transport means no network dupes to forgive). The
    /// final step completes the job: the payout is minted into the policy-routed wallet and the job
    /// leaves the board (02 §4 — payout to claimant or shared wallet; no cash-spawns-on-host).
    /// </summary>
    public bool TryReportTask(string playerKey, int jobId, int taskIndex,
        out JobRecord? job, out bool completed, out long payoutCents, out string? reason)
    {
        job = null;
        completed = false;
        payoutCents = 0;
        if (!_jobs.TryGetValue(jobId, out JobRecord? record)) { reason = $"unknown job {jobId}"; return false; }
        if (record.State != JobLifecycle.Claimed || record.ClaimantKey != playerKey)
        {
            reason = $"job {jobId} is not claimed by you";
            return false;
        }
        if (taskIndex != record.NextTaskIndex)
        {
            reason = $"task {taskIndex} out of order (expected {record.NextTaskIndex})";
            return false;
        }

        record.NextTaskIndex++;
        if (record.NextTaskIndex >= record.Def.Tasks.Count)
        {
            record.State = JobLifecycle.Completed;
            _jobs.Remove(jobId);
            payoutCents = record.Def.PayoutCents;
            Ledger.Mint(Policy.WalletAccountFor(playerKey), payoutCents);
            completed = true;
        }
        job = record;
        reason = null;
        return true;
    }

    /// <summary>Give a claim up: the job returns to the board with progress reset.</summary>
    public bool TryAbandon(string playerKey, int jobId, out JobRecord? job, out string? reason)
    {
        job = null;
        if (!_jobs.TryGetValue(jobId, out JobRecord? record)) { reason = $"unknown job {jobId}"; return false; }
        if (record.State != JobLifecycle.Claimed || record.ClaimantKey != playerKey)
        {
            reason = $"job {jobId} is not claimed by you";
            return false;
        }

        record.State = JobLifecycle.Available;
        record.ClaimantKey = null;
        record.NextTaskIndex = 0;
        job = record;
        reason = null;
        return true;
    }

    /// <summary>
    /// Admit an externally generated job (D13 host-capture: the world source mirrors the game's
    /// own generated jobs onto the board). The server assigns the id — the proposal's is ignored —
    /// and the game's job id is deduped so a re-registration after a reload can't double a job.
    /// </summary>
    public bool TryRegisterExternal(JobDef proposal, out JobRecord? job, out string? reason)
    {
        job = null;
        if (!_config.AcceptExternalJobs) { reason = "external jobs are not accepted here"; return false; }
        if (proposal.GameId.Length == 0) { reason = "external job has no game id"; return false; }
        foreach (JobRecord existing in _jobs.Values)
        {
            if (existing.Def.GameId == proposal.GameId)
            {
                reason = $"game job {proposal.GameId} already registered";
                return false;
            }
        }

        var def = new JobDef(_nextJobId++, proposal.JobType, proposal.Origin, proposal.Destination,
            proposal.CargoKind, proposal.CarCount, proposal.PayoutCents, proposal.RequiredLicenses,
            proposal.Tasks, proposal.GameId);
        var record = new JobRecord(def);
        _jobs[def.Id] = record;
        job = record;
        reason = null;
        return true;
    }

    /// <summary>Drop an unclaimed external job whose native counterpart expired. A claimed job is
    /// never retracted out from under its claimant — the refusal tells the world source so.</summary>
    public bool TryRetract(int jobId, out JobRecord? job, out string? reason)
    {
        job = null;
        if (!_jobs.TryGetValue(jobId, out JobRecord? record)) { reason = $"unknown job {jobId}"; return false; }
        if (record.State != JobLifecycle.Available) { reason = $"job {jobId} is not available"; return false; }

        record.State = JobLifecycle.Expired;
        _jobs.Remove(jobId);
        job = record;
        reason = null;
        return true;
    }

    /// <summary>Buy a license: the fee is burned from the policy-routed wallet and the license
    /// lands in the policy-routed scope (per-player profile or the shared set).</summary>
    public bool TryPurchaseLicense(string playerKey, string licenseId, out long priceCents, out string? reason)
    {
        priceCents = 0;
        if (!_config.LicensePrices.TryGetValue(licenseId, out long price)) { reason = $"unknown license: {licenseId}"; return false; }
        if (!_profiles.TryGetValue(playerKey, out PlayerProfile? profile)) { reason = "no profile"; return false; }

        ICollection<string> scope = Policy.LicensesShared ? _sharedLicenses : profile.Licenses;
        if (scope.Contains(licenseId)) { reason = $"license already owned: {licenseId}"; return false; }
        if (!Ledger.TryBurn(Policy.WalletAccountFor(playerKey), price, out reason)) return false;

        scope.Add(licenseId);
        priceCents = price;
        return true;
    }

    /// <summary>
    /// Mirror a NATIVE license grant (D14): the license lands in the policy scope with NO ledger
    /// charge — the native register's payment arrives separately as an external fee against the
    /// mirrored wallet, so charging here would bill twice. Idempotent: an already-owned license is
    /// a success no-op (<paramref name="newlyGranted"/> false) so grant echoes can't loop. Ids the
    /// price catalog doesn't know are accepted — the game is the authority on what exists.
    /// </summary>
    public bool TryGrantExternal(string playerKey, string licenseId, out bool newlyGranted, out string? reason)
    {
        newlyGranted = false;
        if (licenseId.Length == 0) { reason = "empty license id"; return false; }
        if (!_profiles.TryGetValue(playerKey, out PlayerProfile? profile)) { reason = "no profile"; return false; }

        ICollection<string> scope = Policy.LicensesShared ? _sharedLicenses : profile.Licenses;
        if (!scope.Contains(licenseId))
        {
            scope.Add(licenseId);
            newlyGranted = true;
        }
        reason = null;
        return true;
    }

    /// <summary>Burn a finalized native purchase from the policy wallet (D14). The amount is the
    /// register's total — only overdraft is re-validated here, because the native UI gated
    /// affordability against the SAME balance (the mirror). A refusal means the mirror drifted.</summary>
    public bool TryChargeExternalFee(string playerKey, long amountCents, out string? reason)
    {
        if (amountCents <= 0) { reason = "fee must be positive"; return false; }
        if (!_profiles.ContainsKey(playerKey)) { reason = "no profile"; return false; }
        return Ledger.TryBurn(Policy.WalletAccountFor(playerKey), amountCents, out reason);
    }

    // ── persistence (v1) ──

    /// <summary>Snapshot everything for the save. Deadlines are converted to REMAINING time (the
    /// monotonic clock restarts with the process); players online right now get no grace entry —
    /// <see cref="ApplyRestore"/> grants them a fresh hold, because a restart IS their disconnect.</summary>
    public CareerSaveData Capture()
    {
        long now = _clock.NowMs;
        var save = new CareerSaveData
        {
            Preset = Policy.Preset,
            Minted = Ledger.TotalMinted,
            Burned = Ledger.TotalBurned,
            SharedGrantIssued = _sharedGrantIssued,
            NextJobId = _nextJobId,
            RngState = _rng,
        };
        foreach (KeyValuePair<string, long> kv in Ledger.Accounts) save.Accounts[kv.Key] = kv.Value;
        foreach (PlayerProfile p in _profiles.Values)
            save.Profiles.Add(new ProfileSave(p.Key, p.Name, new List<string>(p.Licenses)));
        save.SharedLicenses.AddRange(_sharedLicenses);
        foreach (JobRecord job in _jobs.Values)
        {
            long remaining = job.State == JobLifecycle.Claimed ? Math.Max(0, job.ClaimExpiresAtMs - now) : 0;
            save.Jobs.Add(new JobSave(job.Def, job.State, job.ClaimantKey ?? string.Empty, job.NextTaskIndex, remaining));
        }
        foreach (KeyValuePair<string, long> kv in _graceUntilMs)
            save.GraceRemainingMs[kv.Key] = Math.Max(0, kv.Value - now);
        return save;
    }

    private void ApplyRestore(CareerSaveData save)
    {
        if (save.Preset != Policy.Preset)
        {
            throw new InvalidDataException(
                $"save was written with the {save.Preset} preset but the server is configured for " +
                $"{Policy.Preset} — migrating wallets between presets is undefined; keep the preset");
        }

        long now = _clock.NowMs;
        Ledger.Restore(save.Accounts, save.Minted, save.Burned);
        foreach (ProfileSave p in save.Profiles)
        {
            var profile = new PlayerProfile(p.Key, p.Name);
            foreach (string lic in p.Licenses) profile.Licenses.Add(lic);
            _profiles[p.Key] = profile;
        }
        foreach (string lic in save.SharedLicenses) _sharedLicenses.Add(lic);
        _sharedGrantIssued = save.SharedGrantIssued;

        foreach (JobSave js in save.Jobs)
        {
            var record = new JobRecord(js.Def)
            {
                State = js.State,
                ClaimantKey = js.ClaimantKey.Length == 0 ? null : js.ClaimantKey,
                NextTaskIndex = js.NextTaskIndex,
            };
            if (record.State == JobLifecycle.Claimed) record.ClaimExpiresAtMs = now + js.ClaimRemainingMs;
            _jobs[record.Def.Id] = record;
        }

        foreach (KeyValuePair<string, long> kv in save.GraceRemainingMs)
            _graceUntilMs[kv.Key] = now + kv.Value;
        foreach (JobRecord job in _jobs.Values)
        {
            if (job.State != JobLifecycle.Claimed || job.ClaimantKey is null) continue;
            if (!_graceUntilMs.ContainsKey(job.ClaimantKey))
                _graceUntilMs[job.ClaimantKey] = now + _config.ReconnectGraceMs;
        }

        _nextJobId = save.NextJobId;
        _rng = save.RngState == 0 ? 1u : save.RngState;
    }

    // ── deterministic generation ──

    /// <summary>xorshift32 — identical on net48 and net8. System.Random's algorithm DIFFERS across
    /// runtimes, which would silently break "same seed ⇒ same board" between the host-embedded
    /// server (net48/Mono) and the dedicated server (net8).</summary>
    private uint NextRng()
    {
        uint x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return _rng = x;
    }

    private int NextInt(int boundExclusive) => (int)(NextRng() % (uint)boundExclusive);

    private JobRecord GenerateJob()
    {
        JobTypeSpec spec = _config.JobTypes[NextInt(_config.JobTypes.Count)];

        IReadOnlyList<string> originPool = spec.Origins.Count > 0 ? spec.Origins : _config.Stations;
        string origin = originPool[NextInt(originPool.Count)];

        // Destination: the spec's route list when it has one, minus the origin; a spec whose only
        // destination IS the origin falls back to the full map (stations >= 2 guarantees a pick).
        List<string> destPool = (spec.Destinations.Count > 0 ? spec.Destinations : _config.Stations)
            .Where(s => !string.Equals(s, origin, StringComparison.Ordinal)).ToList();
        if (destPool.Count == 0)
        {
            destPool = _config.Stations.Where(s => !string.Equals(s, origin, StringComparison.Ordinal)).ToList();
        }
        string destination = destPool[NextInt(destPool.Count)];

        int cars = spec.MinCars + NextInt(spec.MaxCars - spec.MinCars + 1);
        long payoutPerCar = spec.PayoutPerCarCents + (long)(spec.PayoutPerCarKmCents * DistanceKm(origin, destination));

        var tasks = new[]
        {
            new JobTaskDef(JobTaskKind.Load, origin),
            new JobTaskDef(JobTaskKind.Haul, destination),
            new JobTaskDef(JobTaskKind.Unload, destination),
        };
        var def = new JobDef(_nextJobId++, spec.JobType, origin, destination, spec.CargoKind, cars,
            payoutPerCar * cars, spec.RequiredLicenses, tasks);
        return new JobRecord(def);
    }

    private float DistanceKm(string a, string b)
    {
        if (_config.StationDistancesKm.TryGetValue(CareerConfig.DistanceKey(a, b), out float km)) return km;
        if (_config.StationDistancesKm.TryGetValue(CareerConfig.DistanceKey(b, a), out km)) return km;
        return 0f;
    }

    private static void Release(JobRecord job, CareerTick tick)
    {
        job.State = JobLifecycle.Available;
        job.ClaimantKey = null;
        job.NextTaskIndex = 0;
        tick.ReleasedJobs.Add(job);
    }
}
