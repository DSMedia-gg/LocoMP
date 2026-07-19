using System;
using System.IO;
using HarmonyLib;
using LocoMP.Core.Protocol;
using LocoMP.Shim;
using UnityEngine;
using UnityModManagerNet;

namespace LocoMP;

/// <summary>
/// UMM entry point (referenced by Info.json's EntryMethod = "LocoMP.Main.Load"). Composition root for
/// the client: owns the UMM/Harmony lifecycle, the supported-build gate, and the
/// <see cref="SessionController"/> (host/join panel in the UMM options, Ctrl+F10 → LocoMP).
/// </summary>
public static class Main
{
    private static UnityModManager.ModEntry.ModLogger? _logger;
    private static SessionController? _session;
    private static bool _active;
    private static string _extractStatus = "";

    public static bool Load(UnityModManager.ModEntry modEntry)
    {
        _logger = modEntry.Logger;
        Action<string> log = s => _logger?.Log(s);

        // Supported-build gate (03 §10): on an unknown game build the mod stays loaded but inert —
        // a friendly panel message beats a Harmony patch exploding mid-session on B100.
        if (!PresenceShim.IsSupportedBuild)
        {
            string msg = $"LocoMP {modEntry.Info.Version} does not support game build " +
                         $"'{PresenceShim.ReportedGameVersion}' (supported: {string.Join(", ", PresenceShim.SupportedBuilds)}). " +
                         "Check for a LocoMP update.";
            log("[mod] " + msg);
            modEntry.OnGUI = _ => GUILayout.Label(msg);
            return true;
        }

        var harmony = new Harmony(modEntry.Info.Id);
        JunctionHook.Install(harmony, log);
        JobGenSuppressor.Install(harmony, log);
        JobCapture.Install(harmony, log);
        WalletMirror.Install(harmony, log);

        _session = new SessionController(log);

        modEntry.OnToggle = (_, value) =>
        {
            _active = value;
            if (!value) _session?.Leave(); // toggling the mod off ends any live session cleanly
            log($"[mod] {(value ? "enabled" : "disabled — session closed")}.");
            return true;
        };
        modEntry.OnUpdate = (_, dt) => { if (_active) _session?.Update(dt); };
        modEntry.OnGUI = entry =>
        {
            if (!_active) return;
            _session?.OnGUI();
            OnToolsGUI(entry, log);
        };

        log($"LocoMP loaded — protocol v{ProtocolVersion.Current}. Open the UMM options (Ctrl+F10) to host or join.");
        return true;
    }

    /// <summary>Dev tools under the session panel — currently just the M2.2 world extractor.</summary>
    private static void OnToolsGUI(UnityModManager.ModEntry modEntry, Action<string> log)
    {
        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Extract world topology", GUILayout.Width(180)))
        {
            try
            {
                string path = TopologyExtractor.Extract(modEntry.Path, log);
                _extractStatus = "wrote " + Path.GetFileName(path);
            }
            catch (Exception e)
            {
                _extractStatus = "failed: " + e.Message;
                log("[extract] FAILED: " + e);
            }
        }
        if (_extractStatus.Length > 0) GUILayout.Label(_extractStatus);
        GUILayout.EndHorizontal();
    }
}
