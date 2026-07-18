using System;
using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Persistence;
using LocoMP.Core.Session;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The M3 economy fuzz (07 §M3 exit, 03 §11): thousands of random career operations — connects,
/// disconnects, claims, task reports, abandons, purchases, time jumps — with the conservation
/// oracle (sum(wallets) == minted − burned, exactly) asserted after EVERY operation, in both
/// presets. Every 250 ops the whole registry additionally round-trips through the LMPS save codec
/// and the fuzz continues on the restored instance, so persistence is proven under arbitrary
/// mid-flight state, not just curated snapshots.
/// </summary>
public class CareerFuzzTests
{
    private static CareerConfig Config(ProgressionPreset preset) => new()
    {
        Preset = preset,
        StartingBalanceCents = 300_00,
        MaxConcurrentClaims = 2,
        ClaimTtlMs = 600_000,     // long enough that random reports can finish a haul...
        ReconnectGraceMs = 60_000, // ...while grace holds still lapse on the fuzz's big time hops
        TargetAvailableJobs = 6,
        JobSeed = 99,
        Stations = new[] { "SM", "GF", "HB", "FF" },
        JobTypes = new[]
        {
            new JobTypeSpec("FH", "steel", 80_00, 1, 4),
            new JobTypeSpec("LH", "logs", 50_00, 2, 6, new[] { "LH1" }),
        },
        LicensePrices = new Dictionary<string, long> { ["LH1"] = 120_00, ["DE2"] = 90_00 },
    };

    [Theory]
    [InlineData(ProgressionPreset.PerPlayer)]
    [InlineData(ProgressionPreset.SharedCareer)]
    public void Two_thousand_random_operations_conserve_money_across_saves(ProgressionPreset preset)
    {
        var rng = new Random(20260718);
        var clock = new ManualClock();
        CareerConfig config = Config(preset);
        var career = new CareerRegistry(config, clock);
        string[] players = { "k1", "k2", "k3", "k4" };
        var online = new HashSet<string>();
        long completions = 0, releases = 0;

        foreach (string k in players) // start with a full roster; churn still fuzzes it below
        {
            career.Connect(k, k.ToUpperInvariant());
            online.Add(k);
        }

        for (int op = 0; op < 2000; op++)
        {
            string key = players[rng.Next(players.Length)];
            switch (rng.Next(10)) // weighted: hauling (claim/report) dominates, like a real evening
            {
                case 0:
                    career.Connect(key, key.ToUpperInvariant());
                    online.Add(key);
                    break;
                case 1:
                    career.Disconnect(key);
                    online.Remove(key);
                    break;
                case 2:
                case 3: // claim something — a real available job usually, garbage sometimes
                {
                    if (!online.Contains(key)) break;
                    int jobId = rng.Next(4) == 0
                        ? rng.Next(10_000)
                        : career.Jobs.Values.Where(j => j.State == JobLifecycle.Available)
                            .Select(j => j.Def.Id).DefaultIfEmpty(-1).ElementAt(0);
                    career.TryClaim(key, jobId, out _, out _);
                    break;
                }
                case 4:
                case 5:
                case 6: // report the next task on one of my claims (occasionally out of order)
                {
                    JobRecord? mine = career.Jobs.Values.FirstOrDefault(
                        j => j.State == JobLifecycle.Claimed && j.ClaimantKey == key);
                    if (mine is null) break;
                    int index = rng.Next(5) == 0 ? mine.NextTaskIndex + 1 : mine.NextTaskIndex;
                    if (career.TryReportTask(key, mine.Def.Id, index, out _, out bool done, out _, out _) && done)
                        completions++;
                    break;
                }
                case 7:
                {
                    JobRecord? mine = career.Jobs.Values.FirstOrDefault(
                        j => j.State == JobLifecycle.Claimed && j.ClaimantKey == key);
                    if (mine != null) career.TryAbandon(key, mine.Def.Id, out _, out _);
                    break;
                }
                case 8:
                {
                    string license = rng.Next(2) == 0 ? "LH1" : "DE2";
                    career.TryPurchaseLicense(key, license, out _, out _);
                    break;
                }
                case 9:
                    // Mostly small hops so claims can finish; occasionally a jump past the grace
                    // window (60 s) so held claims of disconnected players actually lapse.
                    clock.Advance(rng.Next(10) == 0 ? 70_000 : rng.Next(10_000));
                    break;
            }

            // The server ticks every poll — mirror that, so expiries fire and the board refills.
            releases += career.Tick().ReleasedJobs.Count;

            Assert.True(career.Ledger.ConservationHolds,
                $"conservation broke at op {op}: sum {career.Ledger.SumOfBalances} " +
                $"minted {career.Ledger.TotalMinted} burned {career.Ledger.TotalBurned}");
            Assert.All(career.Ledger.Accounts.Values, b => Assert.True(b >= 0, "negative balance"));

            if (op % 250 == 249) // the save/restore torture: serialize mid-flight and continue
            {
                byte[] bytes = SaveCodec.Write(new ServerSaveData(career.Capture(), new TrainsSaveData()));
                ServerSaveData restored = SaveCodec.Read(bytes);
                var next = new CareerRegistry(config, clock, restored.Career);

                Assert.Equal(career.Ledger.SumOfBalances, next.Ledger.SumOfBalances);
                Assert.Equal(career.Jobs.Count, next.Jobs.Count);
                Assert.True(next.Ledger.ConservationHolds);

                career = next;
                // A restart disconnects everyone; reconnect whoever the fuzz considers online
                // (their claims were held by the restart-grace rule, so nothing was lost).
                foreach (string k in online) career.Connect(k, k.ToUpperInvariant());
            }
        }

        // The fuzz must have actually exercised the interesting paths, not no-opped through.
        Assert.True(completions > 20, $"only {completions} completions — fuzz too tame");
        Assert.True(releases > 0, "no TTL/grace releases happened");
        foreach (JobRecord job in career.Jobs.Values.Where(j => j.State == JobLifecycle.Claimed))
            Assert.True(career.Profiles.ContainsKey(job.ClaimantKey!), "claim by unknown profile");
    }
}
