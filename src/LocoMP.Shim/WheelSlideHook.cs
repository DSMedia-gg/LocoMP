using HarmonyLib;
using DV.Wheels;

namespace LocoMP.Shim;

/// <summary>
/// R4-G (2026-08-07 gauntlet): the F2 family's second observer. DV's
/// <c>WheelSlideTrainsetObserver</c> reacts to <c>PlayerManager.CarChanged</c> by walking
/// <c>car.trainset.cars</c> — but a replica LocoMP just spawned has no <c>trainset</c> yet
/// (DV assigns it on rail placement, a frame later), so entering one right after the join
/// world-clear NREs inside <c>PlayerManager.SetCar</c>'s event chain and interrupts the cab
/// entry itself (the R4 log shows the camera teardown tripping right after). A trainset-less
/// car has nothing to observe: skip the walk, leave the observer's state as-is — the next
/// TrainsetChanged (DV fires it when the set is assigned) re-runs the sweep natively.
/// Session-independent like the F2 destroyed-car guard: a native car mid-teardown can be
/// trainset-null too, and skipping is always the no-op DV intended for "nothing to see".
/// </summary>
public static class WheelSlideHook
{
    public static void Install(Harmony harmony, System.Action<string> log)
    {
        harmony.Patch(
            AccessTools.Method(typeof(WheelSlideTrainsetObserver), "ForceAnyWheelSlidingUpdate"),
            prefix: new HarmonyMethod(typeof(WheelSlideHook), nameof(SkipWithoutTrainset)));
        harmony.Patch(
            AccessTools.Method(typeof(WheelSlideTrainsetObserver), "OnAnyWheelSlideStateChanged"),
            prefix: new HarmonyMethod(typeof(WheelSlideHook), nameof(SkipWithoutTrainset)));
        log("[trains] wheel-slide observer hook installed (trainset-less cars skip the sweep)");
    }

    private static bool SkipWithoutTrainset(WheelSlideTrainsetObserver __instance)
    {
        // Unity fake-null covers a dying observer; a live one with no trainset yet has no set
        // to enumerate. Both walks (the forced sweep and the slide-stop rescan) deref
        // car.trainset.cars unguarded — this is the only seam they share.
        TrainCar? car = __instance == null ? null : __instance.car;
        return car != null && car.trainset != null;
    }
}
