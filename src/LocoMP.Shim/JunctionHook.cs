using System;
using System.Reflection;
using HarmonyLib;

namespace LocoMP.Shim;

/// <summary>
/// The junction seam, replacing the M0 spike. Patches ONLY the inner
/// <c>Junction.Switch(SwitchMode, byte)</c> overload: the M2.1 regression run proved a player throw
/// chains the outer overload into this one (two log lines from one throw), while game-internal sets
/// call it directly — so hooking the inner overload alone observes every real state change exactly
/// once. Applying a REMOTE commit goes through <see cref="ApplyRemote"/>, which suppresses the hook
/// so the server's own echo never loops back as a fresh proposal.
/// </summary>
public static class JunctionHook
{
    private static Action<string>? _log;
    private static bool _suppress;

    /// <summary>(junction, selectedBranch) after any locally originated switch commit.</summary>
    public static event Action<Junction, byte>? Switched;

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        MethodInfo? inner = typeof(Junction).GetMethod(
            nameof(Junction.Switch), new[] { typeof(Junction.SwitchMode), typeof(byte) });
        if (inner is null)
        {
            // The build gate should prevent this; a loud log beats a NullReferenceException.
            log("[trains] Junction.Switch(SwitchMode, byte) not found — junction sync disabled!");
            return;
        }

        harmony.Patch(inner, postfix: new HarmonyMethod(typeof(JunctionHook), nameof(SwitchPostfix)));
        log("[trains] junction hook installed (inner Switch overload only).");
    }

    private static void SwitchPostfix(Junction __instance)
    {
        if (_suppress) return;
        Switched?.Invoke(__instance, __instance.selectedBranch);
    }

    /// <summary>Drive a junction to a server-committed branch without re-triggering the hook.
    /// FORCED: a committed state is authoritative, not a polite request.</summary>
    public static void ApplyRemote(Junction junction, byte branch)
    {
        if (junction == null || junction.selectedBranch == branch) return;
        _suppress = true;
        try { junction.Switch(Junction.SwitchMode.FORCED, branch); }
        catch (Exception e) { _log?.Invoke($"[trains] junction apply failed: {e.Message}"); }
        finally { _suppress = false; }
    }
}
