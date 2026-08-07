using System;
using System.Collections.Generic;
using DV.Simulation.Brake;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// v18 per-car handbrake sync (02 §1 P0), riding the ControlState/ControlInput machinery under
/// <see cref="VirtualControlId.Handbrake"/> — the server already stores the latest value per
/// (car, control) and replays it in the join burst, so this class is a thin adapter over
/// <c>BrakeSystem</c>:
///
/// OWNER — every handbrake on cars we simulate is watched via <c>HandbrakePositionChanged</c>;
/// committed values broadcast as ControlState (id 200). A routed bystander input is applied to the
/// real wheel here, and the resulting change event echoes the committed state back out — input
/// becomes state only through the owner (03 §3), exactly the cab-control chain.
///
/// BYSTANDER — turning the wheel on a car we do NOT simulate captures the same event and sends
/// ControlInput instead: the server routes it to the sim owner (grant-exempt — exterior hardware
/// is operated from the ground; physical reach on this client is the proximity gate) or, for a
/// PARKED set, commits it directly (securing an ownerless cut is ordinary yard work).
///
/// APPLY — remote state lands in a mirror; a slow reconcile tick pushes it onto live replicas via
/// DV's own <c>SetHandbrakePosition</c>. Mirror-then-reconcile (not apply-on-receive) is what makes
/// join-burst replay work: the state arrives before the replica exists, and the tick catches it
/// whenever the car spawns.
/// </summary>
public sealed class HandbrakeSync : IDisposable
{
    private const float ResyncIntervalSeconds = 1.0f;
    private const float ValueEpsilon = 0.005f;

    private readonly NetClient _client;
    private readonly TrainSync _trains;
    private readonly Action<string> _log;

    private readonly Dictionary<int, (TrainCar car, Action<(float, bool)> handler)> _watched = new();
    private readonly Dictionary<int, float> _mirror = new(); // serverCarId → last remote-committed value
    private bool _applying;
    private float _resyncAccum;

    public HandbrakeSync(NetClient client, TrainSync trains, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _trains = trains ?? throw new ArgumentNullException(nameof(trains));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _client.Trains.ControlStateReceived += OnControlState;
        _client.Trains.ControlInputReceived += OnControlInput;
    }

    public void Tick(float dt)
    {
        if (!_client.Joined) return;
        _resyncAccum += dt;
        if (_resyncAccum < ResyncIntervalSeconds) return;
        _resyncAccum = 0;
        ResyncSubscriptions();
        ApplyMirror();
    }

    // ── subscriptions: every live car with a handbrake, own AND remote ──

    private void ResyncSubscriptions()
    {
        List<int>? drop = null;
        foreach (KeyValuePair<int, (TrainCar car, Action<(float, bool)> handler)> kv in _watched)
            if (kv.Value.car == null || !_trains.TryGetLiveCar(kv.Key, out _))
                (drop ??= new List<int>()).Add(kv.Key);
        if (drop != null)
            foreach (int carId in drop) Unhook(carId);

        foreach (KeyValuePair<int, TrainCar> own in _trains.OwnBoundCars) TryHook(own.Key, own.Value);
        foreach (KeyValuePair<int, TrainCar> remote in _trains.Remote.CarsByServerId) TryHook(remote.Key, remote.Value);
    }

    private void TryHook(int carId, TrainCar car)
    {
        if (car == null || _watched.ContainsKey(carId)) return;
        BrakeSystem? brakes = SafeBrakes(car);
        if (brakes == null) return;
        Action<(float, bool)> handler = change => OnLocalHandbrake(carId, change.Item1);
        brakes.HandbrakePositionChanged += handler;
        _watched[carId] = (car, handler);
    }

    private void Unhook(int carId)
    {
        if (!_watched.TryGetValue(carId, out (TrainCar car, Action<(float, bool)> handler) entry)) return;
        _watched.Remove(carId);
        _mirror.Remove(carId);
        if (entry.car == null) return;
        try
        {
            BrakeSystem? brakes = SafeBrakes(entry.car);
            if (brakes != null) brakes.HandbrakePositionChanged -= entry.handler;
        }
        catch { /* car mid-destroy — the subscription dies with it */ }
    }

    private static BrakeSystem? SafeBrakes(TrainCar car)
    {
        try
        {
            BrakeSystem? brakes = car.brakeSystem;
            return brakes != null && brakes.hasHandbrake ? brakes : null;
        }
        catch
        {
            return null;
        }
    }

    // ── capture → wire ──

    private void OnLocalHandbrake(int carId, float value)
    {
        if (_applying) return; // an applied remote value must not re-capture
        if (_trains.TryGetLiveCar(carId, out TrainCar car) && !_trains.Remote.IsRemoteCar(car))
        {
            _client.Trains.SendControlState(carId, VirtualControlId.Handbrake, value);
        }
        else
        {
            // Not ours: report the physical act; the committed state comes back from the owner's
            // echo (or the server's parked commit) and settles the mirror.
            _client.Trains.SendControlInput(carId, VirtualControlId.Handbrake, value);
        }
    }

    // ── wire → apply ──

    /// <summary>A bystander turned a handbrake on a car WE simulate: apply it to the real brake rig.
    /// Deliberately NOT reentry-guarded — the resulting change event is the committed state echoing
    /// out to every replica (the cab-control authority chain).</summary>
    private void OnControlInput(int carId, byte controlId, float value)
    {
        if (controlId != VirtualControlId.Handbrake) return;
        if (!_trains.TryGetLiveCar(carId, out TrainCar car) || _trains.Remote.IsRemoteCar(car)) return;
        BrakeSystem? brakes = SafeBrakes(car);
        if (brakes == null) return;
        try { brakes.SetHandbrakePosition(Mathf.Clamp01(value)); }
        catch (Exception e) { _log($"[trains] handbrake input apply failed (car {carId}): {e.Message}"); }
    }

    private void OnControlState(int carId, byte controlId, float value)
    {
        if (controlId != VirtualControlId.Handbrake) return;
        _mirror[carId] = value;
    }

    private void ApplyMirror()
    {
        foreach (KeyValuePair<int, float> kv in _mirror)
        {
            if (!_trains.Remote.TryGetCarByServerId(kv.Key, out TrainCar car)) continue; // own car → we are authority
            BrakeSystem? brakes = SafeBrakes(car);
            if (brakes == null) continue;
            try
            {
                if (Mathf.Abs(brakes.handbrakePosition - kv.Value) < ValueEpsilon) continue;
                _applying = true;
                brakes.SetHandbrakePosition(Mathf.Clamp01(kv.Value));
            }
            catch
            {
                // A replica's handbrake is cosmetic until adoption — never let a mirror break the tick.
            }
            finally
            {
                _applying = false;
            }
        }
    }

    public void Dispose()
    {
        _client.Trains.ControlStateReceived -= OnControlState;
        _client.Trains.ControlInputReceived -= OnControlInput;
        foreach (int carId in new List<int>(_watched.Keys)) Unhook(carId);
        _mirror.Clear();
    }
}
