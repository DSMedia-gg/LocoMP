using System;
using System.Globalization;
using LocoMP.Core.Session;

namespace LocoMP.Server;

/// <summary>
/// A point-in-time health reading of the running server — everything a leak/soak watch needs, and
/// nothing more, so it's decoupled from <see cref="NetServer"/> and trivially unit-testable. The three
/// conservation flags are the load-bearing signals: they mirror the fuzz oracles
/// (<c>EconomyLedger.ConservationHolds</c>, <c>ItemRegistry.ItemConservationHolds</c>) and the M2 epoch
/// invariant's <c>StaleSnapshotsDropped</c> counter, so a soak is "assert these stay sound for hours".
/// </summary>
public readonly struct SoakSample
{
    public SoakSample(int players, int trainsets, int jobs, int items, long staleSnapshotsDropped,
                      long managedBytes, long workingSetBytes,
                      bool moneyConservationHolds, bool itemConservationHolds)
    {
        Players = players;
        Trainsets = trainsets;
        Jobs = jobs;
        Items = items;
        StaleSnapshotsDropped = staleSnapshotsDropped;
        ManagedBytes = managedBytes;
        WorkingSetBytes = workingSetBytes;
        MoneyConservationHolds = moneyConservationHolds;
        ItemConservationHolds = itemConservationHolds;
    }

    public int Players { get; }
    public int Trainsets { get; }
    public int Jobs { get; }
    public int Items { get; }
    /// <summary>The M2 consist-invariant counter: a stale-epoch snapshot the server refused to apply.
    /// Non-zero is not automatically a failure (a lagging owner can produce one), but it should stay
    /// bounded — a monotonically climbing count under steady load is a real regression signal.</summary>
    public long StaleSnapshotsDropped { get; }
    /// <summary>
    /// The managed heap's <b>RETAINED SET</b> — what survives a full collection, not whatever happens to
    /// be allocated at this instant. Callers must read it with
    /// <c>GC.GetTotalMemory(forceFullCollection: true)</c>.
    ///
    /// <para><b>Why the contract is this strict.</b> Measured on a real 5.5-minute soak, the instantaneous
    /// heap <i>sawtooths</i>: it climbs ~3.3 MB per 20 s to 14–17 MB and collapses on gen2 GC roughly
    /// every 100 s. That is a peak-to-floor ratio of ~4.3×, which on its own exceeds the leak factor —
    /// so judging an instantaneous sample means the verdict depends on where in the sawtooth the reading
    /// lands. The retained set has no sawtooth: across those same three GC cycles the post-collection
    /// floor was flat at 3.6 / 3.9 / 3.9 MB. A leak is precisely a rising floor, so measuring the floor
    /// directly is the only reading that answers the question being asked.</para>
    ///
    /// <para>The cost — a blocking gen2 collection — is paid only when a report is actually due (see
    /// <see cref="SoakReporter.Due"/>), and only on a server that opted into <c>--soak-report</c>.</para>
    /// </summary>
    public long ManagedBytes { get; }

    /// <summary>Process working set. Recorded for context only, never judged: it includes the GC's
    /// unreturned reservations, so it legitimately plateaus well above the retained set.</summary>
    public long WorkingSetBytes { get; }
    public bool MoneyConservationHolds { get; }
    public bool ItemConservationHolds { get; }
}

/// <summary>
/// Interval-driven soak/health reporter for a long unattended run (the M6-B "24 h bot soak" exit).
/// Clock-driven like <see cref="LocoMP.Core.Persistence.Autosaver"/> so tests advance time by hand, and
/// — the point — it <b>latches</b> UNHEALTHY the instant a conservation oracle breaks or the managed
/// heap runs away, so an overnight run's final line and exit code tell you whether the world stayed
/// sound without anyone watching. It only formats and judges; the frontend loop supplies the sample.
/// </summary>
public sealed class SoakReporter
{
    private readonly IClock _clock;
    private readonly long _intervalMs;
    private long _nextDueMs;
    private long _startMs;
    private long _baselineManaged;   // the first report's RETAINED set — the leak yardstick
    private bool _started;

    // How far the RETAINED set may drift above its baseline before we call it a leak.
    //
    // This was 4x when the sample was an instantaneous heap reading, where it had to absorb the whole
    // GC sawtooth (measured peak/floor ~4.3x — the threshold was inside the noise, which is what made
    // the oracle unusable). Post-collection there is no sawtooth to absorb: the signal is floor DRIFT,
    // not magnitude, so the same 4x would need 15.6 MB against a 3.9 MB floor before firing — a precise
    // instrument with a blunt threshold bolted on.
    //
    // Calibrated against a measured 5.5-minute soak (4 trains, 8 bots, 120 joins, 37k poses): retained
    // went 2.0 -> 2.5 MB, i.e. 1.25x, flat from 2 minutes on. 2x leaves ~3x that drift as headroom while
    // still catching a doubling.
    //
    // CAUTION: this is calibrated on workstation GC on a dev PC. A different GC mode or a container
    // memory limit changes heap sizing, so re-baseline before trusting it elsewhere (see the csproj's
    // pinned ServerGarbageCollection). A slope test across reports would be
    // the mode-independent successor to a fixed multiple.
    private const double MemoryLeakFactor = 2.0;

    // A ratio alone is meaningless against a tiny floor — found by the first containerized soak
    // (2026-08-03): a BARE server (no topology, the compose default) baselines at ~0.1 MB
    // retained on its empty first report, so the ~0.4 MB of ordinary roster/transport state that
    // 8 connecting bots add read as ">2× baseline" and latched a false UNHEALTHY. So a runaway
    // needs BOTH: the ratio (drift against a meaningful floor) and an absolute rise no amount of
    // legitimate connection state produces — measured legit load contribution is ~0.4-0.5 MB on
    // both rigs; a real leak crosses 8 MB of RETAINED growth without breaking stride overnight.
    private const long MemoryLeakMinDeltaBytes = 8 * 1024 * 1024;

    public SoakReporter(IClock clock, long intervalMs)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (intervalMs < 1) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _intervalMs = intervalMs;
        _nextDueMs = clock.NowMs; // the first Poll is due immediately — it prints the baseline line
    }

    /// <summary>How many health lines have been emitted.</summary>
    public long ReportsWritten { get; private set; }
    /// <summary>The high-water mark of the managed heap across the run.</summary>
    public long PeakManagedBytes { get; private set; }
    /// <summary>Latches true the first time any report is judged unhealthy and never clears — the
    /// dedicated server returns a non-zero exit code when this is set, so automation can gate on it.</summary>
    public bool EverUnhealthy { get; private set; }

    /// <summary>Cheap check the frontend uses to avoid building a sample (which queries process memory)
    /// on every tick — only gather the sample when a report is actually due.</summary>
    public bool Due() => _clock.NowMs >= _nextDueMs;

    /// <summary>Returns a formatted health line when the interval has elapsed, else null. Latches
    /// <see cref="EverUnhealthy"/> on any breached invariant or a runaway heap.</summary>
    public string? Poll(in SoakSample s)
    {
        if (_clock.NowMs < _nextDueMs) return null;
        if (!_started) { _started = true; _startMs = _clock.NowMs; _baselineManaged = s.ManagedBytes; }
        _nextDueMs = _clock.NowMs + _intervalMs;
        ReportsWritten++;
        if (s.ManagedBytes > PeakManagedBytes) PeakManagedBytes = s.ManagedBytes;

        bool memoryLeak = _baselineManaged > 0 && s.ManagedBytes > _baselineManaged * MemoryLeakFactor
                          && s.ManagedBytes - _baselineManaged > MemoryLeakMinDeltaBytes;
        bool healthy = s.MoneyConservationHolds && s.ItemConservationHolds && !memoryLeak;
        if (!healthy) EverUnhealthy = true;

        var uptime = TimeSpan.FromMilliseconds(Math.Max(0, _clock.NowMs - _startMs));
        string verdict = healthy ? "OK" : "⚠ UNHEALTHY";
        string line =
            $"[soak] {verdict} | up {uptime:hh\\:mm\\:ss} | players {s.Players} | sets {s.Trainsets} | " +
            $"jobs {s.Jobs} | items {s.Items} | stale {s.StaleSnapshotsDropped} | " +
            $"heap {Mb(s.ManagedBytes)} MB (peak {Mb(PeakManagedBytes)}) | ws {Mb(s.WorkingSetBytes)} MB";
        if (!healthy)
        {
            if (!s.MoneyConservationHolds) line += " | MONEY-CONSERVATION-BROKEN";
            if (!s.ItemConservationHolds) line += " | ITEM-CONSERVATION-BROKEN";
            if (memoryLeak) line += $" | HEAP-RUNAWAY (>{MemoryLeakFactor:F0}× baseline {Mb(_baselineManaged)} MB)";
        }
        return line;
    }

    /// <summary>
    /// The managed heap's RETAINED set: what is still reachable after finalizers have run and the
    /// resulting garbage has been collected. This is the reading <see cref="SoakSample.ManagedBytes"/>
    /// requires.
    ///
    /// <para><b>Why three calls and not one.</b> A single collection is not enough: an object with a
    /// finalizer SURVIVES the collection that finds it unreachable — it is promoted to the finalization
    /// queue, its finalizer runs afterwards, and only the NEXT collection reclaims it. So a one-shot
    /// <c>GC.GetTotalMemory(forceFullCollection: true)</c> still counts every finalizable object as
    /// live, and a build-up of those looks exactly like the leak this harness exists to detect. The
    /// canonical collect → drain finalizers → collect sequence removes that false signal.</para>
    ///
    /// <para><b>Blocking.</b> Call it on the report cadence only, and prefer an interval of 30 s or more
    /// for a real soak — frequent stalls make the run stop being representative of unattended
    /// behaviour.</para>
    /// </summary>
    public static long ReadRetainedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: false); // already collected — don't pay for a third
    }

    /// <summary>A one-line end-of-run verdict for the shutdown banner + automation logs.</summary>
    public string Summary() =>
        $"[soak] {(EverUnhealthy ? "FAIL — an invariant broke during the run (see the UNHEALTHY line above)" : "PASS — all invariants held")}" +
        $" | {ReportsWritten} report(s) | peak heap {Mb(PeakManagedBytes)} MB";

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
}
