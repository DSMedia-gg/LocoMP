using System;
using HarmonyLib;

namespace LocoMP.Shim;

/// <summary>
/// M3.5b: while JOINED to someone else's session, the local game world is session-modified — the
/// player's own cars are cleared and the host's consists are spawned in their place — so letting
/// DV save it (autosave, quit-save, sleep) would persist a foreign world into the player's own SP
/// savegame. One false prefix on <c>SaveGameManager.SaveAllowed</c> covers every save path (the
/// autosave coroutine, quit, and manual saves all consult it). Engaged on Join, released on Leave;
/// the HOST keeps saving normally — his world is genuinely his (the mirrored-money hazard there is
/// handled by <see cref="WalletMirror"/> restoring the SP balance in AboutToSave).
/// </summary>
public static class SaveSuppressor
{
    /// <summary>Set by the session controller: true from Join to Leave on CLIENTS only.</summary>
    public static bool Active;

    public static void Install(Harmony harmony, Action<string> log)
    {
        harmony.Patch(
            AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.SaveAllowed)),
            prefix: new HarmonyMethod(typeof(SaveSuppressor), nameof(Prefix)));
        log("[session] native save suppressor installed (engages only while joined to a session)");
    }

    private static bool Prefix(ref bool __result)
    {
        if (!Active) return true;
        __result = false;
        return false;
    }
}
