using System;
using System.IO;
using HarmonyLib;
using LocoMP.Core.Protocol;
using LocoMP.Shim;
using LocoMP.UI;
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
    private static SessionViewModel? _viewModel;
    private static LocoMpUi? _ui;
    private static bool _active;
    private static double _presenceRetryAccum;
    private static int _presenceRetries;
    private static string _extractStatus = "";
    private static string _careerStatus = "";
    private static bool _demoSignal;

    /// <summary>M5.0 transitional default: the IMGUI dev panel stays ON unless LOCOMP_DEBUG=0 —
    /// the uGUI root is still stub tabs, so the panel remains the functional surface. The draft's
    /// intended default (panel off, uGUI primary) flips when M5.1 lands the real join/host screens.</summary>
    private static readonly bool DevPanel =
        Environment.GetEnvironmentVariable("LOCOMP_DEBUG") != "0";

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
        ChainHook.Install(harmony, log);
        OptimizerHook.Install(harmony, log);
        WheelSlideHook.Install(harmony, log);
        JobGenSuppressor.Install(harmony, log);
        JobCapture.Install(harmony, log);
        WalletMirror.Install(harmony, log);
        SaveSuppressor.Install(harmony, log);
        CarSaveFilter.Install(harmony, log);
        CommsRadioHook.Install(harmony, log);
        ManualServiceHook.Install(harmony, log);

        _session = new SessionController(log);

        // M5.0 UI foundation: the view-model seam + the first-party screens. The menu hook is the
        // one DV.UI coupling point (clone-and-rewire, gated + isolated); everything else is ours.
        _viewModel = new SessionViewModel(_session);
        _ui = new LocoMpUi(_viewModel, log, extractTopology: () =>
        {
            // M5.2: the extractor reaches the host menu (10-plan §5 "moved out of dev-tools");
            // the dev-panel button stays as the fallback path.
            try
            {
                string path = TopologyExtractor.Extract(modEntry.Path, log);
                return "wrote " + Path.GetFileName(path);
            }
            catch (Exception e)
            {
                log("[extract] FAILED: " + e);
                return "extract failed: " + e.Message;
            }
        });
        MenuHook.Install(log);
        MenuHook.OpenRequested += origin => { if (_active) _ui?.Open(origin); };
        PauseGuardHook.ModalProbe = () => _active && _ui is { ModalActive: true };
        PauseGuardHook.Install(harmony, log);

        // M5.5: overlay-join hook + the "runs LocoMP" presence marker. Both silently no-op on a
        // Steam-less launch — the Friends tab explains itself, everything else is UDP as before.
        LocoMP.Net.SteamPresence.Install(log);
        LocoMP.Net.SteamPresence.SetIdlePresence();

        modEntry.OnToggle = (_, value) =>
        {
            _active = value;
            if (!value)
            {
                _session?.Leave(); // toggling the mod off ends any live session cleanly
                _ui?.Dispose();
                LocoMP.Net.SteamPresence.Uninstall();
                LocoMP.Net.SteamPresence.ClearPresence();
            }
            else
            {
                LocoMP.Net.SteamPresence.Install(log);
                LocoMP.Net.SteamPresence.SetIdlePresence();
            }
            log($"[mod] {(value ? "enabled" : "disabled — session closed")}.");
            return true;
        };
        modEntry.OnUpdate = (_, dt) =>
        {
            if (!_active) return;
            // R5-1: DV brings Steamworks up ASYNC a few frames after mods load, so the load-time
            // Install above silently missed on every normal Steam launch (live-proven: the mod
            // toggle was the only thing that ever installed presence). Retry on a slow cadence
            // until it lands; a Steam-less launch stops trying after ~90 s.
            if (!LocoMP.Net.SteamPresence.Installed && _presenceRetries < 45)
            {
                _presenceRetryAccum += dt;
                if (_presenceRetryAccum >= 2)
                {
                    _presenceRetryAccum = 0;
                    _presenceRetries++;
                    LocoMP.Net.SteamPresence.Install(log);
                    if (LocoMP.Net.SteamPresence.Installed) LocoMP.Net.SteamPresence.SetIdlePresence();
                }
            }
            _session?.Update(dt);
            _ui?.Tick(dt);
        };
        modEntry.OnGUI = entry =>
        {
            if (!_active || !DevPanel) return; // dev rig behind the flag; the uGUI screens are the product surface
            _session?.OnGUI();
            OnToolsGUI(entry, log);
        };

        log($"LocoMP loaded — protocol v{ProtocolVersion.Current}, build {BuildStamp()}. " +
            "Open the UMM options (Ctrl+F10) to host or join.");
        return true;
    }

    /// <summary>
    /// A per-build identity for the two assemblies that carry game behaviour, logged at load so the log
    /// alone answers "is the build I just staged the one actually running?". Every compile mints a fresh
    /// MVID, so these change on every build and can never accidentally match a stale copy — which is the
    /// whole point: staged files can be reverted after the fact (a Vortex re-deploy replaced a hand-staged
    /// Shim on 2026-07-25, and a whole test round was run and analysed against the OLD build before the
    /// substitution was noticed). File sizes and timestamps on disk prove nothing about what the CLR loaded.
    /// Both are stamped because LocoMP.dll and LocoMP.Shim.dll can go stale independently.
    /// </summary>
    private static string BuildStamp()
    {
        static string Mvid(Type t)
        {
            try { return t.Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8); }
            catch { return "????????"; }
        }
        return $"{Mvid(typeof(Main))}/{Mvid(typeof(PresenceShim))}";
    }

    /// <summary>Dev tools under the session panel: the M2.2 world extractor and the M6-B career
    /// exporter — the two operator-supplied files the dedicated server mounts.</summary>
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

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Export career (.lmpc)", GUILayout.Width(180)))
        {
            try
            {
                var preset = _session?.HostPreset ?? Core.Career.ProgressionPreset.PerPlayer;
                string path = CareerConfigExporter.Export(preset, modEntry.Path, log);
                _careerStatus = "wrote " + Path.GetFileName(path);
            }
            catch (Exception e)
            {
                _careerStatus = "failed: " + e.Message;
                log("[career-export] FAILED: " + e);
            }
        }
        if (_careerStatus.Length > 0) GUILayout.Label(_careerStatus);
        GUILayout.EndHorizontal();

        // M5.0 readiness-gate demo (the exit checklist's mechanism proof): the cover must clear
        // ONLY on the signal button, and the 8 s failsafe must resolve to the explain screen —
        // never a silent lift (plan §5 doctrine). Real boundaries wire in at M5.1/M5.2 (U5).
        GUILayout.BeginHorizontal();
        if (_ui != null && GUILayout.Button("Demo readiness gate (8s)", GUILayout.Width(180)))
        {
            _demoSignal = false;
            _ui.Gate.Begin("Demo cover", new[] { "Contacting", "Streaming", "Spawning" },
                () => _demoSignal, failsafeSeconds: 8.0,
                onFail: f => log($"[ui] demo gate stalled at {f.StalledStage} (expected — use the explain buttons)"));
        }
        if (_ui is { Gate.Active: true } && GUILayout.Button("Fire demo signal", GUILayout.Width(140)))
            _demoSignal = true;
        GUILayout.EndHorizontal();
    }
}
