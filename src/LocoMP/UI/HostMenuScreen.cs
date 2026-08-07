using System;
using System.Globalization;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The M5.2 in-game host menu — the four utility groups (10-plan §5), host-only, pushed over the
/// root screen while hosting. Every command binds the <see cref="SessionViewModel"/> only; the
/// backend beneath is the same server-authorised path a remote admin uses, so nothing here is a
/// host-side back door. Retained-mode like every screen: the section body rebuilds on section
/// switch and on <see cref="SessionViewModel.Changed"/> — cheap at friend-session scale.
/// </summary>
public sealed class HostMenuScreen : IScreen
{
    private static readonly string[] Sections = { "Players", "Session", "World & Save", "Diagnostics" };

    private readonly SessionViewModel _vm;
    private readonly UiPrefs _prefs;
    private readonly Action<string> _log;
    private readonly Action _back;
    private readonly Func<string>? _extractTopology; // returns a status line; null = tool unavailable

    private WidgetKit? _kit;
    private RectTransform? _body;
    private TMP_Text? _status;
    private int _section;
    private string _lastAction = "";

    public HostMenuScreen(SessionViewModel vm, UiPrefs prefs, Action<string> log, Action back, Func<string>? extractTopology)
    {
        _vm = vm;
        _prefs = prefs;
        _log = log;
        _back = back;
        _extractTopology = extractTopology;
    }

    public GameObject? Go { get; private set; }

    public void Build(RectTransform parent, WidgetKit kit)
    {
        _kit = kit;
        RectTransform panel = kit.Panel(parent, vertical: true, name: "LocoMP Host Menu");
        Go = panel.gameObject;
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(860f, 560f);

        RectTransform header = kit.Row(panel, "Header");
        TMP_Text title = kit.Label(header, "Host Menu", size: kit.Theme.TitleSize);
        title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(header, "Back", _back, width: 120f);

        _status = kit.Label(panel, "", dim: true);

        RectTransform tabs = kit.Row(panel, "Sections");
        for (int i = 0; i < Sections.Length; i++)
        {
            int index = i;
            kit.Button(tabs, Sections[i], () => { _section = index; RebuildBody(); }, width: 190f);
        }

        RectTransform bodyHost = kit.Panel(panel, name: "Body");
        bodyHost.GetComponent<Image>().color = kit.Theme.PanelLight;
        bodyHost.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
        var bodyGo = new GameObject("Section Body", typeof(RectTransform));
        _body = (RectTransform)bodyGo.transform;
        _body.SetParent(bodyHost, worldPositionStays: false);
        _body.anchorMin = Vector2.zero;
        _body.anchorMax = Vector2.one;
        _body.offsetMin = new Vector2(16f, 12f);
        _body.offsetMax = new Vector2(-16f, -12f);

        RebuildBody();
    }

    public void OnShow()
    {
        _vm.Changed += OnChanged;
        RebuildBody();
    }

    public void OnHide() => _vm.Changed -= OnChanged;

    private void OnChanged() => RebuildBody();

    private void Note(string line)
    {
        _lastAction = line;
        _log("[ui] " + line);
        RebuildBody();
    }

    private void RebuildBody()
    {
        if (_body == null || _kit == null) return;
        for (int i = _body.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(_body.GetChild(i).gameObject);

        if (_status != null)
            _status.text = (_vm.IsHost
                ? $"Hosting — {_vm.ServerPlayerCount} player(s), preset {_vm.PresetName}" +
                  (_vm.JoinsPaused ? " — JOINS PAUSED" : "")
                : "Not hosting — the host menu is read-only.") +
                (_lastAction.Length > 0 ? $"   • {_lastAction}" : "");

        RectTransform column = _kit.Column(_body, "Section " + Sections[_section]);
        switch (_section)
        {
            case 0: BuildPlayers(column, _kit); break;
            case 1: BuildSession(column, _kit); break;
            case 2: BuildWorldSave(column, _kit); break;
            case 3: BuildDiagnostics(column, _kit); break;
        }
    }

    // ── Players: live roster with role badges, ping, and the moderation verbs ──

    private void BuildPlayers(RectTransform column, WidgetKit kit)
    {
        int selfId = _vm.LocalId;
        kit.Label(column,
            $"you — {Badge(_vm.RoleOf(selfId))}{Ping(selfId)}", dim: true);

        PlayerState[] players = _vm.Players;
        if (players.Length == 0)
            kit.Label(column, "No other players connected.", dim: true);

        foreach (PlayerState p in players)
        {
            int id = p.Id;
            RectTransform row = kit.Row(column, "Player " + id);
            TMP_Text line = kit.Label(row, $"{p.Name}  (id {id}) — {Badge(_vm.RoleOf(id))}{Ping(id)}");
            line.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            bool admin = _vm.RoleOf(id) == PlayerRole.Admin;
            kit.Button(row, admin ? "Demote" : "Promote",
                () => { if (admin) _vm.Demote(id); else _vm.Promote(id); Note($"{(admin ? "demoted" : "promoted")} {p.Name}"); },
                width: 110f);
            kit.Button(row, "Kick", () => { _vm.Kick(id); Note($"kicked {p.Name}"); }, width: 90f);
            kit.Button(row, "Ban", () => { _vm.Ban(id); Note($"banned {p.Name} for this session"); }, width: 90f);
        }

        // R4-A: the session-ban list + unban — entries are (name, opaque id); the server never
        // shares keys. The request below refreshes the snapshot; an unchanged reply is deduped
        // upstream, so this rebuild-triggered re-request cannot repaint-loop.
        kit.Label(column, "Session bans (die with the session — U3)", dim: true);
        if (_vm.Bans.Count == 0)
            kit.Label(column, "No session bans.", dim: true);
        foreach (SessionBan b in _vm.Bans)
        {
            RectTransform row = kit.Row(column, "Ban " + b.Id);
            TMP_Text line = kit.Label(row, $"{b.Name}  (ban {b.Id})");
            line.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            SessionBan entry = b;
            kit.Button(row, "Unban", () => { _vm.Unban(entry.Id); Note($"unbanned {entry.Name}"); }, width: 110f);
        }
        _vm.RequestBanList();
    }

    private string Badge(PlayerRole role) => role switch
    {
        PlayerRole.Owner => "[host]",
        PlayerRole.Admin => "[admin]",
        _ => "player",
    };

    private string Ping(int id) => _vm.PingOf(id) is int ms ? $", {ms} ms" : "";

    // ── Session control: password, cap, hold-the-door, Save & Stop ──

    private void BuildSession(RectTransform column, WidgetKit kit)
    {
        RectTransform pwRow = kit.Row(column, "Password");
        TMP_InputField pw = kit.LabeledField(pwRow, "Password", "empty = open session");
        kit.Button(pwRow, "Apply", () =>
        {
            string value = pw.text.Trim();
            _vm.SetPassword(value);
            // D22: a mid-session change IS the host's password now — persist it so the next
            // re-host keeps it (the R4-C finding: the host dialog's stale config used to win).
            _prefs.HostPassword = value;
            _prefs.Save(_log);
            Note(value.Length == 0 ? "session password removed" : "session password changed");
        }, width: 110f);

        RectTransform capRow = kit.Row(column, "Max players");
        TMP_InputField cap = kit.LabeledField(capRow, "Max players", "1-99");
        kit.Button(capRow, "Apply", () =>
        {
            if (int.TryParse(cap.text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 99)
            {
                _vm.SetMaxPlayers(n);
                Note($"max players set to {n} (raising admits the queue immediately)");
            }
            else Note("max players must be 1-99");
        }, width: 110f);

        kit.Toggle(column, "Pause new joins (present players and reconnects are unaffected)",
            _vm.JoinsPaused, on => { _vm.SetJoinsPaused(on); Note(on ? "joins paused" : "joins resumed"); });

        kit.Label(column, $"Career preset: {_vm.PresetName}", dim: true);

        kit.Label(column, "", dim: true); // spacer
        RectTransform stopRow = kit.Row(column, "Save & Stop");
        kit.Label(stopRow, "End the session cleanly: every player is told, the career saves, your world restores.", dim: true)
            .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(stopRow, "Save & Stop", () => { Note("session ended by Save & Stop"); _vm.SaveAndStop(); }, width: 140f);
    }

    // ── World/save: save-now, autosave cadence, backup rotation, the extractor ──

    private void BuildWorldSave(RectTransform column, WidgetKit kit)
    {
        RectTransform saveRow = kit.Row(column, "Save now");
        kit.Label(saveRow, $"Autosave every {_vm.AutosaveSeconds}s.", dim: true)
            .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(saveRow, "Save now", () => { _vm.SaveNow(); Note("save requested"); }, width: 120f);

        RectTransform intervalRow = kit.Row(column, "Autosave interval");
        TMP_InputField interval = kit.LabeledField(intervalRow, "Autosave (s)", "5-3600");
        kit.Button(intervalRow, "Apply", () =>
        {
            if (int.TryParse(interval.text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s is >= 5 and <= 3600)
            {
                _vm.SetAutosaveInterval(s);
                Note($"autosave interval set to {s}s");
            }
            else Note("autosave interval must be 5-3600 seconds");
        }, width: 110f);

        kit.Label(column, "Backups (newest first) — restoring ends the session so the rollback sticks; " +
                          "the career you roll away from becomes backup 1.", dim: true);
        var backups = _vm.Backups;
        if (backups.Count == 0)
            kit.Label(column, "No backups yet — one rotates in at each save.", dim: true);
        foreach (SaveBackupInfo b in backups)
        {
            SaveBackupInfo backup = b;
            RectTransform row = kit.Row(column, "Backup " + backup.Index);
            double age = (DateTime.UtcNow - backup.LastWriteUtc).TotalMinutes;
            kit.Label(row, $"backup {backup.Index}: {backup.SizeBytes:N0} bytes, {age:F0} min old")
                .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            kit.Button(row, "Restore + end session", () =>
            {
                if (_vm.RestoreBackupAndStop(backup.Index, out string? why))
                    Note($"restored backup {backup.Index} — session ended, host again to load it");
                else
                    Note($"restore failed: {why}");
            }, width: 210f);
        }

        kit.Label(column, "", dim: true); // spacer
        RectTransform extractRow = kit.Row(column, "Extractor");
        kit.Label(extractRow, "Extract the world topology (.lmpw) for the dedicated server.", dim: true)
            .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(extractRow, "Extract topology",
            () => Note(_extractTopology?.Invoke() ?? "extractor unavailable"),
            enabled: _extractTopology != null, width: 170f);
    }

    // ── Diagnostics: the CaptureDiagnostics snapshot + copy-report ──

    private void BuildDiagnostics(RectTransform column, WidgetKit kit)
    {
        if (_vm.Diagnostics is not ServerDiagnostics diag)
        {
            kit.Label(column, "Diagnostics are host-only.", dim: true);
            return;
        }
        string report = DiagnosticsReport(diag);
        foreach (string line in report.Split('\n'))
            kit.Label(column, line);

        RectTransform row = kit.Row(column, "Copy");
        kit.Label(row, "", dim: true).gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(row, "Copy report", () =>
        {
            GUIUtility.systemCopyBuffer = report;
            Note("diagnostics copied to the clipboard");
        }, width: 140f);
    }

    private string DiagnosticsReport(ServerDiagnostics d)
    {
        string peers = "";
        foreach (PlayerState p in _vm.Players)
            peers += $"\n  {p.Name} (id {p.Id}): {( _vm.PingOf(p.Id) is int ms ? ms + " ms" : "ping unknown")}";
        return
            $"players {d.Players} (+{d.Queued} queued) · trainsets {d.Trainsets} · jobs {d.Jobs} · items {d.Items}\n" +
            $"money conservation {(d.MoneyConservationHolds ? "OK" : "BROKEN")} · item conservation {(d.ItemConservationHolds ? "OK" : "BROKEN")}\n" +
            $"stale snapshots dropped {d.StaleSnapshotsDropped} · interest {(d.InterestEnabled ? "on" : "off")} · joins {(d.JoinsPaused ? "PAUSED" : "open")}\n" +
            $"admins {d.Admins} · session bans {d.BannedKeys}\n" +
            $"traffic: sent {d.BytesSent:N0} B / {d.MessagesSent:N0} msg · received {d.BytesReceived:N0} B / {d.MessagesReceived:N0} msg" +
            (peers.Length > 0 ? "\nplayers:" + peers : "");
    }
}
