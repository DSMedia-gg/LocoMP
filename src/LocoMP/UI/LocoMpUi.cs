using System;
using DV.UI;
using DV.Utils;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The UI composition root (M5.0 step 8's object half — Main owns one of these). Ties the seam
/// together: MenuHook raises OpenRequested → this builds the canvas (child-of-DV-canvas in the
/// main menu, own overlay in-game — recon R-UI-3), the router, the widget kit (theme font from
/// the hook's harvest) and pushes the RootScreen. The screens are rebuilt fresh on every open —
/// scene loads destroy the previous canvas (main-menu mode dies with its DV parent), so nothing
/// here survives a scene change by design; <see cref="Tick"/> notices a dead canvas and forgets it.
///
/// The in-game overlay never routes through DV's pause (02 non-goal): the cursor comes from
/// CursorManager's refcounted requests, the sim keeps ticking, ESC closes the overlay only.
/// </summary>
public sealed class LocoMpUi
{
    private readonly SessionViewModel _vm;
    private readonly Action<string> _log;
    private readonly LocoMpTheme _theme = new();

    private LocoMpCanvas? _canvas;
    private ScreenRouter? _router;
    private bool _cursorRequested;

    public LocoMpUi(SessionViewModel vm, Action<string> log)
    {
        _vm = vm;
        _log = log;
        Gate = new ReadinessGate(_theme, log);
        Hud = new StatusHud(_theme);
    }

    /// <summary>The readiness-gate primitive (plan §5) — later slices Begin() it around their
    /// coherence boundaries; M5.0 exercises it via the dev-panel demo buttons.</summary>
    public ReadinessGate Gate { get; }

    /// <summary>The non-blocking status tier (stub until M5.1/M5.4 wire real net states).</summary>
    public StatusHud Hud { get; }

    public bool IsOpen => _canvas is { Alive: true };

    /// <summary>Pumped from UMM OnUpdate every frame (mod enabled or not mid-session teardowns
    /// still need the gate ticking).</summary>
    public void Tick(double dt)
    {
        MenuHook.Tick();
        Gate.Tick(dt);
        if (_canvas is { Alive: false })
        {
            // Scene change took the canvas (and every screen) with it — drop the wreckage.
            _router = null;
            _canvas = null;
            ReleaseCursor();
        }
        if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
    }

    public void Open(MenuHookOrigin origin)
    {
        Close();
        try
        {
            _canvas = origin == MenuHookOrigin.MainMenu && MenuHook.MainMenuCanvas != null
                ? LocoMpCanvas.CreateUnder(MenuHook.MainMenuCanvas)
                : LocoMpCanvas.CreateOverlay();
            _theme.Font ??= MenuHook.HarvestedFont;
            var kit = new WidgetKit(_theme);
            _router = new ScreenRouter(_canvas.Root, kit);
            _router.Push(new RootScreen(_vm, Close));
            RequestCursor();
            _log($"[ui] LocoMP screens opened ({origin})");
        }
        catch (Exception e)
        {
            _log("[ui] failed to open the LocoMP screens: " + e);
            Close();
        }
    }

    public void Close()
    {
        _router?.Clear();
        _router = null;
        _canvas?.Destroy();
        _canvas = null;
        ReleaseCursor();
    }

    /// <summary>Full teardown (mod toggle-off).</summary>
    public void Dispose()
    {
        Close();
        Gate.Clear();
        Hud.Destroy();
    }

    private void RequestCursor()
    {
        // Refcounted DV cursor request (recon R-UI-4). Auto-creating singleton — try/catch, not
        // a null probe (the M4 lesson: probing Instance can construct one on a dead world).
        try
        {
            SingletonBehaviour<CursorManager>.Instance.RequestCursor(this, visible: true);
            _cursorRequested = true;
        }
        catch (Exception)
        {
            _cursorRequested = false;
        }
    }

    private void ReleaseCursor()
    {
        if (!_cursorRequested) return;
        _cursorRequested = false;
        try { SingletonBehaviour<CursorManager>.Instance.RemoveRequest(this); }
        catch (Exception) { /* world/scene torn down — the manager died with it */ }
    }
}
