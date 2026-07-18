using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Session;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The career rules at the registry level (02 §4/§6): starting grants, deterministic generation,
/// the claim gates, strict task order, policy-routed payouts and fees in BOTH presets, claim TTL,
/// and the reconnect-grace hold. The economy invariant — sum(wallets) == minted − burned, exactly —
/// is asserted after every meaningful step; the fuzz suite hammers the same oracle at scale.
/// </summary>
public class CareerRegistryTests
{
    private static CareerConfig Config(ProgressionPreset preset = ProgressionPreset.PerPlayer,
        string? requiredLicense = null, int maxClaims = 3)
    {
        return new CareerConfig
        {
            Preset = preset,
            StartingBalanceCents = 500_00,
            MaxConcurrentClaims = maxClaims,
            ClaimTtlMs = 60_000,
            ReconnectGraceMs = 10_000,
            TargetAvailableJobs = 4,
            JobSeed = 7,
            Stations = new[] { "SM", "GF", "HB" },
            JobTypes = new[]
            {
                new JobTypeSpec("FH", "steel", 100_00, 2, 4,
                    requiredLicense is null ? null : new[] { requiredLicense }),
            },
            LicensePrices = new Dictionary<string, long> { ["hazmat"] = 150_00 },
        };
    }

    private static (CareerRegistry career, ManualClock clock) Fresh(CareerConfig? config = null)
    {
        var clock = new ManualClock();
        var career = new CareerRegistry(config ?? Config(), clock);
        return (career, clock);
    }

    private static JobRecord FirstAvailable(CareerRegistry career) =>
        career.Jobs.Values.First(j => j.State == JobLifecycle.Available);

    private static long CompleteJob(CareerRegistry career, string key, JobRecord job)
    {
        long payout = 0;
        for (int i = 0; i < job.Def.Tasks.Count; i++)
        {
            Assert.True(career.TryReportTask(key, job.Def.Id, i, out _, out bool done, out long p, out string? reason), reason);
            if (done) payout = p;
        }
        return payout;
    }

    [Fact]
    public void Starting_grant_is_minted_once_per_player_and_reconnect_never_regrants()
    {
        var (career, _) = Fresh();

        career.Connect("alice", "Alice");
        Assert.Equal(500_00, career.BalanceFor("alice"));
        Assert.True(career.Ledger.ConservationHolds);

        career.Disconnect("alice");
        career.Connect("alice", "Alice");
        Assert.Equal(500_00, career.BalanceFor("alice"));
        Assert.Equal(500_00, career.Ledger.TotalMinted);
    }

    [Fact]
    public void Shared_preset_mints_one_grant_for_the_whole_session()
    {
        var (career, _) = Fresh(Config(ProgressionPreset.SharedCareer));

        career.Connect("alice", "Alice");
        career.Connect("bob", "Bob");

        Assert.Equal(500_00, career.BalanceFor("alice"));
        Assert.Equal(500_00, career.BalanceFor("bob"));       // the same shared account
        Assert.Equal(500_00, career.Ledger.TotalMinted);      // minted once, not per player
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void Generation_is_deterministic_across_instances_and_runtimes()
    {
        var (a, _) = Fresh();
        var (b, _) = Fresh();
        a.Tick();
        b.Tick();

        Assert.Equal(4, a.Jobs.Count);
        foreach (int id in a.Jobs.Keys)
        {
            JobDef ja = a.Jobs[id].Def;
            JobDef jb = b.Jobs[id].Def;
            Assert.Equal(ja.Origin, jb.Origin);
            Assert.Equal(ja.Destination, jb.Destination);
            Assert.Equal(ja.CarCount, jb.CarCount);
            Assert.Equal(ja.PayoutCents, jb.PayoutCents);
            Assert.NotEqual(ja.Origin, ja.Destination);        // distinct station pair by construction
            Assert.Equal(ja.CarCount * 100_00L, ja.PayoutCents);
        }
    }

    [Fact]
    public void Claim_is_gated_on_licenses_and_the_gate_opens_after_purchase()
    {
        var (career, _) = Fresh(Config(requiredLicense: "hazmat"));
        career.Connect("alice", "Alice");
        career.Tick();
        JobRecord job = FirstAvailable(career);

        Assert.False(career.TryClaim("alice", job.Def.Id, out _, out string? reason));
        Assert.Equal("missing license: hazmat", reason);

        Assert.True(career.TryPurchaseLicense("alice", "hazmat", out long price, out reason), reason);
        Assert.Equal(150_00, price);
        Assert.Equal(350_00, career.BalanceFor("alice"));
        Assert.True(career.Ledger.ConservationHolds);          // the fee was burned, not lost

        Assert.True(career.TryClaim("alice", job.Def.Id, out _, out reason), reason);
    }

    [Fact]
    public void Claim_limit_is_enforced()
    {
        var (career, _) = Fresh(Config(maxClaims: 1));
        career.Connect("alice", "Alice");
        career.Tick();
        var jobs = career.Jobs.Values.Take(2).ToArray();

        Assert.True(career.TryClaim("alice", jobs[0].Def.Id, out _, out _));
        Assert.False(career.TryClaim("alice", jobs[1].Def.Id, out _, out string? reason));
        Assert.Equal("claim limit reached (1)", reason);
    }

    [Fact]
    public void Task_reports_must_arrive_strictly_in_order()
    {
        var (career, _) = Fresh();
        career.Connect("alice", "Alice");
        career.Tick();
        JobRecord job = FirstAvailable(career);
        career.TryClaim("alice", job.Def.Id, out _, out _);

        Assert.False(career.TryReportTask("alice", job.Def.Id, 1, out _, out _, out _, out string? reason));
        Assert.Equal("task 1 out of order (expected 0)", reason);
        Assert.True(career.TryReportTask("alice", job.Def.Id, 0, out _, out _, out _, out _));
        Assert.False(career.TryReportTask("bob", job.Def.Id, 1, out _, out _, out _, out _)); // not the claimant
    }

    [Fact]
    public void Full_job_loop_pays_the_claimant_and_conserves_money_per_player()
    {
        var (career, _) = Fresh();
        career.Connect("alice", "Alice");
        career.Connect("bob", "Bob");
        career.Tick();
        JobRecord job = FirstAvailable(career);

        Assert.True(career.TryClaim("alice", job.Def.Id, out _, out _));
        long payout = CompleteJob(career, "alice", job);

        Assert.Equal(job.Def.PayoutCents, payout);
        Assert.Equal(500_00 + payout, career.BalanceFor("alice"));
        Assert.Equal(500_00, career.BalanceFor("bob"));       // 02 §4: never anyone else's wallet
        Assert.Equal(JobLifecycle.Completed, job.State);
        Assert.False(career.Jobs.ContainsKey(job.Def.Id));    // completed jobs leave the board
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void Full_job_loop_pays_the_shared_wallet_in_shared_career()
    {
        var (career, _) = Fresh(Config(ProgressionPreset.SharedCareer));
        career.Connect("alice", "Alice");
        career.Connect("bob", "Bob");
        career.Tick();
        JobRecord job = FirstAvailable(career);

        Assert.True(career.TryClaim("alice", job.Def.Id, out _, out _));
        long payout = CompleteJob(career, "alice", job);

        Assert.Equal(500_00 + payout, career.BalanceFor("alice"));
        Assert.Equal(500_00 + payout, career.BalanceFor("bob")); // one wallet, both see it
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void Abandon_returns_the_job_with_progress_reset()
    {
        var (career, _) = Fresh();
        career.Connect("alice", "Alice");
        career.Tick();
        JobRecord job = FirstAvailable(career);
        career.TryClaim("alice", job.Def.Id, out _, out _);
        career.TryReportTask("alice", job.Def.Id, 0, out _, out _, out _, out _);

        Assert.True(career.TryAbandon("alice", job.Def.Id, out _, out _));
        Assert.Equal(JobLifecycle.Available, job.State);
        Assert.Null(job.ClaimantKey);
        Assert.Equal(0, job.NextTaskIndex);
    }

    [Fact]
    public void Claim_ttl_expiry_releases_the_job()
    {
        var (career, clock) = Fresh();
        career.Connect("alice", "Alice");
        career.Tick();
        JobRecord job = FirstAvailable(career);
        career.TryClaim("alice", job.Def.Id, out _, out _);

        clock.Advance(59_999);
        Assert.Empty(career.Tick().ReleasedJobs);

        clock.Advance(1);
        CareerTick tick = career.Tick();
        Assert.Contains(job, tick.ReleasedJobs);
        Assert.Equal(JobLifecycle.Available, job.State);
    }

    [Fact]
    public void Reconnect_within_grace_keeps_claims_and_grace_expiry_releases_them()
    {
        var (career, clock) = Fresh();
        career.Connect("alice", "Alice");
        career.Tick();
        JobRecord job = FirstAvailable(career);
        career.TryClaim("alice", job.Def.Id, out _, out _);
        career.TryReportTask("alice", job.Def.Id, 0, out _, out _, out _, out _);

        // Leave and come back inside the hold: the claim and its progress were never touched.
        career.Disconnect("alice");
        clock.Advance(9_999);
        Assert.Empty(career.Tick().ReleasedJobs);
        career.Connect("alice", "Alice");
        Assert.Equal(JobLifecycle.Claimed, job.State);
        Assert.Equal("alice", job.ClaimantKey);
        Assert.Equal(1, job.NextTaskIndex);

        // Leave again and let the hold lapse: the job goes back to the board.
        career.Disconnect("alice");
        clock.Advance(10_000);
        CareerTick tick = career.Tick();
        Assert.Contains(job, tick.ReleasedJobs);
        Assert.Equal(JobLifecycle.Available, job.State);
        Assert.Null(job.ClaimantKey);
    }

    [Fact]
    public void Purchase_refuses_overdraft_and_duplicates()
    {
        var (career, _) = Fresh(Config(ProgressionPreset.PerPlayer));
        career.Connect("alice", "Alice");

        Assert.True(career.TryPurchaseLicense("alice", "hazmat", out _, out _));
        Assert.False(career.TryPurchaseLicense("alice", "hazmat", out _, out string? reason));
        Assert.Equal("license already owned: hazmat", reason);

        Assert.False(career.TryPurchaseLicense("alice", "nope", out _, out reason));
        Assert.Equal("unknown license: nope", reason);
        Assert.True(career.Ledger.ConservationHolds);

        // A wallet too small for the fee: refused, balance untouched, nothing burned.
        CareerConfig poor = Config();
        poor.StartingBalanceCents = 100_00;
        var (broke, _) = Fresh(poor);
        broke.Connect("carol", "Carol");
        Assert.False(broke.TryPurchaseLicense("carol", "hazmat", out _, out reason));
        Assert.StartsWith("insufficient funds", reason);
        Assert.Equal(100_00, broke.BalanceFor("carol"));
        Assert.Equal(0, broke.Ledger.TotalBurned);
    }
}
