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
/// 3. REMOTE initiation. When the target is a REPLICA (not locally simulated — the predicate is
///    per-car since D21, not per-role: the host's radio on a parked replica used to run straight
///    into its own hardening, which was the "radio delete refused" finding), the confirm prefix
///    SUPPRESSES the local mutation and sends a <c>CommsActionRequest</c> (the ChainHook pattern).
///    The server routes it to the car's owner — or, for a PARKED target, acts itself (D21):
///    delete commits as a server retire, rerail claims the set for the initiator and routes the
///    command back to them (their adoption has landed first, same ordered channel). EVERY client
///    subscribes to the command executor now, guarded to own cars — a routed command must never
///    act on a replica. Fees still burn only through the world source (the host); a non-host
///    executor waives them with a log line — the server-side price table is banked follow-up
///    work. Remote summon is banked (spawning a new car at a remote location is a later slice).
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
    // Fee labels are captured at CONFIRM time for the same reason as the car id above: DV's CarRerailed /
    // CarDeleted events do not hand us a usable car (observed in-game 2026-07-27 — both fees logged
    // "rerail ?" / "clear ?"), and by then a deleted car is mid-teardown anyway. At confirm time the
    // controller still holds a live carToRerail/carToDelete, so read the plate there or never.
    private string _pendingRerailLabel = "";
    private string _pendingDeleteLabel = "";

    private bool _eventsHooked;
    private double _discoverAccum; // throttles the radio discovery scan (never per-frame — see Tick)
    private RerailController? _rerail;
    private CommsRadioCarDeleter? _deleter;
    private CommsRadioCrewVehicle? _summoner;

    private const double DiscoverIntervalSeconds = 1.0;

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
        // R3-3 scan assist: let the deleter highlight exactly the cars OnDeleteConfirm would route —
        // hardened replicas we track. Without it the native scan refuses them before highlight and
        // the D21 delete-as-retire is unreachable from the local radio (adopt-then-delete was the
        // only path). The server still owns the verdict; an ineligible target gets its refusal toast.
        CommsRadioHook.DeleteScanAssist = car =>
            car != null && !_trains.IsLocallySimulated(car) && _trains.TryResolveCarId(car, out _);

        // Every client executes comms actions the server routes to it for cars it simulates —
        // the host for its world, and any claimer for an adopted set (D21). OnCommanded guards
        // to own cars, so a stray command can never act on a replica.
        _client.Trains.CommsActionCommanded += OnCommanded;
        // R5-10: the server's comms refusals ride CareerRejected — without this line a refused
        // delete/rerail looks like nothing happened (Round 5: 9 invisible insufficient-funds
        // rejects while the player kept pressing the button).
        _client.Career.RequestRejected += OnServerRejected;
    }

    private void OnServerRejected(string reason, int _)
    {
        if (reason.StartsWith("comms:", StringComparison.Ordinal)
            || reason.StartsWith("retire:", StringComparison.Ordinal)
            || reason.StartsWith("rerail", StringComparison.Ordinal))
            _log($"[comms] server refused: {reason}");
    }

    /// <summary>Pump from the session loop: once the comms radio exists (world loaded), subscribe to
    /// the modes' success events on the HOST (a client's own actions are intercepted before they
    /// fire, so it never needs them).
    ///
    /// PERF: discovery is THROTTLED and anchors on the controller, never a per-frame scan. DV keeps
    /// only the currently-selected comms-radio mode active, so a <c>FindObjectOfType&lt;RerailController&gt;</c>
    /// misses the mode controllers whenever another mode is selected — run every frame (as this once
    /// was) that's three full-scene scans per frame that crater the host frame rate. Instead we scan
    /// at most once a second for the always-active <see cref="CommsRadioController"/> and read its
    /// public mode fields (populated even while a mode's GameObject is inactive), hook once, and never
    /// poll again — the event subscriptions survive the modes going inactive.</summary>
    public void Tick(double dt)
    {
        // Success events hook for EVERY role since D21: a guest deleting an ADOPTED own car must
        // send the same CarDeleteNotice the host does, or the room keeps a ghost. Fee capture
        // inside stays world-source-gated (ChargeSelf no-ops off-host).
        if (_eventsHooked || !_client.Joined) return;
        _discoverAccum += dt;
        if (_discoverAccum < DiscoverIntervalSeconds) return;
        _discoverAccum = 0;

        CommsRadioController? controller = Object.FindObjectOfType<CommsRadioController>();
        if (controller == null) return; // radio not active yet — try again next interval (cheap)

        _rerail = controller.rerailControl;
        _deleter = controller.deleteControl;
        _summoner = controller.crewVehicleControl;
        if (_rerail == null && _deleter == null && _summoner == null) return;

        if (_rerail != null) _rerail.CarRerailed += OnHostRerailed;
        if (_deleter != null) _deleter.CarDeleted += OnHostDeleted;
        if (_summoner != null) _summoner.CarSummoned += OnHostSummoned;
        _eventsHooked = true;
        _log("[comms] comms-radio success capture installed (rerail/delete/summon)");
    }

    // ── confirm-state filters (return true to let the native action proceed) ──

    private bool OnRerailConfirm(RerailController ctrl)
    {
        TrainCar car = ctrl.carToRerail;
        // The native path runs for cars WE simulate (and for unmapped pure-local cars — not our
        // concern). D21: the predicate is per-CAR, not per-role — the host's radio on a parked
        // replica routes exactly like a client's, instead of failing against its own hardening.
        if (car == null || _trains.IsLocallySimulated(car) || !_trains.TryResolveCarId(car, out int carId))
        {
            _pendingRerailPrice = ctrl.rerailPrice; // read before the deduction clears it
            _pendingRerailLabel = PlateOf(car);
            return true;
        }
        // Routed: the server prices this from its own table (R4-M). Clear the confirm-time
        // snapshot so a LATER native success can never bill a stale price.
        _pendingRerailPrice = 0f;
        _pendingRerailLabel = "";
        var rot = Quaternion.LookRotation(ctrl.rerailPointWorldForward);
        Vector3 abs = ctrl.rerailPointWorldAbsPosition; // already absolute (origin-shift-corrected)
        _client.Trains.RequestCommsAction(CommsActionKind.Rerail, carId,
            new Pose(abs.x, abs.y, abs.z, rot.x, rot.y, rot.z, rot.w));
        _log($"[comms] rerail of car {carId} routed (a parked target claims the set for you)");
        return false;
    }

    private bool OnDeleteConfirm(CommsRadioCarDeleter ctrl)
    {
        TrainCar car = ctrl.carToDelete;
        if (car == null || _trains.IsLocallySimulated(car) || !_trains.TryResolveCarId(car, out int carId))
        {
            _pendingDeletePrice = ctrl.removePrice;
            _pendingDeleteCarId = car != null && _trains.TryResolveCarId(car, out int id) ? id : 0;
            _pendingDeleteLabel = PlateOf(car);
            return true;
        }
        // Routed: the server bills its delete fee itself (R4-M) — clear the snapshot so a later
        // native success cannot bill a stale price on top.
        _pendingDeletePrice = 0f;
        _pendingDeleteCarId = 0;
        _pendingDeleteLabel = "";
        _client.Trains.RequestCommsAction(CommsActionKind.Delete, carId, Pose.Identity);
        _log($"[comms] delete of car {carId} routed (a parked target retires server-side)");
        return false;
    }

    private bool OnSummonConfirm(CommsRadioCrewVehicle ctrl)
    {
        // Remote summon is banked; the host snapshots the price, the client is never intercepted.
        if (_isHost) _pendingSummonPrice = ctrl.SummonPrice;
        return true;
    }

    // ── host success events → fee (own scope) + delete removal ──

    // The event's `car` is preferred when it is actually usable and ignored when it is not — DV supplies
    // nothing dependable here, so the confirm-time snapshot is what normally names the fee.
    private void OnHostRerailed(TrainCar car) =>
        ChargeSelf(_pendingRerailPrice, $"rerail {Label(car, _pendingRerailLabel)}");

    private void OnHostDeleted(TrainCar car)
    {
        ChargeSelf(_pendingDeletePrice, $"clear {Label(car, _pendingDeleteLabel)}");
        if (_pendingDeleteCarId != 0)
        {
            _client.Trains.NotifyCarDeleted(_pendingDeleteCarId);
            _log($"[comms] car {_pendingDeleteCarId} deleted — removing it from the session");
            _pendingDeleteCarId = 0;
        }
    }

    private void OnHostSummoned(TrainCar car) =>
        ChargeSelf(_pendingSummonPrice, $"summon {Label(car, "")}");

    /// <summary>Burn a comms-radio fee from the executor's OWN wallet (target 0). Skips free
    /// actions (handcar rerail, player-spawned delete, non-garage summon are all priced 0 by the
    /// game). Only the world source may report external fees (D14) — a non-host executor (a
    /// claimer acting on an adopted set, D21) waives the fee with a log line rather than spam the
    /// server with a doomed report; the server-side price table is the banked follow-up.</summary>
    private void ChargeSelf(float priceDollars, string label)
    {
        long cents = (long)Math.Round(priceDollars * 100.0);
        if (cents <= 0) return;
        if (!_isHost)
        {
            _log($"[comms] {label}: fee waived (${priceDollars:F2} — billing for non-host actions rides the server price table)");
            return;
        }
        _client.Career.ReportExternalFee(cents, label, 0);
        _log($"[comms] {label}: ${priceDollars:F2} charged to your wallet");
    }

    // ── execute a comms action the server routed here (we simulate the target car) ──

    private void OnCommanded(CommsActionKind kind, int carId, Pose dest, int initiator, bool serverBilled)
    {
        // Own cars ONLY: the server routes commands to the sim owner, and for the D21 rerail claim
        // the adoption landed on the same ordered channel just before this — a car that is still a
        // replica here means a stray or a mis-route, and acting on a replica is never correct.
        if (!_trains.TryGetOwnCar(carId, out TrainCar car) || car == null)
        {
            // D24 note: a server-billed command that dies here is the paid-but-failed edge the
            // route-time billing accepts (same as the parked rerail) — this line is its audit trail.
            _log($"[comms] remote {kind} for car {carId}: not simulated here (streamed out / not adopted) — ignored" +
                 (serverBilled ? " (initiator was server-billed)" : ""));
            return;
        }
        _commandServerBilled = serverBilled;
        switch (kind)
        {
            case CommsActionKind.Rerail: ExecuteRemoteRerail(car, carId, dest, initiator); break;
            case CommsActionKind.Delete: ExecuteRemoteDelete(car, carId, initiator); break;
        }
        _commandServerBilled = false;
    }

    /// <summary>True while executing a command the SERVER already billed (D24) — the executor's
    /// own charge must skip or the initiator pays twice.</summary>
    private bool _commandServerBilled;

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
        ChargeInitiator(RerailPrice(car, dist), $"rerail {Label(car, "")}", initiator);
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

    /// <summary>Bill a REMOTE-initiated action to the initiator (FeeExternal with their peer id).
    /// World-source-gated like <see cref="ChargeSelf"/> — a non-host executor waives it. A
    /// SELF-initiated command (the D21 parked-rerail claim-then-execute: the server claimed the set
    /// for us and routed the command back) is skipped everywhere: the server already billed its own
    /// fee table at claim time (R4-M), and reporting on top would double-bill the host case.</summary>
    private void ChargeInitiator(float priceDollars, string label, int initiator)
    {
        long cents = (long)Math.Round(priceDollars * 100.0);
        if (cents <= 0) return;
        if (_commandServerBilled)
        {
            // D24: the server burned its table fee at route/claim time — reporting on top would
            // double-bill (and a non-world-source report would bounce off the fee gate anyway).
            _log($"[comms] {label}: server-billed — no executor report");
            return;
        }
        if (!_isHost)
        {
            // Defensive: post-D24 the server bills every command routed to a non-host executor,
            // so an unbilled command here should not exist — log loudly rather than bill blind.
            _log($"[comms] {label}: UNBILLED command on a non-host executor (${priceDollars:F2}) — waived; this is a bug if it appears post-D24");
            return;
        }
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

    /// <summary>The car's plate (e.g. <c>L-013</c>), or "" when it cannot be read. <c>ID</c> reads through to
    /// the logic car, which is already unregistered by the time <c>CarDeleted</c> fires — so this throws
    /// precisely when we most want the name, which is why the plate is also snapshotted at confirm time.</summary>
    private static string PlateOf(TrainCar? car)
    {
        if (car == null) return "";
        try { return car.ID ?? ""; }
        catch { return ""; }
    }

    /// <summary>The Unity object name (e.g. <c>LocoDE6(Clone)</c>) — readable while the object lives, but a
    /// prefab name shared by every car of that type, so it identifies a MODEL and not a car.</summary>
    private static string NameOf(TrainCar car)
    {
        if (car == null) return "";
        try { return car.name ?? ""; }
        catch { return ""; }
    }

    /// <summary>Name a car for a fee label. Precedence matters and is the whole point: **any** real plate beats
    /// the object name, so the confirm-time snapshot is consulted BEFORE falling back to a prefab name. Getting
    /// this backwards silently defeats the snapshot — the first cut preferred the live car and fell straight to
    /// its object name, so fees read `clear LocoDE6(Clone)` while the correct plate sat unused in
    /// <paramref name="capturedPlate"/> (2026-07-27). A fee line that names a model rather than a car cannot be
    /// reconciled against what was actually charged.</summary>
    private static string Label(TrainCar car, string capturedPlate)
    {
        string live = PlateOf(car);
        if (live.Length > 0) return live;
        if (capturedPlate.Length > 0) return capturedPlate;
        string name = NameOf(car);
        return name.Length > 0 ? name : "?";
    }

    public void Dispose()
    {
        CommsRadioHook.RerailConfirm = null;
        CommsRadioHook.DeleteConfirm = null;
        CommsRadioHook.SummonConfirm = null;
        CommsRadioHook.DeleteScanAssist = null;
        _client.Trains.CommsActionCommanded -= OnCommanded;
        _client.Career.RequestRejected -= OnServerRejected;
        if (_eventsHooked)
        {
            if (_rerail != null) _rerail.CarRerailed -= OnHostRerailed;
            if (_deleter != null) _deleter.CarDeleted -= OnHostDeleted;
            if (_summoner != null) _summoner.CarSummoned -= OnHostSummoned;
        }
    }
}
