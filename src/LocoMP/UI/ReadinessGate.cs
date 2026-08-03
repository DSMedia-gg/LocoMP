using System;
using System.Collections.Generic;
using DV.UI;
using DV.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>What stalled when a gate hit its failsafe — handed to the caller's onFail.</summary>
public readonly struct GateFailure
{
    public GateFailure(string stalledStage) => StalledStage = stalledStage;
    public string StalledStage { get; }
}

/// <summary>
/// The readiness-gate primitive (M5.0 step 5, plan §5): a full-screen input-blocking cover with
/// staged progress text, used at every coherence boundary (join snapshot, world handover,
/// reconnect restore, Save &amp; Stop teardown — wired per boundary at their own slices, U5).
///
/// <b>The invariant is structural: the timer can only FAIL, never clear.</b> The one clear path
/// is the real completion signal (<c>done()</c>); the failsafe resolves to an explanatory state
/// with "keep waiting" / "give up" actions — never a silent lift, never a silent hang (02's
/// "never a bare timeout" doctrine). This is client-local: the sim keeps ticking behind it
/// (the server never pauses — the cover blocks THIS player's interaction, nothing else).
/// </summary>
public sealed class ReadinessGate
{
    private readonly Action<string> _log;
    private readonly LocoMpTheme _theme;
    private readonly List<string> _stages = new();
    private int _stage;
    private Func<bool>? _done;
    private double _failsafe;
    private Action<GateFailure>? _onFail;
    private Action? _onGiveUp;
    private bool _failed;

    private GameObject? _coverGo;
    private TMP_Text? _titleText;
    private TMP_Text? _stageText;
    private GameObject? _explainGo;

    public ReadinessGate(LocoMpTheme theme, Action<string> log)
    {
        _theme = theme;
        _log = log;
    }

    public bool Active { get; private set; }

    /// <summary>Begin a blocking cover. <paramref name="done"/> is the REAL completion signal —
    /// the only thing that clears it. <paramref name="failsafeSeconds"/> only ever triggers the
    /// explain path; <paramref name="onGiveUp"/> runs when the player abandons from there
    /// (e.g. Leave()).</summary>
    public void Begin(string title, IEnumerable<string> stages, Func<bool> done,
                      double failsafeSeconds, Action<GateFailure>? onFail = null, Action? onGiveUp = null)
    {
        Clear();
        _stages.AddRange(stages);
        if (_stages.Count == 0) _stages.Add(title);
        _stage = 0;
        _done = done;
        _failsafe = failsafeSeconds;
        _onFail = onFail;
        _onGiveUp = onGiveUp;
        _failed = false;
        Active = true;
        BuildCover(title);
        RequestCursor(true);
    }

    /// <summary>Advance the staged progress display (never gates completion — display only).</summary>
    public void SetStage(int index)
    {
        _stage = Mathf.Clamp(index, 0, _stages.Count - 1);
        RefreshStageText();
    }

    /// <summary>"Keep waiting" — from the explain screen, or programmatically.</summary>
    public void ExtendFailsafe(double seconds)
    {
        _failsafe += seconds;
        if (_failed)
        {
            _failed = false;
            if (_explainGo != null) _explainGo.SetActive(false);
            RefreshStageText();
        }
    }

    public void Tick(double dt)
    {
        if (!Active || _done == null) return;
        if (_done())
        {
            // The ONLY clear path: the completion signal itself.
            Clear();
            return;
        }
        if (_failed) return; // explain screen is up — the clock stops until the player decides
        if ((_failsafe -= dt) <= 0)
        {
            _failed = true;
            var failure = new GateFailure(_stages[Math.Min(_stage, _stages.Count - 1)]);
            _log($"[ui] readiness gate stalled at '{failure.StalledStage}' — showing the explain screen");
            ShowExplain(failure);
            _onFail?.Invoke(failure);
        }
    }

    public void Clear()
    {
        if (_coverGo != null) UnityEngine.Object.Destroy(_coverGo);
        _coverGo = null;
        _explainGo = null;
        _titleText = null;
        _stageText = null;
        _stages.Clear();
        _done = null;
        _onFail = null;
        _onGiveUp = null;
        _failed = false;
        if (Active) RequestCursor(false);
        Active = false;
    }

    private void RequestCursor(bool on)
    {
        // CursorManager is DV's refcounted cursor request system (recon R-UI-4). Auto-creating
        // singleton — guard with try/catch rather than an Instance-null probe (the M4 lesson:
        // the null check itself can resurrect one on a dead world).
        try
        {
            if (on) SingletonBehaviour<CursorManager>.Instance.RequestCursor(this, visible: true);
            else SingletonBehaviour<CursorManager>.Instance.RemoveRequest(this);
        }
        catch (Exception)
        {
            // No cursor manager (headless-ish edge, world teardown) — the cover still covers.
        }
    }

    private void BuildCover(string title)
    {
        _coverGo = new GameObject("LocoMP ReadinessGate", typeof(RectTransform));
        var canvas = _coverGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = LocoMpCanvas.OverlaySortingOrder + 100; // above every LocoMP screen
        var scaler = _coverGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        _coverGo.AddComponent<GraphicRaycaster>();

        var dimGo = new GameObject("Dim", typeof(RectTransform), typeof(Image));
        var dimRect = (RectTransform)dimGo.transform;
        dimRect.SetParent(_coverGo.transform, worldPositionStays: false);
        dimRect.anchorMin = Vector2.zero;
        dimRect.anchorMax = Vector2.one;
        dimRect.offsetMin = Vector2.zero;
        dimRect.offsetMax = Vector2.zero;
        Image dim = dimGo.GetComponent<Image>();
        dim.color = _theme.CoverDim;
        dim.raycastTarget = true; // the input barrier — clicks stop here

        var kit = new WidgetKit(_theme);
        RectTransform column = kit.Panel(dimRect, vertical: true, name: "Center");
        column.anchorMin = new Vector2(0.5f, 0.5f);
        column.anchorMax = new Vector2(0.5f, 0.5f);
        column.pivot = new Vector2(0.5f, 0.5f);
        column.sizeDelta = new Vector2(640f, 0f);
        column.GetComponent<Image>().color = Color.clear;
        var fitter = column.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _titleText = kit.Label(column, title, dim: false, size: _theme.TitleSize);
        _titleText.alignment = TextAlignmentOptions.Center;
        _stageText = kit.Label(column, "", dim: true);
        _stageText.alignment = TextAlignmentOptions.Center;
        RefreshStageText();

        // The explain state, hidden until the failsafe fires: name the stalled stage, offer
        // keep-waiting / give-up. Built up front so failure needs no construction work.
        var explainKit = kit;
        RectTransform explain = explainKit.Panel(column, vertical: true, name: "Explain");
        explain.GetComponent<Image>().color = _theme.PanelLight;
        _explainGo = explain.gameObject;
        explainKit.Label(explain, "This is taking longer than expected.", dim: false);
        var buttons = explainKit.Row(explain, "ExplainButtons");
        explainKit.Button(buttons, "Keep waiting (+30 s)", () => ExtendFailsafe(30), width: 260f);
        explainKit.Button(buttons, "Give up", () =>
        {
            Action? giveUp = _onGiveUp;
            _log("[ui] readiness gate abandoned by the player");
            Clear();
            giveUp?.Invoke();
        }, width: 160f);
        _explainGo.SetActive(false);
    }

    private void ShowExplain(GateFailure failure)
    {
        if (_explainGo == null) return;
        if (_stageText != null) _stageText.text = $"stalled at: {failure.StalledStage}";
        _explainGo.SetActive(true);
    }

    private void RefreshStageText()
    {
        if (_stageText == null || _stages.Count == 0) return;
        _stageText.text = $"{_stages[Math.Min(_stage, _stages.Count - 1)]}  ({_stage + 1}/{_stages.Count})";
    }
}
