using System;
using LocoMP.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The LocoMP root: the tab bar — Direct Join · Friends · Host · Settings — with a live session
/// status line bound to the view-model. Direct Join and Host carry their real M5.1 forms
/// (<see cref="DirectJoinTab"/>/<see cref="HostTab"/>); Settings is M5.3; Friends went live with
/// the M5.5 Steam slice (<see cref="FriendsTab"/>) — it only stays a placeholder on a Steam-less
/// launch, where there is genuinely nothing to list.
/// </summary>
public sealed class RootScreen : IScreen
{
    private static readonly string[] TabNames = { "Direct Join", "Friends", "Host", "Settings" };

    private readonly SessionViewModel _vm;
    private readonly Action _closeRequested;
    private readonly Action? _openHostMenu;
    private readonly DirectJoinTab _joinTab;
    private readonly FriendsTab _friendsTab;
    private readonly HostTab _hostTab;
    private readonly SettingsTab _settingsTab;
    private readonly GameObject?[] _bodies = new GameObject?[TabNames.Length];
    private TMP_Text? _status;
    private Button? _hostMenuButton;
    private int _tab;

    public RootScreen(SessionViewModel vm, UiPrefs prefs, Action<string> log, Action closeRequested,
                      Action? openHostMenu = null)
    {
        _vm = vm;
        _closeRequested = closeRequested;
        _openHostMenu = openHostMenu;
        _joinTab = new DirectJoinTab(vm, prefs, log);
        _friendsTab = new FriendsTab(vm, prefs, log);
        _hostTab = new HostTab(vm, prefs, log);
        _settingsTab = new SettingsTab(prefs, log);
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
        // M5.2: the host's utility overlay — greyed until a session is being hosted.
        if (_openHostMenu != null)
            _hostMenuButton = kit.Button(header, "Host Menu", _openHostMenu, enabled: _vm.IsHost, width: 140f);
        kit.Button(header, "Close", _closeRequested, width: 120f);

        _status = kit.Label(panel, "", dim: true);

        RectTransform tabs = kit.Row(panel, "Tabs");
        bool steam = SteamPresence.Available;
        for (int i = 0; i < TabNames.Length; i++)
        {
            int index = i;
            bool enabled = index != 1 || steam; // Friends needs Steam under the game (M5.5)
            kit.Button(tabs, TabNames[i], () => SwitchTab(index), enabled, width: 180f);
        }

        // Tab bodies: all four are real forms now (M5.5) — Friends only degrades to a placeholder
        // on a Steam-less launch, where there is genuinely no list to show.
        RectTransform bodyHost = kit.Panel(panel, name: "Body");
        bodyHost.GetComponent<Image>().color = kit.Theme.PanelLight;
        var bodyElement = bodyHost.gameObject.AddComponent<LayoutElement>();
        bodyElement.flexibleHeight = 1f;
        string?[] placeholders =
        {
            null,
            steam ? null : "Friends needs Steam — launch Derail Valley through Steam.",
            null,
            null,
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
            if (placeholders[i] is { } placeholder) kit.Label(bodyRect, placeholder, dim: true);
            _bodies[i] = bodyGo;
            bodyGo.SetActive(false);
        }
        _joinTab.Build((RectTransform)_bodies[0]!.transform, kit);
        if (steam) _friendsTab.Build((RectTransform)_bodies[1]!.transform, kit);
        _hostTab.Build((RectTransform)_bodies[2]!.transform, kit);
        _settingsTab.Build((RectTransform)_bodies[3]!.transform, kit);
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
        // The friends scan runs on tab ENTRY (native Steam calls per friend — never per frame).
        if (index == 1 && SteamPresence.Available) _friendsTab.OnTabShown();
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
        if (_hostMenuButton != null) _hostMenuButton.interactable = _vm.IsHost;
        _joinTab.Refresh();
        if (SteamPresence.Available) _friendsTab.Refresh(); // button gates only — no Steam scan
        _hostTab.Refresh();
        _settingsTab.Refresh();
    }
}
