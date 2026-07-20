using System;
using HarmonyLib;

namespace LocoMP.Shim;

/// <summary>
/// M4 manual service: the two <c>PitStopStation</c> methods that apply a full refuel / repair to the
/// car in the bay <b>without going through the buy button</b> — <c>RefillAll()</c> and
/// <c>RepairAll()</c>. The normal manual-service loop (turn a valve, deposit cash, hit Buy) commits
/// through <c>CashRegisterWithModules.Buy</c>, which D14's WalletMirror already mirrors as a
/// <c>FeeExternal</c> — so the fee is economy-correct for free. These two methods are the exception:
/// they call <c>UpdateCarPitStopParameter</c> directly, so a car gets fully serviced for NOTHING, and
/// (unlike a native <c>RemoveMoney</c>) there is no money movement for the reconcile to even revert.
///
/// The recon found these have NO callers in any B99.7 game assembly (a scene-wired UnityEvent or a
/// future free-service mode is the only way they'd fire) — so this is a defensive guard, not a live
/// leak. But under D14's ledger-is-truth posture a session must never mint value for free, so when a
/// session is live the prefix hands the station to <see cref="ManualServiceSync"/>, which bills the
/// equivalent cost through the wallet. We never suppress the native call — the player still gets
/// serviced, they just pay for it, exactly as the metered path would have charged.
///
/// The filters are set by <see cref="ManualServiceSync"/> while a session is live and cleared outside
/// one (so the bay behaves natively — free, as DV intends — in single-player).
/// </summary>
public static class ManualServiceHook
{
    /// <summary>Full refuel of the bay car (consumable resources). Bill the equivalent cost.</summary>
    public static Action<PitStopStation>? RefillAll;

    /// <summary>Full repair of the bay car (damageable resources). Bill the equivalent cost.</summary>
    public static Action<PitStopStation>? RepairAll;

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(AccessTools.Method(typeof(PitStopStation), nameof(PitStopStation.RefillAll)),
            prefix: new HarmonyMethod(typeof(ManualServiceHook), nameof(RefillAllPrefix)));
        harmony.Patch(AccessTools.Method(typeof(PitStopStation), nameof(PitStopStation.RepairAll)),
            prefix: new HarmonyMethod(typeof(ManualServiceHook), nameof(RepairAllPrefix)));
        log("[service] manual-service guard installed (bill bypassing refill/repair while in session)");
    }

    // Prefixes run BEFORE the native apply, so the per-resource deficits the cost is computed from are
    // still non-zero. We always return true — the guard bills, it never blocks the service.
    private static void RefillAllPrefix(PitStopStation __instance)
    {
        if (__instance != null) RefillAll?.Invoke(__instance);
    }

    private static void RepairAllPrefix(PitStopStation __instance)
    {
        if (__instance != null) RepairAll?.Invoke(__instance);
    }
}
