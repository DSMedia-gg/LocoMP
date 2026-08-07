using System;
using System.Collections.Generic;
using LocoMP.Net;
using LocoMP.Shim;
using TMPro;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The Active-Friends tab body (M5.5, 10-plan §M5.5): Steam friends currently in Derail Valley with
/// LocoMP, one row each — Join when they're in a session, Invite when WE are hosting. The scan walks
/// the Steam friends list (a native call per friend), so it runs on tab entry and the Refresh button,
/// never per frame; <see cref="Refresh"/> only re-evaluates button states against the session phase.
/// Joins ride the ordinary uGUI join flow (cover, mismatch screen, queue) via
/// <see cref="SessionViewModel.JoinSteam"/>.
/// </summary>
public sealed class FriendsTab
{
    private readonly SessionViewModel _vm;
    private readonly UiPrefs _prefs;
    private readonly Action<string> _log;
    private readonly List<(FriendEntry Entry, UnityEngine.UI.Button? Join, UnityEngine.UI.Button? Invite)> _rows = new();

    private WidgetKit? _kit;
    private RectTransform? _rowsHost;
    private TMP_InputField? _password;
    private TMP_Text? _hint;

    internal FriendsTab(SessionViewModel vm, UiPrefs prefs, Action<string> log)
    {
        _vm = vm;
        _prefs = prefs;
        _log = log;
    }

    public void Build(RectTransform parent, WidgetKit kit)
    {
        _kit = kit;
        RectTransform column = kit.Column(parent, "Friends");

        RectTransform header = kit.Row(column, "Friends Header");
        TMP_Text title = kit.Label(header, "Steam friends running LocoMP", dim: true);
        var titleElement = title.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        titleElement.flexibleWidth = 1f;
        kit.Button(header, "Refresh", Rescan, width: 140f);

        _password = kit.LabeledField(column, "Password (if their session has one)", "(none)", masked: true);
        _rowsHost = kit.Column(column, "Friend Rows");
        _hint = kit.Label(column, "", dim: true);
    }

    /// <summary>Tab became visible — do the actual Steam scan (RootScreen calls this on switch).</summary>
    public void OnTabShown() => Rescan();

    /// <summary>Cheap re-evaluation on every view-model change: button gates only, no Steam calls.</summary>
    public void Refresh()
    {
        if (_hint == null) return;
        bool idle = _vm.Phase == SessionPhase.Idle;
        bool worldAlive = PresenceShim.WorldAlive;
        foreach ((FriendEntry entry, UnityEngine.UI.Button? join, UnityEngine.UI.Button? invite) in _rows)
        {
            if (join != null) join.interactable = entry.JoinableHost != null && idle && worldAlive;
            if (invite != null) invite.interactable = _vm.IsHost;
        }
        _hint.text = !SteamPresence.Available
            ? "Steam isn't available — launch Derail Valley through Steam."
            : !worldAlive
                ? "Load your world first, then join from the pause menu (ESC → MULTIPLAYER)."
                : _rows.Count == 0
                    ? "No friends in LocoMP right now. Invite appears here while you host."
                    : _vm.IsHost ? "Invite opens the Steam overlay's confirmation on their side." : "";
    }

    private void Rescan()
    {
        if (_rowsHost == null || _kit == null) return;
        for (int i = _rowsHost.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(_rowsHost.GetChild(i).gameObject);
        _rows.Clear();

        foreach (FriendEntry entry in SteamPresence.ActiveFriends())
        {
            RectTransform row = _kit.Row(_rowsHost, "Friend " + entry.Id);
            string status = entry.JoinableHost != null ? "in a session" : "in Derail Valley";
            TMP_Text label = _kit.Label(row, $"{entry.Name} — {status}");
            var labelElement = label.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            labelElement.flexibleWidth = 1f;

            FriendEntry captured = entry;
            UnityEngine.UI.Button? join = null;
            if (entry.JoinableHost != null)
                join = _kit.Button(row, "Join", () => OnJoin(captured), width: 120f);
            UnityEngine.UI.Button invite = _kit.Button(row, "Invite", () => OnInvite(captured),
                enabled: _vm.IsHost, width: 120f);
            _rows.Add((entry, join, invite));
        }
        Refresh();
    }

    private void OnJoin(FriendEntry entry)
    {
        if (entry.JoinableHost is not { } host) return;
        _log($"[ui] joining {entry.Name}'s session over the Steam relay");
        _vm.JoinSteam(host, _prefs.PlayerName,
            _password != null && _password.text.Length > 0 ? _password.text : null);
    }

    private void OnInvite(FriendEntry entry)
    {
        if (!SteamPresence.Available) return;
        bool sent = SteamPresence.Invite(entry.Id, SteamPresence.LocalSteamId);
        _log(sent
            ? $"[ui] invited {entry.Name} to this session"
            : $"[ui] invite to {entry.Name} failed (Steam refused)");
        if (_hint != null) _hint.text = sent ? $"Invited {entry.Name}." : $"⚠ Invite to {entry.Name} failed.";
    }
}
