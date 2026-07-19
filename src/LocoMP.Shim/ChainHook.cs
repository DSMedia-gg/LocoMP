using System;
using HarmonyLib;

namespace LocoMP.Shim;

/// <summary>
/// M3.5c: the chain-coupler seam. Every manual chain couple/uncouple funnels through
/// <c>ChainCouplerCouplerAdapter.TryCouple/TryUncouple</c> (the interaction FSM's tighten/loosen
/// completions call them), so one prefix pair intercepts the physical act BEFORE it happens.
/// While a session is live, an act involving a REMOTE-driven car is suppressed and routed to the
/// consist's sim owner as a request — the owner performs the real couple/uncouple and its native
/// event drives the normal proposal path. Pure-local acts pass through untouched.
/// </summary>
public static class ChainHook
{
    /// <summary>The live session's handler; null outside sessions (hooks pass everything through).
    /// Return true to let the native act proceed, false when it was routed as a request.</summary>
    public static Func<Coupler, Coupler?, bool>? CoupleFilter;

    /// <summary>Same contract for uncouple (partner is whatever the coupler is attached to).</summary>
    public static Func<Coupler, bool>? UncoupleFilter;

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(
            AccessTools.Method(typeof(ChainCouplerCouplerAdapter), nameof(ChainCouplerCouplerAdapter.TryCouple)),
            prefix: new HarmonyMethod(typeof(ChainHook), nameof(TryCouplePrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(ChainCouplerCouplerAdapter), nameof(ChainCouplerCouplerAdapter.TryUncouple)),
            prefix: new HarmonyMethod(typeof(ChainHook), nameof(TryUncouplePrefix)));
        log("[trains] chain-coupler hook installed (routes remote couples while in a session)");
    }

    private static bool TryCouplePrefix(ChainCouplerCouplerAdapter __instance)
    {
        Func<Coupler, Coupler?, bool>? filter = CoupleFilter;
        if (filter == null || __instance == null || __instance.coupler == null) return true;

        // The chain FSM knows which coupler the player attached this chain to.
        Coupler? partner = null;
        ChainCouplerInteraction? chain = __instance.chainScript;
        if (chain != null && chain.attachedTo != null && chain.attachedTo.couplerAdapter != null)
            partner = chain.attachedTo.couplerAdapter.coupler;

        return filter(__instance.coupler, partner);
    }

    private static bool TryUncouplePrefix(ChainCouplerCouplerAdapter __instance)
    {
        Func<Coupler, bool>? filter = UncoupleFilter;
        if (filter == null || __instance == null || __instance.coupler == null) return true;
        return filter(__instance.coupler);
    }
}
