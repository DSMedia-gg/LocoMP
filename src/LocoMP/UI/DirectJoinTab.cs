using System;
using System.Globalization;
using LocoMP.Core.Session;
using LocoMP.Shim;
using TMPro;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The Direct Join tab body (M5.1): name / address / port / password form, the recent-endpoint
/// list, and the Join action — the uGUI replacement for the IMGUI panel's join fields, driving
/// the same <see cref="SessionViewModel.Join"/> code path. Join is gated on a live world: a
/// joined client mirrors the session INTO the loaded world (SaveSuppressor, TrainSync), so there
/// is nothing to join into from the main menu — that orchestration is a later slice, stated in
/// the hint rather than silently failing.
/// </summary>
public sealed class DirectJoinTab
{
    private readonly SessionViewModel _vm;
    private readonly UiPrefs _prefs;
    private readonly Action<string> _log;

    private TMP_InputField? _name;
    private TMP_InputField? _address;
    private TMP_InputField? _port;
    private TMP_InputField? _password;
    private UnityEngine.UI.Button? _join;
    private TMP_Text? _hint;

    public DirectJoinTab(SessionViewModel vm, UiPrefs prefs, Action<string> log)
    {
        _vm = vm;
        _prefs = prefs;
        _log = log;
    }

    public void Build(RectTransform parent, WidgetKit kit)
    {
        RectTransform column = kit.Column(parent, "Direct Join Form");

        _name = kit.LabeledField(column, "Your name", "Player", _prefs.PlayerName);
        _address = kit.LabeledField(column, "Address", "127.0.0.1", _prefs.Address);
        _port = kit.LabeledField(column, "Port", NetDefaults.Port.ToString(CultureInfo.InvariantCulture),
            _prefs.Port.ToString(CultureInfo.InvariantCulture), fieldWidth: 140f);
        _password = kit.LabeledField(column, "Password", "(none)", masked: true);

        if (_prefs.Recent.Count > 0)
        {
            kit.Label(column, "Recent", dim: true);
            RectTransform recentRow = kit.Row(column, "Recent Endpoints");
            foreach ((string address, int port) in _prefs.Recent)
            {
                string a = address;
                int p = port;
                kit.Button(recentRow, $"{a}:{p}", () =>
                {
                    if (_address != null) _address.text = a;
                    if (_port != null) _port.text = p.ToString(CultureInfo.InvariantCulture);
                });
            }
        }

        RectTransform actions = kit.Row(column, "Actions");
        _join = kit.Button(actions, "Join", OnJoin, width: 220f);
        _hint = kit.Label(column, "", dim: true);
        Refresh();
    }

    public void Refresh()
    {
        if (_join == null || _hint == null) return;
        bool worldAlive = PresenceShim.WorldAlive;
        bool idle = _vm.Phase == SessionPhase.Idle;
        _join.interactable = idle && worldAlive;
        _hint.text = !worldAlive
            ? "Load your world first, then join from the pause menu (ESC → MULTIPLAYER)."
            : idle ? "" : "Already in a session — leave it before joining another.";
    }

    private void OnJoin()
    {
        if (_name == null || _address == null || _port == null || _password == null) return;
        string address = _address.text.Trim();
        if (address.Length == 0)
        {
            if (_hint != null) _hint.text = "⚠ Enter a server address.";
            return;
        }
        if (!int.TryParse(_port.text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            || port is <= 0 or >= 65536)
        {
            if (_hint != null) _hint.text = "⚠ Port must be 1–65535.";
            return;
        }

        string name = _name.text.Trim();
        _prefs.PlayerName = name.Length > 0 ? name : _prefs.PlayerName;
        _prefs.Address = address;
        _prefs.Port = port;
        _prefs.RememberEndpoint(address, port);
        _prefs.Save(_log);

        _vm.Join(new JoinOptions
        {
            PlayerName = name.Length > 0 ? name : "Player",
            Address = address,
            Port = port,
            Password = _password.text.Length > 0 ? _password.text : null,
        });
    }
}
