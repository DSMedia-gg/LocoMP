using System;
using System.Collections.Generic;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoSim.Implementations;

namespace LocoMP.Shim;

/// <summary>
/// M6-A1.1 — the owner-read / remote-drive seam of the cosmetic fidelity channel (11 §3).
///
/// OWNER side: every car we simulate has its A1.1 output ports sampled on a slow cadence
/// (the visible RESULTS the sim computes — sand flow, exhaust plume, engine rpm/on), quantised to
/// bytes and change-gated onto the wire. Ports are resolved by DOT-PREFIXED id suffix against
/// <c>simFlow.AllPorts</c> (ids are "component.SUFFIX" with per-prefab component names, so the
/// suffix is the stable family-agnostic key; the dot keeps RPM_NORMALIZED from swallowing
/// IDLE_RPM_NORMALIZED). A car without a port simply never streams that kind.
///
/// REMOTE side: incoming scalars write the SAME port on the replica through the public
/// <c>Port.Value</c> setter — NOT <c>ExternalValueUpdate</c>, which hard-refuses anything but
/// EXTERNAL_IN ports (decompile-proven); the setter fires <c>ValueUpdatedInternally</c>, which is
/// exactly what DV's own particle/lamp/audio/gauge readers subscribe. We build the transport, DV
/// renders (the U11-1 finding). A kinematic replica's sim does not recompute these outputs, so a
/// written value sticks; the slow reconcile re-applies the session cache to replicas that
/// materialise late or respawn (mirroring the coupler-hardware posture).
///
/// Deliberately NOT here: lights/wipers/sander CONTROLS — those are OverridableBaseControls the
/// M3.5c CabControlSync already mirrors end-to-end (the R5 live row verifies the rendered half);
/// steam fire/glow (A1.4) and gauge pressure streams (A1.3) ride the same wire later by adding
/// kinds, no protocol change.
/// </summary>
public sealed class CosmeticSync : IDisposable
{
    private const float OwnerSampleIntervalSeconds = 0.25f; // 4 Hz cap, change-gated → usually silent
    private const float ReconcileIntervalSeconds = 1.0f;

    /// <summary>kind → the dot-prefixed port-id suffix it mirrors. One row per CosmeticKind; both
    /// sides resolve from the same table so the mapping can never fork.</summary>
    private static readonly (byte kind, string suffix)[] PortMap =
    {
        ((byte)CosmeticKind.SmokeIntensity, ".TOTAL_FLOW_NORMALIZED"), // SteamExhaust plume
        ((byte)CosmeticKind.SandFlow,       ".SAND_FLOW"),             // Sander computed output
        ((byte)CosmeticKind.EngineRpm,      ".RPM_NORMALIZED"),        // diesel/electric prime mover
        ((byte)CosmeticKind.EngineOn,       ".ENGINE_ON"),             // the gate the effects hang off
    };

    private sealed class CarPorts
    {
        public CarPorts(TrainCar car) { Car = car; }
        public readonly TrainCar Car;                       // instance-tracked: respawn = re-resolve
        public readonly Dictionary<byte, Port> Ports = new();
        public readonly Dictionary<byte, byte> LastSent = new();   // owner change gate
        public readonly Dictionary<byte, byte> Applied = new();    // remote reconcile gate
    }

    private readonly NetClient _client;
    private readonly TrainSync _trains;
    private readonly Action<string> _log;
    private readonly Dictionary<int, CarPorts> _cars = new();  // serverCarId → resolved ports
    private readonly List<(byte kind, byte value)> _batch = new();
    private float _sampleAccum;
    private float _reconcileAccum;

    public CosmeticSync(NetClient client, TrainSync trains, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _trains = trains ?? throw new ArgumentNullException(nameof(trains));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _client.Trains.CosmeticReceived += OnCosmetic;
    }

    public void Tick(float dt)
    {
        if (!_client.Joined) return;
        _sampleAccum += dt;
        if (_sampleAccum >= OwnerSampleIntervalSeconds)
        {
            _sampleAccum = 0;
            SampleOwnCars();
        }
        _reconcileAccum += dt;
        if (_reconcileAccum >= ReconcileIntervalSeconds)
        {
            _reconcileAccum = 0;
            ReconcileReplicas();
        }
    }

    // ── owner: sample → quantise → change-gate → send ──

    private void SampleOwnCars()
    {
        foreach (KeyValuePair<int, TrainCar> own in _trains.OwnBoundCars)
        {
            CarPorts? entry = Resolve(own.Key, own.Value);
            if (entry == null || entry.Ports.Count == 0) continue;

            _batch.Clear();
            foreach (KeyValuePair<byte, Port> port in entry.Ports)
            {
                byte quantised = Quantise(port.Value.Value);
                if (entry.LastSent.TryGetValue(port.Key, out byte last) && last == quantised) continue;
                entry.LastSent[port.Key] = quantised;
                _batch.Add((port.Key, quantised));
            }
            if (_batch.Count > 0) _client.Trains.SendCosmetic(own.Key, _batch);
        }
    }

    // ── remote: live applies + late-spawn reconcile from the session cache ──

    private void OnCosmetic(int carId, byte kind, byte value)
    {
        if (!_trains.Remote.TryGetCarByServerId(carId, out TrainCar car)) return; // reconcile catches it
        Apply(carId, car, kind, value);
    }

    /// <summary>Replicas that materialised after their state arrived (join burst before spawn,
    /// interest re-entry, respawn) get the cached values pushed — the same catch-up posture as
    /// the coupler-hardware reconcile.</summary>
    private void ReconcileReplicas()
    {
        foreach (KeyValuePair<int, Dictionary<byte, byte>> kv in _client.Trains.Cosmetics)
        {
            if (!_trains.Remote.TryGetCarByServerId(kv.Key, out TrainCar car)) continue;
            foreach (KeyValuePair<byte, byte> scalar in kv.Value)
                Apply(kv.Key, car, scalar.Key, scalar.Value);
        }
    }

    private void Apply(int carId, TrainCar car, byte kind, byte value)
    {
        CarPorts? entry = Resolve(carId, car);
        if (entry == null) return;
        if (entry.Applied.TryGetValue(kind, out byte applied) && applied == value) return;
        if (!entry.Ports.TryGetValue(kind, out Port port)) return; // this car has no such effect
        try
        {
            port.Value = value / 255f; // fires ValueUpdatedInternally → DV's own readers render
            entry.Applied[kind] = value;
        }
        catch (Exception e)
        {
            _log($"[trains] cosmetic apply failed (car {carId} kind {kind}): {e.Message}");
        }
    }

    // ── shared: per-car-instance port resolution ──

    private CarPorts? Resolve(int carId, TrainCar car)
    {
        if (car == null) return null;
        if (_cars.TryGetValue(carId, out CarPorts? entry) && ReferenceEquals(entry.Car, car))
            return entry;

        // New car, or the id now names a different instance (respawn) — re-resolve from scratch.
        SimulationFlow? flow;
        try { flow = car.SimController != null ? car.SimController.simFlow : null; }
        catch { flow = null; }
        entry = new CarPorts(car);
        if (flow != null)
        {
            foreach (Port port in flow.AllPorts)
            {
                if (port == null || string.IsNullOrEmpty(port.id)) continue;
                foreach ((byte kind, string suffix) in PortMap)
                    if (!entry.Ports.ContainsKey(kind) && port.id.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        entry.Ports[kind] = port;
                        break;
                    }
            }
        }
        _cars[carId] = entry; // cached even when empty — unpowered stock resolves once, not per tick
        return entry;
    }

    private static byte Quantise(float value)
    {
        if (float.IsNaN(value)) return 0;
        float clamped = value < 0f ? 0f : value > 1f ? 1f : value;
        return (byte)(clamped * 255f + 0.5f);
    }

    public void Dispose()
    {
        _client.Trains.CosmeticReceived -= OnCosmetic;
        _cars.Clear();
    }
}
