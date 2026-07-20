using System;
using DV;
using DV.OriginShift;
using DV.PointSet;
using DV.ThingTypes;
using DV.Utils;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using UnityEngine;
using Object = UnityEngine.Object;
using Pose = LocoMP.Core.Presence.Pose;

namespace LocoMP.Shim;

/// <summary>
/// M4 comms radio: rerail / delete / summon for ALL players, with their fees routed through the
/// LocoMP wallet. Three concerns, all hanging off <see cref="CommsRadioHook"/>'s OnUse prefixes plus
/// the modes' public success events:
///
/// 1. HOST fees. Each mode charges via a direct <c>Inventory.RemoveMoney</c>, which WalletMirror's
///    reconcile reverts (so the action would be FREE). The confirm-state prefix snapshots the game's
///    computed price; the mode's success event (CarRerailed/CarDeleted/CarSummoned) fires the fee as
///    a <c>FeeExternal</c> (target 0 = the host's own scope) so it burns through the ledger once.
///
/// 2. DELETE → removal. A native delete only unbinds locally in TrainSync (indistinguishable from a
///    distance stream-out), so the server keeps the set and clients keep a ghost. On CarDeleted the
///    host sends <c>NotifyCarDeleted</c> with the id snapshotted before the destroy, and the server
///    removes it everywhere.
///
/// 3. REMOTE initiation. On a joined client the target is a host-owned replica; the confirm prefix
///    SUPPRESSES the local mutation and sends a <c>CommsActionRequest</c> (the ChainHook pattern).
///    The server routes it to the car's owner (the host), which runs this handler's command executor:
///    it performs the real rerail/delete and charges the INITIATOR via <c>FeeExternal</c> with their
///    peer id. Remote summon is banked (spawning a new car at a remote location is a later slice).
/// </summary>
public sealed class CommsRadioSync : IDisposable
{
    private readonly NetClient _client;
    private readonly TrainSync _trains;
    private readonly bool _isHost;
    private readonly Action<string> _log;

    // Host: the price the confirm prefix read before the mode cleared it, consumed by the success event.
    private float _pendingRerailPrice;
    private float _pendingDeletePrice;
    private float _pendingSummonPrice;
    private int _pendingDeleteCarId; // captured before the destroy unbinds the car

    private bool _eventsHooked;
    private RerailController? _rerail;
    private CommsRadioCarDeleter? _deleter;
    private CommsRadioCrewVehicle? _summoner;

    public CommsRadioSync(NetClient client, TrainSync trains, bool isHost, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _trains = trains ?? throw new ArgumentNullException(nameof(trains));
        _isHost = isHost;
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // The confirm-state filters (CommsRadioHook calls these only in the CONFIRM state).
        CommsRadioHook.RerailConfirm = OnRerailConfirm;
        CommsRadioHook.DeleteConfirm = OnDeleteConfirm;
        CommsRadioHook.SummonConfirm = OnSummonConfirm;

        // The host executes comms actions remote players routed to it.
        if (_isHost) _client.Trains.CommsActionCommanded += OnCommanded;
    }

    /// <summary>Pump from the session loop: once the comms radio exists (world loaded), subscribe to
    /// the modes' success events on the HOST (a client's own actions are intercepted before they
    /// fire, so it never needs them).</summary>
    public void Tick(double dt)
    {
        if (!_isHost || _eventsHooked || !_client.Joined) return;
        _rerail = Object.FindObjectOfType<RerailController>();
        _deleter = Object.FindObjectOfType<CommsRadioCarDeleter>();
        _summoner = Object.FindObjectOfType<CommsRadioCrewVehicle>();
        if (_rerail == null && _deleter == null && _summoner == null) return; // radio not up yet

        if (_rerail != null) _rerail.CarRerailed += OnHostRerailed;
        if (_deleter != null) _deleter.CarDeleted += OnHostDeleted;
        if (_summoner != null) _summoner.CarSummoned += OnHostSummoned;
        _eventsHooked = true;
        _log("[comms] host comms-radio fee capture installed (rerail/delete/summon)");
    }

    // ── confirm-state filters (return true to let the native action proceed) ──

    private bool OnRerailConfirm(RerailController ctrl)
    {
        if (_isHost)
        {
            _pendingRerailPrice = ctrl.rerailPrice; // read before the deduction clears it
            return true;
        }
        // Client: the car is a host-owned replica — route the rerail to its owner, suppress locally.
        TrainCar car = ctrl.carToRerail;
        if (car == null || !_trains.TryResolveCarId(car, out int carId)) return true;
        var rot = Quaternion.LookRotation(ctrl.rerailPointWorldForward);
        Vector3 abs = ctrl.rerailPointWorldAbsPosition; // already absolute (origin-shift-corrected)
        _client.Trains.RequestCommsAction(CommsActionKind.Rerail, carId,
            new Pose(abs.x, abs.y, abs.z, rot.x, rot.y, rot.z, rot.w));
        _log($"[comms] rerail of car {carId} routed to its owner (you pay the fee)");
        return false;
    }

    private bool OnDeleteConfirm(CommsRadioCarDeleter ctrl)
    {
        if (_isHost)
        {
            _pendingDeletePrice = ctrl.removePrice;
            _pendingDeleteCarId = _trains.TryResolveCarId(ctrl.carToDelete, out int id) ? id : 0;
            return true;
        }
        TrainCar car = ctrl.carToDelete;
        if (car == null || !_trains.TryResolveCarId(car, out int carId)) return true;
        _client.Trains.RequestCommsAction(CommsActionKind.Delete, carId, Pose.Identity);
        _log($"[comms] delete of car {carId} routed to its owner (you pay the fee)");
        return false;
    }

    private bool OnSummonConfirm(CommsRadioCrewVehicle ctrl)
    {
        // Remote summon is banked; the host snapshots the price, the client is never intercepted.
        if (_isHost) _pendingSummonPrice = ctrl.SummonPrice;
        return true;
    }

    // ── host success events → fee (own scope) + delete removal ──

    private void OnHostRerailed(TrainCar car) =>
        ChargeSelf(_pendingRerailPrice, $"rerail {SafeId(car)}");

    private void OnHostDeleted(TrainCar car)
    {
        ChargeSelf(_pendingDeletePrice, $"clear {SafeId(car)}");
        if (_pendingDeleteCarId != 0)
        {
            _client.Trains.NotifyCarDeleted(_pendingDeleteCarId);
            _log($"[comms] car {_pendingDeleteCarId} deleted — removing it from the session");
            _pendingDeleteCarId = 0;
        }
    }

    private void OnHostSummoned(TrainCar car) =>
        ChargeSelf(_pendingSummonPrice, $"summon {SafeId(car)}");

    /// <summary>Burn a comms-radio fee from the host's OWN wallet (target 0). Skips free actions
    /// (handcar rerail, player-spawned delete, non-garage summon are all priced 0 by the game).</summary>
    private void ChargeSelf(float priceDollars, string label)
    {
        long cents = (long)Math.Round(priceDollars * 100.0);
        if (cents <= 0) return;
        _client.Career.ReportExternalFee(cents, label, 0);
        _log($"[comms] {label}: ${priceDollars:F2} charged to your wallet");
    }

    // ── host: execute a comms action a remote player routed here ──

    private void OnCommanded(CommsActionKind kind, int carId, Pose dest, int initiator)
    {
        if (!_trains.TryGetLiveCar(carId, out TrainCar car) || car == null)
        {
            _log($"[comms] remote {kind} for car {carId}: no live car here (streamed out?) — ignored");
            return;
        }
        switch (kind)
        {
            case CommsActionKind.Rerail: ExecuteRemoteRerail(car, carId, dest, initiator); break;
            case CommsActionKind.Delete: ExecuteRemoteDelete(car, carId, initiator); break;
        }
    }

    private void ExecuteRemoteRerail(TrainCar car, int carId, Pose dest, int initiator)
    {
        if (!car.IsRerailAllowed)
        {
            _log($"[comms] remote rerail of car {carId}: not derailed / still moving — ignored");
            return;
        }
        Vector3 worldPos = PresenceShim.ToLocalPosition(dest);       // absolute → current world space
        Vector3 forward = PresenceShim.ToRotation(dest) * Vector3.forward;
        if (!TryFindRerailTrack(worldPos, out RailTrack track, out Vector3 point, out Vector3 pointFwd))
        {
            _log($"[comms] remote rerail of car {carId}: no track near the destination — ignored");
            return;
        }
        // Keep the requested facing when the track agrees, else follow the track's own direction.
        if (Vector3.Dot(pointFwd, forward) < 0f) pointFwd = -pointFwd;
        float dist = Vector3.Distance(car.transform.position, point);
        try { car.Rerail(track, point, pointFwd); }
        catch (Exception e) { _log($"[comms] remote rerail of car {carId} failed: {e.Message}"); return; }
        // The car's derailed flag clears → TrainSync's poll files the set rerail. We just bill it.
        ChargeInitiator(RerailPrice(car, dist), $"rerail {SafeId(car)}", initiator);
        _log($"[comms] rerailed car {carId} for player {initiator}");
    }

    private void ExecuteRemoteDelete(TrainCar car, int carId, int initiator)
    {
        if (car.preventDelete)
        {
            _log($"[comms] remote delete of car {carId}: car forbids deletion — ignored");
            return;
        }
        float price = DeletePrice(car);
        try { SingletonBehaviour<CarSpawner>.Instance.DeleteCar(car); }
        catch (Exception e) { _log($"[comms] remote delete of car {carId} failed: {e.Message}"); return; }
        _client.Trains.NotifyCarDeleted(carId);
        ChargeInitiator(price, "clear a car", initiator);
        _log($"[comms] deleted car {carId} for player {initiator} — removed from the session");
    }

    /// <summary>Bill a REMOTE-initiated action to the initiator (FeeExternal with their peer id).</summary>
    private void ChargeInitiator(float priceDollars, string label, int initiator)
    {
        long cents = (long)Math.Round(priceDollars * 100.0);
        if (cents <= 0) return;
        _client.Career.ReportExternalFee(cents, label, initiator);
    }

    // ── price formulas (reimplemented from observed game behaviour — clean-room, our own code) ──

    private static float RerailPrice(TrainCar car, float distance)
    {
        if (car.carType == TrainCarType.HandCar) return 0f;
        float cap = Globals.G.GameParams.RerailMaxPrice;
        return Mathf.RoundToInt(Mathf.Clamp(500f + distance * 150f, 0f, cap));
    }

    private static float DeletePrice(TrainCar car) =>
        car.playerSpawnedCar ? 0f : Mathf.RoundToInt(Globals.G.GameParams.DeleteCarMaxPrice);

    /// <summary>Find a rail track carrying a valid point within 3 m of a world position, expanding to
    /// a wider snap if needed — a trimmed version of the game's own rerail track search.</summary>
    private static bool TryFindRerailTrack(Vector3 worldPos, out RailTrack track, out Vector3 point, out Vector3 forward)
    {
        track = null!;
        point = worldPos;
        forward = Vector3.forward;
        RailTrackRegistryBase registry = RailTrackRegistryBase.Instance;
        if (registry == null || registry.AllTracks == null) return false;
        foreach (float radius in new[] { 3f, 8f, 20f })
        {
            foreach (RailTrack t in registry.AllTracks)
            {
                if (t == null) continue;
                EquiPointSet.Point? p = RailTrack.GetPointWithinRangeWithYOffset(t, worldPos, radius, -1.75f);
                if (!p.HasValue) continue;
                track = t;
                // The point set is origin-shift-corrected (absolute); Rerail wants a world position.
                point = (Vector3)p.Value.position + OriginShift.currentMove;
                forward = p.Value.forward;
                return true;
            }
        }
        return false;
    }

    private static string SafeId(TrainCar car)
    {
        try { return car != null ? car.ID : "?"; }
        catch { return "?"; }
    }

    public void Dispose()
    {
        CommsRadioHook.RerailConfirm = null;
        CommsRadioHook.DeleteConfirm = null;
        CommsRadioHook.SummonConfirm = null;
        if (_isHost) _client.Trains.CommsActionCommanded -= OnCommanded;
        if (_eventsHooked)
        {
            if (_rerail != null) _rerail.CarRerailed -= OnHostRerailed;
            if (_deleter != null) _deleter.CarDeleted -= OnHostDeleted;
            if (_summoner != null) _summoner.CarSummoned -= OnHostSummoned;
        }
    }
}
