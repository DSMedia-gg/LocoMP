using System;
using DV;
using DV.Utils;
using LocoMP.Core.Session;

namespace LocoMP.Shim;

/// <summary>
/// D19 host-pause propagation (v18): when the HOST pauses via DV's own pause menu, the world
/// source's sim freezes regardless of what LocoMP wants — so instead of every peer watching the
/// world silently stop (a desync-shaped mystery), the pause is an acknowledged session state.
///
/// HOST — polls <c>AppUtil.IsTimePaused</c> (a 4 Hz poll beats event wiring here: it can never
/// miss a transition and needs no teardown ordering) and reports changes into
/// <c>NetServer.SetWorldPaused</c>, which broadcasts and freezes the flowing world CLOCK with it.
///
/// CLIENT — a pause notice raises a request in DV's own priority pause system
/// (<c>AppUtil.RequestPause</c> — time stops, physics gravity zeroed, no menu opened), and the
/// resume — or a session loss mid-pause, which NetClient surfaces as an unpause — removes it. Our
/// tick pumps keep running at timeScale 0 (proven live in R2's ESC-pause work), so the resume
/// notice always gets through. D19's boundary holds: LocoMP's OWN overlays still never pause
/// anyone, and a dedicated server never pauses (nothing ever calls the server setter there).
/// </summary>
public sealed class PauseSync : IDisposable
{
    private const float HostPollSeconds = 0.25f;

    private readonly NetClient _client;
    private readonly Action<bool, string>? _setServerPaused; // host mode only
    private readonly Action<string> _log;
    private float _accum;
    private bool _lastReported;
    private bool _holdingPause;

    /// <param name="setServerPaused">Host mode: the NetServer.SetWorldPaused bridge (paused,
    /// reason). Null on a joined client — it only APPLIES pause state, never produces it.</param>
    public PauseSync(NetClient client, Action<bool, string>? setServerPaused, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _setServerPaused = setServerPaused;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (_setServerPaused == null) _client.WorldPauseChanged += OnWorldPause;
    }

    public void Tick(float dt)
    {
        if (_setServerPaused == null || !_client.Joined) return;
        _accum += dt;
        if (_accum < HostPollSeconds) return;
        _accum = 0;
        try
        {
            AppUtil app = SingletonBehaviour<AppUtil>.Instance;
            if (app == null) return;
            bool paused = app.IsTimePaused;
            if (paused == _lastReported) return;
            _lastReported = paused;
            _setServerPaused(paused, paused ? "the host paused the game" : string.Empty);
            _log(paused
                ? "[session] you paused — every player's world is paused with you (D19)"
                : "[session] resumed — every player's world resumes");
        }
        catch
        {
            // Menus / teardown: no AppUtil to read. The next tick sees the settled state.
        }
    }

    private void OnWorldPause(bool paused, string reason)
    {
        try
        {
            AppUtil app = SingletonBehaviour<AppUtil>.Instance;
            if (app == null) return;
            if (paused && !_holdingPause)
            {
                _holdingPause = true;
                app.RequestPause(this, paused: true, priority: 10);
                _log($"[session] HOST PAUSED — {(reason.Length > 0 ? reason : "world frozen until the host resumes")}");
            }
            else if (!paused && _holdingPause)
            {
                _holdingPause = false;
                app.RemovePauseRequest(this);
                _log("[session] host resumed — world unfrozen");
            }
        }
        catch (Exception e)
        {
            _log($"[session] pause apply failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (_setServerPaused == null) _client.WorldPauseChanged -= OnWorldPause;
        if (_holdingPause)
        {
            _holdingPause = false;
            try { SingletonBehaviour<AppUtil>.Instance?.RemovePauseRequest(this); }
            catch { /* teardown — the request system dies with the scene */ }
        }
    }
}
