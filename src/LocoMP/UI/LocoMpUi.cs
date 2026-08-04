using System;
using DV.UI;
using DV.Utils;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Shim;
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
    private static readonly string[] JoinStages =
        { "Connecting to server", "Receiving world", "Receiving career", "Receiving items" };

    private readonly SessionViewModel _vm;
    private readonly Action<string> _log;
    private readonly LocoMpTheme _theme = new();
    private readonly UiPrefs _prefs;

    private LocoMpCanvas? _canvas;
    private ScreenRouter? _router;
    private bool _cursorRequested;
    private bool _pointerEnforced;
    private SessionPhase _lastPhase = SessionPhase.Idle;
    private bool _pendingLeave;

    public LocoMpUi(SessionViewModel vm, Action<string> log)
    {
        _vm = vm;
        _log = log;
        _prefs = UiPrefs.Load(log);
        Gate = new ReadinessGate(_theme, log);
        Hud = new StatusHud(_theme);
        _vm.Changed += OnVmChanged;
        _vm.JoinStageChanged += OnJoinStage;
        _vm.JoinRejected += OnJoinRejected;
        _vm.QueueChanged += OnQueueChanged;
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
        if (_pendingLeave)
        {
            // A reject's Leave is deferred here: the Rejected event fires from inside the client's
            // own receive callback, and tearing the client down mid-poll is not a safe re-entry.
            _pendingLeave = false;
            _vm.Leave();
        }
        if (_canvas is { Alive: false })
        {
            // Scene change took the canvas (and every screen) with it — drop the wreckage.
            _router = null;
            _canvas = null;
            ReleaseCursor();
            WidgetKit.ReleaseKeyboardFocus(); // a field destroyed while selected never fires onDeselect
        }
        if (IsOpen && Input.GetKeyDown(KeyCode.Escape))
        {
            PauseGuardHook.NoteEscConsumed(); // DV's pause poll may run after us this same frame
            Close();
        }
        EnforcePointer();
    }

    /// <summary>True while a LocoMP surface owns interaction — the pause guard's probe.</summary>
    public bool ModalActive => IsOpen || Gate.Active;

    /// <summary>The CursorManager request (held for the overlay/gate lifetime) states our intent,
    /// but it is EDGE-triggered: RequestSystem raises ValueChanged only when the aggregate value
    /// changes, and CursorManager only writes Cursor state from that event — so a direct external
    /// write (UMM re-locks the cursor when its window closes) is never reconciled, and the 8 s
    /// explain screen becomes a softlock (full-screen raycast blocker, no pointer — Round 2
    /// finding). While a modal surface is up we therefore re-assert per frame: DV's own
    /// RequirePointer aggregate (cursor + mouse-look freeze + VR pointer; idempotent dictionary
    /// write, so re-calling is free) plus a divergence-only direct heal for writers that bypass
    /// CursorManager entirely. On modal end DV's computed truth is restored, not blind "off".</summary>
    private void EnforcePointer()
    {
        bool modal = ModalActive;
        if (modal)
        {
            try
            {
                if (PresenceShim.WorldAlive)
                {
                    var controller = SingletonBehaviour<ACanvasController<CanvasController.ElementType>>.Instance;
                    controller.provider.RequirePointer(true);
                }
            }
            catch (Exception) { /* no canvas controller (menu scene, teardown) — direct heal below still applies */ }
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            _pointerEnforced = true;
        }
        else if (_pointerEnforced)
        {
            _pointerEnforced = false;
            try
            {
                if (PresenceShim.WorldAlive)
                {
                    var controller = SingletonBehaviour<ACanvasController<CanvasController.ElementType>>.Instance;
                    controller.provider.RequirePointer(controller.IsPointerRequired());
                }
            }
            catch (Exception) { /* nothing to restore without a controller */ }
        }
    }

    /// <summary>Phase-edge reactions: the join gate begins when a join goes in flight and is
    /// force-cleared when the session returns to Idle (Leave, failed join teardown); the HUD
    /// carries the non-blocking session-lost banner (plan §5's status tier).</summary>
    private void OnVmChanged()
    {
        SessionPhase phase = _vm.Phase;
        if (phase == _lastPhase) return;
        // Cover only uGUI-initiated joins — the dev panel keeps its legacy flow (Cody, 2026-08-04).
        if (phase == SessionPhase.Connecting && _vm.JoinInitiatedFromUi) BeginJoinGate();
        else if (phase == SessionPhase.Idle && Gate.Active) Gate.Clear();
        // Joined/Hosting do NOT clear the gate — admission precedes the burst's end; only the
        // sentinel (via done()) or Idle may take the cover down.

        if (phase == SessionPhase.SessionLost) Hud.Show("SESSION LOST — leave to restore your world");
        else if (_lastPhase == SessionPhase.SessionLost) Hud.Hide();
        _lastPhase = phase;
    }

    private void BeginJoinGate()
    {
        if (Gate.Active) return;
        Gate.Begin("Joining session", JoinStages, () => _vm.JoinSettled,
            failsafeSeconds: 30.0, onGiveUp: () => _vm.Leave());
    }

    /// <summary>The server is holding us for a slot (D18). Each update is live proof the wait is
    /// healthy: show the place in line and top the failsafe back up, so an ACTIVE queue never nags —
    /// a STALLED one (no movement for 30 s) still reaches the explain screen, whose keep-waiting /
    /// give-up pair is exactly the right surface for a long queue. Position 0 needs nothing: the
    /// accept or reject that caused it drives the normal stage/reject flow.</summary>
    private void OnQueueChanged(int position, int total)
    {
        if (!Gate.Active || position <= 0) return;
        Gate.SetDetail($"server full — waiting for a free slot (position {position} of {total})");
        Gate.Nudge(30.0);
    }

    private void OnJoinStage(JoinStage stage)
    {
        if (!Gate.Active) return;
        int index = stage switch
        {
            JoinStage.Connecting => 0,
            JoinStage.World => 1,
            JoinStage.Career => 2,
            JoinStage.Items => 3,
            _ => -1, // Complete clears via done() on the next tick; None resolves via Idle
        };
        if (index >= 0) Gate.SetStage(index);
    }

    private void OnJoinRejected(RejectInfo info)
    {
        // A dev-panel join keeps its legacy reject flow (error text, manual Leave) — the cover,
        // auto-leave and modal belong to the uGUI path only (Cody, 2026-08-04).
        if (!_vm.JoinInitiatedFromUi) return;
        Gate.Clear();          // the join is dead — never leave the cover to wait out its failsafe
        _pendingLeave = true;  // tear down the half-open client next tick (see Tick)
        if (info.IsVersionMismatch && _router != null)
        {
            ScreenRouter router = _router;
            router.Push(new MismatchScreen(info, () => router.Pop()));
        }
        // Non-version refusals (password, full, key) read fine as the root status line's ⚠ text.
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
            _router.Push(new RootScreen(_vm, _prefs, _log, Close));
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
        WidgetKit.ReleaseKeyboardFocus(); // failsafe — destroy skips onDeselect (input-lockout hazard)
    }

    /// <summary>Full teardown (mod toggle-off).</summary>
    public void Dispose()
    {
        _vm.Changed -= OnVmChanged;
        _vm.JoinStageChanged -= OnJoinStage;
        _vm.JoinRejected -= OnJoinRejected;
        _vm.QueueChanged -= OnQueueChanged;
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
