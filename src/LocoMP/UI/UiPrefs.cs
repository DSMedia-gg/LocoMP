using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LocoMP.Core.Session;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The LocoMP settings store (M5.1 form memory, grown into the M5.3 backend): player name, join
/// endpoints, and the client preferences, as key=value lines beside the other LocoMP files in
/// <see cref="Application.persistentDataPath"/> (the PlayerKeyStore pattern — one flat file, no
/// serializer dependency). Passwords are NEVER persisted. Unreadable/absent file = defaults,
/// never a throw; an out-of-range value clamps instead of poisoning the session.
///
/// <para><b>Live-apply (M5.3):</b> the settings screen edits fields then calls
/// <see cref="NotifyChanged"/>; consumers subscribe to <see cref="Changed"/> — the composition
/// root pushes <see cref="UiScale"/>/<see cref="TrainSmoothing"/> into their statics, the chat
/// overlay re-reads its options. Nothing here requires relaunching a session.</para>
/// </summary>
public sealed class UiPrefs
{
    private const int MaxRecent = 5;

    public const float MinUiScale = 0.7f;
    public const float MaxUiScale = 1.5f;
    public const float MinChatFade = 3f;
    public const float MaxChatFade = 60f;
    public const float MinSmoothing = 5f;
    public const float MaxSmoothing = 60f;

    public string PlayerName = Environment.UserName;
    public string Address = "127.0.0.1";
    public int Port = NetDefaults.Port;
    public int HostPort = NetDefaults.Port;

    // ── M5.3 client preferences ────────────────────────────────────────────────────────────────

    /// <summary>LocoMP UI size multiplier (1 = DV-native). Applied via <see cref="LocoMpCanvas.UiScale"/>.</summary>
    public float UiScale = 1f;

    /// <summary>Chat opts (M5.4 overlay): master switch, passive-fade seconds, open key.</summary>
    public bool ChatEnabled = true;
    public float ChatFadeSeconds = 12f;
    public KeyCode ChatKey = KeyCode.Return;

    /// <summary>In-game hotkey opening the LocoMP screens (None = menu buttons only). Ships BOUND
    /// (R4 UX call): D19 makes the host's ESC pause everyone, so ESC-diving to reach the screens is
    /// session-hostile — the default must not require it. J = the key Cody picked live (free in DV's
    /// default bindings); rebindable in Settings.</summary>
    public KeyCode MenuKey = KeyCode.J;

    /// <summary>Remote-train smoothing rate per second (the Shim's RealCarSync lerp) — higher =
    /// tighter tracking, lower = softer under jitter. The 08-05 live verdict tuned the default.</summary>
    public float TrainSmoothing = 30f;

    /// <summary>A settings edit was committed (the screen calls <see cref="NotifyChanged"/>) —
    /// consumers re-read and re-apply. Not raised by Load: startup application is the
    /// composition root's explicit job.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Recent join endpoints, newest first, deduplicated, capped at 5.</summary>
    public readonly List<(string Address, int Port)> Recent = new();

    private static string PrefsPath => Path.Combine(Application.persistentDataPath, "locomp-ui-prefs.txt");

    public static UiPrefs Load(Action<string> log)
    {
        var prefs = new UiPrefs();
        try
        {
            if (!File.Exists(PrefsPath)) return prefs;
            foreach (string line in File.ReadAllLines(PrefsPath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);
                switch (key)
                {
                    case "name" when value.Length > 0: prefs.PlayerName = value; break;
                    case "address" when value.Length > 0: prefs.Address = value; break;
                    case "port" when TryPort(value, out int p): prefs.Port = p; break;
                    case "hostPort" when TryPort(value, out int hp): prefs.HostPort = hp; break;
                    case "recent":
                        // "address port" — split on the LAST space so an exotic address survives.
                        int space = value.LastIndexOf(' ');
                        if (space > 0 && TryPort(value.Substring(space + 1), out int rp) &&
                            prefs.Recent.Count < MaxRecent)
                            prefs.Recent.Add((value.Substring(0, space), rp));
                        break;
                    case "uiScale" when TryFloat(value, out float scale):
                        prefs.UiScale = Mathf.Clamp(scale, MinUiScale, MaxUiScale);
                        break;
                    case "chatEnabled": prefs.ChatEnabled = value != "0"; break;
                    case "chatFade" when TryFloat(value, out float fade):
                        prefs.ChatFadeSeconds = Mathf.Clamp(fade, MinChatFade, MaxChatFade);
                        break;
                    case "chatKey" when TryKey(value, out KeyCode ck): prefs.ChatKey = ck; break;
                    case "menuKey" when TryKey(value, out KeyCode mk): prefs.MenuKey = mk; break;
                    case "trainSmoothing" when TryFloat(value, out float rate):
                        prefs.TrainSmoothing = Mathf.Clamp(rate, MinSmoothing, MaxSmoothing);
                        break;
                }
            }
        }
        catch (Exception e)
        {
            log($"[ui] prefs unreadable ({e.Message}) — using defaults");
        }
        return prefs;
    }

    public void Save(Action<string> log)
    {
        try
        {
            var lines = new List<string>
            {
                "name=" + PlayerName,
                "address=" + Address,
                "port=" + Port.ToString(CultureInfo.InvariantCulture),
                "hostPort=" + HostPort.ToString(CultureInfo.InvariantCulture),
                "uiScale=" + UiScale.ToString("0.###", CultureInfo.InvariantCulture),
                "chatEnabled=" + (ChatEnabled ? "1" : "0"),
                "chatFade=" + ChatFadeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "chatKey=" + ChatKey,
                "menuKey=" + MenuKey,
                "trainSmoothing=" + TrainSmoothing.ToString("0.###", CultureInfo.InvariantCulture),
            };
            foreach ((string address, int port) in Recent)
                lines.Add($"recent={address} {port.ToString(CultureInfo.InvariantCulture)}");
            File.WriteAllLines(PrefsPath, lines);
        }
        catch (Exception e)
        {
            // Prefs are a convenience — a full disk must not break joining.
            log($"[ui] prefs not saved ({e.Message})");
        }
    }

    /// <summary>Move (or insert) an endpoint at the head of the recent list.</summary>
    public void RememberEndpoint(string address, int port)
    {
        Recent.RemoveAll(r => r.Address == address && r.Port == port);
        Recent.Insert(0, (address, port));
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
        port is > 0 and < 65536;

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryKey(string text, out KeyCode key) =>
        Enum.TryParse(text, ignoreCase: true, out key) && Enum.IsDefined(typeof(KeyCode), key);
}
