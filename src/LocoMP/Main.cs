using System;
using HarmonyLib;
using LocoMP.Core.Protocol;
using LocoMP.Shim;
using UnityModManagerNet;

namespace LocoMP;

/// <summary>
/// UMM entry point (referenced by Info.json's EntryMethod = "LocoMP.Main.Load"). Composition root for
/// the client: owns the UMM/Harmony lifecycle and the M1 <see cref="SessionController"/> (host/join
/// panel in the UMM options, Ctrl+F10 → LocoMP). The M0 junction hook stays installed — it's quiet,
/// event-driven, and still feeding B99.7 API intel toward M2.
/// </summary>
public static class Main
{
    private static UnityModManager.ModEntry.ModLogger? _logger;
    private static SessionController? _session;
    private static bool _active;

    public static bool Load(UnityModManager.ModEntry modEntry)
    {
        _logger = modEntry.Logger;
        Action<string> log = s => _logger?.Log(s);

        var harmony = new Harmony(modEntry.Info.Id);
        WorldStateSpike.Install(harmony, log);

        _session = new SessionController(log);

        modEntry.OnToggle = (_, value) =>
        {
            _active = value;
            if (!value) _session?.Leave(); // toggling the mod off ends any live session cleanly
            log($"[mod] {(value ? "enabled" : "disabled — session closed")}.");
            return true;
        };
        modEntry.OnUpdate = (_, dt) => { if (_active) _session?.Update(dt); };
        modEntry.OnGUI = _ => { if (_active) _session?.OnGUI(); };

        log($"LocoMP loaded — protocol v{ProtocolVersion.Current}. Open the UMM options (Ctrl+F10) to host or join.");
        return true;
    }
}
