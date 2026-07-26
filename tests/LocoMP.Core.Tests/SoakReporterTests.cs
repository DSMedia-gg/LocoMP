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
