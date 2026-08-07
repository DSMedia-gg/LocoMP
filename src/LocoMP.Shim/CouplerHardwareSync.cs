using System;
using System.Collections.Generic;
using DV.MultipleUnit;
using DV.Simulation.Brake;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;

namespace LocoMP.Shim;

/// <summary>
/// v18 coupler-hardware sync (02 §1 P0 — brake hoses, anglecocks, MU cables), the discrete state
/// around the chain that F1's coupling work never replicated. Symmetric on every client:
///
/// CAPTURE — DV's own seams: the static <c>Brakeset.HoseConnectionChanged</c> and
/// <c>MultipleUnitCable.AnyConnectionChanged</c> events (connects AND native auto-breaks), plus a
/// per-end <c>CockChanged</c> hook maintained by the reconcile tick. Every client reports its own
/// world's changes; the server absorbs the N-fold restatement of an auto-break silently, so no
/// owner-gating is needed here. A report folds into the local mirror optimistically (the state
/// broadcast excludes the reporter by design).
///
/// APPLY — committed lines land immediately on live replicas via the same native methods a hand
/// uses (<c>Connect</c>/<c>Disconnect</c>/<c>SetCock</c>), under a reentry guard so the resulting
/// native events don't re-report. DV derives brakeset merge/split from hose+cock state itself, so
/// replicating the discrete state buys the pneumatic topology for free.
///
/// RECONCILE (slow tick) — two one-directional passes that make ordering irrelevant:
/// mirror-present state is IMPOSED on live cars that lack it (join-burst lines arrive before the
/// replicas spawn), and local-present state missing from the mirror is REPORTED (this is what seeds
/// the host's pre-connected job consists into the session with zero special casing). Absence is
/// never imposed — disconnects/closes ride the immediate event path, so a not-yet-spawned replica
/// (default: disconnected, cocks closed) can never be "corrected" into a stale state.
/// </summary>
public sealed class CouplerHardwareSync : IDisposable
{
    private const float ReconcileIntervalSeconds = 2.0f;

    private readonly NetClient _client;
    private readonly TrainSync _trains;
    private readonly Action<string> _log;

    private readonly HoseConnectionChangedDelegate _hoseHandler;
    private readonly MUCableConnectionChangedDelegate _muHandler;
    private readonly Dictionary<int, (TrainCar car, Action<bool> front, Action<bool> rear)> _cockHooks = new();
    private bool _applying;
    private float _accum;

    public CouplerHardwareSync(NetClient client, TrainSync trains, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _trains = trains ?? throw new ArgumentNullException(nameof(trains));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _hoseHandler = OnHoseChanged;
        _muHandler = OnMuChanged;
        Brakeset.HoseConnectionChanged += _hoseHandler;
        MultipleUnitCable.AnyConnectionChanged += _muHandler;
        _client.Trains.CouplerHardwareChanged += OnCommitted;
    }

    public void Tick(float dt)
    {
        if (!_client.Joined) return;
        _accum += dt;
        if (_accum < ReconcileIntervalSeconds) return;
        _accum = 0;

        // The game clears the static events on scene teardown (Brakeset resets them to null), which
        // would silently orphan a whole session's capture — re-arming each tick is idempotent.
        Brakeset.HoseConnectionChanged -= _hoseHandler;
        Brakeset.HoseConnectionChanged += _hoseHandler;
        MultipleUnitCable.AnyConnectionChanged -= _muHandler;
        MultipleUnitCable.AnyConnectionChanged += _muHandler;

        ResyncCockHooks();
        Reconcile();
    }

    // ── capture: DV event seams → report + optimistic fold ──

    private void OnHoseChanged(bool connected, HoseAndCock a, HoseAndCock b)
    {
        if (_applying) return;
        if (!TryEnd(a, out int carA, out CoupleEnd endA) || !TryEnd(b, out int carB, out CoupleEnd endB)) return;
        CouplerHardwareRegistry mirror = _client.Trains.Hardware;
        if (connected)
        {
            if (mirror.HoseAt(carA, endA) is (int pc, CoupleEnd pe) && pc == carB && pe == endB) return;
            Send(new CouplerHardwareReport(CouplerHardwareKind.HoseConnect, carA, endA, carB, endB));
        }
        else
        {
            if (mirror.HoseAt(carA, endA) is null) return;
            Send(new CouplerHardwareReport(CouplerHardwareKind.HoseDisconnect, carA, endA, carB, endB));
        }
    }

    private void OnMuChanged(bool connected, MultipleUnitCable a, MultipleUnitCable b)
    {
        if (_applying) return;
        if (!TryEnd(a, out int carA, out CoupleEnd endA) || !TryEnd(b, out int carB, out CoupleEnd endB)) return;
        CouplerHardwareRegistry mirror = _client.Trains.Hardware;
        if (connected)
        {
            if (mirror.MuAt(carA, endA) is (int pc, CoupleEnd pe) && pc == carB && pe == endB) return;
            Send(new CouplerHardwareReport(CouplerHardwareKind.MuConnect, carA, endA, carB, endB));
        }
        else
        {
            if (mirror.MuAt(carA, endA) is null) return;
            Send(new CouplerHardwareReport(CouplerHardwareKind.MuDisconnect, carA, endA, carB, endB));
        }
    }

    private void OnCockChanged(int carId, CoupleEnd end, bool open)
    {
        if (_applying) return;
        if (_client.Trains.Hardware.CockOpen(carId, end) == open) return;
        Send(new CouplerHardwareReport(CouplerHardwareKind.CockSet, carId, end, flag: open));
    }

    private void Send(in CouplerHardwareReport report)
    {
        _client.Trains.ReportCouplerHardware(report);
        // Optimistic: the commit broadcast excludes us (our world already did it), so the local
        // mirror folds now. A server refusal (adjacency/forgery class) logs host-side and the
        // canonical state reasserts on the next join/replay burst.
        _client.Trains.Hardware.ApplyCommitted(report);
    }

    // ── apply: committed lines → live replicas, immediately ──

    private void OnCommitted(CouplerHardwareReport report)
    {
        try
        {
            _applying = true;
            switch (report.Kind)
            {
                case CouplerHardwareKind.HoseConnect:
                {
                    HoseAndCock? a = LiveEnd(report.CarA, report.EndA);
                    HoseAndCock? b = LiveEnd(report.CarB, report.EndB);
                    if (a == null || b == null) return; // not spawned yet — the reconcile tick imposes later
                    if (a.IsHoseConnected || b.IsHoseConnected) return; // DV settles first; reconcile retries
                    a.Connect(b);
                    break;
                }
                case CouplerHardwareKind.HoseDisconnect:
                {
                    HoseAndCock? a = LiveEnd(report.CarA, report.EndA);
                    if (a != null && a.IsHoseConnected) a.Disconnect();
                    break;
                }
                case CouplerHardwareKind.CockSet:
                {
                    HoseAndCock? a = LiveEnd(report.CarA, report.EndA);
                    a?.SetCock(report.Flag);
                    break;
                }
                case CouplerHardwareKind.MuConnect:
                {
                    MultipleUnitCable? a = LiveCable(report.CarA, report.EndA);
                    MultipleUnitCable? b = LiveCable(report.CarB, report.EndB);
                    if (a == null || b == null || a.IsConnected || b.IsConnected) return;
                    a.Connect(b, playAudio: true);
                    break;
                }
                case CouplerHardwareKind.MuDisconnect:
                {
                    MultipleUnitCable? a = LiveCable(report.CarA, report.EndA);
                    if (a != null && a.IsConnected) a.Disconnect(playAudio: true);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            _log($"[trains] hardware apply failed ({report}): {e.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    // ── reconcile: impose mirror-present onto live cars; report local-present the mirror lacks ──

    private void Reconcile()
    {
        foreach (KeyValuePair<int, TrainCar> kv in LiveSessionCars())
        {
            ReconcileCar(kv.Key, kv.Value, CoupleEnd.Front);
            ReconcileCar(kv.Key, kv.Value, CoupleEnd.Rear);
        }
    }

    private void ReconcileCar(int carId, TrainCar car, CoupleEnd end)
    {
        CouplerHardwareRegistry mirror = _client.Trains.Hardware;
        try
        {
            HoseAndCock? hose = EndOf(car, end);
            if (hose != null)
            {
                (int car, CoupleEnd end)? mirrored = mirror.HoseAt(carId, end);
                if (hose.IsHoseConnected)
                {
                    if (TryEnd(hose.connectedTo, out int pc, out CoupleEnd pe) && mirrored is null)
                        Send(new CouplerHardwareReport(CouplerHardwareKind.HoseConnect, carId, end, pc, pe)); // seed
                }
                else if (mirrored is (int mc, CoupleEnd me))
                {
                    HoseAndCock? partner = LiveEnd(mc, me);
                    if (partner != null && !partner.IsHoseConnected)
                    {
                        _applying = true;
                        try { hose.Connect(partner); } finally { _applying = false; }
                    }
                }

                bool open = hose.cockOpen;
                bool mirroredOpen = mirror.CockOpen(carId, end);
                if (open && !mirroredOpen)
                {
                    Send(new CouplerHardwareReport(CouplerHardwareKind.CockSet, carId, end, flag: true)); // seed
                }
                else if (!open && mirroredOpen)
                {
                    _applying = true;
                    try { hose.SetCock(true); } finally { _applying = false; }
                }
            }

            MultipleUnitCable? cable = CableOf(car, end);
            if (cable != null)
            {
                (int car, CoupleEnd end)? mirrored = mirror.MuAt(carId, end);
                if (cable.IsConnected)
                {
                    if (TryEnd(cable.connectedTo, out int pc, out CoupleEnd pe) && mirrored is null)
                        Send(new CouplerHardwareReport(CouplerHardwareKind.MuConnect, carId, end, pc, pe)); // seed
                }
                else if (mirrored is (int mc, CoupleEnd me))
                {
                    MultipleUnitCable? partner = LiveCable(mc, me);
                    if (partner != null && !partner.IsConnected)
                    {
                        _applying = true;
                        try { cable.Connect(partner, playAudio: false); } finally { _applying = false; }
                    }
                }
            }
        }
        catch
        {
            // A car mid-despawn can null out under us — the next tick sees the settled world.
        }
    }

    // ── cock hooks (per-end instance events; maintained like CabControlSync's subscriptions) ──

    private void ResyncCockHooks()
    {
        List<int>? drop = null;
        foreach (KeyValuePair<int, (TrainCar car, Action<bool> front, Action<bool> rear)> kv in _cockHooks)
            if (kv.Value.car == null || !_trains.TryGetLiveCar(kv.Key, out _))
                (drop ??= new List<int>()).Add(kv.Key);
        if (drop != null)
            foreach (int carId in drop) UnhookCocks(carId);

        foreach (KeyValuePair<int, TrainCar> kv in LiveSessionCars())
        {
            if (_cockHooks.ContainsKey(kv.Key)) continue;
            TrainCar car = kv.Value;
            HoseAndCock? front = EndOf(car, CoupleEnd.Front);
            HoseAndCock? rear = EndOf(car, CoupleEnd.Rear);
            if (front == null || rear == null) continue;
            int carId = kv.Key;
            Action<bool> onFront = open => OnCockChanged(carId, CoupleEnd.Front, open);
            Action<bool> onRear = open => OnCockChanged(carId, CoupleEnd.Rear, open);
            front.CockChanged += onFront;
            rear.CockChanged += onRear;
            _cockHooks[carId] = (car, onFront, onRear);
        }
    }

    private void UnhookCocks(int carId)
    {
        if (!_cockHooks.TryGetValue(carId, out (TrainCar car, Action<bool> front, Action<bool> rear) entry)) return;
        _cockHooks.Remove(carId);
        if (entry.car == null) return;
        try
        {
            HoseAndCock? front = EndOf(entry.car, CoupleEnd.Front);
            HoseAndCock? rear = EndOf(entry.car, CoupleEnd.Rear);
            if (front != null) front.CockChanged -= entry.front;
            if (rear != null) rear.CockChanged -= entry.rear;
        }
        catch { /* car mid-destroy — the events die with it */ }
    }

    // ── resolution helpers ──

    private IEnumerable<KeyValuePair<int, TrainCar>> LiveSessionCars()
    {
        foreach (KeyValuePair<int, TrainCar> own in _trains.OwnBoundCars)
            if (own.Value != null) yield return own;
        foreach (KeyValuePair<int, TrainCar> remote in _trains.Remote.CarsByServerId)
            yield return remote;
    }

    private bool TryEnd(HoseAndCock? end, out int carId, out CoupleEnd coupleEnd)
    {
        carId = 0;
        coupleEnd = CoupleEnd.Front;
        if (end == null) return false;
        try
        {
            BrakeSystem sys = end.parentSystem;
            if (sys == null) return false;
            TrainCar car = sys.GetComponent<TrainCar>();
            if (car == null || !_trains.TryResolveCarId(car, out carId)) return false;
            coupleEnd = end.isFront ? CoupleEnd.Front : CoupleEnd.Rear;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryEnd(MultipleUnitCable? cable, out int carId, out CoupleEnd coupleEnd)
    {
        carId = 0;
        coupleEnd = CoupleEnd.Front;
        if (cable == null) return false;
        try
        {
            TrainCar? car = cable.muModule != null ? cable.muModule.train : null;
            if (car == null || !_trains.TryResolveCarId(car, out carId)) return false;
            coupleEnd = cable.isFront ? CoupleEnd.Front : CoupleEnd.Rear;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HoseAndCock? EndOf(TrainCar car, CoupleEnd end)
    {
        try
        {
            BrakeSystem? sys = car != null ? car.brakeSystem : null;
            if (sys == null) return null;
            return end == CoupleEnd.Front ? sys.Front : sys.Rear;
        }
        catch
        {
            return null;
        }
    }

    private static MultipleUnitCable? CableOf(TrainCar car, CoupleEnd end)
    {
        try
        {
            MultipleUnitModule? mu = car != null ? car.muModule : null;
            if (mu == null) return null;
            return end == CoupleEnd.Front ? mu.FrontCable : mu.RearCable;
        }
        catch
        {
            return null;
        }
    }

    private HoseAndCock? LiveEnd(int carId, CoupleEnd end) =>
        _trains.TryGetLiveCar(carId, out TrainCar car) ? EndOf(car, end) : null;

    private MultipleUnitCable? LiveCable(int carId, CoupleEnd end) =>
        _trains.TryGetLiveCar(carId, out TrainCar car) ? CableOf(car, end) : null;

    public void Dispose()
    {
        Brakeset.HoseConnectionChanged -= _hoseHandler;
        MultipleUnitCable.AnyConnectionChanged -= _muHandler;
        _client.Trains.CouplerHardwareChanged -= OnCommitted;
        foreach (int carId in new List<int>(_cockHooks.Keys)) UnhookCocks(carId);
    }
}
