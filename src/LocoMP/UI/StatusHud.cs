using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The non-blocking status tier (plan §5): a small corner pill — "syncing…", "reconnecting…",
/// "waiting for control" — for transient hitches where the world is fine, just behind. It never
/// steals control and never blocks input; the blocking tier is <see cref="ReadinessGate"/>.
///
/// <para>D25 shape: a bordered pill with a spinner for busy states; <see cref="Show"/> with
/// <c>alert: true</c> drops the spinner and frames red (the SESSION LOST banner is an alarm, not
/// activity). The spinner is driven by <see cref="Tick"/> from the composition root — no
/// coroutines, nothing to leak on scene death.</para>
/// </summary>
public sealed class StatusHud
{
    private readonly LocoMpTheme _theme;
    private GameObject? _go;
    private TMP_Text? _text;
    private Image? _border;
    private GameObject? _spinner;
    private RectTransform? _spinnerRect;

    public StatusHud(LocoMpTheme theme) => _theme = theme;

    public void Show(string message, bool alert = false)
    {
        if (_go == null) Build();
        if (_text != null) _text.text = message;
        if (_border != null) _border.color = alert ? _theme.DangerDim : _theme.Hairline;
        if (_spinner != null) _spinner.SetActive(!alert);
        _go!.SetActive(true);
    }

    public void Hide()
    {
        if (_go != null) _go.SetActive(false);
    }

    /// <summary>Pumped from the composition root while the mod runs; rotates the spinner when the
    /// pill is visible in its busy shape. Cheap no-op otherwise.</summary>
    public void Tick(double dt)
    {
        if (_spinnerRect == null || _go == null || !_go.activeSelf) return;
        if (_spinner != null && !_spinner.activeSelf) return;
        _spinnerRect.Rotate(0f, 0f, (float)(-dt * 300.0));
    }

    public void Destroy()
    {
        if (_go != null) Object.Destroy(_go);
        _go = null;
        _text = null;
        _border = null;
        _spinner = null;
        _spinnerRect = null;
    }

    private void Build()
    {
        _theme.EnsureRuntimeAssets();
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
        rect.sizeDelta = new Vector2(360f, 40f);
        Image panel = panelGo.GetComponent<Image>();
        panel.color = _theme.Panel;
        if (_theme.RoundedFill != null)
        {
            panel.sprite = _theme.RoundedFill;
            panel.type = Image.Type.Sliced;
        }
        if (_theme.RoundedBorder != null)
        {
            var borderGo = new GameObject("Border", typeof(RectTransform), typeof(Image));
            var borderRect = (RectTransform)borderGo.transform;
            borderRect.SetParent(rect, worldPositionStays: false);
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = Vector2.zero;
            borderRect.offsetMax = Vector2.zero;
            _border = borderGo.GetComponent<Image>();
            _border.sprite = _theme.RoundedBorder;
            _border.type = Image.Type.Sliced;
            _border.color = _theme.Hairline;
            _border.raycastTarget = false;
        }

        // The spinner: a dot orbiting inside a 18 px rig — sprite-only (no glyph dependency),
        // rotated from Tick.
        var spinGo = new GameObject("Spinner", typeof(RectTransform));
        _spinnerRect = (RectTransform)spinGo.transform;
        _spinnerRect.SetParent(rect, worldPositionStays: false);
        _spinnerRect.anchorMin = new Vector2(0f, 0.5f);
        _spinnerRect.anchorMax = new Vector2(0f, 0.5f);
        _spinnerRect.pivot = new Vector2(0.5f, 0.5f);
        _spinnerRect.anchoredPosition = new Vector2(22f, 0f);
        _spinnerRect.sizeDelta = new Vector2(18f, 18f);
        _spinner = spinGo;
        var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(Image));
        var dotRect = (RectTransform)dotGo.transform;
        dotRect.SetParent(_spinnerRect, worldPositionStays: false);
        dotRect.anchorMin = new Vector2(0.5f, 0.5f);
        dotRect.anchorMax = new Vector2(0.5f, 0.5f);
        dotRect.pivot = new Vector2(0.5f, 0.5f);
        dotRect.anchoredPosition = new Vector2(0f, 7f);
        dotRect.sizeDelta = new Vector2(6f, 6f);
        Image dot = dotGo.GetComponent<Image>();
        if (_theme.Dot != null) dot.sprite = _theme.Dot;
        dot.color = _theme.Accent;
        dot.raycastTarget = false;

        var textGo = new GameObject("Text", typeof(RectTransform));
        var textRect = (RectTransform)textGo.transform;
        textRect.SetParent(rect, worldPositionStays: false);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(40f, 4f);
        textRect.offsetMax = new Vector2(-14f, -4f);
        _text = textGo.AddComponent<TextMeshProUGUI>();
        if (_theme.Font != null) _text.font = _theme.Font;
        _text.fontSize = 20;
        _text.color = _theme.Text;
        _text.alignment = TextAlignmentOptions.MidlineLeft;
        _text.raycastTarget = false;
    }
}
