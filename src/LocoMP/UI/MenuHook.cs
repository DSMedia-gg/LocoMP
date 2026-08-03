using System;
using System.Linq;
using DV.UI;
using DV.UIFramework;
using LocoMP.Shim;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocoMP.UI;

/// <summary>Where an open request came from — decides which canvas mode the UI opens in.</summary>
public enum MenuHookOrigin
{
    MainMenu,
    PauseMenu,
}

/// <summary>
/// The single DV.UI coupling point (M5.0 step 4). Clean-room pattern per the recon (R-UI-1/2):
/// at scene load, CLONE one of DV's own serialized menu buttons — `MainMenuController.sessionsButton`
/// in the main menu, `PauseMenuController.manualButton` in the pause menu — relabel it MULTIPLAYER
/// and rewire its <c>ButtonDV.Clicked</c>. The clone inherits DV's look, font, animator and VR
/// interactivity; we ship none of their assets and compile against nothing but the two controller
/// types. The DV TMP font is harvested here for the theme (R-UI-5).
///
/// Failure mode is "no button, one log line" — every injection is individually try/caught and the
/// whole hook sits behind the supported-build gate, so a B100 layout change degrades instead of
/// crashing DV's menu build (10-M5-UIUX-PLAN §8 risk row).
///
/// Injections run two frames AFTER sceneLoaded (via <see cref="Tick"/> from the UMM update pump):
/// the controllers wire their providers in Awake/Start, and a settled menu is what we clone.
/// </summary>
public static class MenuHook
{
    private const string CloneName = "ButtonMultiplayer (LocoMP)";

    public static event Action<MenuHookOrigin>? OpenRequested;

    /// <summary>DV's menu TMP font, harvested from the first successful clone (theme input).</summary>
    public static TMP_FontAsset? HarvestedFont { get; private set; }

    /// <summary>The canvas the main-menu button lives in — the host for child-mode LocoMP UI.</summary>
    public static Canvas? MainMenuCanvas { get; private set; }

    private static Action<string>? _log;
    private static bool _installed;
    private static int _framesUntilInject = -1;

    public static void Install(Action<string> log)
    {
        if (_installed) return;
        if (!PresenceShim.IsSupportedBuild) return; // unknown build ⇒ no button, never a crash
        _installed = true;
        _log = log;
        SceneManager.sceneLoaded += (_, _) => _framesUntilInject = 2;
        _framesUntilInject = 2; // the mod can load with the main menu already up
    }

    /// <summary>Pumped from UMM OnUpdate (composition root Tick).</summary>
    public static void Tick()
    {
        if (_framesUntilInject < 0) return;
        if (_framesUntilInject-- > 0) return;
        try { InjectMainMenu(); }
        catch (Exception e) { _log?.Invoke("[ui] main-menu hook failed (degrading to no button): " + e.Message); }
        try { InjectPauseMenu(); }
        catch (Exception e) { _log?.Invoke("[ui] pause-menu hook failed (degrading to no button): " + e.Message); }
    }

    private static void InjectMainMenu()
    {
        MainMenu? menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
        if (menu == null || menu.controller == null) return;
        ButtonDV? template = menu.controller.sessionsButton;
        if (template == null || AlreadyInjected(template.transform.parent)) return;

        MainMenuCanvas = template.GetComponentInParent<Canvas>();
        Clone(template, after: menu.controller.settingsButton != null
            ? menu.controller.settingsButton.transform
            : template.transform,
            origin: MenuHookOrigin.MainMenu);
        _log?.Invoke("[ui] MULTIPLAYER button added to the main menu");
    }

    private static void InjectPauseMenu()
    {
        // The pause menu is PRELOADED inactive by DV's canvas controller, so FindObjectOfType
        // misses it — sweep loaded-scene instances instead (asset/prefab copies have no scene).
        PauseMenuController? controller = Resources.FindObjectsOfTypeAll<PauseMenuController>()
            .FirstOrDefault(c => c.gameObject.scene.IsValid());
        if (controller == null) return;
        ButtonDV? template = controller.manualButton;
        if (template == null || AlreadyInjected(template.transform.parent)) return;

        PauseMenuController captured = controller;
        Clone(template, after: template.transform, origin: MenuHookOrigin.PauseMenu,
            beforeOpen: () => captured.RequestClose()); // close DV's pause first — our overlay never pauses
        _log?.Invoke("[ui] LocoMP button added to the pause menu");
    }

    private static bool AlreadyInjected(Transform parent) =>
        parent != null && parent.Find(CloneName) != null;

    private static void Clone(ButtonDV template, Transform after, MenuHookOrigin origin, Action? beforeOpen = null)
    {
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
        clone.name = CloneName;
        clone.transform.SetSiblingIndex(after.GetSiblingIndex() + 1);

        // Strip behaviour that must not ride along: UIMenuRequester would re-target DV's own
        // submenu switcher, and any localization binder would overwrite our label on locale
        // refresh (matched by name so we need no DV.Localization reference).
        foreach (Component component in clone.GetComponentsInChildren<Component>(includeInactive: true))
        {
            if (component is UIMenuRequester || component.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                UnityEngine.Object.Destroy(component);
        }

        TextMeshProUGUI? label = clone.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
        if (label != null)
        {
            label.text = "MULTIPLAYER";
            if (HarvestedFont == null) HarvestedFont = label.font;
        }

        var button = clone.GetComponent<ButtonDV>();
        button.Clicked += _ =>
        {
            beforeOpen?.Invoke();
            OpenRequested?.Invoke(origin);
        };
        clone.SetActive(true);
    }
}
