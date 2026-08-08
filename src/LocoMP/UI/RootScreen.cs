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
///
/// <para>D25 affordances: the title reads "Multiplayer" DV-style with the LocoMP version as a dim
/// tag (tester screenshots identify the build); <b>Leave session</b> lives in the header whenever a
/// session is live and we are not the host — including SESSION LOST, whose banner tells the player
/// to leave (pre-D25 the uGUI had NO leave path at all); the Host Menu button exists only for the
/// host (was: permanently greyed for clients).</para>
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
    private readonly Button?[] _tabButtons = new Button?[TabNames.Length];
    private WidgetKit? _kit;
    private RectTransform? _statusRow;
    private Button? _leaveButton;
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
        _kit = kit;
        RectTransform panel = kit.Panel(parent, vertical: true, name: "LocoMP Root");
        Go = panel.gameObject;
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(860f, 560f);

        RectTransform header = kit.Row(panel, "Header");
        kit.Label(header, "Multiplayer", size: kit.Theme.TitleSize);
        TMP_Text tag = kit.Label(header, "LocoMP " + ModVersion(), dim: true, size: kit.Theme.MetaSize);
        tag.color = kit.Theme.TextDisabled;
        tag.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        // D25: leaving is a header affordance for clients (and the SESSION LOST state), never a
        // hunt through tabs. The host's exit stays Save & Stop in the Host Menu — deliberate.
        _leaveButton = kit.Button(header, "Leave session", () => _vm.Leave(), ButtonTier.Danger, width: 180f);
        if (_openHostMenu != null)
            _hostMenuButton = kit.Button(header, "Host Menu", _openHostMenu, width: 140f);
        kit.Button(header, "Close", _closeRequested, width: 120f);

        _statusRow = kit.Row(panel, "Status");

        RectTransform tabs = kit.Row(panel, "Tabs");
        bool steam = SteamPresence.Available;
        for (int i = 0; i < TabNames.Length; i++)
        {
            int index = i;
            bool enabled = index != 1 || steam; // Friends needs Steam under the game (M5.5)
            _tabButtons[i] = kit.TabButton(tabs, TabNames[i], () => SwitchTab(index), enabled, width: 180f);
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
            steam ? null : "Launch Derail Valley through Steam to use Friends.",
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
        {
            if (_bodies[i] is { } body) body.SetActive(i == _tab);
            // Skip disabled tabs (steam-less Friends) — SetTabActive would repaint their label
            // out of the disabled colour.
            if (_kit != null && _tabButtons[i] is { } button && button.interactable)
                _kit.SetTabActive(button, i == _tab);
        }
        // The friends scan runs on tab ENTRY (native Steam calls per friend — never per frame).
        if (index == 1 && SteamPresence.Available) _friendsTab.OnTabShown();
    }

    private void Refresh()
    {
        if (_kit is not { } kit || _statusRow == null) return;
        RebuildStatus(kit);
        if (_leaveButton != null)
            _leaveButton.gameObject.SetActive(_vm.InSession && !_vm.IsHost);
        if (_hostMenuButton != null)
            _hostMenuButton.gameObject.SetActive(_vm.IsHost);
        _joinTab.Refresh();
        if (SteamPresence.Available) _friendsTab.Refresh(); // button gates only — no Steam scan
        _hostTab.Refresh();
        _settingsTab.Refresh();
    }

    /// <summary>The status line as chip + text (D25 §10.2). Rebuild-on-change is safe here — the
    /// row holds no input fields.</summary>
    private void RebuildStatus(WidgetKit kit)
    {
        RectTransform row = _statusRow!;
        for (int i = row.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(row.GetChild(i).gameObject);

        (string chip, ChipKind kind, string line) = _vm.Phase switch
        {
            SessionPhase.Connecting => ("Connecting", ChipKind.Info, ""),
            SessionPhase.Hosting => ("Hosting", ChipKind.Success, Players(_vm.ServerPlayerCount)),
            SessionPhase.Joined => ("In session", ChipKind.Info, Players(_vm.Players.Length)),
            SessionPhase.SessionLost => ("Session lost", ChipKind.Danger,
                "leave to restore your world, then reload your save"),
            _ => ("Not in session", ChipKind.Neutral, ""),
        };
        kit.Chip(row, chip, kind);
        if (line.Length > 0) kit.Label(row, line, dim: true);
        if (_vm.Error.Length > 0 && _vm.Phase is SessionPhase.Idle or SessionPhase.SessionLost)
        {
            TMP_Text error = kit.Label(row, "⚠ " + _vm.Error, dim: false);
            error.color = kit.Theme.Danger;
        }
    }

    private static string Players(int count) => count == 1 ? "1 player" : count + " players";

    private static string ModVersion()
    {
        try
        {
            Version? version = typeof(RootScreen).Assembly.GetName().Version;
            return version == null ? "dev" : version.ToString(3);
        }
        catch (Exception)
        {
            return "dev";
        }
    }
}
