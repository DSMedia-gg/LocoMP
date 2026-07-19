using System;
using System.Collections.Generic;
using HarmonyLib;

namespace LocoMP.Shim;

/// <summary>
/// Stops DV's client-side procedural job generation while a LocoMP session is live (02 §4 — job
/// generation moves into the core; clients receive job data, never generate). One choke point
/// covers every path: entering a station's generation zone, RegenerateJobs, and expiry-driven
/// attempts all funnel through StationProceduralJobsController.TryToGenerateJobs, and a false
/// prefix skips it entirely (verified against B99.7 — this IS 02's verification item 4 answered).
/// Jobs that existed before the session are left alone: their overviews are host-local props, and
/// deleting world objects mid-join is a bigger risk than a stale booklet on a desk.
/// </summary>
public static class JobGenSuppressor
{
    /// <summary>Set by the session controller: true from session start to Leave. When false the
    /// prefix passes through and DV's own generation resumes untouched.</summary>
    public static bool Active;

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(
            AccessTools.Method(typeof(StationProceduralJobsController), nameof(StationProceduralJobsController.TryToGenerateJobs)),
            prefix: new HarmonyMethod(typeof(JobGenSuppressor), nameof(Prefix)));
        log("[career] DV job-generation suppressor installed (engages only while in a session)");
    }

    private static bool Prefix() => !Active;

    /// <summary>Halt any generation coroutine already mid-flight when a session starts — the
    /// prefix only blocks NEW attempts, not one currently walking its station.</summary>
    public static void StopAll(Action<string> log)
    {
        List<StationController> stations = StationController.allStations;
        if (stations == null) return;
        int stopped = 0;
        foreach (StationController s in stations)
        {
            StationProceduralJobsController? controller = s == null ? null : s.ProceduralJobsController;
            if (controller != null && controller.IsJobGenerationActive)
            {
                controller.StopJobGeneration();
                stopped++;
            }
        }
        if (stopped > 0) log($"[career] stopped {stopped} in-flight DV job generation coroutine(s)");
    }
}
