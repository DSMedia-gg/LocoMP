using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// Owns the LocoMP UI's root RectTransform (M5.0 step 3), in one of two modes:
///
/// <para><b>Child-of-DV-canvas</b> (<see cref="CreateUnder"/>, main menu): a full-rect child of
/// DV's own menu canvas. This is the recon R-UI-3 result — DV's <c>MainMenuVREnabler</c> converts
/// that canvas to world space and swaps in the VRTK event system, so parenting inside it inherits
/// the working flat AND VR interaction for free, with zero VRTK code on our side.</para>
///
/// <para><b>Own overlay</b> (<see cref="CreateOverlay"/>, in-game): a ScreenSpaceOverlay canvas of
/// our own, drawn above DV's HUD. The world keeps ticking behind it (the overlay never routes
/// through DV's pause — 02 non-goal); the cursor comes from <c>CursorManager</c> requests at the
/// composition root. In-game VR interaction is the M5.6 slice (world-space + VRTK canvas
/// component, added by reflection when that lands).</para>
///
/// Never enables EventSystem navigation events — DV's controllers error-log if they see them on.
/// </summary>
public sealed class LocoMpCanvas
{
    public const int OverlaySortingOrder = 5000;

    /// <summary>The M5.3 UI-scale preference (1 = DV-native size), applied wherever a LocoMP
    /// scaler is built. Set from <see cref="UiPrefs.UiScale"/> at load and on settings change; a
    /// LIVE surface picks it up on its next rebuild (screens rebuild per open, the chat overlay
    /// rebuilds itself on the change event) — "live-apply where safe".</summary>
    public static float UiScale = 1f;

    /// <summary>Scale a LocoMP CanvasScaler: a larger scale SHRINKS the reference resolution, so
    /// everything laid out in it renders bigger. ScaleWithScreenSize keeps the result
    /// resolution-independent either way.</summary>
    public static void ApplyScale(CanvasScaler scaler)
    {
        float s = Mathf.Clamp(UiScale, UiPrefs.MinUiScale, UiPrefs.MaxUiScale);
        scaler.referenceResolution = new Vector2(1920f / s, 1080f / s);
    }

    private readonly GameObject _rootGo;

    /// <summary>Non-null only in overlay mode.</summary>
    public Canvas? OwnCanvas { get; }

    public RectTransform Root { get; }

    /// <summary>True until the underlying objects are destroyed (scene unload kills the
    /// child-mode root with its DV parent — poll this from the composition root's Tick).</summary>
    public bool Alive => _rootGo != null;

    private LocoMpCanvas(GameObject rootGo, Canvas? ownCanvas)
    {
        _rootGo = rootGo;
        OwnCanvas = ownCanvas;
        Root = (RectTransform)rootGo.transform;
    }

    /// <summary>Full-rect child of a live DV canvas (main-menu mode).</summary>
    public static LocoMpCanvas CreateUnder(Canvas host)
    {
        var go = new GameObject("LocoMP UI", typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(host.transform, worldPositionStays: false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.SetAsLastSibling(); // draw over the DV menu it covers
        EnsureEventSystem();
        return new LocoMpCanvas(go, null);
    }

    /// <summary>Standalone overlay canvas (in-game mode).</summary>
    public static LocoMpCanvas CreateOverlay()
    {
        var go = new GameObject("LocoMP UI (overlay)", typeof(RectTransform));
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = OverlaySortingOrder;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        ApplyScale(scaler);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        EnsureEventSystem();
        return new LocoMpCanvas(go, canvas);
    }

    public void SetVisible(bool visible)
    {
        if (_rootGo != null) _rootGo.SetActive(visible);
    }

    public void Destroy()
    {
        if (_rootGo != null) Object.Destroy(_rootGo);
    }

    /// <summary>Reuse the scene's EventSystem if one exists (both DV scenes have one); create a
    /// bare fallback otherwise. Navigation events stay off either way (DV's own invariant).</summary>
    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        if (Object.FindObjectOfType<EventSystem>() != null) return;
        var go = new GameObject("LocoMP EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        go.GetComponent<EventSystem>().sendNavigationEvents = false;
    }
}
