using System;
using DV.UI;
using HarmonyLib;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// M5.0 exit-run finding (2026-08-04 Round 2): DV reads ESC through
/// <c>CanvasProviderDV.ShouldTryToggle(PauseMenu)</c> every frame from the canvas controller's own
/// Update — nothing "consumes" legacy input, so the same press that closes the LocoMP overlay also
/// opened DV's pause menu. DV's own precedent for this exact situation is in the same method: an
/// open inventory suppresses the pause toggle. This prefix extends that rule to LocoMP's modal
/// surfaces (overlay open, readiness gate up), plus the frame in which the overlay just consumed
/// ESC — Update order between UMM's OnUpdate and DV's controllers is not deterministic, so "the
/// overlay was open at any point this frame" needs the frame stamp, not just the live probe.
/// </summary>
public static class PauseGuardHook
{
    /// <summary>Live "is a LocoMP modal surface up?" probe (overlay or readiness gate); wired by
    /// Main at load, null-safe by construction.</summary>
    public static Func<bool>? ModalProbe;

    private static int _escConsumedFrame = -1;

    /// <summary>Called by the overlay when ESC just closed it — suppresses DV's pause toggle for
    /// the remainder of THIS frame regardless of Update order.</summary>
    public static void NoteEscConsumed() => _escConsumedFrame = Time.frameCount;

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(
            AccessTools.Method(typeof(CanvasProviderDV), nameof(CanvasProviderDV.ShouldTryToggle)),
            prefix: new HarmonyMethod(typeof(PauseGuardHook), nameof(ShouldTryTogglePrefix)));
        log("[ui] pause guard installed (ESC over a LocoMP surface no longer opens DV's pause menu)");
    }

    private static bool ShouldTryTogglePrefix(CanvasController.ElementType type, ref bool __result)
    {
        if (type != CanvasController.ElementType.PauseMenu) return true;
        bool suppress = Time.frameCount == _escConsumedFrame || ModalProbe?.Invoke() == true;
        if (!suppress) return true;
        __result = false;
        return false;
    }
}
