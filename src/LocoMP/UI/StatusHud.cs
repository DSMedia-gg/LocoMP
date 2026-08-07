using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The non-blocking status tier (M5.0 stub, plan §5): a small corner line — "syncing…",
/// "reconnecting…", "waiting for control" — for transient hitches where the world is fine, just
/// behind. It never steals control and never blocks input; the blocking tier is
/// <see cref="ReadinessGate"/>. Wired to real network states in M5.1/M5.4.
/// </summary>
public sealed class StatusHud
{
    private readonly LocoMpTheme _theme;
    private GameObject? _go;
    private TMP_Text? _text;

    public StatusHud(LocoMpTheme theme) => _theme = theme;

    public void Show(string message)
    {
        if (_go == null) Build();
        if (_text != null) _text.text = message;
        _go!.SetActive(true);
    }

    public void Hide()
    {
        if (_go != null) _go.SetActive(false);
    }

    public void Destroy()
    {
        if (_go != null) Object.Destroy(_go);
        _go = null;
        _text = null;
    }

    private void Build()
    {
        _go = new GameObject("LocoMP StatusHud", typeof(RectTransform));
        var canvas = _go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = LocoMpCanvas.OverlaySortingOrder - 1; // under the screens, over the HUD
        var scaler = _go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        LocoMpCanvas.ApplyScale(scaler);

        var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)panelGo.transform;
        rect.SetParent(_go.transform, worldPositionStays: false);
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-24f, -24f);
        rect.sizeDelta = new Vector2(340f, 40f);
        panelGo.GetComponent<Image>().color = _theme.Panel;

        var textGo = new GameObject("Text", typeof(RectTransform));
        var textRect = (RectTransform)textGo.transform;
        textRect.SetParent(rect, worldPositionStays: false);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(12f, 4f);
        textRect.offsetMax = new Vector2(-12f, -4f);
        _text = textGo.AddComponent<TextMeshProUGUI>();
        if (_theme.Font != null) _text.font = _theme.Font;
        _text.fontSize = 20;
        _text.color = _theme.Text;
        _text.alignment = TextAlignmentOptions.MidlineLeft;
        _text.raycastTarget = false;
    }
}
