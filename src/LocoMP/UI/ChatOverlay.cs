using System;
using System.Collections.Generic;
using System.Text;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The in-game chat overlay (M5.4): a bottom-left feed of recent lines that fades to nothing when
/// the conversation goes quiet, and an Enter-to-talk input row. Two rules inherited from the M5.0
/// doctrine: it NEVER pauses or covers the sim (chat is ambient, not modal), and typing must not
/// fire game hotkeys — the input field rides <see cref="WidgetKit"/>'s keyboard-focus discipline
/// (DV focus refcount + Rewired kb+mouse detach on select, released on deselect/close, with
/// <see cref="LocoMpUi"/> holding the scene-death failsafe).
///
/// <para>Renders only the server's committed echoes (<see cref="SessionViewModel.ChatReceived"/>) —
/// never a local draft — so what the player sees is exactly what the session saw. Lives outside
/// the screen router: it exists while in a session whether or not the LocoMP screens are open.</para>
/// </summary>
public sealed class ChatOverlay
{
    private const int PassiveMaxLines = 8;
    private const int OpenMaxLines = 14;
    private const float PanelWidth = 620f;
    private const float FeedHeight = 264f;
    private const float InputHeight = 40f;

    private readonly SessionViewModel _vm;
    private readonly LocoMpTheme _theme;
    private readonly UiPrefs _prefs;
    private readonly Action<string> _log;
    private readonly List<(ChatEntry entry, float at)> _lines = new();

    private GameObject? _go;
    private Image? _background;
    private TMP_Text? _feed;
    private GameObject? _inputRow;
    private TMP_InputField? _input;
    private string _accentHex = "5FA8E0";
    private string _dimHex = "8C99A6";
    private bool _wasInSession;
    private float _nextRepaint;
    private int _openedFrame = -1;

    public ChatOverlay(SessionViewModel vm, LocoMpTheme theme, UiPrefs prefs, Action<string> log)
    {
        _vm = vm;
        _theme = theme;
        _prefs = prefs;
        _log = log;
        _vm.ChatReceived += OnChat;
        // M5.3 live-apply: chat opts and the UI scale land on the next rebuild — tear the canvas
        // down (input first: a destroyed field never fires onDeselect) and let use rebuild it.
        _prefs.Changed += OnPrefsChanged;
    }

    private void OnPrefsChanged()
    {
        CloseInput();
        if (_go != null) UnityEngine.Object.Destroy(_go);
        _go = null;
        _feed = null;
        _input = null;
        _inputRow = null;
        _background = null;
    }

    /// <summary>True while the input row owns the keyboard — <see cref="LocoMpUi"/> routes ESC to
    /// <see cref="CloseInput"/> first when it is.</summary>
    public bool InputOpen { get; private set; }

    /// <summary>Pumped every frame from <see cref="LocoMpUi.Tick"/>. <paramref name="allowOpen"/>
    /// gates the Enter-to-talk key: false while the LocoMP screens or the readiness gate own
    /// interaction (their fields/covers must not race chat for the keyboard).</summary>
    public void Tick(bool allowOpen)
    {
        if (!_prefs.ChatEnabled)
        {
            // Master switch (M5.3 chat opts): the feed goes fully dark; the backlog still
            // accumulates in the client mirror, so re-enabling shows the conversation so far.
            if (InputOpen) CloseInput();
            if (_go != null) _go.SetActive(false);
            return;
        }
        if (!_vm.InSession)
        {
            if (_wasInSession)
            {
                // Session over: the backlog died with the client mirror; the overlay follows.
                _lines.Clear();
                CloseInput();
                if (_go != null) _go.SetActive(false);
            }
            _wasInSession = false;
            return;
        }
        _wasInSession = true;

        if (InputOpen && _go == null)
        {
            // Scene change destroyed the canvas mid-type — the field never fired onDeselect, so
            // release the keyboard here or the player is locked out of the game (the kit hazard).
            InputOpen = false;
            WidgetKit.ReleaseKeyboardFocus();
        }

        if (!InputOpen && allowOpen &&
            (Input.GetKeyDown(_prefs.ChatKey) ||
             (_prefs.ChatKey == KeyCode.Return && Input.GetKeyDown(KeyCode.KeypadEnter))))
            OpenInput();

        // Passive fade: cheap 1 Hz repaint retires expired lines; event repaints handle arrivals.
        if (!InputOpen && Time.realtimeSinceStartup >= _nextRepaint) Repaint();
    }

    private void OnChat(ChatEntry entry)
    {
        _lines.Add((entry, Time.realtimeSinceStartup));
        if (_lines.Count > NetClient.ChatLogCapacity) _lines.RemoveAt(0);
        if (_vm.InSession && _prefs.ChatEnabled) Repaint();
    }

    private void OpenInput()
    {
        if (_go == null) Build();
        InputOpen = true;
        _openedFrame = Time.frameCount;
        _inputRow!.SetActive(true);
        Repaint();
        // Programmatic focus: no cursor needed to start typing (the sim keeps running and the
        // mouse stays on the game). Selecting fires the kit's TakeKeyboardFocus.
        EventSystem.current?.SetSelectedGameObject(_input!.gameObject);
        _input!.ActivateInputField();
    }

    /// <summary>Close the input row without sending (ESC, session end, scene death).</summary>
    public void CloseInput()
    {
        if (!InputOpen) return;
        InputOpen = false;
        if (_input != null)
        {
            _input.text = string.Empty;
            _input.DeactivateInputField();
            if (EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject == _input.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
        }
        if (_inputRow != null) _inputRow.SetActive(false);
        WidgetKit.ReleaseKeyboardFocus(); // failsafe — a deactivate can skip onDeselect
        if (_go != null) Repaint();
    }

    private void Submit(string text)
    {
        // The same Enter that opened the field can reach TMP's submit path a frame later with an
        // empty draft — an open-then-instantly-close would make the key feel broken, so ignore it.
        if (Time.frameCount == _openedFrame) return;
        if (!string.IsNullOrWhiteSpace(text)) _vm.SendChat(text);
        CloseInput();
    }

    /// <summary>Full teardown (mod toggle-off). The overlay unsubscribes — it may outlive one
    /// canvas (scene loads) but never the view-model.</summary>
    public void Destroy()
    {
        _vm.ChatReceived -= OnChat;
        _prefs.Changed -= OnPrefsChanged;
        CloseInput();
        if (_go != null) UnityEngine.Object.Destroy(_go);
        _go = null;
        _feed = null;
        _input = null;
        _inputRow = null;
        _background = null;
    }

    private void Repaint()
    {
        if (_go == null) Build();
        _go!.SetActive(true);
        float now = Time.realtimeSinceStartup;
        _nextRepaint = now + 1f;

        var sb = new StringBuilder();
        int shown = 0;
        bool ramping = false;      // R4-L: something is mid-fade — repaint fast until it settles
        float maxAlpha = 0f;       // the most-visible line drives the background's alpha
        int max = InputOpen ? OpenMaxLines : PassiveMaxLines;
        int start = Math.Max(0, _lines.Count - max);
        for (int i = start; i < _lines.Count; i++)
        {
            (ChatEntry entry, float at) = _lines[i];
            float age = now - at;
            if (!InputOpen && age >= _prefs.ChatFadeSeconds) continue;
            // R4-L: the old fade was a hard blink-out at expiry. Each line now ramps its alpha to
            // zero over the final stretch instead (TMP alpha tag — our own markup; user text is
            // already escaped). While the input is open everything reads solid.
            float alpha = 1f;
            if (!InputOpen && age > _prefs.ChatFadeSeconds - FadeRampSeconds)
            {
                alpha = Mathf.Clamp01((_prefs.ChatFadeSeconds - age) / FadeRampSeconds);
                ramping = true;
            }
            if (alpha > maxAlpha) maxAlpha = alpha;
            if (shown++ > 0) sb.Append('\n');
            sb.Append("<alpha=#").Append(((int)(alpha * 255f)).ToString("X2")).Append('>');
            AppendLine(sb, entry);
        }

        if (ramping) _nextRepaint = now + 0.05f; // smooth ramp; quiet feeds stay at the 1 Hz tick

        _feed!.text = sb.ToString();
        // Quiet and closed = fully invisible: no background rectangle squatting on the HUD. The
        // background follows the brightest line down so the panel doesn't outlive its last line.
        _background!.enabled = InputOpen || shown > 0;
        if (_background.enabled)
            _background.color = InputOpen ? _theme.Panel : new Color(0f, 0f, 0f, 0.45f * maxAlpha);
    }

    /// <summary>Seconds over which an expiring line ramps to invisible (R4-L; expiry itself stays
    /// <see cref="UiPrefs.ChatFadeSeconds"/>).</summary>
    private const float FadeRampSeconds = 1.5f;

    private void AppendLine(StringBuilder sb, ChatEntry e)
    {
        switch (e.Kind)
        {
            case ChatMessageKind.Player:
                sb.Append("<color=#").Append(_accentHex).Append('>').Append(Escape(e.SenderName))
                  .Append(":</color> ").Append(Escape(e.Text));
                break;
            case ChatMessageKind.Server:
                sb.Append("<color=#").Append(_accentHex).Append(">[server]</color> ").Append(Escape(e.Text));
                break;
            default:
                sb.Append("<color=#").Append(_dimHex).Append(">* ").Append(Escape(e.SenderName))
                  .Append(' ').Append(SystemVerb(e.Kind)).Append("</color>");
                break;
        }
    }

    private static string SystemVerb(ChatMessageKind kind) => kind switch
    {
        ChatMessageKind.Joined => "joined",
        ChatMessageKind.Left => "left",
        ChatMessageKind.Kicked => "was kicked",
        ChatMessageKind.Banned => "was banned",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Defuse TMP rich-text in player-authored content (names and messages): a zero-width
    /// space after '&lt;' stops tag parsing without visibly altering the text — a player typing
    /// &lt;color&gt; must not restyle the feed.</summary>
    private static string Escape(string s) =>
        s.IndexOf('<') < 0 ? s : s.Replace("<", "<​");

    private void Build()
    {
        _theme.Font ??= MenuHook.HarvestedFont;
        _accentHex = ColorUtility.ToHtmlStringRGB(_theme.Accent);
        _dimHex = ColorUtility.ToHtmlStringRGB(_theme.TextDim);

        _go = new GameObject("LocoMP ChatOverlay", typeof(RectTransform));
        var canvas = _go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = LocoMpCanvas.OverlaySortingOrder - 2; // under the screens and the HUD
        var scaler = _go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        LocoMpCanvas.ApplyScale(scaler);
        _go.AddComponent<GraphicRaycaster>(); // the input field needs pointer raycasts to be clickable

        var containerGo = new GameObject("Chat", typeof(RectTransform));
        var container = (RectTransform)containerGo.transform;
        container.SetParent(_go.transform, worldPositionStays: false);
        container.anchorMin = Vector2.zero;
        container.anchorMax = Vector2.zero;
        container.pivot = Vector2.zero;
        container.anchoredPosition = new Vector2(24f, 96f); // clear of DV's bottom-left HUD corner
        container.sizeDelta = new Vector2(PanelWidth, FeedHeight + InputHeight + 8f);

        var backgroundGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
        var backgroundRect = (RectTransform)backgroundGo.transform;
        backgroundRect.SetParent(container, worldPositionStays: false);
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;
        _background = backgroundGo.GetComponent<Image>();
        _background.raycastTarget = false; // the feed never eats clicks aimed at the world

        var feedGo = new GameObject("Feed", typeof(RectTransform));
        var feedRect = (RectTransform)feedGo.transform;
        feedRect.SetParent(container, worldPositionStays: false);
        feedRect.anchorMin = Vector2.zero;
        feedRect.anchorMax = Vector2.one;
        feedRect.offsetMin = new Vector2(12f, InputHeight + 8f);
        feedRect.offsetMax = new Vector2(-12f, -8f);
        _feed = feedGo.AddComponent<TextMeshProUGUI>();
        if (_theme.Font != null) _feed.font = _theme.Font;
        _feed.fontSize = 19;
        _feed.color = _theme.Text;
        _feed.alignment = TextAlignmentOptions.BottomLeft;
        _feed.richText = true;
        _feed.raycastTarget = false;

        var rowGo = new GameObject("Input Row", typeof(RectTransform));
        var rowRect = (RectTransform)rowGo.transform;
        rowRect.SetParent(container, worldPositionStays: false);
        rowRect.anchorMin = Vector2.zero;
        rowRect.anchorMax = new Vector2(1f, 0f);
        rowRect.pivot = new Vector2(0.5f, 0f);
        rowRect.anchoredPosition = Vector2.zero;
        rowRect.sizeDelta = new Vector2(0f, InputHeight);
        _inputRow = rowGo;

        var kit = new WidgetKit(_theme);
        _input = kit.Field(rowRect, "press Enter to chat");
        var inputRect = (RectTransform)_input.transform;
        inputRect.anchorMin = Vector2.zero;   // manual rect — no layout group out here
        inputRect.anchorMax = Vector2.one;
        inputRect.offsetMin = Vector2.zero;
        inputRect.offsetMax = Vector2.zero;
        _input.characterLimit = ChatPolicy.MaxLength;
        _input.onSubmit.AddListener(Submit);
        // Clicking away while typing deselects the field: the keyboard is released by the kit's
        // onDeselect, so the row must close with it or an unfocused field looks live.
        _input.onDeselect.AddListener(_ => { if (InputOpen && Time.frameCount != _openedFrame) CloseInput(); });

        _inputRow.SetActive(false);
        _log("[ui] chat overlay built");
    }
}
