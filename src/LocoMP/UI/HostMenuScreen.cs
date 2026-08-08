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
/// host-side back door.
///
/// <para>D25 refresh discipline (10-plan §10.5): the section SHELL (form fields, static rows)
/// builds once per section switch; <see cref="SessionViewModel.Changed"/> refreshes only the
/// DYNAMIC containers (roster, bans, backups, diagnostics) and display labels. Pre-D25 this screen
/// rebuilt everything on every change — a ping tick could wipe a half-typed password.</para>
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
    private RectTransform? _statusRow;
    private readonly Button?[] _sectionButtons = new Button?[Sections.Length];
    private int _section;
    private string _lastAction = "";
    private Action? _refreshDynamic;

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

        _statusRow = kit.Row(panel, "Status");

        RectTransform tabs = kit.Row(panel, "Sections");
        for (int i = 0; i < Sections.Length; i++)
        {
            int index = i;
            _sectionButtons[i] = kit.TabButton(tabs, Sections[i],
                () => { _section = index; RebuildBody(); }, width: 190f);
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

    /// <summary>Targeted refresh (D25): status + dynamic containers only — never the form shell,
    /// so in-progress typing survives roster/ping/ban events.</summary>
    private void OnChanged()
    {
        RefreshStatus();
        _refreshDynamic?.Invoke();
    }

    private void Note(string line)
    {
        _lastAction = line;
        _log("[ui] " + line);
        OnChanged();
    }

    private void RefreshStatus()
    {
        if (_kit is not { } kit || _statusRow == null) return;
        RectTransform row = _statusRow;
        for (int i = row.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(row.GetChild(i).gameObject);
        if (_vm.IsHost)
        {
            kit.Chip(row, "Hosting", ChipKind.Success);
            if (_vm.JoinsPaused) kit.Chip(row, "Joins paused", ChipKind.Warning);
            string players = _vm.ServerPlayerCount == 1 ? "1 player" : _vm.ServerPlayerCount + " players";
            kit.Label(row, $"{players} · preset {_vm.PresetName}", dim: true);
        }
        else
        {
            kit.Chip(row, "Read-only", ChipKind.Neutral);
            kit.Label(row, "Not hosting.", dim: true);
        }
        if (_lastAction.Length > 0) kit.Label(row, "· " + _lastAction, dim: true);
    }

    private void RebuildBody()
    {
        if (_body == null || _kit == null) return;
        for (int i = 0; i < Sections.Length; i++)
            if (_sectionButtons[i] is { } button) _kit.SetTabActive(button, i == _section);
        for (int i = _body.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(_body.GetChild(i).gameObject);
        RefreshStatus();
        _refreshDynamic = null;

        RectTransform column = _kit.Column(_body, "Section " + Sections[_section]);
        switch (_section)
        {
            case 0: BuildPlayers(column, _kit); break;
            case 1: BuildSession(column, _kit); break;
            case 2: BuildWorldSave(column, _kit); break;
            case 3: BuildDiagnostics(column, _kit); break;
        }
        _refreshDynamic?.Invoke();
    }

    // ── Players: live roster with role chips, ping ramp, and the moderation verbs ──

    private void BuildPlayers(RectTransform column, WidgetKit kit)
    {
        RectTransform dynamic = kit.Column(column, "Roster");
        _refreshDynamic = () => RefreshPlayers(dynamic, kit);
    }

    private void RefreshPlayers(RectTransform host, WidgetKit kit)
    {
        if (host == null) return;
        for (int i = host.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(host.GetChild(i).gameObject);

        int selfId = _vm.LocalId;
        RectTransform selfRow = kit.StripedRow(host, odd: true, "Player self");
        string selfName = _prefs.PlayerName.Length > 0 ? _prefs.PlayerName : "you";
        TMP_Text selfLabel = kit.Label(selfRow, selfName + " (you)");
        selfLabel.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        RoleChip(kit, selfRow, _vm.RoleOf(selfId));
        PingBits(kit, selfRow, selfId);

        PlayerState[] players = _vm.Players;
        if (players.Length == 0)
            kit.Subline(host, "No other players connected.");

        bool odd = false;
        foreach (PlayerState p in players)
        {
            int id = p.Id;
            RectTransform row = kit.StripedRow(host, odd, "Player " + id);
            odd = !odd;
            TMP_Text line = kit.Label(row, p.Name);
            line.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            RoleChip(kit, row, _vm.RoleOf(id));
            PingBits(kit, row, id);
            bool admin = _vm.RoleOf(id) == PlayerRole.Admin;
            kit.Button(row, admin ? "Demote" : "Promote",
                () => { if (admin) _vm.Demote(id); else _vm.Promote(id); Note($"{(admin ? "demoted" : "promoted")} {p.Name}"); },
                width: 110f);
            kit.Button(row, "Kick", () => { _vm.Kick(id); Note($"kicked {p.Name}"); },
                ButtonTier.Danger, width: 90f);
            kit.Button(row, "Ban", () => { _vm.Ban(id); Note($"banned {p.Name}"); },
                ButtonTier.Danger, width: 90f);
        }

        // R4-A: the session-ban list + unban — entries are (name, opaque id); the server never
        // shares keys. The request below refreshes the snapshot; an unchanged reply is deduped
        // upstream, so this refresh-triggered re-request cannot repaint-loop.
        kit.SectionLabel(host, "Session bans (cleared at session end)");
        if (_vm.Bans.Count == 0)
            kit.Subline(host, "No session bans.");
        foreach (SessionBan b in _vm.Bans)
        {
            RectTransform row = kit.Row(host, "Ban " + b.Id);
            TMP_Text line = kit.Label(row, b.Name);
            line.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            SessionBan entry = b;
            kit.Button(row, "Unban", () => { _vm.Unban(entry.Id); Note($"unbanned {entry.Name}"); }, width: 110f);
        }
        _vm.RequestBanList();
    }

    private void RoleChip(WidgetKit kit, RectTransform row, PlayerRole role)
    {
        if (role == PlayerRole.Owner) kit.Chip(row, "Host", ChipKind.Warning);
        else if (role == PlayerRole.Admin) kit.Chip(row, "Admin", ChipKind.Info);
    }

    private void PingBits(WidgetKit kit, RectTransform row, int id)
    {
        if (_vm.PingOf(id) is not int ms) return;
        kit.PingDot(row, kit.PingColor(ms));
        TMP_Text value = kit.Label(row, ms + " ms", dim: true, size: kit.Theme.MetaSize);
        value.alignment = TextAlignmentOptions.MidlineRight;
        value.gameObject.AddComponent<LayoutElement>().preferredWidth = 64f;
    }

    // ── Session control: password, cap, hold-the-door, Save & Stop ──

    private void BuildSession(RectTransform column, WidgetKit kit)
    {
        RectTransform pwRow = kit.Row(column, "Password");
        TMP_InputField pw = kit.LabeledField(pwRow, "Password", "(open session)");
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
        TMP_InputField cap = kit.LabeledField(capRow, "Max players", "1–99");
        kit.Button(capRow, "Apply", () =>
        {
            if (int.TryParse(cap.text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 99)
            {
                _vm.SetMaxPlayers(n);
                Note($"max players set to {n}");
            }
            else Note("max players must be 1–99");
        }, width: 110f);

        Toggle joins = kit.Toggle(column, "Pause new joins",
            _vm.JoinsPaused, on => { _vm.SetJoinsPaused(on); Note(on ? "joins paused" : "joins resumed"); });
        TMP_Text preset = kit.Label(column, "", dim: true);

        kit.Separator(column, kit.Theme.DangerDim);
        RectTransform stopRow = kit.Row(column, "Save & Stop");
        kit.Subline(stopRow, "Saves and ends the session for all players.")
            .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(stopRow, "Save & Stop", () => { Note("session ended by Save & Stop"); _vm.SaveAndStop(); },
            ButtonTier.Danger, width: 150f);

        _refreshDynamic = () =>
        {
            if (preset != null) preset.text = $"Career preset: {_vm.PresetName}";
            if (joins != null) joins.SetIsOnWithoutNotify(_vm.JoinsPaused);
        };
    }

    // ── World/save: save-now, autosave cadence, backup rotation, the exporter ──

    private void BuildWorldSave(RectTransform column, WidgetKit kit)
    {
        RectTransform saveRow = kit.Row(column, "Save now");
        TMP_Text autosaveLine = kit.Label(saveRow, "", dim: true);
        autosaveLine.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(saveRow, "Save now", () => { _vm.SaveNow(); Note("save requested"); }, width: 120f);

        RectTransform intervalRow = kit.Row(column, "Autosave interval");
        TMP_InputField interval = kit.LabeledField(intervalRow, "Autosave (s)", "5–3600");
        kit.Button(intervalRow, "Apply", () =>
        {
            if (int.TryParse(interval.text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s is >= 5 and <= 3600)
            {
                _vm.SetAutosaveInterval(s);
                Note($"autosave interval set to {s} s");
            }
            else Note("autosave interval must be 5–3600 seconds");
        }, width: 110f);

        kit.SectionLabel(column, "Backups (newest first)");
        kit.Subline(column, "Restoring ends the session.");
        RectTransform backups = kit.Column(column, "Backup Rows");

        RectTransform exportRow = kit.Row(column, "Exporter");
        kit.Subline(exportRow, "Export world topology (.lmpw).")
            .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        kit.Button(exportRow, "Export",
            () => Note(_extractTopology?.Invoke() ?? "exporter unavailable"),
            ButtonTier.Secondary, enabled: _extractTopology != null, width: 120f);

        _refreshDynamic = () => RefreshBackups(backups, kit, autosaveLine);
    }

    private void RefreshBackups(RectTransform host, WidgetKit kit, TMP_Text autosaveLine)
    {
        if (autosaveLine != null) autosaveLine.text = $"Autosave every {_vm.AutosaveSeconds} s.";
        if (host == null) return;
        for (int i = host.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(host.GetChild(i).gameObject);
        var backups = _vm.Backups;
        if (backups.Count == 0)
        {
            kit.Subline(host, "No backups yet — one rotates in at each save.");
            return;
        }
        bool odd = false;
        foreach (SaveBackupInfo b in backups)
        {
            SaveBackupInfo backup = b;
            RectTransform row = kit.StripedRow(host, odd, "Backup " + backup.Index);
            odd = !odd;
            TMP_Text name = kit.Label(row, "Backup " + backup.Index);
            name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            double age = (DateTime.UtcNow - backup.LastWriteUtc).TotalMinutes;
            kit.Label(row, $"{Bytes(backup.SizeBytes)} · {age:F0} min ago", dim: true, size: kit.Theme.MetaSize);
            kit.Button(row, "Restore", () =>
            {
                if (_vm.RestoreBackupAndStop(backup.Index, out string? why))
                    Note($"restored backup {backup.Index} — session ended, host again to load it");
                else
                    Note($"restore failed: {why}");
            }, ButtonTier.Danger, width: 120f);
        }
    }

    // ── Diagnostics: the CaptureDiagnostics snapshot + copy-report ──

    private void BuildDiagnostics(RectTransform column, WidgetKit kit)
    {
        RectTransform dynamic = kit.Column(column, "Diag");
        _refreshDynamic = () => RefreshDiagnostics(dynamic, kit);
    }

    private void RefreshDiagnostics(RectTransform host, WidgetKit kit)
    {
        if (host == null) return;
        for (int i = host.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(host.GetChild(i).gameObject);
        if (_vm.Diagnostics is not ServerDiagnostics diag)
        {
            kit.Subline(host, "Diagnostics are host-only.");
            return;
        }

        KvRow(kit, host, "Players", $"{diag.Players}" + (diag.Queued > 0 ? $" (+{diag.Queued} queued)" : ""));
        KvRow(kit, host, "Trainsets", diag.Trainsets.ToString(CultureInfo.InvariantCulture));
        KvRow(kit, host, "Jobs", diag.Jobs.ToString(CultureInfo.InvariantCulture));
        KvRow(kit, host, "Items", diag.Items.ToString(CultureInfo.InvariantCulture));
        KvRow(kit, host, "Stale snapshots dropped", diag.StaleSnapshotsDropped.ToString(CultureInfo.InvariantCulture));
        KvRow(kit, host, "Admins · bans", $"{diag.Admins} · {diag.BannedKeys}");
        KvRow(kit, host, "Traffic sent", $"{Bytes(diag.BytesSent)} · {diag.MessagesSent:N0} msgs");
        KvRow(kit, host, "Traffic received", $"{Bytes(diag.BytesReceived)} · {diag.MessagesReceived:N0} msgs");

        RectTransform verdicts = kit.Row(host, "Verdicts");
        kit.Label(verdicts, "Money", dim: true, size: kit.Theme.MetaSize);
        kit.Chip(verdicts, diag.MoneyConservationHolds ? "OK" : "Broken",
            diag.MoneyConservationHolds ? ChipKind.Success : ChipKind.Danger);
        kit.Label(verdicts, "Items", dim: true, size: kit.Theme.MetaSize);
        kit.Chip(verdicts, diag.ItemConservationHolds ? "OK" : "Broken",
            diag.ItemConservationHolds ? ChipKind.Success : ChipKind.Danger);
        kit.Label(verdicts, "Interest", dim: true, size: kit.Theme.MetaSize);
        kit.Chip(verdicts, diag.InterestEnabled ? "On" : "Off",
            diag.InterestEnabled ? ChipKind.Info : ChipKind.Neutral);
        kit.Label(verdicts, "Joins", dim: true, size: kit.Theme.MetaSize);
        kit.Chip(verdicts, diag.JoinsPaused ? "Paused" : "Open",
            diag.JoinsPaused ? ChipKind.Warning : ChipKind.Info);

        RectTransform row2 = kit.Row(host, "Copy");
        kit.Label(row2, "", dim: true).gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        string report = DiagnosticsReport(diag);
        kit.Button(row2, "Copy report", () =>
        {
            GUIUtility.systemCopyBuffer = report;
            Note("diagnostics copied to the clipboard");
        }, width: 140f);
    }

    private void KvRow(WidgetKit kit, RectTransform host, string key, string value)
    {
        RectTransform row = kit.Row(host, "Kv " + key);
        row.GetComponent<LayoutElement>().preferredHeight = 30f;
        TMP_Text k = kit.Label(row, key, dim: true);
        k.gameObject.AddComponent<LayoutElement>().preferredWidth = 320f;
        kit.Label(row, value);
    }

    /// <summary>Humanised byte counts (D25 §10.3) — panel display only; the clipboard report keeps
    /// raw numbers (it is FOR bug reports).</summary>
    private static string Bytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

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
