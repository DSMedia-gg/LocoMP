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
    public long ManagedBytes { get; }
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
    private long _baselineManaged;   // the leak yardstick — see RebaseOnGrowingLoad
    private int _peakPlayers;
    private bool _started;

    // The managed heap may legitimately grow to this multiple of the baseline before we call it a
    // leak — GC timing, steady-state caches and the first-report warm-up all inflate it, so this is
    // deliberately generous. The hard, exact signals are the conservation flags, not memory.
    private const double MemoryLeakFactor = 4.0;

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
        RebaseOnGrowingLoad(s);

        bool memoryLeak = _baselineManaged > 0 && s.ManagedBytes > _baselineManaged * MemoryLeakFactor;
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
    /// Move the leak yardstick up while the session is still GROWING — heap that arrives with new
    /// players is load, not a leak.
    ///
    /// <para><b>Why this exists.</b> The baseline used to be simply the first report's heap, and the
    /// first report fires immediately, on an <i>empty</i> server. Load always arrives afterwards, so a
    /// perfectly healthy run blew 4× purely from working set: the first real-exe soak (90 s, 4 trains, an
    /// 8-bot swarm, 24 joins) latched HEAP-RUNAWAY at 13.0 MB against a 3.0 MB no-players baseline and
    /// exited FAIL, while every exact oracle — money, items, trainset count, stale snapshots — held. An
    /// endurance harness that cries wolf on every successful overnight run is worse than none, because
    /// the exit code is the whole product.</para>
    ///
    /// <para><b>Why peak player count is the right trigger.</b> A soak's swarm reaches its size and then
    /// churns within it, so the peak stops climbing early and the baseline FREEZES — from that moment any
    /// further growth is unexplained by load, which is exactly the property an overnight verdict needs.
    /// The trade is deliberate and worth naming: a leak occurring <i>while</i> the session is still
    /// ramping up is absorbed into the baseline. That is the correct bias here — a missed leak costs one
    /// more soak run, a false FAIL costs trust in every run.</para>
    /// </summary>
    private void RebaseOnGrowingLoad(in SoakSample s)
    {
        if (s.Players <= _peakPlayers) return;
        _peakPlayers = s.Players;
        // Never ratchet DOWN: a quiet moment mid-run must not tighten the yardstick and manufacture a
        // leak out of the next legitimate reload.
        if (s.ManagedBytes > _baselineManaged) _baselineManaged = s.ManagedBytes;
    }

    /// <summary>A one-line end-of-run verdict for the shutdown banner + automation logs.</summary>
    public string Summary() =>
        $"[soak] {(EverUnhealthy ? "FAIL — an invariant broke during the run (see the UNHEALTHY line above)" : "PASS — all invariants held")}" +
        $" | {ReportsWritten} report(s) | peak heap {Mb(PeakManagedBytes)} MB";

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
}
