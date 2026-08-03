using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The LocoMP root (M5.0 stub): the tab bar — Direct Join · Friends · Host · Settings — with
/// placeholder tab bodies and a live session status line bound to the view-model. Real tab
/// content is M5.1+ (join/host forms), M5.3 (settings); Friends ships visible-but-disabled until
/// the M5.5 Steam slice so the information architecture is complete from the first drop
/// (10-M5-UIUX-PLAN §6). M5.0's exit only needs this to open, switch tabs, and close.
/// </summary>
public sealed class RootScreen : IScreen
{
    private static readonly string[] TabNames = { "Direct Join", "Friends", "Host", "Settings" };

    private readonly SessionViewModel _vm;
    private readonly Action _closeRequested;
    private readonly GameObject?[] _bodies = new GameObject?[TabNames.Length];
    private TMP_Text? _status;
    private int _tab;

    public RootScreen(SessionViewModel vm, Action closeRequested)
    {
        _vm = vm;
        _closeRequested = closeRequested;
    }

    public GameObject? Go { get; private set; }

    public void Build(RectTransform parent, WidgetKit kit)
    {
        RectTransform panel = kit.Panel(parent, vertical: true, name: "LocoMP Root");
        Go = panel.gameObject;
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(860f, 560f);

        RectTransform header = kit.Row(panel, "Header");
        TMP_Text title = kit.Label(header, "LocoMP — Multiplayer", size: kit.Theme.TitleSize);
        var titleElement = title.gameObject.AddComponent<LayoutElement>();
        titleElement.flexibleWidth = 1f;
        kit.Button(header, "Close", _closeRequested, width: 120f);

        _status = kit.Label(panel, "", dim: true);

        RectTransform tabs = kit.Row(panel, "Tabs");
        for (int i = 0; i < TabNames.Length; i++)
        {
            int index = i;
            bool enabled = index != 1; // Friends waits for the M5.5 Steam slice
            kit.Button(tabs, TabNames[i], () => SwitchTab(index), enabled, width: 180f);
        }

        // Placeholder bodies — one per tab so M5.1+ can drop real content in per slice.
        RectTransform bodyHost = kit.Panel(panel, name: "Body");
        bodyHost.GetComponent<Image>().color = kit.Theme.PanelLight;
        var bodyElement = bodyHost.gameObject.AddComponent<LayoutElement>();
        bodyElement.flexibleHeight = 1f;
        string[] placeholders =
        {
            "Direct join arrives with M5.1 — use the dev panel (Ctrl+F10) meanwhile.",
            "Friends goes live once LocoMP connects via Steam (M5.5).",
            "Hosting arrives with M5.1 — use the dev panel (Ctrl+F10) meanwhile.",
            "Settings arrive with M5.3.",
        };
        for (int i = 0; i < TabNames.Length; i++)
        {
            var bodyGo = new GameObject("Tab " + TabNames[i], typeof(RectTransform));
            var bodyRect = (RectTransform)bodyGo.transform;
            bodyRect.SetParent(bodyHost, worldPositionStays: false);
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = new Vector2(16f, 12f);
            bodyRect.offsetMax = new Vector2(-16f, -12f);
            kit.Label(bodyRect, placeholders[i], dim: true);
            _bodies[i] = bodyGo;
            bodyGo.SetActive(false);
        }
        SwitchTab(0);
    }

    public void OnShow()
    {
        _vm.Changed += Refresh;
        Refresh();
    }

    public void OnHide() => _vm.Changed -= Refresh;

    private void SwitchTab(int index)
    {
        _tab = index;
        for (int i = 0; i < _bodies.Length; i++)
            if (_bodies[i] is { } body) body.SetActive(i == _tab);
    }

    private void Refresh()
    {
        if (_status == null) return;
        string line = _vm.Phase switch
        {
            SessionPhase.Idle => "Not in a session.",
            SessionPhase.Connecting => "Connecting…",
            SessionPhase.Hosting => $"Hosting — {_vm.ServerPlayerCount} player(s).",
            SessionPhase.Joined => $"In a session — {_vm.Players.Length} player(s).",
            SessionPhase.SessionLost => "SESSION LOST — leave to restore your world, then reload your save.",
            _ => "",
        };
        if (_vm.Error.Length > 0 && _vm.Phase is SessionPhase.Idle or SessionPhase.SessionLost)
            line += $"   ⚠ {_vm.Error}";
        _status.text = line;
    }
}
