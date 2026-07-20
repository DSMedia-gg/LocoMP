using System;
using DV;
using HarmonyLib;

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

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(AccessTools.Method(typeof(RerailController), nameof(RerailController.OnUse)),
            prefix: new HarmonyMethod(typeof(CommsRadioHook), nameof(RerailOnUsePrefix)));
        harmony.Patch(AccessTools.Method(typeof(CommsRadioCarDeleter), nameof(CommsRadioCarDeleter.OnUse)),
            prefix: new HarmonyMethod(typeof(CommsRadioHook), nameof(DeleteOnUsePrefix)));
        harmony.Patch(AccessTools.Method(typeof(CommsRadioCrewVehicle), nameof(CommsRadioCrewVehicle.OnUse)),
            prefix: new HarmonyMethod(typeof(CommsRadioHook), nameof(SummonOnUsePrefix)));
        log("[comms] comms-radio hook installed (rerail/delete/summon fees + remote routing)");
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
