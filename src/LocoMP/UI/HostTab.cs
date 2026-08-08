using System;
using System.Globalization;
using LocoMP.Core.Career;
using LocoMP.Core.Session;
using LocoMP.Shim;
using TMPro;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The Host tab body (M5.1): the full <see cref="HostOptions"/> surface — port, password, max
/// players, autosave cadence, the D3 preset toggle, D15 auto-grant, fresh-career and D10 interest
/// toggles — driving <see cref="SessionViewModel.Host"/>, the same code path as the dev panel.
/// Hosting is gated on a live world for the same reason joining is: the host IS the world source.
/// </summary>
public sealed class HostTab
{
    private readonly SessionViewModel _vm;
    private readonly UiPrefs _prefs;
    private readonly Action<string> _log;

    private WidgetKit? _kit;
    private TMP_InputField? _name;
    private TMP_InputField? _port;
    private TMP_InputField? _password;
    private TMP_InputField? _maxPlayers;
    private TMP_InputField? _autosave;
    private bool _sharedCareer;
    private bool _freshCareer;
    private bool _autoGrant;
    private bool _interest;
    private UnityEngine.UI.Button? _host;
    private TMP_Text? _hint;

    public HostTab(SessionViewModel vm, UiPrefs prefs, Action<string> log)
    {
        _vm = vm;
        _prefs = prefs;
        _log = log;
    }

    public void Build(RectTransform parent, WidgetKit kit)
    {
        RectTransform column = kit.Column(parent, "Host Form");

        _name = kit.LabeledField(column, "Your name", "Player", _prefs.PlayerName);
        _port = kit.LabeledField(column, "Port", NetDefaults.Port.ToString(CultureInfo.InvariantCulture),
            _prefs.HostPort.ToString(CultureInfo.InvariantCulture), fieldWidth: 140f);
        _password = kit.LabeledField(column, "Password", "(open session)", _prefs.HostPassword, masked: true);
        _maxPlayers = kit.LabeledField(column, "Max players", "32", "32", fieldWidth: 140f);
        _autosave = kit.LabeledField(column, "Autosave (seconds)", "120", "120", fieldWidth: 140f);

        kit.Toggle(column, "Shared career", _sharedCareer, v => _sharedCareer = v);
        kit.Toggle(column, "Fresh career", _freshCareer, v => _freshCareer = v);
        kit.Toggle(column, "Auto-grant licenses", _autoGrant, v => _autoGrant = v);
        kit.Toggle(column, "Only sync nearby trains and players", _interest, v => _interest = v);

        RectTransform actions = kit.Row(column, "Actions");
        _kit = kit;
        _host = kit.Button(actions, "Host session", OnHost, ButtonTier.Primary, width: 220f);
        _hint = kit.Label(column, "", dim: true);
        Refresh();
    }

    public void Refresh()
    {
        if (_kit == null || _host == null || _hint == null) return;
        bool worldAlive = PresenceShim.WorldAlive;
        bool idle = _vm.Phase == SessionPhase.Idle;
        _kit.SetEnabled(_host, idle && worldAlive, ButtonTier.Primary);
        SetHint(!worldAlive
            ? "Host from the pause menu once your world is loaded."
            : idle ? "" : "Already in a session.");
    }

    private void SetHint(string text)
    {
        if (_hint == null || _kit == null) return;
        _hint.text = text;
        _hint.color = text.StartsWith("⚠", StringComparison.Ordinal)
            ? _kit.Theme.Danger : _kit.Theme.TextDim;
    }

    private void OnHost()
    {
        if (_name == null || _port == null || _password == null || _maxPlayers == null || _autosave == null) return;
        if (!TryParse(_port.text, 1, 65535, out int port))
        {
            SetHint("⚠ Port must be 1–65535.");
            return;
        }
        if (!TryParse(_maxPlayers.text, 1, 256, out int maxPlayers))
        {
            SetHint("⚠ Max players must be 1–256.");
            return;
        }
        if (!TryParse(_autosave.text, 15, 3600, out int autosaveSeconds))
        {
            SetHint("⚠ Autosave must be 15–3600 seconds.");
            return;
        }

        string name = _name.text.Trim();
        _prefs.PlayerName = name.Length > 0 ? name : _prefs.PlayerName;
        _prefs.HostPort = port;
        _prefs.HostPassword = _password.text; // D22: what you hosted with is what you host with next
        _prefs.Save(_log);

        _vm.Host(new HostOptions
        {
            PlayerName = name.Length > 0 ? name : "Player",
            Port = port,
            Password = _password.text.Length > 0 ? _password.text : null,
            Preset = _sharedCareer ? ProgressionPreset.SharedCareer : ProgressionPreset.PerPlayer,
            FreshCareer = _freshCareer,
            AutoGrantLicenses = _autoGrant,
            InterestFiltering = _interest,
            MaxPlayers = maxPlayers,
            AutosaveIntervalSeconds = autosaveSeconds,
        });
    }

    private static bool TryParse(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        value >= min && value <= max;
}
