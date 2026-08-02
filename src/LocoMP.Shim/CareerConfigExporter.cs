using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DV.ThingTypes;
using LocoMP.Core.Career;
using LocoMP.Core.Protocol;

namespace LocoMP.Shim;

/// <summary>
/// The in-game half of the dedicated server's real career (M6-B): reads the LIVE Derail Valley
/// world and writes a complete <c>.lmpc</c> the server's <c>--config</c> loads — real stations,
/// route distances and license catalog via <see cref="CareerConfigBuilder"/>, PLUS the job shapes
/// that builder deliberately skips. On a host, jobs come from native capture (D13), but the
/// dedicated server's core GENERATOR is the job source — so the export mines each station's own
/// procedural-jobs ruleset (which cargo leaves which yard for which destinations, at what consist
/// sizes, behind which licenses) into <see cref="JobTypeSpec"/>s. The board a fresh server
/// generates then routes real cargo along the real map instead of the synthetic placeholder.
/// </summary>
public static class CareerConfigExporter
{
    // Payout calibration, v1: a flat per-car term plus the per-car-km distance term the generator
    // multiplies by the exported route table. DV's own payment model is mass-and-cargo-based and
    // not cheaply reproducible outside a live job, so these are deliberately simple, deliberately
    // visible constants: a 6-car 25 km haul pays ~$465, a 10-car 140 km haul ~$2,500 — the right
    // ballpark for career pacing without pretending to be the game's exact economist.
    private const long FlatPerCarCents = 40_00;
    private const long PerCarKmCents = 1_50;

    /// <summary>Build the full config from the live world and write it beside the mod as
    /// <c>career-&lt;build&gt;.lmpc</c>. Throws with a readable message when the world isn't up —
    /// the button reports it as status rather than half-writing a file.</summary>
    public static string Export(ProgressionPreset preset, string outputDir, Action<string> log)
    {
        if (outputDir is null) throw new ArgumentNullException(nameof(outputDir));
        if (log is null) throw new ArgumentNullException(nameof(log));

        if (!CareerConfigBuilder.TryBuild(preset, out CareerConfig config, log))
            throw new InvalidOperationException("world not ready — load into a session first");

        int shapes = AppendJobShapes(config, log);
        if (shapes == 0)
            throw new InvalidOperationException("no station exported a single job shape — nothing worth writing");

        // The host-mode posture the builder stamped is wrong for the file's consumer: a dedicated
        // server has no world source feeding captured jobs (the generator IS the source), and the
        // host's claim cap (99, deferring to the game's own licensing) would disable claim
        // limiting where no native validator exists.
        config.AcceptExternalJobs = false;
        config.MaxConcurrentClaims = 3;

        byte[] bytes = CareerConfigCodec.Write(config);
        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, $"career-{Sanitize(PresenceShim.GameBuild)}.lmpc");
        File.WriteAllBytes(path, bytes);
        log($"[career-export] wrote {bytes.Length:N0} bytes → {path} " +
            $"({config.Stations.Count} stations, {shapes} job shapes, preset {preset})");
        return path;
    }

    /// <summary>One <see cref="JobTypeSpec"/> per (origin station, output cargo group, cargo):
    /// the group's member stations are the destinations, the group's cargo decides the license
    /// gate, and the consist size range comes from the station's own ruleset.</summary>
    private static int AppendJobShapes(CareerConfig config, Action<string> log)
    {
        var specs = new List<JobTypeSpec>();
        List<StationController> all = StationController.allStations;
        if (all == null) return 0;

        foreach (StationController station in all.Where(s => s != null && s.StationInfoValid))
        {
            StationProceduralJobsRuleset ruleset = station.proceduralJobsRuleset;
            if (ruleset == null || ruleset.outputCargoGroups == null) continue;

            string origin = station.stationInfo.YardID;
            int minCars = Math.Max(1, ruleset.minCarsPerJob);
            int maxCars = Math.Max(minCars, ruleset.maxCarsPerJob);

            foreach (CargoGroup group in ruleset.outputCargoGroups)
            {
                if (group?.cargoTypes == null) continue;
                string[] destinations = (group.stations ?? new List<StationController>())
                    .Where(d => d != null && d.StationInfoValid)
                    .Select(d => d.stationInfo.YardID)
                    .Where(id => !string.Equals(id, origin, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (destinations.Length == 0) continue;

                string[] licenses = RequiredLicenseIds(group.cargoTypes);
                foreach (CargoType cargo in group.cargoTypes.Distinct())
                {
                    specs.Add(new JobTypeSpec("Transport", cargo.ToString(), FlatPerCarCents,
                        minCars, maxCars, licenses, new[] { origin }, destinations, PerCarKmCents));
                }
            }
        }

        config.JobTypes = specs;
        log($"[career-export] mined {specs.Count} job shape(s) from {config.Stations.Count} station ruleset(s)");
        return specs.Count;
    }

    /// <summary>The license gate a generated job of this cargo group carries: what the game itself
    /// requires for a Transport job plus the group's cargo licenses. Failure-tolerant — a group
    /// the license manager can't answer for ships ungated rather than not at all.</summary>
    private static string[] RequiredLicenseIds(List<CargoType> cargoTypes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (JobLicenseType_v2 v2 in LicenseManager.Instance.GetRequiredLicensesForJobType(JobType.Transport))
                if (v2 != null) ids.Add(v2.id);
            foreach (JobLicenseType_v2 v2 in LicenseManager.Instance.GetRequiredLicensesForCargoTypes(cargoTypes))
                if (v2 != null) ids.Add(v2.id);
        }
        catch (Exception)
        {
            // An unanswerable group ships ungated — a playable board beats a perfect one.
        }
        return ids.ToArray();
    }

    private static string Sanitize(string raw)
    {
        char[] chars = raw.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] == ' ') chars[i] = '_';
        return new string(chars);
    }
}
