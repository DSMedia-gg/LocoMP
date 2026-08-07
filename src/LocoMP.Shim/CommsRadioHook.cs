using System;
using DV;
using HarmonyLib;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// M4 comms radio: the OnUse seam of the three world-mutating comms-radio modes. Each mode charges
/// its fee via a DIRECT <c>Inventory.RemoveMoney</c> (not a cash register), so D14's WalletMirror
/// would otherwise revert it free — and a joined player's action runs on a host-owned replica,
/// fighting the host's authority. One prefix per mode's <c>OnUse</c> (fired on every button press;
/// we act only in the CONFIRM state, the commit) lets the live session either snapshot the fee (host)
/// or intercept + route the action to the owner (client), the ChainHook pattern.
///
/// The filters are set by <see cref="CommsRadioSync"/> while a session is live and cleared outside
/// one (so the radio behaves natively in single-player). A filter returns TRUE to let the native
/// OnUse proceed, FALSE to suppress it (the action was routed as a request instead).
/// </summary>
public static class CommsRadioHook
{
    /// <summary>Rerail confirm: host snapshots the price + car and proceeds; client routes + suppresses.</summary>
    public static Func<RerailController, bool>? RerailConfirm;

    /// <summary>Delete confirm: host snapshots price + car id (before the destroy) and proceeds; client routes.</summary>
    public static Func<CommsRadioCarDeleter, bool>? DeleteConfirm;

    /// <summary>Summon confirm: host snapshots the price and proceeds (remote summon is banked, so the
    /// client is never intercepted here). Always returns true.</summary>
    public static Func<CommsRadioCrewVehicle, bool>? SummonConfirm;

    /// <summary>Delete scan assist (R3-3): may the deleter point at this <c>preventDelete</c> car?
    /// The native scan refuses hardened cars BEFORE highlight, so without this the D21 delete-as-retire
    /// confirm filter is unreachable from the local radio — a hardened parked replica simply never
    /// glows. Answer true only for cars whose delete the session would route (a replica we track).</summary>
    public static Func<TrainCar, bool>? DeleteScanAssist;

    // The car we softened for the current OnUpdate pass. Re-hardened in the postfix; ALSO re-hardened
    // at the top of the next prefix, so a skipped postfix (original threw) heals within a frame.
    private static TrainCar? _softened;

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(AccessTools.Method(typeof(RerailController), nameof(RerailController.OnUse)),
            prefix: new HarmonyMethod(typeof(CommsRadioHook), nameof(RerailOnUsePrefix)));
        harmony.Patch(AccessTools.Method(typeof(CommsRadioCarDeleter), nameof(CommsRadioCarDeleter.OnUse)),
            prefix: new HarmonyMethod(typeof(CommsRadioHook), nameof(DeleteOnUsePrefix)));
        harmony.Patch(AccessTools.Method(typeof(CommsRadioCrewVehicle), nameof(CommsRadioCrewVehicle.OnUse)),
            prefix: new HarmonyMethod(typeof(CommsRadioHook), nameof(SummonOnUsePrefix)));
        harmony.Patch(AccessTools.Method(typeof(CommsRadioCarDeleter), nameof(CommsRadioCarDeleter.OnUpdate)),
            prefix: new HarmonyMethod(typeof(CommsRadioHook), nameof(DeleteOnUpdatePrefix)),
            postfix: new HarmonyMethod(typeof(CommsRadioHook), nameof(DeleteOnUpdatePostfix)));
        log("[comms] comms-radio hook installed (rerail/delete/summon fees + remote routing + scan assist)");
    }

    /// <summary>Softens <c>preventDelete</c> for exactly one native OnUpdate pass when the player is
    /// aiming at an assisted car, so the NATIVE scan points/highlights it (own debounce, own audio).
    /// Re-pointing from a postfix instead would fight the native <c>PointToCar(null)</c> every frame —
    /// highlight flicker plus the hover sound per frame. The flag is restored in the postfix below,
    /// so every other consumer of <c>preventDelete</c> still sees the car as hardened.</summary>
    private static void DeleteOnUpdatePrefix(CommsRadioCarDeleter __instance)
    {
        if (_softened != null) { _softened.preventDelete = true; _softened = null; }
        Func<TrainCar, bool>? assist = DeleteScanAssist;
        if (assist == null || __instance == null ||
            __instance.CurrentState != CommsRadioCarDeleter.State.ScanCarToDelete ||
            __instance.carToDelete != null)
            return;
        if (!Physics.Raycast(__instance.signalOrigin.position, __instance.signalOrigin.forward,
                out RaycastHit hit, 100f, __instance.trainCarMask))
            return;
        TrainCar car = TrainCar.Resolve(hit.transform.root);
        if (car == null || car == PlayerManager.Car || !car.preventDelete || !assist(car)) return;
        car.preventDelete = false;
        _softened = car;
    }

    private static void DeleteOnUpdatePostfix()
    {
        if (_softened != null) { _softened.preventDelete = true; _softened = null; }
    }

    private static bool RerailOnUsePrefix(RerailController __instance)
    {
        Func<RerailController, bool>? f = RerailConfirm;
        if (f == null || __instance == null || __instance.CurrentState != RerailController.State.ConfirmRerail) return true;
        return f(__instance);
    }

    private static bool DeleteOnUsePrefix(CommsRadioCarDeleter __instance)
    {
        Func<CommsRadioCarDeleter, bool>? f = DeleteConfirm;
        if (f == null || __instance == null || __instance.CurrentState != CommsRadioCarDeleter.State.ConfirmDelete) return true;
        return f(__instance);
    }

    private static bool SummonOnUsePrefix(CommsRadioCrewVehicle __instance)
    {
        Func<CommsRadioCrewVehicle, bool>? f = SummonConfirm;
        if (f == null || __instance == null || __instance.CurrentState != CommsRadioCrewVehicle.State.ConfirmSummon) return true;
        return f(__instance);
    }
}
