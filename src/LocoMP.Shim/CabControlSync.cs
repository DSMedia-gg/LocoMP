using System;
using System.Collections.Generic;
using DV.HUD;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using LocoMP.Core.Session;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// M3.5c multi-crew cab controls, both directions over one uniform surface
/// (<see cref="OverridableBaseControl"/> — every cab control in the game, throttle to firedoor,
/// keyed by the 42-value ControlType enum that fits the wire's byte):
///
/// OWNER side — every control on cars WE simulate is watched; committed values broadcast as
/// ControlState (the server keeps the latest per control and replays them in the join burst, so a
/// newcomer's replica levers match reality). A grant holder's ControlInput is applied to the real
/// control here, which fires the same watch and echoes the committed state back out — the input
/// becomes state only through the owner (03 §3).
///
/// HOLDER side — sitting in a REMOTE car's cab with the control grant, the local lever moves are
/// captured and sent as ControlInput to the owner. Incoming ControlState is applied to replica
/// levers EXCEPT while we are the occupying grant holder of that cab (never fight the player's
/// hand); a reentry guard keeps applied state from re-capturing as input.
///
/// Sends are per-control rate-limited with a trailing flush — a lever drag fires ControlUpdated
/// every frame, and 10/s per touched control is plenty for levers.
/// </summary>
public sealed class CabControlSync : IDisposable
{
    private const float SendMinIntervalSeconds = 0.1f;
    private const float ResyncIntervalSeconds = 1.0f;
    private const float ValueEpsilon = 0.003f;

    private enum Role : byte { Owner, Holder }

    private sealed class Watched
    {
        public Watched(TrainCar car, Role role) { Car = car; Role = role; }

        public readonly TrainCar Car;
        public Role Role;
        public readonly List<(OverridableBaseControl control, Action<float> handler)> Hooks = new();
    }

    private sealed class Outgoing
    {
        public float Value;
        public bool Dirty;
        public float LastSentAt = -999f;
    }

    private readonly NetClient _client;
    private readonly TrainSync _trains;
    private readonly Action<string> _log;
    private readonly Dictionary<int, Watched> _watched = new();          // serverCarId → hooks
    private readonly Dictionary<(int carId, byte control), Outgoing> _outbox = new();
    private readonly Dictionary<(int carId, byte control), Role> _sendRole = new();
    private bool _applyingRemote;
    private float _resyncAccum;

    public CabControlSync(NetClient client, TrainSync trains, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _trains = trains ?? throw new ArgumentNullException(nameof(trains));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _client.Trains.ControlInputReceived += OnControlInput;
        _client.Trains.ControlStateReceived += OnControlState;
    }

    /// <summary>Pump: keep subscriptions matched to the world and flush rate-limited sends.</summary>
    public void Tick(float dt)
    {
        if (!_client.Joined) return;
        _resyncAccum += dt;
        if (_resyncAccum >= ResyncIntervalSeconds)
        {
            _resyncAccum = 0;
            ResyncSubscriptions();
        }
        FlushOutbox();
    }

    // ── subscription management ──

    /// <summary>Cheap steady-state diff: cars we should be watching = every car we simulate
    /// (Owner) + the remote cab we occupy with the grant (Holder). Cars falling out (destroyed,
    /// unbound, cab left, grant lost) unhook.</summary>
    private void ResyncSubscriptions()
    {
        var desired = new Dictionary<int, Role>();
        foreach (KeyValuePair<int, TrainCar> own in _trains.OwnBoundCars)
            if (HasControls(own.Value)) desired[own.Key] = Role.Owner;

        TrainCar occupied = PlayerManager.Car;
        if (occupied != null && _trains.Remote.TryGetServerCarId(occupied, out int occupiedId) &&
            _client.Trains.Grants.TryGetValue(occupiedId, out int holder) && holder == _client.LocalId &&
            HasControls(occupied))
        {
            desired[occupiedId] = Role.Holder;
        }

        List<int>? drop = null;
        foreach (KeyValuePair<int, Watched> kv in _watched)
            if (kv.Value.Car == null || !desired.ContainsKey(kv.Key)) (drop ??= new List<int>()).Add(kv.Key);
        if (drop != null)
            foreach (int carId in drop) Unhook(carId);

        foreach (KeyValuePair<int, Role> kv in desired)
        {
            if (_watched.TryGetValue(kv.Key, out Watched? existing))
            {
                existing.Role = kv.Value;
                continue;
            }
            Hook(kv.Key, kv.Value);
        }
    }

    private static bool HasControls(TrainCar car)
    {
        try { return car.SimController != null && car.SimController.controlsOverrider != null; }
        catch { return false; }
    }

    private void Hook(int carId, Role role)
    {
        if (!_trains.TryGetLiveCar(carId, out TrainCar car)) return;
        var watched = new Watched(car, role);
        foreach (OverridableBaseControl control in car.GetComponentsInChildren<OverridableBaseControl>(true))
        {
            if (control == null) continue;
            byte id = SafeControlId(control);
            if (id == 0) continue; // ControlType.None or unreadable
            OverridableBaseControl captured = control;
            Action<float> handler = value => OnLocalControl(carId, captured, id, value);
            captured.ControlUpdated += handler;
            watched.Hooks.Add((captured, handler));
        }
        if (watched.Hooks.Count == 0) return;
        _watched[carId] = watched;
        if (role == Role.Holder)
            _log($"[trains] cab controls live: your inputs in car {carId} now drive the owner's loco ({watched.Hooks.Count} controls)");
    }

    private void Unhook(int carId)
    {
        if (!_watched.TryGetValue(carId, out Watched? watched)) return;
        _watched.Remove(carId);
        foreach ((OverridableBaseControl control, Action<float> handler) in watched.Hooks)
        {
            if (control != null) control.ControlUpdated -= handler;
        }
        watched.Hooks.Clear();
    }

    private static byte SafeControlId(OverridableBaseControl control)
    {
        try
        {
            var type = (int)control.ControlType;
            return type > 0 && type <= byte.MaxValue ? (byte)type : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    // ── capture → wire (rate-limited) ──

    private void OnLocalControl(int carId, OverridableBaseControl control, byte controlId, float value)
    {
        if (_applyingRemote) return; // an applied remote value must not re-capture
        if (!_watched.TryGetValue(carId, out Watched? watched)) return;

        (int carId, byte controlId) key = (carId, controlId);
        if (!_outbox.TryGetValue(key, out Outgoing? entry)) _outbox[key] = entry = new Outgoing();
        entry.Value = value;
        entry.Dirty = true;
        _sendRole[key] = watched.Role;
    }

    private void FlushOutbox()
    {
        if (_outbox.Count == 0) return;
        float now = Time.unscaledTime;
        foreach (KeyValuePair<(int carId, byte control), Outgoing> kv in _outbox)
        {
            Outgoing entry = kv.Value;
            if (!entry.Dirty || now - entry.LastSentAt < SendMinIntervalSeconds) continue;
            entry.Dirty = false;
            entry.LastSentAt = now;
            if (_sendRole.TryGetValue(kv.Key, out Role role) && role == Role.Holder)
                _client.Trains.SendControlInput(kv.Key.carId, kv.Key.control, entry.Value);
            else
                _client.Trains.SendControlState(kv.Key.carId, kv.Key.control, entry.Value);
        }
    }

    // ── wire → apply ──

    /// <summary>A grant holder drove a control on a car WE simulate: apply it to the real cab.
    /// Deliberately NOT reentry-guarded — the resulting ControlUpdated is the committed state
    /// echoing back out to every replica, which is exactly the authority chain.</summary>
    private void OnControlInput(int carId, byte controlId, float value)
    {
        if (!_trains.TryGetLiveCar(carId, out TrainCar car) || _trains.Remote.IsRemoteCar(car)) return;
        OverridableBaseControl? control = FindControl(car, controlId);
        if (control == null) return;
        try { control.Set(Mathf.Clamp01(value)); }
        catch (Exception e) { _log($"[trains] control input apply failed (car {carId}): {e.Message}"); }
    }

    /// <summary>The owner committed a control value — mirror it onto the replica's lever, unless
    /// we are the occupying grant holder of that cab (our hand is the source; never fight it).</summary>
    private void OnControlState(int carId, byte controlId, float value)
    {
        if (!_trains.Remote.TryGetCarByServerId(carId, out TrainCar car)) return;
        if (PlayerManager.Car == car &&
            _client.Trains.Grants.TryGetValue(carId, out int holder) && holder == _client.LocalId)
        {
            return;
        }
        OverridableBaseControl? control = FindControl(car, controlId);
        if (control == null) return;
        try
        {
            if (Mathf.Abs(control.Value - value) < ValueEpsilon) return;
            _applyingRemote = true;
            control.Set(Mathf.Clamp01(value));
        }
        catch
        {
            // Replica levers are cosmetic on a kinematic car — never let a mirror break the tick.
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private static OverridableBaseControl? FindControl(TrainCar car, byte controlId)
    {
        try
        {
            BaseControlsOverrider? overrider = car.SimController != null ? car.SimController.controlsOverrider : null;
            if (overrider == null) return null;
            return overrider.GetControl((InteriorControlsManager.ControlType)controlId);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _client.Trains.ControlInputReceived -= OnControlInput;
        _client.Trains.ControlStateReceived -= OnControlState;
        foreach (int carId in new List<int>(_watched.Keys)) Unhook(carId);
        _outbox.Clear();
        _sendRole.Clear();
    }
}
