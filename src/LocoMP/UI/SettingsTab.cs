using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The Settings tab body (M5.3): the client preferences — default name, UI scale, remote-train
/// smoothing, chat options, and the optional menu hotkey — editing <see cref="UiPrefs"/> and
/// committing via Apply (clamp → store → <see cref="UiPrefs.NotifyChanged"/> → save). Everything
/// live-applies where safe: smoothing is read per frame by the Shim, chat opts on the overlay's
/// next repaint, UI scale on each surface's next rebuild — no session relaunch, no game restart
/// (the M5.3 exit bar: "settings survive a restart and apply without relaunching a session").
/// </summary>
public sealed class SettingsTab
{
    private readonly UiPrefs _prefs;
    private readonly Action<string> _log;

    private TMP_InputField? _name;
    private TMP_InputField? _uiScale;
    private TMP_InputField? _smoothing;
    private TMP_InputField? _chatFade;
    private TMP_InputField? _chatKey;
    private TMP_InputField? _menuKey;
    private bool _chatEnabled;
    private TMP_Text? _hint;

    public SettingsTab(UiPrefs prefs, Action<string> log)
    {
        _prefs = prefs;
        _log = log;
    }

    public void Build(RectTransform parent, WidgetKit kit)
    {
        RectTransform column = kit.Column(parent, "Settings Form");
        _chatEnabled = _prefs.ChatEnabled;

        _name = kit.LabeledField(column, "Default name", "Player", _prefs.PlayerName);
        _uiScale = kit.LabeledField(column, "UI scale",
            "1.0", F(_prefs.UiScale), fieldWidth: 140f);
        _smoothing = kit.LabeledField(column, "Train smoothing",
            "30", F(_prefs.TrainSmoothing), fieldWidth: 140f);
        kit.Toggle(column, "Chat overlay", _chatEnabled, v => _chatEnabled = v);
        _chatFade = kit.LabeledField(column, "Chat fade (seconds)",
            "12", F(_prefs.ChatFadeSeconds), fieldWidth: 140f);
        _chatKey = kit.LabeledField(column, "Chat key",
            "Return", _prefs.ChatKey.ToString(), fieldWidth: 200f);
        _menuKey = kit.LabeledField(column, "Menu hotkey",
            "None", _prefs.MenuKey.ToString(), fieldWidth: 200f);
        kit.Label(column,
            "Keys use Unity KeyCode names (Return, T, F6, None…). UI scale applies to newly " +
            "opened LocoMP panels; everything else applies immediately.", dim: true);

        RectTransform actions = kit.Row(column, "Actions");
        kit.Button(actions, "Apply & save", OnApply, width: 220f);
        _hint = kit.Label(column, "", dim: true);
    }

    public void Refresh() { }

    private void OnApply()
    {
        if (_name == null || _uiScale == null || _smoothing == null ||
            _chatFade == null || _chatKey == null || _menuKey == null) return;

        if (!TryFloat(_uiScale.text, UiPrefs.MinUiScale, UiPrefs.MaxUiScale, out float scale))
        {
            _hint!.text = $"⚠ UI scale must be {F(UiPrefs.MinUiScale)}–{F(UiPrefs.MaxUiScale)}.";
            return;
        }
        if (!TryFloat(_smoothing.text, UiPrefs.MinSmoothing, UiPrefs.MaxSmoothing, out float smoothing))
        {
            _hint!.text = $"⚠ Train smoothing must be {F(UiPrefs.MinSmoothing)}–{F(UiPrefs.MaxSmoothing)}.";
            return;
        }
        if (!TryFloat(_chatFade.text, UiPrefs.MinChatFade, UiPrefs.MaxChatFade, out float fade))
        {
            _hint!.text = $"⚠ Chat fade must be {F(UiPrefs.MinChatFade)}–{F(UiPrefs.MaxChatFade)} seconds.";
            return;
        }
        if (!TryKey(_chatKey.text, out KeyCode chatKey) || chatKey == KeyCode.None)
        {
            _hint!.text = "⚠ Chat key: use a Unity KeyCode name (e.g. Return, T).";
            return;
        }
        if (!TryKey(_menuKey.text, out KeyCode menuKey))
        {
            _hint!.text = "⚠ Menu hotkey: use a Unity KeyCode name, or None for menu buttons only.";
            return;
        }

        string name = _name.text.Trim();
        if (name.Length > 0) _prefs.PlayerName = name;
        _prefs.UiScale = scale;
        _prefs.TrainSmoothing = smoothing;
        _prefs.ChatEnabled = _chatEnabled;
        _prefs.ChatFadeSeconds = fade;
        _prefs.ChatKey = chatKey;
        _prefs.MenuKey = menuKey;
        _prefs.NotifyChanged();   // live-apply fan-out (composition root + chat overlay)
        _prefs.Save(_log);
        _hint!.text = "Settings applied and saved.";
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryFloat(string text, float min, float max, out float value) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        value >= min && value <= max;

    private static bool TryKey(string text, out KeyCode key) =>
        Enum.TryParse(text.Trim(), ignoreCase: true, out key) && Enum.IsDefined(typeof(KeyCode), key);
}
