using System;
using HarmonyLib;
using LocoMP.Core.Protocol;
using LocoMP.Shim;
using UnityModManagerNet;

namespace LocoMP;

/// <summary>
/// UMM entry point (referenced by Info.json's EntryMethod = "LocoMP.Main.Load"). Composition root for
/// the client: it owns the UMM/Harmony lifecycle and drives the M0 world-state spike. Real host/join
/// UI + the embedded server land in M1 (07 §M1).
/// </summary>
public static class Main
{
    private static UnityModManager.ModEntry.ModLogger? _logger;
    private static bool _active;
    private static double _sinceLastLog;

    private const double LogIntervalSeconds = 5.0;

    public static bool Load(UnityModManager.ModEntry modEntry)
    {
        _logger = modEntry.Logger;
        Action<string> log = s => _logger?.Log(s);

        var harmony = new Harmony(modEntry.Info.Id);
        WorldStateSpike.Install(harmony, log);

        // UMM calls OnToggle with the persisted enabled state at load, so logging starts automatically
        // when the mod is enabled — no manual step — and stops cleanly when toggled off.
        modEntry.OnToggle = (_, value) =>
        {
            _active = value;
            log($"[shim] world-state logging {(value ? "enabled" : "disabled")}.");
            return true;
        };
        modEntry.OnUpdate = OnUpdate;

        log($"LocoMP M0 shim spike loaded — protocol v{ProtocolVersion.Current}.");
        return true;
    }

    private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
    {
        if (!_active) return;

        _sinceLastLog += deltaTime;
        if (_sinceLastLog < LogIntervalSeconds) return;
        _sinceLastLog = 0;

        if (_logger != null)
        {
            WorldStateSpike.LogCars(s => _logger.Log(s));
        }
    }
}
