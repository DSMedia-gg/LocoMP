using System;
using DV.Interaction.Inputs;
using DV.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>Visual weight of a button (D25, 10-plan §10.2): one filled Primary per screen,
/// Secondary outlines for everything else, Danger for destructive verbs, Tab for switchers.</summary>
public enum ButtonTier
{
    Primary,
    Secondary,
    Danger,
    Tab,
}

/// <summary>Chip semantics (session state, roles, verdicts) — colours resolve from the theme so a
/// harvested DV palette flows through.</summary>
public enum ChipKind
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>
/// The widget factory (M5.0 step 3; D25 skin): runtime-built uGUI + TMP styled from
/// <see cref="LocoMpTheme"/>. Plain construction, no prefabs and no AssetBundle (plan §3 default —
/// U1 resolved runtime-built); corners/borders come from the theme's procedural 9-slice sprites and
/// degrade to the old flat rectangles when those are null. Hover/pressed states ride uGUI
/// ColorBlocks (the clone-ButtonDV lane from ui-recon stays a future option). Everything returns
/// the component the caller binds to; layout is driven by LayoutGroups on the parents.
/// </summary>
public sealed class WidgetKit
{
    private readonly LocoMpTheme _t;

    public WidgetKit(LocoMpTheme theme)
    {
        _t = theme;
        _t.EnsureRuntimeAssets();
    }

    public LocoMpTheme Theme => _t;

    /// <summary>A themed panel: rounded, hairline-framed (<paramref name="framed"/>). With
    /// <paramref name="vertical"/> it lays its children out as a padded column.</summary>
    public RectTransform Panel(RectTransform parent, bool vertical = false, string name = "Panel",
        bool framed = true)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, worldPositionStays: false);
        Image image = go.GetComponent<Image>();
        image.color = _t.Panel;
        ApplyRounding(image);
        if (framed) Border(rect, _t.Hairline);
        if (vertical)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 14, 14);
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
        layout.childAlignment = TextAnchor.MiddleLeft;
        var element = go.AddComponent<LayoutElement>();
        element.preferredHeight = _t.RowHeight;
        return rect;
    }

    /// <summary>A row with zebra striping (D25 §10.2): odd rows carry a 3% white wash.</summary>
    public RectTransform StripedRow(RectTransform parent, bool odd, string name = "Row")
    {
        RectTransform row = Row(parent, name);
        if (odd)
        {
            var image = row.gameObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.03f);
            ApplyRounding(image);
            image.raycastTarget = false;
        }
        return row;
    }

    /// <summary>A 1 px horizontal separator (danger-zone framing, section splits).</summary>
    public void Separator(RectTransform parent, Color? color = null)
    {
        var go = new GameObject("Separator", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, worldPositionStays: false);
        go.GetComponent<Image>().color = color ?? _t.Hairline;
        go.GetComponent<Image>().raycastTarget = false;
        var element = go.AddComponent<LayoutElement>();
        element.preferredHeight = 1f;
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

    /// <summary>An explainer demoted below label weight (D25 copy rules §10.3).</summary>
    public TMP_Text Subline(RectTransform parent, string text)
    {
        TMP_Text tmp = Label(parent, text, dim: false, size: _t.MetaSize);
        tmp.color = _t.TextDisabled;
        return tmp;
    }

    /// <summary>An uppercase letter-spaced section microheader (D25 §10.2).</summary>
    public TMP_Text SectionLabel(RectTransform parent, string text)
    {
        TMP_Text tmp = Label(parent, text.ToUpperInvariant(), dim: true, size: _t.SectionSize);
        tmp.characterSpacing = 6f;
        tmp.alignment = TextAlignmentOptions.BottomLeft;
        var element = tmp.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = 30f;
        return tmp;
    }

    /// <summary>Legacy overload — Secondary tier (the pre-D25 call shape).</summary>
    public Button Button(RectTransform parent, string label, Action onClick, bool enabled = true,
        float width = 0f)
        => Button(parent, label, onClick, ButtonTier.Secondary, enabled, width);

    public Button Button(RectTransform parent, string label, Action onClick, ButtonTier tier,
        bool enabled = true, float width = 0f)
    {
        var go = new GameObject("Button " + label, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, worldPositionStays: false);
        Image image = go.GetComponent<Image>();
        image.color = Color.white; // the ColorBlock carries the rendered colour (white base = absolute)
        ApplyRounding(image);
        Border((RectTransform)go.transform, BorderColorFor(tier));

        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.colors = ColorsFor(tier);
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
        tmp.color = LabelColorFor(tier);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.text = label;
        tmp.raycastTarget = false;

        if (tier == ButtonTier.Tab)
        {
            var underGo = new GameObject("Underline", typeof(RectTransform), typeof(Image));
            var underRect = (RectTransform)underGo.transform;
            underRect.SetParent(go.transform, worldPositionStays: false);
            underRect.anchorMin = new Vector2(0f, 0f);
            underRect.anchorMax = new Vector2(1f, 0f);
            underRect.pivot = new Vector2(0.5f, 0f);
            underRect.offsetMin = new Vector2(2f, 0f);
            underRect.offsetMax = new Vector2(-2f, 3f);
            underGo.GetComponent<Image>().color = _t.Accent;
            underGo.GetComponent<Image>().raycastTarget = false;
            underGo.AddComponent<LayoutElement>().ignoreLayout = true;
            underGo.SetActive(false);
        }

        if (!enabled) SetEnabled(button, false, tier);
        return button;
    }

    /// <summary>A tab-bar switcher (transparent, hover wash, underline when active).</summary>
    public Button TabButton(RectTransform parent, string label, Action onClick, bool enabled = true,
        float width = 0f)
        => Button(parent, label, onClick, ButtonTier.Tab, enabled, width);

    /// <summary>The one enable/disable path (D25): interactable + border + label together, so a
    /// disabled control reads as an outline ghost in every tier. Callers restate the tier —
    /// stateless by design (screens rebuild often; the kit holds no per-button registry).</summary>
    public void SetEnabled(Button button, bool enabled, ButtonTier tier = ButtonTier.Secondary)
    {
        button.interactable = enabled;
        Transform t = button.transform;
        if (t.Find("Border") is { } borderTf && borderTf.TryGetComponent(out Image border))
            border.color = enabled ? BorderColorFor(tier) : _t.Hairline;
        if (t.Find("Label") is { } labelTf && labelTf.TryGetComponent(out TextMeshProUGUI label))
            label.color = enabled ? LabelColorFor(tier) : _t.TextDisabled;
    }

    /// <summary>Active-tab state: underline + bright label (never hover-only — VR laser rule).</summary>
    public void SetTabActive(Button button, bool active)
    {
        Transform t = button.transform;
        if (t.Find("Underline") is { } under) under.gameObject.SetActive(active);
        if (t.Find("Label") is { } labelTf && labelTf.TryGetComponent(out TextMeshProUGUI label))
            label.color = active ? _t.Text : _t.TextDim;
    }

    /// <summary>A bordered uppercase tag (session state, roles, verdicts — D25 §10.2).</summary>
    public RectTransform Chip(RectTransform parent, string text, ChipKind kind)
    {
        (Color textColor, Color borderColor, Color fill) = ChipColors(kind);
        var go = new GameObject("Chip " + text, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, worldPositionStays: false);
        Image image = go.GetComponent<Image>();
        image.color = fill;
        ApplyRounding(image);
        image.raycastTarget = false;
        Border(rect, borderColor);

        var labelGo = new GameObject("Label", typeof(RectTransform));
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.SetParent(rect, worldPositionStays: false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        if (_t.Font != null) tmp.font = _t.Font;
        tmp.fontSize = _t.MetaSize - 1;
        tmp.characterSpacing = 5f;
        tmp.color = textColor;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.text = text.ToUpperInvariant();
        tmp.raycastTarget = false;

        var element = go.AddComponent<LayoutElement>();
        element.preferredHeight = 26f;
        element.preferredWidth = Mathf.Ceil(tmp.GetPreferredValues(tmp.text).x) + 20f;
        return rect;
    }

    /// <summary>A small coloured dot (ping ramp, markers).</summary>
    public Image PingDot(RectTransform parent, Color color, float size = 9f)
    {
        var go = new GameObject("Dot", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, worldPositionStays: false);
        Image image = go.GetComponent<Image>();
        if (_t.Dot != null) image.sprite = _t.Dot;
        image.color = color;
        image.raycastTarget = false;
        var element = go.AddComponent<LayoutElement>();
        element.preferredWidth = size;
        element.preferredHeight = size;
        return image;
    }

    /// <summary>Ping colour ramp (D25 §10.2): green under 80 ms, amber under 150, red beyond.</summary>
    public Color PingColor(int ms) => ms < 80 ? _t.Success : ms < 150 ? _t.Warning : _t.Danger;

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
        Image image = go.GetComponent<Image>();
        image.color = _t.Field;
        ApplyRounding(image);
        Image? border = Border((RectTransform)go.transform, _t.Hairline);
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
        // strict keyboard-focus refcount for the field's selected lifetime. The same select edge
        // drives the D25 focus ring (accent border while selected).
        input.onSelect.AddListener(_ =>
        {
            TakeKeyboardFocus();
            if (border != null) border.color = _t.Accent;
        });
        input.onDeselect.AddListener(_ =>
        {
            ReleaseKeyboardFocus();
            if (border != null) border.color = _t.Hairline;
        });
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
        Image box = boxGo.GetComponent<Image>();
        box.color = _t.Field;
        ApplyRounding(box);
        Border(boxRect, _t.Hairline, ignoreLayout: false);

        // The mark: DV's font carries ✓ on B99.7 — but never assume a glyph (font harvest can
        // fail, B100 may swap fonts). HasCharacter gates it; the fallback is an accent square.
        Graphic mark;
        bool tick = _t.Font != null && _t.Font.HasCharacter('✓');
        if (tick)
        {
            var markGo = new GameObject("Mark", typeof(RectTransform));
            var markRect = (RectTransform)markGo.transform;
            markRect.SetParent(boxRect, worldPositionStays: false);
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.offsetMin = Vector2.zero;
            markRect.offsetMax = Vector2.zero;
            var tmp = markGo.AddComponent<TextMeshProUGUI>();
            tmp.font = _t.Font;
            tmp.fontSize = 18f;
            tmp.color = _t.Accent;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.text = "✓";
            tmp.raycastTarget = false;
            mark = tmp;
        }
        else
        {
            var markGo = new GameObject("Mark", typeof(RectTransform), typeof(Image));
            var markRect = (RectTransform)markGo.transform;
            markRect.SetParent(boxRect, worldPositionStays: false);
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.offsetMin = new Vector2(5f, 5f);
            markRect.offsetMax = new Vector2(-5f, -5f);
            Image image = markGo.GetComponent<Image>();
            ApplyRounding(image);
            image.color = _t.Accent;
            mark = image;
        }

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(go.transform, worldPositionStays: false);
        var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
        if (_t.Font != null) labelTmp.font = _t.Font;
        labelTmp.fontSize = _t.Size;
        labelTmp.color = _t.Text;
        labelTmp.text = label;
        labelTmp.raycastTarget = false;
        ((RectTransform)labelGo.transform).sizeDelta = new Vector2(560f, _t.RowHeight);

        var toggle = go.AddComponent<Toggle>();
        toggle.targetGraphic = box;
        toggle.graphic = mark;
        toggle.isOn = on;
        toggle.onValueChanged.AddListener(v => changed(v));
        return toggle;
    }

    // ── shared construction helpers ───────────────────────────────────────────────────────────

    private void ApplyRounding(Image image)
    {
        if (_t.RoundedFill == null) return;
        image.sprite = _t.RoundedFill;
        image.type = Image.Type.Sliced;
    }

    /// <summary>A stretched hairline-frame child. LayoutElement.ignoreLayout keeps it out of any
    /// layout group on the parent; raycastTarget stays off so it never eats clicks.</summary>
    private Image? Border(RectTransform parent, Color color, bool ignoreLayout = true)
    {
        if (_t.RoundedBorder == null) return null;
        var go = new GameObject("Border", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, worldPositionStays: false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        Image image = go.GetComponent<Image>();
        image.sprite = _t.RoundedBorder;
        image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
        if (ignoreLayout) go.AddComponent<LayoutElement>().ignoreLayout = true;
        return image;
    }

    private ColorBlock ColorsFor(ButtonTier tier)
    {
        Color normal, hover, pressed;
        switch (tier)
        {
            case ButtonTier.Primary:
                normal = _t.Accent;
                hover = _t.AccentHover;
                pressed = _t.AccentPressed;
                break;
            case ButtonTier.Danger:
                normal = WithAlpha(_t.Danger, 0.10f);
                hover = WithAlpha(_t.Danger, 0.55f);
                pressed = WithAlpha(_t.Danger, 0.75f);
                break;
            case ButtonTier.Tab:
                normal = Color.clear;
                hover = new Color(1f, 1f, 1f, 0.04f);
                pressed = new Color(1f, 1f, 1f, 0.08f);
                break;
            default:
                normal = new Color(1f, 1f, 1f, 0.02f);
                hover = new Color(1f, 1f, 1f, 0.06f);
                pressed = new Color(1f, 1f, 1f, 0.10f);
                break;
        }
        return new ColorBlock
        {
            normalColor = normal,
            highlightedColor = hover,
            pressedColor = pressed,
            selectedColor = normal,
            disabledColor = Color.clear,
            colorMultiplier = 1f,
            fadeDuration = 0.1f,
        };
    }

    private Color BorderColorFor(ButtonTier tier) => tier switch
    {
        ButtonTier.Primary => Color.clear,
        ButtonTier.Danger => _t.DangerDim,
        ButtonTier.Tab => Color.clear,
        _ => _t.Hairline,
    };

    private Color LabelColorFor(ButtonTier tier) => tier switch
    {
        ButtonTier.Primary => Color.white,
        ButtonTier.Danger => _t.Text,
        ButtonTier.Tab => _t.TextDim,
        _ => _t.Text,
    };

    private (Color text, Color border, Color fill) ChipColors(ChipKind kind)
    {
        Color baseColor = kind switch
        {
            ChipKind.Info => _t.Accent,
            ChipKind.Success => _t.Success,
            ChipKind.Warning => _t.Warning,
            ChipKind.Danger => _t.Danger,
            _ => _t.TextDim,
        };
        if (kind == ChipKind.Neutral)
            return (_t.TextDim, _t.Hairline, Color.clear);
        return (Color.Lerp(baseColor, Color.white, 0.35f),
                Color.Lerp(baseColor, _t.Panel, 0.45f),
                WithAlpha(baseColor, 0.08f));
    }

    private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
}
