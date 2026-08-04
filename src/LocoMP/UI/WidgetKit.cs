using System;
using DV.Interaction.Inputs;
using DV.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The widget factory (M5.0 step 3): runtime-built uGUI + TMP styled from <see cref="LocoMpTheme"/>.
/// Plain construction, no prefabs and no AssetBundle (plan §3 default; U1 stays open) — the DV
/// native look rides in through the harvested theme font and the DV-matched palette. Everything
/// returns the component the caller binds to; layout is driven by LayoutGroups on the parents
/// (each widget carries a LayoutElement so columns/rows size sanely).
/// </summary>
public sealed class WidgetKit
{
    private readonly LocoMpTheme _t;

    public WidgetKit(LocoMpTheme theme) => _t = theme;

    public LocoMpTheme Theme => _t;

    /// <summary>A themed panel. With <paramref name="vertical"/> it lays its children out as a
    /// padded column (the standard screen body).</summary>
    public RectTransform Panel(RectTransform parent, bool vertical = false, string name = "Panel")
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, worldPositionStays: false);
        go.GetComponent<Image>().color = _t.Panel;
        if (vertical)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 12, 12);
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
        }
        return rect;
    }

    /// <summary>A plain vertical column that fills its parent (no background) — the container a
    /// tab body's form lives in when the parent rect has no layout group of its own (M5.1).</summary>
    public RectTransform Column(RectTransform parent, string name = "Column")
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, worldPositionStays: false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var layout = go.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return rect;
    }

    /// <summary>A horizontal row (tab bars, button rows) inside a vertical body.</summary>
    public RectTransform Row(RectTransform parent, string name = "Row")
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, worldPositionStays: false);
        var layout = go.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var element = go.AddComponent<LayoutElement>();
        element.preferredHeight = _t.RowHeight;
        return rect;
    }

    public TMP_Text Label(RectTransform parent, string text, bool dim = false, int? size = null)
    {
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, worldPositionStays: false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (_t.Font != null) tmp.font = _t.Font;
        tmp.fontSize = size ?? _t.Size;
        tmp.color = dim ? _t.TextDim : _t.Text;
        tmp.text = text;
        tmp.raycastTarget = false;
        return tmp;
    }

    public Button Button(RectTransform parent, string label, Action onClick, bool enabled = true, float width = 0f)
    {
        var go = new GameObject("Button " + label, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, worldPositionStays: false);
        go.GetComponent<Image>().color = enabled ? _t.Accent : _t.AccentDisabled;
        var button = go.AddComponent<Button>();
        // At runtime AddComponent leaves targetGraphic unset (the editor auto-find never ran), so
        // without this a disabled button keeps its full accent tint — M5.1 toggles interactable live.
        button.targetGraphic = go.GetComponent<Image>();
        button.interactable = enabled;
        button.onClick.AddListener(() => onClick());
        var element = go.AddComponent<LayoutElement>();
        element.preferredHeight = _t.RowHeight;
        if (width > 0f) element.preferredWidth = width;

        var labelGo = new GameObject("Label", typeof(RectTransform));
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.SetParent(go.transform, worldPositionStays: false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(12f, 2f);
        labelRect.offsetMax = new Vector2(-12f, -2f);
        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        if (_t.Font != null) tmp.font = _t.Font;
        tmp.fontSize = _t.Size;
        tmp.color = enabled ? _t.Text : _t.TextDim;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.text = label;
        tmp.raycastTarget = false;
        return button;
    }

    /// <summary>A label + input field on one row — the M5.1 form staple. The field stretches to
    /// the row's remaining width unless <paramref name="fieldWidth"/> pins it.</summary>
    public TMP_InputField LabeledField(RectTransform parent, string label, string placeholder,
        string initial = "", bool masked = false, float fieldWidth = 0f)
    {
        RectTransform row = Row(parent, "Row " + label);
        TMP_Text text = Label(row, label);
        var labelElement = text.gameObject.AddComponent<LayoutElement>();
        labelElement.preferredWidth = 190f;
        TMP_InputField field = Field(row, placeholder, initial, fieldWidth, masked);
        if (fieldWidth <= 0f) field.GetComponent<LayoutElement>().flexibleWidth = 1f;
        return field;
    }

    public TMP_InputField Field(RectTransform parent, string placeholder, string initial = "",
        float width = 0f, bool masked = false)
    {
        var go = new GameObject("Field " + placeholder, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, worldPositionStays: false);
        go.GetComponent<Image>().color = _t.Field;
        var element = go.AddComponent<LayoutElement>();
        element.preferredHeight = _t.RowHeight;
        if (width > 0f) element.preferredWidth = width;

        var areaGo = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        var areaRect = (RectTransform)areaGo.transform;
        areaRect.SetParent(go.transform, worldPositionStays: false);
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = new Vector2(10f, 4f);
        areaRect.offsetMax = new Vector2(-10f, -4f);

        TextMeshProUGUI MakeText(string name, Color color, FontStyles style)
        {
            var textGo = new GameObject(name, typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(areaRect, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            if (_t.Font != null) tmp.font = _t.Font;
            tmp.fontSize = _t.Size;
            tmp.color = color;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            return tmp;
        }

        TextMeshProUGUI placeholderText = MakeText("Placeholder", _t.TextDim, FontStyles.Italic);
        placeholderText.text = placeholder;
        TextMeshProUGUI valueText = MakeText("Text", _t.Text, FontStyles.Normal);

        var input = go.AddComponent<TMP_InputField>();
        input.textViewport = areaRect;
        input.textComponent = valueText;
        input.placeholder = placeholderText;
        input.lineType = TMP_InputField.LineType.SingleLine;
        if (masked) input.contentType = TMP_InputField.ContentType.Password;
        input.text = initial;
        // Typing in a LocoMP field must not fire game hotkeys (recon R-UI-4): route through DV's
        // strict keyboard-focus refcount for the field's selected lifetime.
        input.onSelect.AddListener(_ => TakeKeyboardFocus());
        input.onDeselect.AddListener(_ => ReleaseKeyboardFocus());
        return input;
    }

    // DV's InputFocusManager error-logs on a double take/release, and focus we did not take is not
    // ours to release (DV's own fields use the same manager) — hence the guard + ownership latch.
    //
    // The flag alone is NOT the guard (Round 2 finding): only the cab-control keyboard handlers
    // consult hasKeyboardFocus — movement, hotbar, F-keys read Rewired directly. The layer that
    // actually silences them is InputManager.SetKeyboardAndMouseEnabled: a refcounted request that
    // detaches the keyboard+mouse controllers from Rewired's player. uGUI/TMP typing reads
    // UnityEngine.Input, so the field keeps working while every game action goes quiet.
    private static readonly object KbmFocusToken = new object();
    private static bool _tookKeyboardFocus;
    private static bool _detachedKbm;

    private static void TakeKeyboardFocus()
    {
        try
        {
            InputFocusManager manager = SingletonBehaviour<InputFocusManager>.Instance;
            if (manager != null && !manager.hasKeyboardFocus)
            {
                manager.TakeKeyboardFocus();
                _tookKeyboardFocus = true;
            }
        }
        catch (Exception)
        {
            // Main menu / teardown: no manager alive — game hotkeys are inert there anyway.
        }
        if (!_detachedKbm)
        {
            try
            {
                InputManager.SetKeyboardAndMouseEnabled(KbmFocusToken, enabled: false);
                _detachedKbm = true;
            }
            catch (Exception)
            {
                // Rewired not initialized (main menu edge) — nothing to silence there.
            }
        }
    }

    /// <summary>Latch-guarded and idempotent — also the FAILSAFE: LocoMpUi calls this on overlay
    /// close/scene death, because a field destroyed while selected never fires onDeselect, and a
    /// keyboard+mouse detach nobody releases is a total input lockout.</summary>
    internal static void ReleaseKeyboardFocus()
    {
        if (_tookKeyboardFocus)
        {
            _tookKeyboardFocus = false;
            try
            {
                InputFocusManager manager = SingletonBehaviour<InputFocusManager>.Instance;
                if (manager != null && manager.hasKeyboardFocus) manager.ReleaseKeyboardFocus();
            }
            catch (Exception)
            {
                // Manager died with the scene — nothing to release.
            }
        }
        if (_detachedKbm)
        {
            _detachedKbm = false;
            try { InputManager.SetKeyboardAndMouseEnabled(KbmFocusToken, enabled: true); }
            catch (Exception) { /* Rewired torn down — the detach died with it */ }
        }
    }

    public Toggle Toggle(RectTransform parent, string label, bool on, Action<bool> changed)
    {
        var go = new GameObject("Toggle " + label, typeof(RectTransform));
        go.transform.SetParent(parent, worldPositionStays: false);
        var layout = go.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childAlignment = TextAnchor.MiddleLeft;
        var element = go.AddComponent<LayoutElement>();
        element.preferredHeight = _t.RowHeight;

        var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image));
        var boxRect = (RectTransform)boxGo.transform;
        boxRect.SetParent(go.transform, worldPositionStays: false);
        boxRect.sizeDelta = new Vector2(24f, 24f);
        boxGo.GetComponent<Image>().color = _t.Field;

        var markGo = new GameObject("Mark", typeof(RectTransform), typeof(Image));
        var markRect = (RectTransform)markGo.transform;
        markRect.SetParent(boxRect, worldPositionStays: false);
        markRect.anchorMin = Vector2.zero;
        markRect.anchorMax = Vector2.one;
        markRect.offsetMin = new Vector2(5f, 5f);
        markRect.offsetMax = new Vector2(-5f, -5f);
        Image mark = markGo.GetComponent<Image>();
        mark.color = _t.Accent;

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(go.transform, worldPositionStays: false);
        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        if (_t.Font != null) tmp.font = _t.Font;
        tmp.fontSize = _t.Size;
        tmp.color = _t.Text;
        tmp.text = label;
        tmp.raycastTarget = false;
        ((RectTransform)labelGo.transform).sizeDelta = new Vector2(420f, _t.RowHeight);

        var toggle = go.AddComponent<Toggle>();
        toggle.targetGraphic = boxGo.GetComponent<Image>();
        toggle.graphic = mark;
        toggle.isOn = on;
        toggle.onValueChanged.AddListener(v => changed(v));
        return toggle;
    }
}
