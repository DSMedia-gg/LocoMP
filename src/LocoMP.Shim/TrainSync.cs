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
    private readonly RealCarSync _remote;

    private TrackIndexMap? _map;
    private bool _worldRegistered;
    private bool _worldCleared;
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

    // M3.5c: mid-session registration scan (job spawns, summons, streamed-back consists) and live
    // cargo polling (same no-delegate-guessing posture as derail polling). NOTE: there is
    // deliberately NO cull of native spawns on joined clients — deleting what the world spawns is
    // a fight we cannot win (station loco spawners and restoration controllers respawn their
    // locos every frame; run C melted twice proving it). Native client-side spawns coexist
    // unsynced; real world suppression is the dedicated server's job (M6).
    private double _scanAccum;
    private double _cargoAccum;
    private readonly HashSet<int> _pendingNewSets = new();
    private readonly Dictionary<int, (string cargo, float amount)> _lastCargo = new();

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
        _remote = new RealCarSync(log);

        client.Trains.View.TrainsetAdded += OnTrainsetAdded;
        client.Trains.View.TransactionApplied += OnTransaction;
        client.Trains.View.TrainsetRemoved += OnTrainsetRemoved;
        client.Trains.View.SnapshotApplied += OnSnapshot;
        client.Trains.TrainsetRegistered += OnRegistered;
        client.Trains.JunctionChanged += OnRemoteJunction;
        client.Trains.GrantChanged += OnGrantChanged;
        client.Trains.CargoChanged += OnRemoteCargo;
        client.Trains.CoupleRequested += OnCoupleRequested;
        client.Trains.UncoupleRequested += OnUncoupleRequested;
        JunctionHook.Switched += OnLocalJunction;
        PlayerManager.CarChanged += OnPlayerCarChanged;
        ChainHook.CoupleFilter = FilterChainCouple;
        ChainHook.UncoupleFilter = FilterChainUncouple;
    }

    /// <summary>The remote-car half — exposed for the cab-control mirror (M3.5c).</summary>
    public RealCarSync Remote => _remote;

    /// <summary>Live cars WE simulate, with their server car ids (cab-control capture).</summary>
    public IEnumerable<KeyValuePair<int, TrainCar>> OwnBoundCars
    {
        get
        {
            foreach (Binding b in _bindings.Values)
                for (int i = 0; i < b.Cars.Length; i++)
                    if (b.Cars[i] != null) yield return new KeyValuePair<int, TrainCar>(b.Def.Cars[i].Id, b.Cars[i]);
        }
    }

    /// <summary>Server car id for OWN bound cars and remotely-spawned cars alike.</summary>
    public bool TryResolveCarId(TrainCar car, out int carId) => TryAnyCarId(car, out carId);

    /// <summary>A live car by server id — own bound cars first, then remote replicas.</summary>
    public bool TryGetLiveCar(int carId, out TrainCar car)
    {
        if (_carById.TryGetValue(carId, out car!) && car != null) return true;
        return _remote.TryGetCarByServerId(carId, out car);
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
        if (!_isHost && !_worldCleared) ClearNativeWorld();

        if (_isHost && _worldRegistered)
        {
            _scanAccum += dt;
            if (_scanAccum >= 2.0)
            {
                _scanAccum = 0;
                ScanForUnboundSets();
            }
        }
        _cargoAccum += dt;
        if (_cargoAccum >= 1.0)
        {
            _cargoAccum = 0;
            PollCargo();
        }

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

        _remote.Tick((float)dt);
    }

    /// <summary>M3.5b, joined clients only: the local SP world's own cars are NOT the session's
    /// cars — clear them so the host's consists (spawned as real cars) are the only trains in the
    /// world. The player's own savegame is protected by <see cref="SaveSuppressor"/>; leaving the
    /// session means reloading the save to get the own world back.</summary>
    private void ClearNativeWorld()
    {
        CarSpawner spawner = CarSpawner.Instance;
        if (spawner == null || spawner.AllCars == null) return;
        _worldCleared = true;

        var mine = new List<TrainCar>();
        foreach (TrainCar car in spawner.AllCars)
        {
            if (car != null && !_remote.IsRemoteCar(car)) mine.Add(car);
        }
        if (mine.Count == 0) return;

        if (PlayerManager.Car != null)
            _log("[trains] WARNING: you are inside one of your own cars — it is part of the world being cleared");
        try
        {
            spawner.DeleteTrainCarsInstant(mine);
            _log($"[trains] cleared {mine.Count} local car(s) — this session runs the host's world " +
                 "(reload your save after leaving to restore your own)");
        }
        catch (Exception e)
        {
            _log($"[trains] local world clear failed: {e.Message}");
        }
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
        var liveryHint = new List<string>();
        foreach (Trainset set in sets)
        {
            CarDef[] specs = BuildSpecs(set);
            foreach (CarDef spec in specs)
                if (liveryHint.Count < 3 && !liveryHint.Contains(spec.Kind)) liveryHint.Add(spec.Kind);
            _client.Trains.RegisterTrainset((uint)(set.id + 1), specs);
            cars += specs.Length;
        }
        _log($"[trains] registered {sets.Count} trainset(s) / {cars} car(s) with the session");
        if (liveryHint.Count > 0)
            // Real livery ids straight from this world — paste into the bot so ITS consist spawns
            // as real cars here (a bot without --livery keeps the old ghost boxes).
            _log($"[trains] bot livery hint: --livery {string.Join(",", liveryHint.ToArray())}");
    }

    private static CarDef[] BuildSpecs(Trainset set)
    {
        var specs = new CarDef[set.cars.Count];
        for (int i = 0; i < specs.Length; i++)
        {
            TrainCar car = set.cars[i];
            (string cargoId, float cargoAmount) = CargoOf(car);
            specs[i] = new CarDef(0, KindOf(car), car.derailed,
                SafeId(car), SafeGuid(car), cargoId, cargoAmount);
        }
        return specs;
    }

    /// <summary>Mid-session sweep (M3.5c): any live trainset whose cars are ALL unknown either
    /// streamed back in (re-bind it to its still-live server def by car GameIds — registering it
    /// again would duplicate the set) or is genuinely new (job chain spawns, crew vehicle summons —
    /// register it). One-scan settle so half-spawned consists aren't caught mid-assembly.</summary>
    private void ScanForUnboundSets()
    {
        List<Trainset> sets = Trainset.allSets;
        if (sets is null) return;
        foreach (Trainset set in sets)
        {
            if (set?.cars == null || set.cars.Count == 0) continue;
            bool skip = false;
            foreach (TrainCar car in set.cars)
            {
                if (car == null || IsLeavingWorld(car) || _idByCar.ContainsKey(car) || _remote.IsRemoteCar(car))
                {
                    skip = true;
                    break;
                }
            }
            if (skip) { _pendingNewSets.Remove(set.id); continue; }
            if (_pendingNewSets.Add(set.id)) continue; // first sighting — settle one scan
            _pendingNewSets.Remove(set.id);

            if (TryRebindStreamedSet(set)) continue;
            _client.Trains.RegisterTrainset((uint)(set.id + 1), BuildSpecs(set));
            _log($"[trains] mid-session consist registered ({set.cars.Count} car(s)) — job spawn or summon");
        }
    }

    /// <summary>DV's distance streaming destroyed this consist's GameObjects earlier (we unbound
    /// it) and has now rebuilt them: new TrainCar objects, same game identity. Match against our
    /// own still-live server defs by car GameId and re-bind instead of re-registering.</summary>
    private bool TryRebindStreamedSet(Trainset set)
    {
        var byGameId = new Dictionary<string, TrainCar>(StringComparer.Ordinal);
        foreach (TrainCar car in set.cars)
        {
            string id = SafeId(car);
            if (id.Length == 0 || byGameId.ContainsKey(id)) return false; // identity unusable
            byGameId[id] = car;
        }

        foreach (TrainsetDef def in _client.Trains.View.Sets.Values)
        {
            if (_bindings.ContainsKey(def.Id) || def.OwnerId != _client.LocalId) continue;
            if (def.Cars.Count != set.cars.Count) continue;
            bool all = true;
            foreach (CarDef spec in def.Cars)
                if (spec.GameId.Length == 0 || !byGameId.ContainsKey(spec.GameId)) { all = false; break; }
            if (!all) continue;

            var cars = new TrainCar[def.Cars.Count];
            for (int i = 0; i < cars.Length; i++)
            {
                cars[i] = byGameId[def.Cars[i].GameId];
                _carById[def.Cars[i].Id] = cars[i]; // the destroyed incarnation's entry dies here
                _idByCar[cars[i]] = def.Cars[i].Id;
                _wasDerailed[def.Cars[i].Id] = cars[i].derailed;
                HookCar(cars[i]);
            }
            Bind(def, cars);
            _log($"[trains] consist {def.Id} streamed back in — re-bound to its {cars.Length} live car(s)");
            return true;
        }
        return false;
    }

    /// <summary>Cargo transitions are POLLED like derail transitions (no guessing at the game's
    /// delegate signatures). First sight of a car is the baseline — registration already carried
    /// its load; only CHANGES are announced.</summary>
    private void PollCargo()
    {
        foreach (Binding binding in _bindings.Values)
        {
            for (int i = 0; i < binding.Cars.Length; i++)
            {
                TrainCar car = binding.Cars[i];
                if (car == null) continue;
                int carId = binding.Def.Cars[i].Id;
                (string cargo, float amount) now = CargoOf(car);
                if (_lastCargo.TryGetValue(carId, out (string cargo, float amount) was))
                {
                    if (was.cargo == now.cargo && was.amount == now.amount) continue;
                    _lastCargo[carId] = now;
                    _client.Trains.SendCargoState(carId, now.cargo, now.amount);
                    _log($"[trains] car {carId} cargo → {(now.cargo.Length == 0 ? "empty" : $"{now.cargo} ({now.amount:F0})")} — announced");
                }
                else
                {
                    _lastCargo[carId] = now; // baseline
                }
            }
        }
    }

    private static string KindOf(TrainCar car)
    {
        try { return car.carLivery != null ? car.carLivery.id : car.carType.ToString(); }
        catch { return "unknown"; }
    }

    private static string SafeId(TrainCar car)
    {
        try { return car.ID ?? ""; }
        catch { return ""; }
    }

    private static string SafeGuid(TrainCar car)
    {
        try { return car.CarGUID ?? ""; }
        catch { return ""; }
    }

    /// <summary>Registration-time cargo as a (v2 id, amount) pair; empty when the car carries
    /// nothing. Live load/unload sync is a banked M3.5c debt.</summary>
    private static (string, float) CargoOf(TrainCar car)
    {
        try
        {
            DV.Logic.Job.Car? logic = car.logicCar;
            if (logic == null) return ("", 0f);
            DV.ThingTypes.CargoType type = logic.CurrentCargoTypeInCar;
            if (type == DV.ThingTypes.CargoType.None) return ("", 0f);
            if (!DV.Globals.G.Types.CargoType_to_v2.TryGetValue(type, out DV.ThingTypes.CargoType_v2 v2) || v2 == null)
                return ("", 0f);
            return (v2.id, logic.LoadedCargoAmount);
        }
        catch
        {
            return ("", 0f);
        }
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
        // Our own sets are wired via the registration token; anything else is remote → real cars
        // (spawned on the first admitted snapshot; ghost boxes when a livery can't resolve).
        if (def.OwnerId != _client.LocalId)
        {
            _remote.EnsureSet(def);
            _log($"[trains] remote consist {def.Id} ({def.Cars.Count} car(s), owner {def.OwnerId}) — spawning on first snapshot");
        }
    }

    private void OnTransaction(TrainsetTransaction txn)
    {
        var remoteProducts = new List<TrainsetDef>();
        foreach (int retired in txn.RetiredIds) _bindings.Remove(retired);

        foreach (TrainsetDef def in txn.Products)
        {
            if (def.OwnerId == _client.LocalId)
            {
                _remote.Remove(def.Id);
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
                remoteProducts.Add(def);
            }
        }
        // One holistic pass over the remote half: cars survive merges/splits by server car id.
        _remote.ApplyTransaction(txn.RetiredIds, remoteProducts);
        RebuildCarSetIndex();
    }

    private void OnTrainsetRemoved(int trainsetId)
    {
        _bindings.Remove(trainsetId);
        _remote.Remove(trainsetId);
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

        // A LOCAL car physically chained to a REMOTE-driven car is still reverted (the remote car
        // is kinematic — a silent half-couple would anchor the local train to a teleporting body,
        // the incumbent's snap-back shape), but since M3.5c the INTENT is routed to the consist's
        // simulating player as a couple request. Mostly a backstop: the ChainHook prefix
        // intercepts chain interactions BEFORE the physical couple ever happens.
        TrainCar? local = e.thisCoupler?.train, other = e.otherCoupler?.train;
        if (local != null && other != null &&
            _remote.IsRemoteCar(other) != _remote.IsRemoteCar(local) &&
            (_idByCar.ContainsKey(local) || _idByCar.ContainsKey(other) || !_isHost))
        {
            _log("[trains] physical couple with a remote-driven car — reverting and routing as a request");
            try { e.thisCoupler!.Uncouple(playAudio: false, calledOnOtherCoupler: false, dueToBrokenCouple: false, viaChainInteraction: false); }
            catch { /* best effort — the def-side membership was never touched */ }
            if (TryAnyCarId(local, out int reqA) && TryAnyCarId(other, out int reqB))
                _client.Trains.RequestCouple(reqA, CouplerEnd(e.thisCoupler!), reqB, CouplerEnd(e.otherCoupler!));
            return;
        }

        if (!TryGetPair(e.thisCoupler!, e.otherCoupler!, out int carA, out int carB)) return;
        if (IsDuplicatePair(carA, carB, couple: true)) return;

        if (!TryTrainsetEnd(carA, out CoupleEnd endA) || !TryTrainsetEnd(carB, out CoupleEnd endB))
        {
            _log($"[trains] couple contact on a mid-set car ({carA}/{carB}) — ignored");
            return;
        }

        float relV = RelativeSpeed(e.thisCoupler!.train, e.otherCoupler!.train);
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

    // ── couple/uncouple requests (M3.5c) ──

    private static CoupleEnd CouplerEnd(Coupler coupler) =>
        coupler.isFrontCoupler ? CoupleEnd.Front : CoupleEnd.Rear;

    /// <summary>ChainHook filter: a chain tighten involving a remote-driven car becomes a request
    /// to its simulating player instead of a local physical couple. Pure-local chains (and
    /// anything outside a session) pass through to the native path untouched.</summary>
    private bool FilterChainCouple(Coupler mine, Coupler? theirs)
    {
        if (!_client.Joined || theirs == null) return true;
        TrainCar a = mine.train, b = theirs.train;
        if (a == null || b == null) return true;
        if (!_remote.IsRemoteCar(a) && !_remote.IsRemoteCar(b)) return true;
        if (!TryAnyCarId(a, out int carA) || !TryAnyCarId(b, out int carB)) return true;

        _client.Trains.RequestCouple(carA, CouplerEnd(mine), carB, CouplerEnd(theirs));
        _log($"[trains] chain couple involves a remote-driven car — asked its simulating player (cars {carA}+{carB})");
        return false;
    }

    /// <summary>ChainHook filter: a chain loosen on a remote-driven car becomes an uncouple
    /// request; the commit arrives as a split transaction and RepairCouplings follows it.</summary>
    private bool FilterChainUncouple(Coupler mine)
    {
        if (!_client.Joined) return true;
        TrainCar car = mine.train;
        if (car == null || !_remote.IsRemoteCar(car)) return true;
        if (!TryAnyCarId(car, out int carId)) return true;

        _client.Trains.RequestUncouple(carId, CouplerEnd(mine));
        _log($"[trains] chain uncouple on a remote-driven car — asked its simulating player (car {carId})");
        return false;
    }

    /// <summary>A remote player physically chained one of OUR cars — perform the real couple; the
    /// native Coupled event then proposes the merge (the normal owner path, one authority chain).</summary>
    private void OnCoupleRequested(int carA, CoupleEnd endA, int carB, CoupleEnd endB)
    {
        if (!WorldAlive) return;
        if (!TryGetLiveCar(carA, out TrainCar a) || !TryGetLiveCar(carB, out TrainCar b))
        {
            _log($"[trains] remote couple request for unknown cars {carA}/{carB} — ignored");
            return;
        }
        Coupler? mine = endA == CoupleEnd.Front ? a.frontCoupler : a.rearCoupler;
        Coupler? theirs = endB == CoupleEnd.Front ? b.frontCoupler : b.rearCoupler;
        if (mine == null || theirs == null) return;
        if (mine.IsCoupled() || theirs.IsCoupled())
        {
            _log($"[trains] remote couple request {carA}+{carB}: a coupler is already coupled — ignored");
            return;
        }
        if ((mine.transform.position - theirs.transform.position).sqrMagnitude > 12f * 12f)
        {
            _log($"[trains] remote couple request {carA}+{carB}: couplers too far apart — ignored");
            return;
        }
        _log($"[trains] remote couple request: coupling car {carA} ({endA}) to car {carB} ({endB})");
        try { mine.CoupleTo(theirs, playAudio: true, viaChainInteraction: false); }
        catch (Exception ex) { _log($"[trains] remote couple failed: {ex.Message}"); }
    }

    /// <summary>A remote player physically unhooked one of OUR couplers — perform the real
    /// uncouple; the native Uncoupled event then proposes the split.</summary>
    private void OnUncoupleRequested(int carId, CoupleEnd end)
    {
        if (!WorldAlive || !TryGetLiveCar(carId, out TrainCar car)) return;
        Coupler? coupler = end == CoupleEnd.Front ? car.frontCoupler : car.rearCoupler;
        if (coupler == null || !coupler.IsCoupled()) return;
        _log($"[trains] remote uncouple request: car {carId} ({end})");
        try { coupler.Uncouple(playAudio: true, calledOnOtherCoupler: false, dueToBrokenCouple: false, viaChainInteraction: false); }
        catch (Exception ex) { _log($"[trains] remote uncouple failed: {ex.Message}"); }
    }

    /// <summary>Owner-announced cargo change on a car we do NOT simulate — mirror it onto the
    /// replica (def + live logic car), so job paperwork and re-materializations read true.</summary>
    private void OnRemoteCargo(int carId, string cargoId, float amount)
    {
        if (_setByCarId.ContainsKey(carId)) return; // ours — we are the authority, nothing to apply
        _remote.ApplyCargo(carId, cargoId, amount);
        _log($"[trains] remote car {carId} cargo → {(cargoId.Length == 0 ? "empty" : cargoId)}");
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
        if (_grantCar != null && TryAnyCarId(_grantCar, out int prevId))
            _client.Trains.ReleaseControlGrant(prevId);
        _grantCar = car;
        if (car != null && TryAnyCarId(car, out int carId))
        {
            _log($"[trains] entered car {carId} — requesting control grant");
            _client.Trains.RequestControlGrant(carId);
        }
    }

    /// <summary>Server car id for OWN bound cars and remotely-spawned cars alike — entering a
    /// remote-driven cab requests a grant the same way (input routing over it is M3.5c).</summary>
    private bool TryAnyCarId(TrainCar car, out int carId) =>
        _idByCar.TryGetValue(car, out carId) || _remote.TryGetServerCarId(car, out carId);

    private void OnGrantChanged(int carId, int holderId) =>
        _log($"[trains] control grant: car {carId} → {(holderId == 0 ? "free" : $"player {holderId}")}");

    // ── receive side ──

    private void OnSnapshot(TrainsetSnapshot snap)
    {
        if (_bindings.ContainsKey(snap.TrainsetId) || _map is null) return; // never re-apply our own sets
        _remote.Apply(snap, _map);
    }

    public void Dispose()
    {
        ChainHook.CoupleFilter = null;
        ChainHook.UncoupleFilter = null;
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
        _remote.Clear();
    }
}
