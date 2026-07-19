using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.OriginShift;
using DV.ThingTypes;
using LocoMP.Core.Career;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// Builds the host's <see cref="CareerConfig"/> from the LIVE game world (M3.3, amended by D13):
/// real stations with absolute positions, real route distances via the game's own
/// <see cref="JobPaymentCalculator"/>, license requirements + the purchasable catalog straight
/// from <see cref="LicenseManager"/>/<see cref="Globals"/>. Under D13 the board's JOBS come from
/// host-native capture (<see cref="JobCapture"/>), so no synthetic job shapes are emitted — the
/// deterministic core generator stays reserved for the dedicated server. Core stays game-free:
/// this is pure DATA crossing the boundary (hard rule 3).
/// </summary>
public static class CareerConfigBuilder
{
    /// <summary>Report steps only count within this range of the task's station (server-checked
    /// against the reporter's own presence pose; game-validated captured jobs are exempt).</summary>
    private const float TaskProximityRadiusM = 500f;

    /// <summary>False while the world is still loading (no stations yet) — host with an empty
    /// board and try again, or re-host once the world is up.</summary>
    public static bool TryBuild(ProgressionPreset preset, out CareerConfig config, Action<string> log)
    {
        config = new CareerConfig { Preset = preset };
        try
        {
            List<StationController> all = StationController.allStations;
            var stations = all == null
                ? new List<StationController>()
                : all.Where(s => s != null && s.StationInfoValid).ToList();
            if (stations.Count < 2)
            {
                log("[career] world not ready (fewer than two stations) — hosting with an empty job board");
                return false;
            }

            var ids = new List<string>();
            var locations = new Dictionary<string, StationLocation>(StringComparer.Ordinal);
            foreach (StationController s in stations)
            {
                string id = s.stationInfo.YardID;
                if (locations.ContainsKey(id)) continue;
                ids.Add(id);
                Vector3 abs = s.transform.position - OriginShift.currentMove; // absolute, like every pose
                locations[id] = new StationLocation(abs.x, abs.y, abs.z);
            }

            var distances = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < stations.Count; i++)
            {
                for (int j = i + 1; j < stations.Count; j++)
                {
                    try
                    {
                        float meters = JobPaymentCalculator.GetDistanceBetweenStations(stations[i], stations[j]);
                        if (meters > 0)
                        {
                            distances[CareerConfig.DistanceKey(
                                stations[i].stationInfo.YardID, stations[j].stationInfo.YardID)] = meters / 1000f;
                        }
                    }
                    catch (Exception)
                    {
                        // A pair the calculator can't measure just loses its distance term.
                    }
                }
            }

            LicenseManager licenses = LicenseManager.Instance;
            var transportLicenses = licenses.GetRequiredLicensesForJobType(JobType.Transport);

            // The game grants Freight Haul at career start — without matching it, every job on the
            // board is license-locked and the starting wallet can't buy the way out (a real
            // deadlock, found live 2026-07-18). Asking the game what Transport requires keeps this
            // correct even if B100 changes the mapping.
            config.StartingLicenses = transportLicenses.Select(v2 => v2.id).ToArray();

            var prices = new Dictionary<string, long>(StringComparer.Ordinal);
            List<JobLicenseType_v2> catalog = Globals.G.Types.jobLicenses;
            if (catalog != null)
            {
                foreach (JobLicenseType_v2 v2 in catalog)
                    if (v2 != null && v2.price > 0) prices[v2.id] = (long)(v2.price * 100f);
            }
            // General licenses (concurrent jobs, train length, …) sell at the career manager too —
            // under D14 those purchases burn from the mirrored wallet, and remotes need catalog
            // prices to render them in the panel shop.
            List<GeneralLicenseType_v2> generalCatalog = Globals.G.Types.generalLicenses;
            if (generalCatalog != null)
            {
                foreach (GeneralLicenseType_v2 v2 in generalCatalog)
                    if (v2 != null && v2.price > 0) prices[v2.id] = (long)(v2.price * 100f);
            }

            config.Stations = ids;
            config.StationLocations = locations;
            config.StationDistancesKm = distances;
            config.LicensePrices = prices;
            config.TaskProximityRadiusM = TaskProximityRadiusM;
            config.AcceptExternalJobs = true;     // D13: the board is fed by host-native capture
            config.ClaimTtlMs = 2 * 60 * 60_000L; // real hauls take a while

            // D14: the wallet is also the license budget (career manager purchases burn from it),
            // so match DV's own career-mode starting cash — $2000 on B99.7 (not exposed via
            // GameParams; re-check on B100). The core default ($500) stays for the dedicated path.
            config.StartingBalanceCents = 2000_00;

            // D14: the game's own concurrent-jobs licensing governs how many captured jobs a
            // player may hold — a second, stricter core limit would refuse takes the native
            // validator already allowed, and a post-take refusal costs the physical leaflet.
            // The default (3) still applies wherever the core generator is the source (dedicated).
            config.MaxConcurrentClaims = 99;

            log($"[career] built from the live world: {ids.Count} stations, {distances.Count} route " +
                $"distances, {prices.Count} purchasable licenses; jobs come from the game (D13)");
            return true;
        }
        catch (Exception e)
        {
            log($"[career] config build failed ({e.Message}) — hosting with an empty job board");
            config = new CareerConfig { Preset = preset };
            return false;
        }
    }
}
