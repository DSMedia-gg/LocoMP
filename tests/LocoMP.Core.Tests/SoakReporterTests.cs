using LocoMP.Core.Session;
using LocoMP.Server;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The dedicated server's soak/health reporter: it throttles to its interval, and — the load-bearing
/// behaviour for an unattended run — it LATCHES unhealthy the moment a conservation oracle breaks or
/// the managed heap runs away, so the run's exit code and summary line report a failure no one watched.
/// </summary>
public class SoakReporterTests
{
    private static SoakSample Healthy(long managedBytes = 10_000_000, long staleDropped = 0) =>
        new(players: 3, trainsets: 4, jobs: 8, items: 12, staleSnapshotsDropped: staleDropped,
            managedBytes: managedBytes, workingSetBytes: 80_000_000,
            moneyConservationHolds: true, itemConservationHolds: true);

    [Fact]
    public void It_emits_a_baseline_immediately_then_throttles_to_the_interval()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        Assert.NotNull(r.Poll(Healthy()));   // first poll = the baseline line, due immediately
        Assert.Null(r.Poll(Healthy()));      // same instant → throttled
        clock.Advance(999);
        Assert.Null(r.Poll(Healthy()));      // not yet
        clock.Advance(1);
        Assert.NotNull(r.Poll(Healthy()));   // interval elapsed
        Assert.Equal(2, r.ReportsWritten);
    }

    private static SoakSample With(int players, long managedBytes) =>
        new(players: players, trainsets: 4, jobs: 8, items: 12, staleSnapshotsDropped: 0,
            managedBytes: managedBytes, workingSetBytes: 80_000_000,
            moneyConservationHolds: true, itemConservationHolds: true);

    /// <summary>
    /// Regression for the false FAIL the first real-exe soak produced (2026-07-27), now expressed in the
    /// terms that actually fixed it. Because <see cref="SoakSample.ManagedBytes"/> is the post-collection
    /// RETAINED set, load no longer moves it much: the measured 5.5-minute soak (4 trains, 8 bots, 120
    /// joins) went 2.0 → 2.5 MB, flat from two minutes on. Those are the real numbers, replayed — an
    /// empty-server baseline is no longer a problem to work around, because there is barely a step.
    /// </summary>
    [Fact]
    public void A_loaded_run_stays_healthy_against_an_empty_server_baseline()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        r.Poll(With(players: 0, managedBytes: 2_000_000)); // baseline, empty — as the real run starts
        clock.Advance(1000);
        long[] measured = { 2_200_000, 2_300_000, 2_300_000, 2_400_000, 2_400_000, 2_400_000, 2_500_000 };
        foreach (long bytes in measured)
        {
            r.Poll(With(players: 8, managedBytes: bytes));
            clock.Advance(1000);
        }

        Assert.False(r.EverUnhealthy);
        Assert.Contains("PASS", r.Summary());
    }

    /// <summary>
    /// The leak signal itself: a retained set that keeps drifting up. Because the reading is
    /// post-collection there is no GC sawtooth to hide in, so the threshold can sit close to real
    /// behaviour — this run doubles, which a healthy one (1.25×) never approaches.
    /// </summary>
    [Fact]
    public void A_retained_set_that_keeps_drifting_up_latches_unhealthy()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        for (int i = 0; i <= 10; i++)
        {
            r.Poll(With(players: 8, managedBytes: 2_000_000 + i * 1_500_000)); // 2.0 → 17.0 MB
            clock.Advance(1000);
        }

        Assert.True(r.EverUnhealthy, "sustained retained-set drift is exactly what a leak looks like");
        Assert.Contains("FAIL", r.Summary());
    }

    /// <summary>
    /// Regression for the false FAIL the first CONTAINERIZED soak produced (2026-08-03): a BARE server
    /// (no topology — the compose default) baselines at ~0.1 MB retained, so the ~0.4 MB of ordinary
    /// roster/transport state that 8 connecting bots add is 5× the baseline — and a ratio alone cannot
    /// tell that apart from a leak. It is not one: it is bounded working state. The runaway verdict
    /// therefore also requires an absolute rise (8 MB) no legitimate connection state produces.
    /// </summary>
    [Fact]
    public void A_bare_server_baseline_is_not_tripped_by_ordinary_connection_state()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        r.Poll(With(players: 0, managedBytes: 100_000));   // bare server: near-zero floor
        clock.Advance(1000);
        foreach (long bytes in new long[] { 400_000, 500_000, 400_000, 500_000, 400_000 })
        {
            r.Poll(With(players: 8, managedBytes: bytes)); // 8 bots' worth of state — 4-5× the floor
            clock.Advance(1000);
        }

        Assert.False(r.EverUnhealthy, "bounded connection state on a tiny floor is not a leak");
        Assert.Contains("PASS", r.Summary());
    }

    /// <summary>The verdict needs BOTH conditions: the ratio (drift against the floor) and an absolute
    /// rise (8 MB) that bounded working state never produces. A 25% drift — the healthy measured
    /// figure — must pass; a doubling alone is still not enough on a small floor (the container
    /// finding); a doubling that has ALSO grown past the absolute floor is a leak being a leak.</summary>
    [Theory]
    [InlineData(2_500_000, false)]  // +25%: the measured healthy run
    [InlineData(3_900_000, false)]  // +95%: uncomfortable, still under the ratio bar
    [InlineData(4_100_000, false)]  // +105%: ratio tripped, but +2.1 MB is bounded-state territory
    [InlineData(9_900_000, false)]  // ~5×, +7.9 MB: still under the absolute floor — not yet a verdict
    [InlineData(11_000_000, true)]  // ~5.5×, +9 MB: both conditions — a leak being a leak
    public void The_leak_threshold_needs_both_the_ratio_and_absolute_growth(long endBytes, bool expectUnhealthy)
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        r.Poll(With(players: 8, managedBytes: 2_000_000));
        clock.Advance(1000);
        r.Poll(With(players: 8, managedBytes: endBytes));

        Assert.Equal(expectUnhealthy, r.EverUnhealthy);
    }

    /// <summary>The retained-set reader must survive finalizable garbage — the case a single collection
    /// gets wrong, because a finalizable object survives the collection that finds it unreachable and is
    /// only reclaimed by the next one. Left uncollected it reads as a leak.</summary>
    [Fact]
    public void The_retained_reader_does_not_count_finalizable_garbage_as_live()
    {
        long before = SoakReporter.ReadRetainedBytes();

        for (int i = 0; i < 20_000; i++) MakeFinalizableGarbage();

        long after = SoakReporter.ReadRetainedBytes();

        // 20k finalizable objects holding ~1 KB each is ~20 MB if they are counted as live. The reader
        // drains finalizers between collections, so the retained set should barely move.
        Assert.True(after - before < 8_000_000,
            $"finalizable garbage leaked into the retained reading: {before:N0} → {after:N0} B");
    }

    private static void MakeFinalizableGarbage()
    {
        var _ = new Finalizable();
    }

    private sealed class Finalizable
    {
        private readonly byte[] _payload = new byte[1024];
        ~Finalizable() { _ = _payload.Length; } // a real finalizer, so the object survives one collection
    }

    [Fact]
    public void A_healthy_run_stays_healthy_and_summarises_as_pass()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 500);
        for (int i = 0; i < 10; i++) { r.Poll(Healthy()); clock.Advance(500); }

        Assert.False(r.EverUnhealthy);
        Assert.Contains("PASS", r.Summary());
        Assert.Equal(10, r.ReportsWritten);
    }

    [Fact]
    public void A_broken_money_oracle_latches_unhealthy_and_never_clears()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 500);

        r.Poll(Healthy());
        clock.Advance(500);
        string? bad = r.Poll(new SoakSample(1, 1, 1, 1, 0, 10_000_000, 80_000_000,
            moneyConservationHolds: false, itemConservationHolds: true));
        Assert.Contains("UNHEALTHY", bad);
        Assert.Contains("MONEY-CONSERVATION-BROKEN", bad);
        Assert.True(r.EverUnhealthy);

        // A subsequent healthy report does NOT clear the latch — the run is already tainted.
        clock.Advance(500);
        r.Poll(Healthy());
        Assert.True(r.EverUnhealthy);
        Assert.Contains("FAIL", r.Summary());
    }

    [Fact]
    public void A_runaway_managed_heap_is_flagged_against_the_baseline()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 500);

        r.Poll(Healthy(managedBytes: 10_000_000)); // baseline
        clock.Advance(500);
        string? line = r.Poll(Healthy(managedBytes: 50_000_000)); // 5× baseline → past the 4× factor
        Assert.Contains("HEAP-RUNAWAY", line);
        Assert.True(r.EverUnhealthy);
    }
}
