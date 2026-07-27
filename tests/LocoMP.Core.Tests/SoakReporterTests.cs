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
    /// Regression for the false FAIL the first real-exe soak produced (2026-07-27). The baseline was the
    /// first report's heap, and the first report fires on an EMPTY server — so the legitimate working set
    /// of 8 players arriving afterwards blew the 4× threshold and the run exited FAIL with every exact
    /// oracle green (3.0 MB at 0 players → 13.0 MB at 8, the real numbers reproduced here). Load-driven
    /// growth is not a leak.
    /// </summary>
    [Fact]
    public void Heap_growth_that_arrives_with_new_players_is_load_not_a_leak()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        r.Poll(With(players: 0, managedBytes: 3_000_000)); // baseline on an empty server, as in the real run
        clock.Advance(1000);
        r.Poll(With(players: 0, managedBytes: 4_500_000));
        clock.Advance(1000);
        for (int p = 1; p <= 8; p++)                        // the swarm arrives; heap tracks the load
        {
            r.Poll(With(players: p, managedBytes: 3_000_000 + p * 1_250_000));
            clock.Advance(1000);
        }

        Assert.False(r.EverUnhealthy);                      // 13.0 MB vs a 3.0 MB empty-server baseline
        Assert.Contains("PASS", r.Summary());
    }

    /// <summary>Once the swarm reaches its size the baseline FREEZES, so growth that is no longer
    /// explained by more players still latches — the property the overnight exit code exists for.</summary>
    [Fact]
    public void Heap_that_keeps_growing_after_the_load_settles_still_latches()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        r.Poll(With(players: 0, managedBytes: 3_000_000));
        clock.Advance(1000);
        for (int p = 1; p <= 8; p++)                        // ramp to 8 players / 13 MB, as above
        {
            r.Poll(With(players: p, managedBytes: 3_000_000 + p * 1_250_000));
            clock.Advance(1000);
        }
        Assert.False(r.EverUnhealthy);

        // Player count now churns WITHIN its peak — no new load — while the heap climbs anyway.
        for (int i = 1; i <= 8; i++)
        {
            r.Poll(With(players: 6, managedBytes: 13_000_000 + i * 6_000_000));
            clock.Advance(1000);
        }

        Assert.True(r.EverUnhealthy, "unexplained growth past the settled baseline is the leak signal");
        Assert.Contains("FAIL", r.Summary());
    }

    /// <summary>A quiet moment must not ratchet the yardstick DOWN and manufacture a leak out of the
    /// next legitimate reload.</summary>
    [Fact]
    public void A_drop_in_players_never_tightens_the_baseline()
    {
        var clock = new ManualClock();
        var r = new SoakReporter(clock, intervalMs: 1000);

        r.Poll(With(players: 8, managedBytes: 12_000_000));
        clock.Advance(1000);
        r.Poll(With(players: 0, managedBytes: 4_000_000));  // everyone leaves; the heap falls back
        clock.Advance(1000);
        r.Poll(With(players: 8, managedBytes: 12_000_000)); // and they all come back

        Assert.False(r.EverUnhealthy);
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
