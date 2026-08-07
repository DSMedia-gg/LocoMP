using System;
using DV.Utils;
using DV.WeatherSystem;
using LocoMP.Core.Session;

namespace LocoMP.Shim;

/// <summary>
/// v18 time-of-day sync (02 §3 — every player sees the same sun). One class, two roles:
///
/// HOST — the world source's sky is the truth. Reports it on a slow heartbeat and IMMEDIATELY on a
/// time JUMP (sleep / fast travel both land in <c>WeatherPresetManager.RefreshTimeOfDay</c>, whose
/// TimeJump event this hooks), as an OADate — the same encoding DV's own save uses
/// (<c>WeatherDriver.GetSaveData</c>), so nothing is invented.
///
/// CLIENT — projects the last authoritative time forward at the world rate and corrects the local
/// sky only past a drift threshold: both skies flow at the same rate, so steady state applies
/// NOTHING and the sun never visibly snaps. A client's own local skip (their bed still works) drifts
/// past the threshold instantly and snaps back — 02 §3's "skips are host/server-approved" enforced
/// by correction rather than by blocking the interaction. Corrections go through DV's own
/// <c>RealDateTime</c> + <c>RefreshTimeOfDay</c> so every native minute/hour listener fires.
/// </summary>
public sealed class WorldTimeSync : IDisposable
{
    private const float HostHeartbeatSeconds = 5f;
    /// <summary>Client tolerance in WORLD seconds (~0.25° of sun arc — invisible) before a
    /// correction is applied. Big enough that clock skew never causes a fight, small enough that a
    /// sleep skip lands everywhere within one tick.</summary>
    private const double ApplyThresholdWorldSeconds = 60.0;
    private const float TickIntervalSeconds = 1.0f;

    private readonly NetClient _client;
    private readonly bool _isHost;
    private readonly Action<string> _log;

    private WeatherPresetManager? _hookedManager;
    private Action? _jumpHandler;
    private float _sendAccum;
    private float _tickAccum;

    // Client: the authoritative anchor — the OADate as of receipt, projected forward locally.
    private bool _haveAuth;
    private double _authOa;
    private float _authDayLen;
    private float _sinceAuthSeconds;
    private bool _applying;
    private bool _announcedSync;

    public WorldTimeSync(NetClient client, bool isHost, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _isHost = isHost;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (!_isHost) _client.WorldTimeChanged += OnWorldTime;
    }

    private void OnWorldTime(double oaDate, float dayLengthMinutes)
    {
        _authOa = oaDate;
        _authDayLen = dayLengthMinutes > 0 ? dayLengthMinutes : WorldClock.DefaultDayLengthMinutes;
        _sinceAuthSeconds = 0;
        _haveAuth = true;
    }

    public void Tick(float dt)
    {
        if (!_isHost && _haveAuth) _sinceAuthSeconds += dt;
        _tickAccum += dt;
        if (_tickAccum < TickIntervalSeconds) return;
        _tickAccum = 0;

        if (!_client.Joined) return;
        WeatherPresetManager? manager = TryGetManager();
        if (manager == null) return;

        if (_isHost) HostTick(manager, TickIntervalSeconds);
        else ClientTick(manager);
    }

    private static WeatherPresetManager? TryGetManager()
    {
        try
        {
            WeatherDriver driver = SingletonBehaviour<WeatherDriver>.Instance;
            return driver != null ? driver.manager : null;
        }
        catch
        {
            return null; // menus / mid-load — nothing to sync yet
        }
    }

    // ── host: capture + report ──

    private void HostTick(WeatherPresetManager manager, float dt)
    {
        if (!ReferenceEquals(manager, _hookedManager)) HookJump(manager);
        _sendAccum += dt;
        if (_sendAccum < HostHeartbeatSeconds) return;
        _sendAccum = 0;
        Report(manager);
    }

    private void HookJump(WeatherPresetManager manager)
    {
        UnhookJump();
        Action handler = () =>
        {
            // A jump (sleep / fast travel) must reach every peer NOW, not at the next heartbeat —
            // their sun is suddenly hours wrong and the drift threshold exists for exactly this.
            _sendAccum = 0;
            Report(manager);
        };
        manager.TimeJump += handler;
        _hookedManager = manager;
        _jumpHandler = handler;
    }

    private void UnhookJump()
    {
        if (_hookedManager != null && _jumpHandler != null)
        {
            try { _hookedManager.TimeJump -= _jumpHandler; }
            catch { /* scene teardown may have beaten us to it */ }
        }
        _hookedManager = null;
        _jumpHandler = null;
    }

    private void Report(WeatherPresetManager manager)
    {
        try
        {
            double oa = manager.RealDateTime.ToOADate();
            float dayLen = (float)manager.DayLengthInMinutes;
            if (!(dayLen > 0)) dayLen = WorldClock.DefaultDayLengthMinutes;
            _client.SendWorldTimeReport(oa, dayLen);
        }
        catch (Exception e)
        {
            _log($"[time] report failed: {e.Message}");
        }
    }

    // ── client: project + correct ──

    private void ClientTick(WeatherPresetManager manager)
    {
        if (!_haveAuth || _applying) return;
        try
        {
            double projected = _authOa + _sinceAuthSeconds * (1440.0 / _authDayLen) / 86400.0;
            double localOa = manager.RealDateTime.ToOADate();
            double driftWorldSeconds = Math.Abs(projected - localOa) * 86400.0;

            // Day length first: it is what keeps the steady state drift-free between corrections.
            try
            {
                if (Math.Abs((float)manager.DayLengthInMinutes - _authDayLen) > 0.01f)
                    manager.todTime.DayLengthInMinutes.CurrentValue = _authDayLen;
            }
            catch { /* a missing todTime rig only costs rate-matching, not correctness */ }

            if (driftWorldSeconds <= ApplyThresholdWorldSeconds)
            {
                if (!_announcedSync)
                {
                    _announcedSync = true;
                    _log("[time] world clock synced to the session");
                }
                return;
            }

            _applying = true;
            manager.todSky.Cycle.RealDateTime = DateTime.FromOADate(projected);
            manager.RefreshTimeOfDay(); // DV's own jump path: minute/hour listeners + sky rebuild
            _log($"[time] world clock corrected {driftWorldSeconds / 60.0:F1} world-min to " +
                 $"{DateTime.FromOADate(projected):HH:mm}");
        }
        catch (Exception e)
        {
            _log($"[time] apply failed: {e.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    public void Dispose()
    {
        if (!_isHost) _client.WorldTimeChanged -= OnWorldTime;
        UnhookJump();
    }
}
