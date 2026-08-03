using System;
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

    public TMP_InputField Field(RectTransform parent, string placeholder, string initial = "", float width = 0f)
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
        input.text = initial;
        return input;
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
