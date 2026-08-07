using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Persistence;
using LocoMP.Core.Session;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// D23 (closes R3-4): on-time job completion pays base + bonus, DV-parity — bonus = base × 0.5,
/// eligible iff the claim-to-completion span fits the window + the 60 s turn-in grace, measured on
/// a job clock that FREEZES while the world is paused (DV's JobsManager.Time advances by frame
/// delta, so a native pause freezes it too — D19 makes pauses ordinary session events here).
/// Conservation is asserted around every payout: the bonus is minted, never conjured.
/// </summary>
public class JobBonusTests
{
    private const float StationKm = 0.2f; // window = round(0.2 × 7.5) = 2 min → 120 s (+60 grace)
    private const int WindowSeconds = 120;

    private static CareerConfig Config()
    {
        var distances = new Dictionary<string, float>();
        string[] stations = { "SM", "GF", "HB" };
        foreach (string a in stations)
            foreach (string b in stations)
                if (a != b) distances[CareerConfig.DistanceKey(a, b)] = StationKm;
        return new CareerConfig
        {
            Preset = ProgressionPreset.PerPlayer,
            StartingBalanceCents = 500_00,
            MaxConcurrentClaims = 3,
            ClaimTtlMs = 3_600_000, // far beyond any bonus window — TTL must not eat the claim
            ReconnectGraceMs = 10_000,
            TargetAvailableJobs = 4,
            JobSeed = 7,
            Stations = stations,
            JobTypes = new[] { new JobTypeSpec("FH", "steel", 100_00, 2, 4) },
            StationDistancesKm = distances,
        };
    }

    private static (CareerRegistry career, ManualClock clock, JobRecord job) ClaimedJob()
    {
        var clock = new ManualClock();
        var career = new CareerRegistry(Config(), clock);
        career.Connect("alice", "Alice");
        career.Tick(); // generate the board + start the job clock
        JobRecord job = career.Jobs.Values.First(j => j.State == JobLifecycle.Available);
        Assert.True(career.TryClaim("alice", job.Def.Id, out _, out string? reason), reason);
        return (career, clock, job);
    }

    private static long Complete(CareerRegistry career, JobRecord job)
    {
        long payout = 0;
        for (int i = 0; i < job.Def.Tasks.Count; i++)
        {
            Assert.True(career.TryReportTask("alice", job.Def.Id, i, out _, out bool done, out long p, out string? r), r);
            if (done) payout = p;
        }
        return payout;
    }

    private static void Advance(CareerRegistry career, ManualClock clock, int seconds)
    {
        clock.Advance(seconds * 1000L);
        career.Tick();
    }

    [Fact]
    public void Generated_jobs_carry_a_dv_parity_bonus_and_window()
    {
        var (_, _, job) = ClaimedJob();
        Assert.Equal(WindowSeconds, job.Def.BonusTimeSeconds);
        Assert.Equal(job.Def.PayoutCents / 2, job.Def.BonusPayoutCents); // base × 0.5
        Assert.True(job.Def.BonusPayoutCents > 0, "the scenario must actually have a bonus at stake");
    }

    [Fact]
    public void On_time_completion_pays_base_plus_bonus()
    {
        var (career, clock, job) = ClaimedJob();
        Advance(career, clock, 10); // well inside the window

        long payout = Complete(career, job);

        Assert.Equal(job.Def.PayoutCents + job.Def.BonusPayoutCents, payout);
        Assert.Equal(500_00 + payout, career.BalanceFor("alice"));
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void Late_completion_pays_base_only()
    {
        var (career, clock, job) = ClaimedJob();
        Advance(career, clock, WindowSeconds + 61); // one second past window + grace

        long payout = Complete(career, job);

        Assert.Equal(job.Def.PayoutCents, payout);
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void The_sixty_second_grace_still_pays_the_bonus()
    {
        // DV parity: Job.GetBonusPaymentForTheJob allows TimeLimit + 60 — the walk from the
        // parked train to the station office is free.
        var (career, clock, job) = ClaimedJob();
        Advance(career, clock, WindowSeconds + 59);

        Assert.Equal(job.Def.PayoutCents + job.Def.BonusPayoutCents, Complete(career, job));
    }

    [Fact]
    public void World_pause_freezes_the_bonus_window()
    {
        var (career, clock, job) = ClaimedJob();
        Advance(career, clock, 10);

        career.JobClockPaused = true;                 // D19: host ESC pauses the session
        Advance(career, clock, WindowSeconds * 10);   // an eternity of wall time, all paused
        career.JobClockPaused = false;
        Advance(career, clock, 10);                   // 20 s of REAL job time total

        Assert.Equal(job.Def.PayoutCents + job.Def.BonusPayoutCents, Complete(career, job));
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void A_restart_resumes_the_window_instead_of_refreshing_it()
    {
        var (career, clock, job) = ClaimedJob();
        Advance(career, clock, WindowSeconds); // window nearly spent (grace remains)

        byte[] saved = SaveCodec.Write(new ServerSaveData(career.Capture(),
            new TrainsSaveData(), new ItemsSaveData()));
        var clock2 = new ManualClock();
        var restored = new CareerRegistry(Config(), clock2, SaveCodec.Read(saved).Career);
        restored.Connect("alice", "Alice");
        restored.Tick();
        Advance(restored, clock2, 61); // grace was all that was left; now it's gone too

        long payout = 0;
        for (int i = 0; i < job.Def.Tasks.Count; i++)
        {
            Assert.True(restored.TryReportTask("alice", job.Def.Id, i, out _, out bool done, out long p, out string? r), r);
            if (done) payout = p;
        }
        Assert.Equal(job.Def.PayoutCents, payout); // base only — the window did NOT refresh
    }

    [Fact]
    public void A_restart_with_time_remaining_still_pays_the_bonus()
    {
        var (career, clock, job) = ClaimedJob();
        Advance(career, clock, 10);

        byte[] saved = SaveCodec.Write(new ServerSaveData(career.Capture(),
            new TrainsSaveData(), new ItemsSaveData()));
        var clock2 = new ManualClock();
        var restored = new CareerRegistry(Config(), clock2, SaveCodec.Read(saved).Career);
        restored.Connect("alice", "Alice");
        restored.Tick();
        Advance(restored, clock2, 10); // 20 s total — well inside

        long payout = 0;
        for (int i = 0; i < job.Def.Tasks.Count; i++)
        {
            Assert.True(restored.TryReportTask("alice", job.Def.Id, i, out _, out bool done, out long p, out string? r), r);
            if (done) payout = p;
        }
        Assert.Equal(job.Def.PayoutCents + job.Def.BonusPayoutCents, payout);
    }

    [Fact]
    public void A_job_without_a_window_never_pays_a_bonus()
    {
        // Negative control: distances absent → 0 km → no window → base only, however fast.
        var clock = new ManualClock();
        CareerConfig config = Config();
        config.StationDistancesKm = new Dictionary<string, float>();
        var career = new CareerRegistry(config, clock);
        career.Connect("alice", "Alice");
        career.Tick();
        JobRecord job = career.Jobs.Values.First(j => j.State == JobLifecycle.Available);
        Assert.Equal(0, job.Def.BonusTimeSeconds);
        Assert.Equal(0, job.Def.BonusPayoutCents);
        Assert.True(career.TryClaim("alice", job.Def.Id, out _, out string? reason), reason);

        Assert.Equal(job.Def.PayoutCents, Complete(career, job));
    }
}
