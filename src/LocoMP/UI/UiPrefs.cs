using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LocoMP.Core.Session;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The M5.1 form-memory store: player name, last join endpoint, and the recent-address list, as
/// key=value lines beside the other LocoMP files in <see cref="Application.persistentDataPath"/>
/// (the PlayerKeyStore pattern). Deliberately minimal — the real settings surface (UI scale,
/// keybinds, host defaults, live-apply) is M5.3; this only stops retyping an IP every session.
/// Passwords are NEVER persisted. Unreadable/absent file = defaults, never a throw.
/// </summary>
public sealed class UiPrefs
{
    private const int MaxRecent = 5;

    public string PlayerName = Environment.UserName;
    public string Address = "127.0.0.1";
    public int Port = NetDefaults.Port;
    public int HostPort = NetDefaults.Port;

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
}
