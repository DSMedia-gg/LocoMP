using System;
using System.Collections.Generic;
using DV.OriginShift;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using UnityEngine;

// UnityEngine defines its own Pose struct; the alias pins the unqualified name to Core's wire type.
using Pose = LocoMP.Core.Presence.Pose;

namespace LocoMP.Shim;

/// <summary>
/// The M2.3 train seam: everything between live TrainCars and Core's train protocol.
/// Send side — the host registers every game trainset, streams spline-space snapshots for the sets
/// it simulates, translates the game's own coupler/derail/junction happenings into proposals
/// (public game events where they exist, the Harmony junction hook where they don't, and derail by
/// polling <c>TrainCar.derailed</c> per capture tick rather than guessing at the game's custom
/// delegate signatures). Receive side — remote commits move ghosts, junctions, and grant state.
/// The host's own game already performed its local physics; commits for our own sets only re-bind
/// bookkeeping, never re-apply physics.
/// </summary>
public sealed class TrainSync : IDisposable
{
    private const double StreamIntervalSeconds = 1.0 / 20; // matches the pose rate

    private readonly NetClient _client;
    private readonly bool _isHost;
    private readonly Action<string> _log;
    private readonly GhostConsists _ghosts;

    private TrackIndexMap? _map;
    private bool _worldRegistered;
    private double _streamAccum;

    // Server-assigned car ids ↔ live cars (car ids survive merges/splits, M2.1 design).
    private readonly Dictionary<int, TrainCar> _carById = new();
    private readonly Dictionary<TrainCar, int> _idByCar = new();
    private readonly Dictionary<int, int> _setByCarId = new();
    private readonly Dictionary<int, Binding> _bindings = new(); // serverSetId → cars we simulate
    private readonly HashSet<TrainCar> _hookedCars = new();
    private readonly HashSet<TrainCar> _despawning = new();
    private readonly Dictionary<TrainCar, Action> _destroyHooks = new();
    private readonly Dictionary<int, bool> _wasDerailed = new();
    private (int lo, int hi) _lastPair = (-1, -1);
    private bool _lastPairWasCouple;
    private float _lastPairTime;
    private TrainCar? _grantCar;
    private bool _carDeleteHooked;
    private bool _worldDeathAnnounced;
    private bool _emptyWorldLogged;
    private bool _botHintLogged;

    private sealed class Binding
    {
        public Binding(TrainsetDef def, TrainCar[] cars)
        {
            Def = def;
            Cars = cars;
        }

        public TrainsetDef Def;
        public readonly TrainCar[] Cars;
    }

    public TrainSync(NetClient client, bool isHost, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _isHost = isHost;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ghosts = new GhostConsists(log);

        client.Trains.View.TrainsetAdded += OnTrainsetAdded;
        client.Trains.View.TransactionApplied += OnTransaction;
        client.Trains.View.TrainsetRemoved += OnTrainsetRemoved;
        client.Trains.View.SnapshotApplied += OnSnapshot;
        client.Trains.TrainsetRegistered += OnRegistered;
        client.Trains.JunctionChanged += OnRemoteJunction;
        client.Trains.GrantChanged += OnGrantChanged;
        JunctionHook.Switched += OnLocalJunction;
        PlayerManager.CarChanged += OnPlayerCarChanged;
    }

    /// <summary>Fired once when the game world this session was built on is unloaded (quit to
    /// menu, save load). The session controller closes the session — a fresh host in the new world
    /// re-registers everything; limping along on destroyed objects renders invisible ghosts.</summary>
    public event Action? WorldUnloaded;

    /// <summary>The world our maps and bindings refer to still exists (the registry singleton dies
    /// with the world scene). Every proposal path checks this — teardown fires real game events
    /// (74 couplers uncoupling at once) that must not become protocol traffic.</summary>
    private static bool WorldAlive => RailTrackRegistryBase.Instance != null;

    /// <summary>Pump everything. Called from the session controller's Update.</summary>
    public void Tick(double dt)
    {
        if (_map != null && !WorldAlive)
        {
            if (!_worldDeathAnnounced)
            {
                _worldDeathAnnounced = true;
                _log("[trains] game world unloaded — this session's world is gone");
                WorldUnloaded?.Invoke();
            }
            return;
        }

        _map ??= TrackIndexMap.TryBuild(_log); // world may still be loading on the first ticks
        if (_map is null || !_client.Joined) return;

        if (!_carDeleteHooked && CarSpawner.Instance != null)
        {
            _carDeleteHooked = true;
            CarSpawner.Instance.CarAboutToBeDeleted += OnCarAboutToBeDeleted;
        }

        if (_isHost && !_worldRegistered) RegisterWorld();

        if (!_botHintLogged && _isHost)
        {
            // The topology file carries no world coordinates, so the bot cannot know which edge is
            // near the player — the host does. Same paste-me pattern as the --at line.
            Transform player = PlayerManager.PlayerTransform;
            if (player != null && _map.TryNearestEdge(player.position, out uint nearEdge, out float dist))
            {
                _botHintLogged = true;
                _log($"[trains] ghost-train hint: --start-edge {nearEdge}  (nearest edge, ~{dist:F0} m from you)");
            }
        }

        _streamAccum += dt;
        if (_streamAccum >= StreamIntervalSeconds)
        {
            _streamAccum = 0;
            CaptureAndStream();
        }

        _ghosts.Tick((float)dt);
    }

    // ── registration & bookkeeping ──

    /// <summary>The host offers every existing game trainset to the server (world source, 03 §3).
    /// The game trainset id (+1, to dodge the reserved 0) doubles as the correlation token.</summary>
    private void RegisterWorld()
    {
        List<Trainset> sets = Trainset.allSets;
        if (sets is null || sets.Count == 0)
        {
            // Hosting during the loading screen lands here — keep retrying until the cars exist.
            if (!_emptyWorldLogged)
            {
                _emptyWorldLogged = true;
                _log("[trains] world has no trainsets yet — will register once they spawn");
            }
            return;
        }

        _worldRegistered = true;
        int cars = 0;
        foreach (Trainset set in sets)
        {
            var specs = new CarDef[set.cars.Count];
            for (int i = 0; i < specs.Length; i++)
                specs[i] = new CarDef(0, KindOf(set.cars[i]), set.cars[i].derailed);
            _client.Trains.RegisterTrainset((uint)(set.id + 1), specs);
            cars += specs.Length;
        }
        _log($"[trains] registered {sets.Count} trainset(s) / {cars} car(s) with the session");
    }

    private static string KindOf(TrainCar car)
    {
        try { return car.carLivery != null ? car.carLivery.id : car.carType.ToString(); }
        catch { return "unknown"; }
    }

    private void OnRegistered(uint token, TrainsetDef def)
    {
        Trainset? gameSet = null;
        foreach (Trainset s in Trainset.allSets)
            if (s.id == (int)token - 1) { gameSet = s; break; }
        if (gameSet is null || gameSet.cars.Count != def.Cars.Count)
        {
            _log($"[trains] registration commit for token {token} no longer matches a live set — resync");
            _client.Trains.RequestResync(def.Id);
            return;
        }

        var cars = new TrainCar[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
        {
            cars[i] = gameSet.cars[i];
            _carById[def.Cars[i].Id] = cars[i];
            _idByCar[cars[i]] = def.Cars[i].Id;
            _wasDerailed[def.Cars[i].Id] = def.Cars[i].Derailed;
            HookCar(cars[i]);
        }
        Bind(def, cars);
    }

    private void Bind(TrainsetDef def, TrainCar[] cars)
    {
        _bindings[def.Id] = new Binding(def, cars);
        RebuildCarSetIndex();
    }

    private void RebuildCarSetIndex()
    {
        _setByCarId.Clear();
        foreach (Binding b in _bindings.Values)
            foreach (CarDef car in b.Def.Cars)
                _setByCarId[car.Id] = b.Def.Id;
    }

    /// <summary>CarSpawner's deletion notice — same handling as the per-car destroy hook.</summary>
    private void OnCarAboutToBeDeleted(TrainCar car)
    {
        if (car != null) OnCarGone(car);
    }

    private void OnTrainsetAdded(TrainsetDef def)
    {
        // Our own sets are wired via the registration token; anything else is remote → ghost it.
        if (def.OwnerId != _client.LocalId)
        {
            _ghosts.EnsureSet(def);
            _log($"[trains] remote consist {def.Id} ({def.Cars.Count} car(s), owner {def.OwnerId}) — ghost created");
        }
    }

    private void OnTransaction(TrainsetTransaction txn)
    {
        foreach (int retired in txn.RetiredIds)
        {
            _bindings.Remove(retired);
            _ghosts.Remove(retired);
        }

        foreach (TrainsetDef def in txn.Products)
        {
            if (def.OwnerId == _client.LocalId)
            {
                _ghosts.Remove(def.Id);
                var cars = new TrainCar[def.Cars.Count];
                bool complete = true;
                for (int i = 0; i < cars.Length; i++)
                {
                    if (!_carById.TryGetValue(def.Cars[i].Id, out TrainCar car) || car == null) { complete = false; break; }
                    cars[i] = car;
                }
                if (complete) Bind(def, cars);
                else if (WorldAlive) // during teardown every car is "unknown" — spamming resyncs helps nobody
                {
                    _log($"[trains] transaction product {def.Id} references unknown cars — resync");
                    _client.Trains.RequestResync(def.Id);
                }
            }
            else
            {
                _ghosts.EnsureSet(def);
            }
        }
        RebuildCarSetIndex();
    }

    private void OnTrainsetRemoved(int trainsetId)
    {
        _bindings.Remove(trainsetId);
        _ghosts.Remove(trainsetId);
        RebuildCarSetIndex();
    }

    // ── send side ──

    private void CaptureAndStream()
    {
        if (_map is null) return;
        foreach (Binding binding in _bindings.Values)
        {
            TrainsetDef def = binding.Def;

            // Derail transitions are polled for EVERY live car up front — inside the snapshot loop
            // one un-capturable car would break out before later cars were ever polled (the run-№4
            // L-039 derail went unreported that way).
            for (int i = 0; i < binding.Cars.Length; i++)
            {
                TrainCar car = binding.Cars[i];
                if (car != null) ReportDerailTransition(def, def.Cars[i].Id, car.derailed);
            }

            var snapCars = new CarSnapshot[binding.Cars.Length];
            bool valid = true;

            for (int i = 0; i < binding.Cars.Length && valid; i++)
            {
                TrainCar car = binding.Cars[i];
                if (car == null) { valid = false; break; }

                if (car.derailed)
                {
                    Vector3 abs = car.transform.position - OriginShift.currentMove;
                    Quaternion rot = car.transform.rotation;
                    snapCars[i] = CarSnapshot.OffRail(new Pose(abs.x, abs.y, abs.z, rot.x, rot.y, rot.z, rot.w));
                    continue;
                }

                Bogie front = car.FrontBogie, rear = car.RearBogie;
                if (front == null || rear == null || front.track == null || rear.track == null ||
                    front.traveller == null || rear.traveller == null)
                {
                    valid = false;
                    break;
                }
                _map.CalibrateFrom(front);
                if (!_map.TryGetEdgeId(front.track, out uint frontEdge) ||
                    !_map.TryGetEdgeId(rear.track, out uint rearEdge))
                {
                    valid = false;
                    break;
                }
                float speed = ForwardSpeed(car);
                snapCars[i] = CarSnapshot.Railed(
                    new BogieState(frontEdge, (float)front.traveller.Span, speed * front.TrackDirectionSign),
                    new BogieState(rearEdge, (float)rear.traveller.Span, speed * rear.TrackDirectionSign));
            }

            if (valid)
                _client.Trains.SendSnapshot(new TrainsetSnapshot(def.Id, def.Epoch, _client.EstimatedServerTimeMs, snapCars));
        }
    }

    private static float ForwardSpeed(TrainCar car)
    {
        try { return car.GetForwardSpeed(); }
        catch { return 0f; } // unavailable before bogies finish initializing
    }

    /// <summary>Derail state is POLLED at the capture rate: flipping true files the derail report,
    /// flipping back (comms-radio rerail) files the rerail request. The commit's epoch bump makes
    /// any in-flight old-epoch snapshot self-discarding — order here is not delicate.</summary>
    private void ReportDerailTransition(TrainsetDef def, int carId, bool derailedNow)
    {
        if (_wasDerailed.TryGetValue(carId, out bool was) && was == derailedNow) return;
        _wasDerailed[carId] = derailedNow;
        if (derailedNow)
        {
            _log($"[trains] car {carId} derailed — reporting");
            _client.Trains.ReportDerail(def.Id, new[] { carId });
        }
        else
        {
            _log($"[trains] car {carId} rerailed — requesting set rerail");
            _client.Trains.RequestRerail(def.Id);
        }
    }

    // ── game events → proposals ──

    private void HookCar(TrainCar car)
    {
        if (car == null || !_hookedCars.Add(car)) return;
        foreach (Coupler coupler in car.couplers)
        {
            if (coupler == null) continue;
            coupler.Coupled += OnCoupled;
            coupler.Uncoupled += OnUncoupled;
        }

        // The one signal that fires for EVERY way a car leaves the world — pooled deletion, scene
        // unload, AND distance-based entity conversion (the run-№3 storm: far consists' GameObjects
        // are destroyed mid-session while the scene stays loaded, cascading real Uncoupled events).
        Action onGone = () => OnCarGone(car);
        car.OnCarAboutToBeDestroyed += onGone;
        _destroyHooks[car] = onGone;
    }

    private void OnCarGone(TrainCar car)
    {
        _despawning.Add(car);
        if (_idByCar.TryGetValue(car, out int carId) && _setByCarId.TryGetValue(carId, out int setId) &&
            _bindings.Remove(setId))
        {
            _log($"[trains] set {setId} left the streamed world (car destroyed/converted) — unbound " +
                 "(respawn rebinding is M3's world-lifecycle work)");
        }
    }

    private void OnCoupled(object sender, CoupleEventArgs e)
    {
        if (!WorldAlive || IsDespawning(e.thisCoupler, e.otherCoupler)) return;
        if (!TryGetPair(e.thisCoupler, e.otherCoupler, out int carA, out int carB)) return;
        if (IsDuplicatePair(carA, carB, couple: true)) return;

        if (!TryTrainsetEnd(carA, out CoupleEnd endA) || !TryTrainsetEnd(carB, out CoupleEnd endB))
        {
            _log($"[trains] couple contact on a mid-set car ({carA}/{carB}) — ignored");
            return;
        }

        float relV = RelativeSpeed(e.thisCoupler.train, e.otherCoupler.train);
        _log($"[trains] couple contact: car {carA} ({endA}) + car {carB} ({endB}) at {relV:F1} m/s — proposing");
        _client.Trains.ProposeCouple(carA, endA, carB, endB, relV);
    }

    private void OnUncoupled(object sender, UncoupleEventArgs e)
    {
        if (!WorldAlive || IsDespawning(e.thisCoupler, e.otherCoupler)) return;
        if (!TryGetPair(e.thisCoupler, e.otherCoupler, out int carA, out int carB)) return;
        if (IsDuplicatePair(carA, carB, couple: false)) return;

        // Both cars must still be in the same mirrored set, adjacent in def order.
        if (!_setByCarId.TryGetValue(carA, out int setId) ||
            !_setByCarId.TryGetValue(carB, out int setB) || setId != setB) return;
        if (!_bindings.TryGetValue(setId, out Binding? binding)) return;
        int idxA = IndexOfCar(binding.Def, carA), idxB = IndexOfCar(binding.Def, carB);
        if (idxA < 0 || idxB < 0 || Math.Abs(idxA - idxB) != 1)
        {
            _log($"[trains] uncouple between non-adjacent cars {carA}/{carB} — ignored");
            return;
        }

        int gap = Math.Min(idxA, idxB);
        _log($"[trains] uncouple: set {setId} between index {gap} and {gap + 1} — proposing" +
             (e.dueToBrokenCouple ? " (broken coupling)" : ""));
        _client.Trains.ProposeUncouple(setId, gap);
    }

    /// <summary>One physical contact may raise the event on one coupler or both (unconfirmed which
    /// on B99.7 — the run-№4 couple test produced ZERO proposals under the old "lower id speaks"
    /// dedupe, so both-fire cannot be assumed). Handle every event; collapse repeats of the same
    /// unordered pair inside a short window instead.</summary>
    private bool IsDuplicatePair(int carA, int carB, bool couple)
    {
        (int lo, int hi) pair = (Math.Min(carA, carB), Math.Max(carA, carB));
        float now = Time.unscaledTime;
        if (pair == _lastPair && couple == _lastPairWasCouple && now - _lastPairTime < 0.5f) return true;
        _lastPair = pair;
        _lastPairWasCouple = couple;
        _lastPairTime = now;
        return false;
    }

    private bool IsDespawning(Coupler a, Coupler b) =>
        IsLeavingWorld(a?.train) || IsLeavingWorld(b?.train);

    /// <summary>True while a car is despawning OR its scene is unloading. Scene-unload destruction
    /// kills cars BEFORE the registry singleton (so WorldAlive still reads true) and never fires
    /// CarAboutToBeDeleted — `scene.isLoaded` is the signal that catches it (run №3 finding).</summary>
    private bool IsLeavingWorld(TrainCar? car)
    {
        if (car == null) return true;
        if (_despawning.Contains(car)) return true;
        try { return !car.gameObject.scene.isLoaded; }
        catch { return true; } // destroyed mid-teardown — definitely leaving
    }

    private bool TryGetPair(Coupler a, Coupler b, out int carA, out int carB)
    {
        carA = carB = 0;
        return a != null && b != null && a.train != null && b.train != null &&
               _idByCar.TryGetValue(a.train, out carA) && _idByCar.TryGetValue(b.train, out carB);
    }

    private bool TryTrainsetEnd(int carId, out CoupleEnd end)
    {
        end = CoupleEnd.Front;
        if (!_setByCarId.TryGetValue(carId, out int setId) ||
            !_bindings.TryGetValue(setId, out Binding? binding)) return false;
        int idx = IndexOfCar(binding.Def, carId);
        if (idx == 0) { end = CoupleEnd.Front; return true; }
        if (idx == binding.Def.Cars.Count - 1) { end = CoupleEnd.Rear; return true; }
        return false;
    }

    private static int IndexOfCar(TrainsetDef def, int carId)
    {
        for (int i = 0; i < def.Cars.Count; i++)
            if (def.Cars[i].Id == carId) return i;
        return -1;
    }

    private static float RelativeSpeed(TrainCar a, TrainCar b)
    {
        try
        {
            if (a.rb != null && b.rb != null) return (a.rb.velocity - b.rb.velocity).magnitude;
        }
        catch { /* fall through */ }
        return 0f;
    }

    // ── junctions ──

    private void OnLocalJunction(Junction junction, byte branch)
    {
        if (_map is null || !_map.TryGetJunctionId(junction, out uint id)) return;
        _client.Trains.ThrowJunction(id, branch);
    }

    private void OnRemoteJunction(uint junctionId, byte branch)
    {
        // Our own throws echo back here too; ApplyRemote no-ops when the branch already matches.
        if (_map != null && _map.TryGetJunction(junctionId, out Junction junction))
            JunctionHook.ApplyRemote(junction, branch);
    }

    // ── control grants (03 §3) ──

    private void OnPlayerCarChanged(TrainCar car)
    {
        if (_grantCar != null && _idByCar.TryGetValue(_grantCar, out int prevId))
            _client.Trains.ReleaseControlGrant(prevId);
        _grantCar = car;
        if (car != null && _idByCar.TryGetValue(car, out int carId))
        {
            _log($"[trains] entered car {carId} — requesting control grant");
            _client.Trains.RequestControlGrant(carId);
        }
    }

    private void OnGrantChanged(int carId, int holderId) =>
        _log($"[trains] control grant: car {carId} → {(holderId == 0 ? "free" : $"player {holderId}")}");

    // ── receive side ──

    private void OnSnapshot(TrainsetSnapshot snap)
    {
        if (_bindings.ContainsKey(snap.TrainsetId) || _map is null) return; // never re-apply our own sets
        _ghosts.Apply(snap, _map);
    }

    public void Dispose()
    {
        JunctionHook.Switched -= OnLocalJunction;
        PlayerManager.CarChanged -= OnPlayerCarChanged;
        if (_carDeleteHooked && CarSpawner.Instance != null)
            CarSpawner.Instance.CarAboutToBeDeleted -= OnCarAboutToBeDeleted;
        foreach (TrainCar car in _hookedCars)
        {
            if (car == null) continue;
            foreach (Coupler coupler in car.couplers)
            {
                if (coupler == null) continue;
                coupler.Coupled -= OnCoupled;
                coupler.Uncoupled -= OnUncoupled;
            }
            if (_destroyHooks.TryGetValue(car, out Action? onGone)) car.OnCarAboutToBeDestroyed -= onGone;
        }
        _hookedCars.Clear();
        _destroyHooks.Clear();
        _ghosts.Clear();
    }
}
