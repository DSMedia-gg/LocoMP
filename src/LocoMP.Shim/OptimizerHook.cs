using System;
using HarmonyLib;

namespace LocoMP.Shim;

/// <summary>
/// F2 (2026-08-04 gauntlet): the physics-sleep seam. DV's <c>TrainsOptimizer</c> sweeps track
/// occupancy every frame and calls <c>TrainCar.ForceOptimizationState</c> on whatever it finds —
/// including a replica LocoMP just tore down (occupancy outlives the GameObject by a frame, and
/// the method dereferences <c>rb</c> and the ECS entity of a destroyed car → the dematerialize
/// NRE), and including LIVE replicas, where <c>ForceSleep(false)</c> re-enables gravity on bodies
/// the M3.5b hardening deliberately keeps kinematic. Both are the same statement: a remotely-
/// simulated car has no LOCAL physics for the optimizer to manage. One prefix skips it for
/// destroyed cars always, and for remote replicas while a session is live.
/// </summary>
public static class OptimizerHook
{
    /// <summary>The live session's "is this a remote replica?" probe; null outside sessions
    /// (destroyed-car protection stays active regardless — it is not session state).</summary>
    public static Func<TrainCar, bool>? IsRemoteCar;

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(
            AccessTools.Method(typeof(TrainCar), nameof(TrainCar.ForceOptimizationState)),
            prefix: new HarmonyMethod(typeof(OptimizerHook), nameof(ForceOptimizationStatePrefix)));
        log("[trains] optimizer hook installed (replicas + dying cars opt out of physics-sleep management)");
    }

    private static bool ForceOptimizationStatePrefix(TrainCar __instance)
    {
        // Unity fake-null = destroyed (or being destroyed): rb/entity are gone, the original NREs.
        if (__instance == null) return false;
        Func<TrainCar, bool>? probe = IsRemoteCar;
        return probe == null || !probe(__instance);
    }
}
