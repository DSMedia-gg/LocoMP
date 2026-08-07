using System;
using System.Collections.Generic;
using DV;
using DV.ThingTypes;
using LocoMP.Core.Trains;
using UnityEngine;

// System is imported for Action; pin the unqualified Object back to Unity's (Destroy lives there).
using Object = UnityEngine.Object;

namespace LocoMP.Shim;

/// <summary>
/// M3.5b: remote consists become REAL TrainCars. Sets simulated by other players are spawned
/// through the game's own savegame-restore primitive (<c>CarSpawner.SpawnLoadedCar</c> — exact
/// per-bogie track+span placement, carries the source world's car id/guid so job task trees and
/// booklets can name these cars later), then driven kinematically from the same spline-space
/// snapshots the ghosts used: the owner's physics is the truth, ours never runs. Every car is
/// hardened against the local game acting on it (preventDelete, preventAutoCouple, kinematic
/// rigidbodies) — the incumbent's snap-back class of bugs comes from letting local physics fight
/// the remote authority, which is exactly what kinematic drive forecloses. Falls back to the M2
/// ghost boxes per SET when a livery can't be resolved (modded host, or the bot's synthetic
/// "ghost-loco" kinds), so the old rig keeps working unchanged.
/// </summary>
public sealed class RealCarSync
{
    // Was 12 (τ = 1/12 s ≈ 83 ms), which made the SMOOTHING — not the 20 Hz packet rate — ~75% of
    // a remote train's position lag (~3 m at 100 km/h, felt hardest while coupling to a remote
    // consist beside your zero-lag native one). 30 cuts τ to ~33 ms (~58 ms total with snapshot
    // staleness) at zero protocol risk. Deliberately shipped AHEAD of any dead reckoning: if
    // jitter turns ugly at 30, that diagnosis — lag vs jitter — decides whether extrapolation
    // (TopologyWalker + the 03 §5 250 ms cap) is worth building at all. Judge it in-game.
    // M5.3: user-tunable ("interpolation prefs") — the settings store pushes its clamped value
    // here; the 08-05 live verdict picked the default.
    public static float LerpRate = 30f;
    private const float SnapDistance = 80f;
    private const float HardenSeconds = 3f;  // car components finish initializing over a few frames
    private const int MaxRespawnsPerSet = 3;

    // Proximity materialization: DV's distance streaming destroys far cars' GameObjects no matter
    // what (preventDelete doesn't cover ECS conversion — run B lost a consist spawned 359 m out
    // within a second). So remote consists exist as DATA always but as real cars only near the
    // player — the D10 interest-management shape, forced early. Hysteresis keeps the boundary calm.
    private const float MaterializeRadius = 250f;
    private const float DematerializeRadius = 330f;
    private const float StreamOutCooldownSeconds = 10f; // DV killed it near us — back off, retry

    // Mechanism B (M4 orphan concept, train half): periodically re-assert server truth against
    // local native state instead of trusting one-shot repairs — the item side has done this from
    // the start (ItemSync.Reconcile); the train side had no equivalent, which IS the third M4
    // smoke finding. Coupler truth is swept every interval; a set with no live snapshot stream
    // asks the server to replay its baseline (03 §4's ResyncRequest — the server replays def AND
    // position since the orphan fix), which also re-runs the materialize distance check, healing
    // the parked-far-consist case (Apply only ever runs on an arriving snapshot). The
    // request/log/cadence decision lives in game-free Core (BaselineReplayPolicy) so finding #3's
    // parked-set resync loop is pinned by tests: a baselined set polls quietly, only a set with no
    // data yet recovers at the fast cadence and logs.
    private const float ReconcileIntervalSeconds = 1f;
    private const float StaleSnapshotSeconds = 5f;  // stream quiet this long + unspawned → replay

    // F1 (2026-08-04 gauntlet): chain gestures on remote cars apply NATIVELY now (the gesture FSM
    // assumes a synchronous act) while the request travels to the sim owner — so until the commit
    // lands, the def and the native coupler state deliberately disagree for that one pair. This
    // window keeps the sweep from "correcting" the disagreement mid-flight; expiry re-asserts the
    // def, which is what makes a refused request visible (the chain pops back).
    private const float PairAssertSuppressSeconds = 5f;

    private sealed class Entry
    {
        public Entry(CarDef def) => Def = def;

        public CarDef Def;
        public TrainCar? Car;
        public Vector3 TargetPos;
        public Quaternion TargetRot = Quaternion.identity;
        public bool HasTarget;
        public uint LastFrontEdge = uint.MaxValue;
        public uint LastRearEdge = uint.MaxValue;

        // Dead reckoning (03 §5): the last railed snapshot's two bogie states + when it landed, so the
        // smoothing target can be re-extrapolated along the rail every frame. Railed is set ONLY by
        // snapshot application (true = railed branch, false = derailed); every other target-setter
        // (spawn, pool-adopt) leaves it false so a reused entry never coasts on stale bogie state.
        public BogieState FrontState;
        public BogieState RearState;
        public float SnapAt;
        public bool Railed;
    }

    private sealed class RemoteSet
    {
        public RemoteSet(TrainsetDef def, Entry[] cars)
        {
            Def = def;
            Cars = cars;
        }

        public TrainsetDef Def;
        public Entry[] Cars;
        public bool Spawned;
        public bool CouplingChecked;
        public float HardenUntil;
        public float NextMaterializeAllowed;
        public bool FarLogged;
        public float LastSnapshotAt;    // when Apply last saw this set (seeded at creation)
        public float NextResyncAllowed; // reconcile's per-set request throttle
        public bool ResyncLogged;       // first fresh request logs; retries stay quiet
        public bool EverBaselined;      // ≥1 real snapshot applied — position data in hand (finding #3)
    }

    private readonly Dictionary<int, RemoteSet> _sets = new();
    private readonly HashSet<int> _ghostSets = new(); // sets delegated to the box fallback
    private readonly Dictionary<TrainCar, int> _serverIdByCar = new();
    private readonly Dictionary<int, TrainCar> _carByServerId = new();
    private readonly Dictionary<TrainCar, Action> _destroyHooks = new();
    private readonly Dictionary<int, int> _respawns = new();
    private bool _deletingOurs;
    private readonly Dictionary<(int, int), float> _pairAssertSuppressed = new();
    private readonly HashSet<int> _unresolvedWarned = new();
    private readonly HashSet<int> _occupiedWarned = new();
    private readonly GhostConsists _ghosts;
    private readonly Action<string> _log;
    private readonly Action<int>? _requestResync; // the 03 §4 escape hatch, wired by TrainSync
    private float _reconcileAccum;

    public RealCarSync(Action<string> log, Action<int>? requestResync = null)
    {
        _log = log;
        _requestResync = requestResync;
        _ghosts = new GhostConsists(log);
    }

    /// <summary>Server car id of a spawned remote car (grants target cars by server id).</summary>
    public bool TryGetServerCarId(TrainCar car, out int carId) => _serverIdByCar.TryGetValue(car, out carId);

    /// <summary>The live replica behind a server car id (control-state mirroring, M3.5c).</summary>
    public bool TryGetCarByServerId(int carId, out TrainCar car) =>
        _carByServerId.TryGetValue(carId, out car) && car != null;

    /// <summary>True for cars this class spawned — i.e. cars simulated by ANOTHER player.</summary>
    public bool IsRemoteCar(TrainCar car) => _serverIdByCar.ContainsKey(car);

    /// <summary>Every live replica by server car id (v18 hardware sync walks these to hook/apply).
    /// Snapshot-free enumeration — skip entries whose GameObject died since registration.</summary>
    public IEnumerable<KeyValuePair<int, TrainCar>> CarsByServerId
    {
        get
        {
            foreach (KeyValuePair<int, TrainCar> kv in _carByServerId)
                if (kv.Value != null) yield return kv;
        }
    }

    /// <summary>A chain gesture just applied a couple/uncouple to this pair NATIVELY and routed
    /// the intent to the sim owner (F1) — hold the reconcile's truth-assert for this one pair
    /// until the commit lands or the window expires (expiry = the def visibly wins).</summary>
    public void SuppressPairAssert(int carIdA, int carIdB)
    {
        (int, int) key = carIdA < carIdB ? (carIdA, carIdB) : (carIdB, carIdA);
        _pairAssertSuppressed[key] = Time.unscaledTime + PairAssertSuppressSeconds;
    }

    private bool IsPairAssertSuppressed(int carIdA, int carIdB)
    {
        (int, int) key = carIdA < carIdB ? (carIdA, carIdB) : (carIdB, carIdA);
        if (!_pairAssertSuppressed.TryGetValue(key, out float until)) return false;
        if (Time.unscaledTime >= until)
        {
            _pairAssertSuppressed.Remove(key);
            return false;
        }
        return true;
    }

    /// <summary>True while we are inside the game's spawn call — its CarSpawned event fires before
    /// the car lands in our maps, and the joined-client native-spawn cleaner must not eat it.</summary>
    public bool SpawningRemote { get; private set; }

    /// <summary>Live cargo change from the owner (M3.5c): remembered on the def (so a
    /// re-materialization spawns the current load) and mirrored onto the live logic car.</summary>
    public void ApplyCargo(int carId, string cargoId, float amount)
    {
        foreach (RemoteSet set in _sets.Values)
        {
            foreach (Entry entry in set.Cars)
            {
                if (entry.Def.Id != carId) continue;
                entry.Def = entry.Def.WithCargo(cargoId, amount);
                if (entry.Car != null)
                {
                    try
                    {
                        DV.Logic.Job.Car? logic = entry.Car.logicCar;
                        if (logic != null && logic.CurrentCargoTypeInCar != CargoType.None) logic.DumpCargo();
                    }
                    catch (Exception e)
                    {
                        _log($"[trains] cargo unload mirror failed for car {carId}: {e.Message}");
                    }
                    if (cargoId.Length > 0) MirrorCargo(entry.Car, entry.Def);
                }
                return;
            }
        }
    }

    /// <summary>Record a newly announced remote set. Actual spawning waits for the first admitted
    /// snapshot (the join burst carries a baseline, so this is one round-trip at most).</summary>
    public void EnsureSet(TrainsetDef def)
    {
        if (_ghostSets.Contains(def.Id))
        {
            _ghosts.EnsureSet(def);
            return;
        }
        if (_sets.TryGetValue(def.Id, out RemoteSet? existing) && existing.Cars.Length == def.Cars.Count)
        {
            existing.Def = def;
            return;
        }
        CreateSet(def, null);
    }

    /// <summary>Membership transaction over remote sets: cars survive by SERVER car id (merge/split
    /// products reuse the same cars, M2.1 design), so we re-map live TrainCars instead of
    /// despawn+respawn — then repair the physical couplings to match the new membership.</summary>
    public void ApplyTransaction(IEnumerable<int> retiredIds, IEnumerable<TrainsetDef> remoteProducts)
    {
        var pool = new Dictionary<int, TrainCar>();
        foreach (int retired in retiredIds)
        {
            _ghosts.Remove(retired);
            _ghostSets.Remove(retired);
            if (!_sets.TryGetValue(retired, out RemoteSet? set)) continue;
            _sets.Remove(retired);
            foreach (Entry entry in set.Cars)
            {
                if (entry.Car == null) continue;
                pool[entry.Def.Id] = entry.Car;
                Unmap(entry.Car);
            }
        }

        foreach (TrainsetDef def in remoteProducts) CreateSet(def, pool);

        // Anything left in the pool belongs to no product — despawn it rather than leak it.
        if (pool.Count > 0)
        {
            var strays = new List<TrainCar>(pool.Values);
            _log($"[trains] {strays.Count} remote car(s) left no trainset after a transaction — despawning");
            DeleteCars(strays);
        }

        ReconcileCouplings();
    }

    public void Remove(int trainsetId)
    {
        _ghosts.Remove(trainsetId);
        _ghostSets.Remove(trainsetId);
        if (!_sets.TryGetValue(trainsetId, out RemoteSet? set)) return;
        _sets.Remove(trainsetId);
        DespawnEntries(set);
    }

    /// <summary>D21 adoption: the server just made US the sim owner of this set (a claimed parked
    /// wreck), so its replicas stop being replicas — hand every live TrainCar over to local
    /// simulation, in the order of <paramref name="def"/>'s cars, softened back to ordinary
    /// physics. ATOMIC: fails with nothing changed unless EVERY car of the def is live here — a
    /// ghost, unspawned, or dematerialized set has no physical cars to adopt (the caller releases
    /// the claim instead of simulating what it cannot see).</summary>
    public bool TryAdopt(TrainsetDef def, out TrainCar[] cars)
    {
        cars = Array.Empty<TrainCar>();
        if (_ghostSets.Contains(def.Id)) return false;
        if (!_sets.TryGetValue(def.Id, out RemoteSet? set) || !set.Spawned) return false;

        var byId = new Dictionary<int, TrainCar>();
        foreach (Entry entry in set.Cars)
            if (entry.Car != null) byId[entry.Def.Id] = entry.Car;

        var adopted = new TrainCar[def.Cars.Count];
        for (int i = 0; i < adopted.Length; i++)
        {
            if (!byId.TryGetValue(def.Cars[i].Id, out TrainCar car)) return false;
            adopted[i] = car;
        }

        _sets.Remove(def.Id);
        _respawns.Remove(def.Id);
        foreach (TrainCar car in adopted)
        {
            if (_destroyHooks.TryGetValue(car, out Action onGone))
            {
                car.OnCarAboutToBeDestroyed -= onGone;
                _destroyHooks.Remove(car);
            }
            Unmap(car);
            SoftenCar(car);
        }
        cars = adopted;
        return true;
    }

    public void Clear()
    {
        foreach (RemoteSet set in _sets.Values) DespawnEntries(set);
        _sets.Clear();
        _serverIdByCar.Clear();
        _carByServerId.Clear();
        _destroyHooks.Clear();
        _ghostSets.Clear();
        _pairAssertSuppressed.Clear();
        _ghosts.Clear();
    }

    /// <summary>Feed one admitted snapshot: spawns the set on first resolvable positions, then
    /// keeps per-car lerp targets (railed cars from their two bogie spline points, derailed cars
    /// from their 6-DOF pose) and the bogies' logical track assignment current.</summary>
    public void Apply(TrainsetSnapshot snap, TrackIndexMap map)
    {
        if (_ghostSets.Contains(snap.TrainsetId))
        {
            _ghosts.Apply(snap, map);
            return;
        }
        if (!_sets.TryGetValue(snap.TrainsetId, out RemoteSet? set) || set.Cars.Length != snap.Cars.Length) return;

        set.LastSnapshotAt = Time.unscaledTime;
        set.EverBaselined = true;  // we now hold real position data for this set (finding #3)
        set.ResyncLogged = false; // the stream answered — a future silence is a new event

        if (!set.Spawned)
        {
            if (!TryDistanceToPlayer(snap, map, out float dist)) return;
            if (dist > MaterializeRadius)
            {
                if (!set.FarLogged)
                {
                    set.FarLogged = true;
                    _log($"[trains] remote consist {set.Def.Id} is ~{dist:F0} m away — " +
                         $"materializes as real cars within {MaterializeRadius:F0} m");
                }
                return;
            }
            if (Time.unscaledTime < set.NextMaterializeAllowed) return;
            if (!TrySpawnSet(set, snap, map)) return;
            set.FarLogged = false;
        }
        else if (TryDistanceToPlayer(snap, map, out float dist) && dist > DematerializeRadius)
        {
            _log($"[trains] remote consist {set.Def.Id} rolled ~{dist:F0} m out — dematerialized " +
                 "(still synced as data; returns when close)");
            Dematerialize(set);
            return;
        }

        for (int i = 0; i < set.Cars.Length; i++)
        {
            Entry entry = set.Cars[i];
            if (entry.Car == null)
            {
                HandleLostCars(set);
                return;
            }
            CarSnapshot state = snap.Cars[i];
            if (state.Derailed)
            {
                entry.TargetPos = PresenceShim.ToLocalPosition(state.Pose);
                entry.TargetRot = new Quaternion(state.Pose.Rx, state.Pose.Ry, state.Pose.Rz, state.Pose.Rw);
                entry.HasTarget = true;
                entry.Railed = false; // off the rail — nothing to extrapolate along
            }
            else
            {
                // Dead reckoning (03 §5): store the bogie states + arrival time and let the smoother
                // re-extrapolate the target every frame (ResolveRailTarget in Tick). The zero-elapsed
                // resolve here gives an immediate target and validates the spline points.
                entry.FrontState = state.Front;
                entry.RearState = state.Rear;
                entry.SnapAt = Time.unscaledTime;
                entry.Railed = true;
                if (!ResolveRailTarget(entry, map, 0f)) continue; // unresolvable — keep the last target
                entry.HasTarget = true;
                UpdateBogieTracks(entry, state, map);
            }
        }

        if (!set.CouplingChecked && AllPlaced(set))
        {
            set.CouplingChecked = true;
            CoupleAdjacent(set);
        }
    }

    /// <summary>Recompute a railed entry's smoothing target from its stored bogie states, extrapolated
    /// forward by <paramref name="elapsed"/> seconds along the rail (03 §5 dead reckoning) — so the
    /// target the smoother chases tracks a constant-speed car instead of the last snapshot. Returns
    /// false and leaves the previous target untouched if either spline point is unresolvable. DV's
    /// TryGetLocalPoint clamps an over-run S into its edge, so extrapolation past an edge end just
    /// holds at the boundary and never leaves the rail.</summary>
    private bool ResolveRailTarget(Entry entry, TrackIndexMap map, float elapsed)
    {
        float sFront = DeadReckoning.ExtrapolateS(entry.FrontState.S, entry.FrontState.V, elapsed);
        float sRear = DeadReckoning.ExtrapolateS(entry.RearState.S, entry.RearState.V, elapsed);
        if (!map.TryGetLocalPoint(entry.FrontState.EdgeId, sFront, out Vector3 front, out Vector3 fwd) ||
            !map.TryGetLocalPoint(entry.RearState.EdgeId, sRear, out Vector3 rear, out _))
            return false;
        Vector3 axis = front - rear;
        entry.TargetPos = (front + rear) * 0.5f;
        entry.TargetRot = Quaternion.LookRotation(axis.sqrMagnitude > 0.01f ? axis : fwd);
        return true;
    }

    /// <summary>Advance smoothing + keep the hardening honest while components finish initializing.
    /// Call once per frame. Also pumps the mechanism-B reconcile (coupler truth + baseline replay)
    /// on its own slower cadence.</summary>
    public void Tick(float dt, TrackIndexMap map)
    {
        _ghosts.Tick(dt);
        float t = Mathf.Clamp01(LerpRate * dt);
        float now = Time.unscaledTime;

        _reconcileAccum += dt;
        if (_reconcileAccum >= ReconcileIntervalSeconds)
        {
            _reconcileAccum = 0;
            Reconcile(now);
        }
        foreach (RemoteSet set in _sets.Values)
        {
            if (!set.Spawned) continue;
            bool harden = now < set.HardenUntil;
            foreach (Entry entry in set.Cars)
            {
                if (entry.Car == null || !entry.HasTarget) continue;
                if (harden) HardenCar(entry.Car);
                if (entry.Railed) ResolveRailTarget(entry, map, now - entry.SnapAt); // 03 §5 dead reckoning
                Transform tr = entry.Car.transform;
                if ((entry.TargetPos - tr.position).sqrMagnitude > SnapDistance * SnapDistance)
                {
                    tr.SetPositionAndRotation(entry.TargetPos, entry.TargetRot);
                    continue;
                }
                tr.SetPositionAndRotation(
                    Vector3.Lerp(tr.position, entry.TargetPos, t),
                    Quaternion.Slerp(tr.rotation, entry.TargetRot, t));
            }
        }
    }

    // ── spawning ──

    private void CreateSet(TrainsetDef def, Dictionary<int, TrainCar>? pool)
    {
        // Every kind must resolve to a real livery or the whole set falls back to boxes — a consist
        // with holes in the middle reads as broken; all-or-nothing keeps the failure legible.
        foreach (CarDef car in def.Cars)
        {
            if (!Globals.G.Types.TryGetLivery(car.Kind, out _))
            {
                if (_ghostSets.Add(def.Id))
                    _log($"[trains] remote consist {def.Id}: livery '{car.Kind}' unknown — using ghost boxes");
                _ghosts.EnsureSet(def);
                return;
            }
        }

        var entries = new Entry[def.Cars.Count];
        int claimed = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new Entry(def.Cars[i]);
            if (pool != null && pool.TryGetValue(def.Cars[i].Id, out TrainCar pooled) && pooled != null)
            {
                pool.Remove(def.Cars[i].Id);
                entries[i].Car = pooled;
                entries[i].HasTarget = true; // keep the current transform as target until the next snapshot
                entries[i].TargetPos = pooled.transform.position;
                entries[i].TargetRot = pooled.transform.rotation;
                entries[i].Railed = false; // hold static until the next snapshot re-establishes bogie states
                Map(pooled, def.Cars[i].Id);
                claimed++;
            }
        }

        var set = new RemoteSet(def, entries)
        {
            // Seeded as "just heard from" so the reconcile grants the normal join/replay round-trip
            // its quiet window before ever asking the server to repeat itself.
            LastSnapshotAt = Time.unscaledTime,
        };
        if (claimed == entries.Length)
        {
            // All cars survived a merge/split re-map — nothing to spawn.
            set.Spawned = true;
            set.HardenUntil = Time.unscaledTime + HardenSeconds;
        }
        else if (claimed > 0)
        {
            // A partial claim means the product references cars we never had (shouldn't happen —
            // products are built from retired parents). Start the set over rather than mix.
            _log($"[trains] remote consist {def.Id}: only {claimed}/{entries.Length} cars re-mapped — respawning whole set");
            foreach (Entry entry in entries)
            {
                if (entry.Car != null) Unmap(entry.Car);
            }
            var partial = new List<TrainCar>();
            foreach (Entry entry in entries)
            {
                if (entry.Car != null) partial.Add(entry.Car);
                entry.Car = null;
                entry.HasTarget = false;
            }
            DeleteCars(partial);
        }
        _sets[def.Id] = set;
    }

    private bool TrySpawnSet(RemoteSet set, TrainsetSnapshot snap, TrackIndexMap map)
    {
        CarSpawner spawner = CarSpawner.Instance;
        if (spawner == null) return false;

        // Resolve every car's placement first — spawning half a consist helps nobody.
        var positions = new (Vector3 pos, Quaternion rot, RailTrack? front, double frontS, RailTrack? rear, double rearS, bool derailed)[set.Cars.Length];
        for (int i = 0; i < set.Cars.Length; i++)
        {
            CarSnapshot state = snap.Cars[i];
            if (state.Derailed)
            {
                positions[i] = (PresenceShim.ToLocalPosition(state.Pose),
                    new Quaternion(state.Pose.Rx, state.Pose.Ry, state.Pose.Rz, state.Pose.Rw),
                    null, 0, null, 0, true);
                continue;
            }
            if (!map.TryGetLocalPoint(state.Front.EdgeId, state.Front.S, out Vector3 front, out Vector3 fwd) ||
                !map.TryGetLocalPoint(state.Rear.EdgeId, state.Rear.S, out Vector3 rear, out _) ||
                !map.TryGetTrack(state.Front.EdgeId, out RailTrack frontTrack) ||
                !map.TryGetTrack(state.Rear.EdgeId, out RailTrack rearTrack))
            {
                if (_unresolvedWarned.Add(set.Def.Id))
                    _log($"[trains] WARNING: remote consist {set.Def.Id} cannot be placed — " +
                         "track points unresolvable (stale world map?)");
                return false;
            }
            Vector3 axis = front - rear;
            positions[i] = ((front + rear) * 0.5f,
                Quaternion.LookRotation(axis.sqrMagnitude > 0.01f ? axis : fwd),
                frontTrack, state.Front.S, rearTrack, state.Rear.S, false);
        }

        // Never spawn into occupied space: the start hint points at the edge nearest the PLAYER,
        // which is usually where the host's own train sits — run №1 materialized three cars inside
        // it (couple contact + a stress derail on a local flatbed). The consist moves, so deferring
        // until its current position is clear resolves itself within seconds.
        for (int i = 0; i < positions.Length; i++)
        {
            (Vector3 pos, Quaternion rot, _, _, _, _, _) = positions[i];
            if (CarSpawner.IsBoxOverlappingSimple(pos + Vector3.up * 2f, new Vector3(1.7f, 1.9f, 8.5f), rot))
            {
                if (_occupiedWarned.Add(set.Def.Id))
                    _log($"[trains] remote consist {set.Def.Id}: spawn point occupied by existing cars — " +
                         "waiting for it to roll onto clear track");
                return false;
            }
        }

        // The derailed leg passes null tracks to SpawnLoadedCar (savegame-restore semantics for an
        // off-rail car). It had never fired in a live run as of 2026-07-19 — announce it loudly so
        // the run that finally exercises it is attributable; the catch below is its safety net.
        foreach (var p in positions)
        {
            if (!p.derailed) continue;
            _log($"[trains] remote consist {set.Def.Id}: spawning with DERAILED car(s) — " +
                 "the null-track spawn path (ghost fallback catches a failure)");
            break;
        }

        try
        {
            SpawningRemote = true;
            for (int i = 0; i < set.Cars.Length; i++)
            {
                Entry entry = set.Cars[i];
                if (entry.Car != null) continue; // survived a re-map
                if (!Globals.G.Types.TryGetLivery(entry.Def.Kind, out TrainCarLivery livery))
                    throw new InvalidOperationException($"livery '{entry.Def.Kind}' vanished");

                (Vector3 pos, Quaternion rot, RailTrack? frontTrack, double frontS, RailTrack? rearTrack, double rearS, bool derailed) = positions[i];
                string carId = entry.Def.GameId.Length > 0 ? entry.Def.GameId : $"LMP-{entry.Def.Id}";
                string carGuid = entry.Def.GameGuid.Length > 0 ? entry.Def.GameGuid : $"locomp-{entry.Def.Id}";

                TrainCar spawned = spawner.SpawnLoadedCar(livery.prefab, carId, carGuid,
                    playerSpawnedCar: false, uniqueCar: false, pos, rot,
                    bogie1Derailed: derailed, frontTrack, frontS,
                    bogie2Derailed: derailed, rearTrack, rearS);
                if (spawned == null) throw new InvalidOperationException($"SpawnLoadedCar returned null for '{entry.Def.Kind}'");

                entry.Car = spawned;
                entry.TargetPos = pos;
                entry.TargetRot = rot;
                entry.HasTarget = true;
                entry.Railed = false; // this Apply's snapshot loop (railed branch) sets it true next
                Map(spawned, entry.Def.Id);
                HardenCar(spawned);
                MirrorCargo(spawned, entry.Def);
                // DV can still destroy the car behind our back (distance streaming's ECS
                // conversion ignores preventDelete) — that's a stream-out, not an error.
                TrainCar hooked = spawned;
                Action onGone = () => OnRemoteCarDestroyed(set, hooked);
                hooked.OnCarAboutToBeDestroyed += onGone;
                _destroyHooks[hooked] = onGone;
            }
        }
        catch (Exception e)
        {
            _log($"[trains] remote consist {set.Def.Id}: real-car spawn FAILED ({e.Message}) — falling back to ghost boxes");
            FallBackToGhost(set);
            return false;
        }
        finally
        {
            SpawningRemote = false;
        }

        set.Spawned = true;
        set.HardenUntil = Time.unscaledTime + HardenSeconds;
        string where = "";
        Transform player = PlayerManager.PlayerTransform;
        if (player != null && set.Cars[0].Car != null)
            where = $", ~{Vector3.Distance(player.position, set.Cars[0].Car!.transform.position):F0} m from you";
        _log($"[trains] remote consist {set.Def.Id}: {set.Cars.Length} real car(s) on the rails " +
             $"(edge {(snap.Cars[0].Derailed ? "-" : snap.Cars[0].Front.EdgeId.ToString())}{where})");
        // Paste-me hint (livery/start-edge pattern): a bot streams its own car positions and only
        // the game knows real coupler pitches — its uniform 16 m guess leaves real cars floating
        // apart, and the forced couple across the gap wedges DV's chain FSM half-dead (G3′
        // 2026-08-04). Pitch alone is not enough: DV seats the spawned body from the streamed
        // BOGIE track points, so the bot's inset guess shifted each car within its span and left
        // the rear joint wide (G3′ round 2). Everything measures in the car's LOCAL frame — the
        // coupler anchors and bogie pivots are direct children of the pivot, so local Z is the
        // along-car axis by construction (PivotTo*CouplerZOffset is DV's own accessor for it).
        // Measured off the cars just spawned; invariant "." so the line always pastes.
        var geom = new string[set.Cars.Length];
        for (int i = 0; i < set.Cars.Length; i++)
        {
            TrainCar? car = set.Cars[i].Car;
            float len = car?.InterCouplerDistance ?? 16f;
            float frontInset = Mathf.Min(3.5f, len / 4f); // mirror the bot's guess if unmeasurable
            float rearInset = frontInset;
            if (car != null && car.FrontBogie != null && car.RearBogie != null)
            {
                frontInset = car.PivotToFrontCouplerZOffset - car.FrontBogie.transform.localPosition.z;
                rearInset = car.RearBogie.transform.localPosition.z - car.PivotToRearCouplerZOffset;
            }
            geom[i] = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F2}:{1:F2}:{2:F2}", len, frontInset, rearInset);
        }
        _log($"[trains] bot spacing hint: --car-geometry {string.Join(",", geom)}  " +
             "(coupler pitch : front bogie inset : rear bogie inset, per car)");
        return true;
    }

    /// <summary>Everything that stops the LOCAL game from acting on a remotely-simulated car: no
    /// deletion (streaming or otherwise), no auto-coupling into local consists, no local physics
    /// (the owner's snapshots are the only mover). Re-applied for a few seconds after spawn because
    /// car components initialize across frames.</summary>
    private static void HardenCar(TrainCar car)
    {
        car.preventDelete = true;
        foreach (Coupler coupler in car.couplers)
        {
            if (coupler != null) coupler.preventAutoCouple = true;
        }
        foreach (Rigidbody rb in car.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!rb.isKinematic) rb.isKinematic = true;
        }
        // Kinematic bogies never advance their traveller, and DV logs a "Point Set Traveller not
        // moving even though velocity is" warning per frame it notices — distance tracking is a
        // local-simulation concern this car doesn't have.
        if (car.FrontBogie != null) car.FrontBogie.DistanceTrackingEnabled = false;
        if (car.RearBogie != null) car.RearBogie.DistanceTrackingEnabled = false;
    }

    /// <summary>The inverse of <see cref="HardenCar"/>, for adoption (D21): everything local
    /// simulation needs comes back. The rigidbody sweep un-kinematics everything the harden swept —
    /// on the exterior shell (body + bogies) every rb is dynamic natively, and interiors live on a
    /// separate loaded object this sweep never reaches. A parked car wakes stationary (zeroed
    /// velocities), which is exactly what parked means.</summary>
    private static void SoftenCar(TrainCar car)
    {
        car.preventDelete = false;
        foreach (Coupler coupler in car.couplers)
        {
            if (coupler != null) coupler.preventAutoCouple = false;
        }
        foreach (Rigidbody rb in car.GetComponentsInChildren<Rigidbody>(true))
        {
            if (rb.isKinematic) rb.isKinematic = false;
        }
        if (car.FrontBogie != null) car.FrontBogie.DistanceTrackingEnabled = true;
        if (car.RearBogie != null) car.RearBogie.DistanceTrackingEnabled = true;
    }

    /// <summary>Registration-time cargo, mirrored onto the logic car so the load is visible and
    /// (M3.5c) job validation reads the right cargo. Live load/unload sync is a banked debt.</summary>
    private void MirrorCargo(TrainCar car, CarDef def)
    {
        if (def.CargoId.Length == 0) return;
        try
        {
            if (!Globals.G.Types.TryGetCargo(def.CargoId, out CargoType_v2 cargo))
            {
                _log($"[trains] cargo '{def.CargoId}' unknown — car {def.GameId} spawns empty");
                return;
            }
            DV.Logic.Job.Car? logic = car.logicCar;
            if (logic == null)
            {
                car.LogicCarInitialized += () => MirrorCargo(car, def);
                return;
            }
            float amount = def.CargoAmount > 0 ? def.CargoAmount : logic.capacity;
            logic.LoadCargo(amount, cargo.v1, null);
        }
        catch (Exception e)
        {
            _log($"[trains] cargo mirror failed for car {def.GameId}: {e.Message}");
        }
    }

    private void UpdateBogieTracks(Entry entry, CarSnapshot state, TrackIndexMap map)
    {
        if (state.Front.EdgeId == entry.LastFrontEdge && state.Rear.EdgeId == entry.LastRearEdge) return;
        TrainCar car = entry.Car!;
        try
        {
            if (map.TryGetTrack(state.Front.EdgeId, out RailTrack front) && car.FrontBogie != null && !car.FrontBogie.HasDerailed)
                car.FrontBogie.SetTrack(front, state.Front.S, state.Front.V >= 0 ? 1 : -1);
            if (map.TryGetTrack(state.Rear.EdgeId, out RailTrack rear) && car.RearBogie != null && !car.RearBogie.HasDerailed)
                car.RearBogie.SetTrack(rear, state.Rear.S, state.Rear.V >= 0 ? 1 : -1);
            entry.LastFrontEdge = state.Front.EdgeId;
            entry.LastRearEdge = state.Rear.EdgeId;
        }
        catch
        {
            // Track occupancy is a nicety (logic-car track queries); never let it break the render.
        }
    }

    // ── physical coupling mirror ──

    /// <summary>Couple def-adjacent cars so chains/hoses read right and the consist walks as one.
    /// EXPLICIT partner couplers only (`CoupleTo`) — run №1 proved a scan-based TryCouple can grab
    /// a bystander: the consist spawned near the host's train and chained itself to it.</summary>
    private void CoupleAdjacent(RemoteSet set)
    {
        for (int i = 0; i + 1 < set.Cars.Length; i++)
        {
            TrainCar? a = set.Cars[i].Car, b = set.Cars[i + 1].Car;
            if (a == null || b == null) continue;
            int idA = set.Cars[i].Def.Id, idB = set.Cars[i + 1].Def.Id;
            if (IsPairAssertSuppressed(idA, idB))
                continue; // an in-flight uncouple request holds this exact pair (F1)
            Coupler? mine = NearestCoupler(a, b.transform.position);
            Coupler? theirs = NearestCoupler(b, a.transform.position);
            if (mine == null || theirs == null || mine.IsCoupled() || theirs.IsCoupled()) continue;
            if ((mine.transform.position - theirs.transform.position).sqrMagnitude > 8f * 8f) continue;
            try
            {
                mine.CoupleTo(theirs, playAudio: false, viaChainInteraction: false);
                _log($"[trains] coupled def-adjacent cars {idA}+{idB}");
            }
            catch (Exception e)
            {
                _log($"[trains] couple of def-adjacent cars {idA}+{idB} FAILED ({e.Message})");
            }
        }
    }

    /// <summary>Mechanism B: re-assert server truth against local native state, periodically —
    /// not only when a transaction happens to arrive. Two directions: coupler state follows the
    /// def (below), and an unspawned set whose stream has gone quiet asks the server to replay its
    /// baseline. Scope is deliberately _sets, not the whole client view: a set absent from _sets
    /// is ghost-delegated or interest-hidden, and both of those are decisions, not drift.
    /// <para>The request/log/cadence call is <see cref="BaselineReplayPolicy"/> (game-free, tested):
    /// a set with no data yet recovers fast and logs once; a baselined-but-parked set (finding #3)
    /// polls quietly at the slow keep-alive cadence rather than re-logging and re-pulling forever.</para></summary>
    private void Reconcile(float now)
    {
        ReconcileCouplings();

        if (_requestResync == null) return;
        foreach (KeyValuePair<int, RemoteSet> kv in _sets)
        {
            RemoteSet set = kv.Value;
            BaselineReplayPolicy.Decision d = BaselineReplayPolicy.Evaluate(
                set.Spawned, set.EverBaselined, now - set.LastSnapshotAt,
                StaleSnapshotSeconds, now, set.NextResyncAllowed, set.ResyncLogged);
            if (!d.Request) continue;
            set.NextResyncAllowed = d.NextAllowed;
            if (d.Log)
            {
                set.ResyncLogged = true;
                _log($"[trains] remote consist {kv.Key} has no live stream and is not materialized — " +
                     "requesting a baseline replay (03 §4)");
            }
            _requestResync(kv.Key);
        }
    }

    /// <summary>Break couplings that don't match membership and make the ones that should exist.
    /// The def is the truth; the physical state follows it — in BOTH directions, every time this
    /// runs. A remote car's coupler may only ever hold its def-adjacent neighbour: a partner that
    /// is not a mapped remote car (the host's own native car, a foreign spawn) is exactly the
    /// stale-split leftover the M4 smoke pass caught ("coupler pointing straight down", no
    /// re-couple possible) — the old repair SKIPPED those, and only ran after a transaction, so a
    /// missed repair never healed. Runs from ApplyTransaction (immediacy) and the reconcile
    /// cadence (truth re-asserted). Idempotent; forced coupler ops on remote cars cannot echo as
    /// proposals (TrainSync.TryGetPair requires both cars in the OWN-bound map).</summary>
    private void ReconcileCouplings()
    {
        foreach (RemoteSet set in _sets.Values)
        {
            if (!set.Spawned) continue;
            for (int i = 0; i < set.Cars.Length; i++)
            {
                TrainCar? car = set.Cars[i].Car;
                if (car == null) continue;
                foreach (Coupler coupler in car.couplers)
                {
                    if (coupler == null || !coupler.IsCoupled()) continue;
                    TrainCar other = coupler.coupledTo != null ? coupler.coupledTo.train : null!;
                    if (other == null) continue;
                    // Server truth: one set never spans own and remote cars, so a partner outside
                    // the remote map can NEVER be legitimate — detach it, don't skip it.
                    bool known = _serverIdByCar.TryGetValue(other, out int otherId);
                    bool adjacent = known &&
                        ((i > 0 && set.Cars[i - 1].Def.Id == otherId) ||
                         (i + 1 < set.Cars.Length && set.Cars[i + 1].Def.Id == otherId));
                    if (adjacent) continue;
                    int myId = set.Cars[i].Def.Id;
                    if (known && IsPairAssertSuppressed(myId, otherId))
                        continue; // an in-flight couple request holds this exact pair (F1)
                    // Every action logs — the 08-04 gauntlet spent a session unable to tell
                    // "sweep never acted" from "sweep acted invisibly" from "sweep threw".
                    try
                    {
                        coupler.Uncouple(playAudio: false, calledOnOtherCoupler: false, dueToBrokenCouple: false, viaChainInteraction: false);
                        _log($"[trains] reconcile: detached car {myId} from " +
                             (known ? $"car {otherId}" : "a foreign car") + " (not def-adjacent)");
                    }
                    catch (Exception e)
                    {
                        _log($"[trains] reconcile: detach on car {myId} FAILED ({e.Message})");
                    }
                }
            }

            // The inverse assertion the old repair never had: def-adjacent neighbours must be
            // coupled. CoupleAdjacent is idempotent (skips held couplers, 8 m proximity guard).
            if (AllPlaced(set)) CoupleAdjacent(set);

            ReconcileChainStates(set);
        }
    }

    /// <summary>Chain-VISUAL truth follows coupler truth (the third face of F1, found live
    /// 2026-08-05). The only bridge from a programmatic <c>CoupleTo</c>/<c>Uncouple</c> into the
    /// chain FSM is <c>ChainCouplerCouplerAdapter</c>'s Coupled/Uncoupled subscription, and that
    /// subscription is made by a coroutine that waits FRAMES after spawn — our spawn-tick
    /// CoupleAdjacent fires the event into a void on fresh cars. Healing = disable/enable the
    /// chain script: OnEnable re-runs DV's own Determine_Next_State → TryRestoreState, which
    /// rebuilds the pair from the <c>Coupler.state</c> fields. DV's streaming optimizer toggles
    /// these scripts routinely, so the primitive is vanilla-safe.
    ///
    /// Predicate (refined 2026-08-05 after the S060 wedge survived the original heal): a coupled
    /// pair is healthy only when (1) top-level FSM agrees it is attached, (2) the OWNER side has a
    /// live screw + intact mutual <c>attachedTo</c> link (Entry_Attached's own postconditions —
    /// the S060 wedge read consistent top-level but had a dead tensioner AND a dead hook), and
    /// (3) the pair has EXACTLY ONE owner (Attached_* on one side, Other_Attached_* on the
    /// other — DV's asymmetric-by-design invariant; two owners or zero is a wedge). Pair-level
    /// heals stamp DV's own programmatic-couple fields first (mine Attached_Tight, partner
    /// Parked, joint tight via <c>SetChainTight</c>) so the re-determine is DETERMINISTIC and
    /// restores TIGHT — forced couples restoring loose was the G3′ nit; tight is the vanilla
    /// default for a committed couple, and a loose restore hands out single-grab uncouples.
    ///
    /// Never touched: Being_Dragged (a live player gesture), the screw animations while coupled
    /// (Attached_Tightening/Loosening — the F1 uncouple flow is loosen THEN grab; re-tightening
    /// on the sweep cadence would race the player's hand), a healthy Attached_Loose pair (a
    /// player loosened it deliberately; the screw is live and vanilla gives them that), scripts
    /// inactive-in-hierarchy (they re-determine on activation), and pairs inside the F1
    /// suppression window (an in-flight request holds them).</summary>
    private void ReconcileChainStates(RemoteSet set)
    {
        foreach (Entry entry in set.Cars)
        {
            TrainCar? car = entry.Car;
            if (car == null) continue;
            foreach (Coupler coupler in car.couplers)
            {
                if (coupler == null) continue;
                ChainCouplerInteraction? chain = coupler.ChainScript;
                if (chain == null || !chain.isActiveAndEnabled) continue;
                if (chain.couplerAdapter == null || chain.couplerAdapter.coupler == null) continue;

                ChainCouplerInteraction.State fsm = chain.CurrentState;
                if (fsm == ChainCouplerInteraction.State.Being_Dragged) continue;
                bool transient = fsm == ChainCouplerInteraction.State.Enabled
                    || fsm == ChainCouplerInteraction.State.Determine_Next_State
                    || fsm == ChainCouplerInteraction.State.Disabled;
                if (transient) continue;

                bool coupled = coupler.IsCoupled();
                if (!coupled)
                {
                    // The original (proven) half: an attached-looking FSM on an uncoupled coupler
                    // re-determines from truth. No fields to stamp — there is no pair.
                    bool fsmSaysAttached = fsm != ChainCouplerInteraction.State.Parked
                        && fsm != ChainCouplerInteraction.State.Dangling;
                    if (fsmSaysAttached)
                        HealChain(chain, null, entry, car, coupler, $"was {fsm} while uncoupled");
                    continue;
                }

                // Coupled from here. The screw animations are sub-second live gestures.
                if (fsm == ChainCouplerInteraction.State.Attached_Tightening_Couple
                    || fsm == ChainCouplerInteraction.State.Attached_Loosening_Uncouple) continue;

                // An in-flight request holds this pair (F1) — same rule as the coupler sweep.
                if (coupler.coupledTo != null && coupler.coupledTo.train != null
                    && _serverIdByCar.TryGetValue(coupler.coupledTo.train, out int partnerId)
                    && IsPairAssertSuppressed(entry.Def.Id, partnerId)) continue;

                ChainCouplerInteraction? partner = coupler.coupledTo?.ChainScript;
                bool iOwn = IsChainOwnerState(fsm);
                bool partnerOwns = partner != null && IsChainOwnerState(partner.CurrentState);

                if (fsm == ChainCouplerInteraction.State.Parked
                    || fsm == ChainCouplerInteraction.State.Dangling)
                {
                    // Detached-looking while coupled (the original heal, proven in G3′): toggle
                    // WITHOUT stamping — if the partner is a healthy owner, TryRestoreState maps
                    // us to Other_Attached_Parked from the fields CoupleTo already stamped;
                    // stamping ourselves tight here would steal a healthy partner's ownership.
                    HealChain(chain, null, entry, car, coupler, $"was {fsm} while coupled");
                    continue;
                }

                // S060: the owner's screw and link are Entry_Attached postconditions — a dead
                // tensioner (inactive screwButton) or a broken mutual link is the sub-state wedge
                // the top-level check cannot see.
                bool deadOwner = iOwn &&
                    (chain.screwButton == null || !chain.screwButton.activeSelf
                     || chain.attachedTo == null || chain.attachedTo.attachedTo != chain);

                // Exactly one owner per pair (asymmetric by design). Both-own or neither-owns
                // can settle visually "coupled" while every interactable on it is dead.
                bool ownerCountWrong = partner != null && iOwn == partnerOwns;

                if (!deadOwner && !ownerCountWrong) continue;

                string why = deadOwner
                    ? $"owner {fsm} with dead screw/link"
                    : iOwn ? $"two owners ({fsm} both sides)" : "no owner on either side";
                HealChain(chain, partner, entry, car, coupler, why);
            }
        }
    }

    /// <summary>Attached_* = this side owns the chain visual; Other_Attached_* = the partner
    /// does. CurrentState never reports the abstract Attached/Enabled superstates.</summary>
    private static bool IsChainOwnerState(ChainCouplerInteraction.State s) =>
        s == ChainCouplerInteraction.State.Attached_Loose
        || s == ChainCouplerInteraction.State.Attached_Tight
        || s == ChainCouplerInteraction.State.Attached_Tightening_Couple
        || s == ChainCouplerInteraction.State.Attached_Loosening_Uncouple;

    /// <summary>The heal primitive. With a partner (pair-level wedge) it first stamps DV's own
    /// programmatic-couple fields — mine Attached_Tight, partner Parked, joint tight — exactly
    /// what <c>CoupleTo(viaChainInteraction: false)</c> writes, so the re-determine on both sides
    /// deterministically rebuilds owner Attached_Tight / partner Other_Attached_Parked (TIGHT
    /// restore). Without a partner it only toggles; TryRestoreState reads the existing fields.</summary>
    private void HealChain(ChainCouplerInteraction chain, ChainCouplerInteraction? partner,
        Entry entry, TrainCar car, Coupler coupler, string why)
    {
        try
        {
            ChainCouplerInteraction.State before = chain.CurrentState;
            if (partner != null)
            {
                coupler.state = ChainCouplerInteraction.State.Attached_Tight;
                if (coupler.coupledTo != null)
                    coupler.coupledTo.state = ChainCouplerInteraction.State.Parked;
                coupler.SetChainTight(tight: true); // joint + field; no-ops if the joint is gone
            }
            chain.enabled = false;
            chain.enabled = true;
            if (partner != null && partner.isActiveAndEnabled)
            {
                // Partner second: its Determine sees us already in Attached and yields to
                // Other_Attached_Parked (the Parked field is not restorable, by DV's own list).
                partner.enabled = false;
                partner.enabled = true;
            }
            string end = car.frontCoupler == coupler ? "front" : "rear";
            _log($"[trains] reconcile: chain FSM on car {entry.Def.Id} ({end}) {why} — " +
                 $"re-determined {before}→{chain.CurrentState}" +
                 (partner != null ? $" (partner {partner.CurrentState})" : ""));
        }
        catch (Exception e)
        {
            _log($"[trains] reconcile: chain FSM heal on car {entry.Def.Id} FAILED ({e.Message})");
        }
    }

    private static bool AllPlaced(RemoteSet set)
    {
        foreach (Entry entry in set.Cars)
        {
            if (entry.Car == null || !entry.HasTarget) return false;
        }
        return true;
    }

    private static Coupler? NearestCoupler(TrainCar car, Vector3 target)
    {
        Coupler? best = null;
        float bestSqr = float.MaxValue;
        foreach (Coupler coupler in car.couplers)
        {
            if (coupler == null) continue;
            float d = (coupler.transform.position - target).sqrMagnitude;
            if (d < bestSqr)
            {
                bestSqr = d;
                best = coupler;
            }
        }
        return best;
    }

    // ── lifecycle plumbing ──

    /// <summary>Distance from the local player to the set's lead car per THIS snapshot (render
    /// space). False when there is no player or the position can't be resolved — in which case
    /// nothing should materialize.</summary>
    private static bool TryDistanceToPlayer(TrainsetSnapshot snap, TrackIndexMap map, out float distance)
    {
        distance = float.MaxValue;
        Transform player = PlayerManager.PlayerTransform;
        if (player == null) return false;
        CarSnapshot lead = snap.Cars[0];
        Vector3 pos;
        if (lead.Derailed)
        {
            pos = PresenceShim.ToLocalPosition(lead.Pose);
        }
        else if (!map.TryGetLocalPoint(lead.Front.EdgeId, lead.Front.S, out pos, out _))
        {
            return false;
        }
        distance = Vector3.Distance(player.position, pos);
        return true;
    }

    /// <summary>Voluntary despawn past the hysteresis boundary — beats letting DV's streamer kill
    /// the cars at its own (unknown) radius and treating it as a surprise.</summary>
    private void Dematerialize(RemoteSet set)
    {
        DespawnEntries(set);
        foreach (Entry entry in set.Cars) entry.HasTarget = false;
        set.Spawned = false;
        set.CouplingChecked = false;
        set.FarLogged = false;
    }

    /// <summary>DV destroyed one of our spawned cars (distance streaming's ECS conversion — it
    /// ignores preventDelete). Tear the set down quietly: survivors are deleted by us, everything
    /// unmaps, and the set re-materializes near the player after a cooldown.</summary>
    private void OnRemoteCarDestroyed(RemoteSet set, TrainCar dying)
    {
        if (_deletingOurs || !set.Spawned) return;
        set.Spawned = false;
        set.CouplingChecked = false;
        set.FarLogged = false;
        set.NextMaterializeAllowed = Time.unscaledTime + StreamOutCooldownSeconds;
        _log($"[trains] remote consist {set.Def.Id}: the game streamed its cars out — " +
             "re-materializes when close (after a short cooldown)");
        var survivors = new List<TrainCar>();
        foreach (Entry entry in set.Cars)
        {
            TrainCar? car = entry.Car;
            entry.Car = null;
            entry.HasTarget = false;
            if (car == null) continue;
            UnhookDestroy(car);
            Unmap(car);
            if (!ReferenceEquals(car, dying)) survivors.Add(car);
        }
        DeleteCars(survivors);
    }

    private void UnhookDestroy(TrainCar car)
    {
        if (_destroyHooks.TryGetValue(car, out Action? onGone))
        {
            car.OnCarAboutToBeDestroyed -= onGone;
            _destroyHooks.Remove(car);
        }
    }

    private void HandleLostCars(RemoteSet set)
    {
        // Something local destroyed a hardened car (shouldn't happen). Respawn the set from the
        // next snapshot a few times; if it keeps dying, stop fighting and fall back to boxes.
        int attempts = _respawns.TryGetValue(set.Def.Id, out int n) ? n + 1 : 1;
        _respawns[set.Def.Id] = attempts;
        _log($"[trains] remote consist {set.Def.Id} lost car(s) locally — " +
             (attempts <= MaxRespawnsPerSet ? $"respawning (attempt {attempts}/{MaxRespawnsPerSet})" : "falling back to ghost boxes"));
        DespawnEntries(set);
        foreach (Entry entry in set.Cars)
        {
            entry.Car = null;
            entry.HasTarget = false;
        }
        set.Spawned = false;
        set.CouplingChecked = false;
        set.NextMaterializeAllowed = Time.unscaledTime + StreamOutCooldownSeconds;
        if (attempts > MaxRespawnsPerSet) FallBackToGhost(set);
    }

    private void FallBackToGhost(RemoteSet set)
    {
        DespawnEntries(set);
        _sets.Remove(set.Def.Id);
        _ghostSets.Add(set.Def.Id);
        _ghosts.EnsureSet(set.Def);
    }

    private void DespawnEntries(RemoteSet set)
    {
        var cars = new List<TrainCar>();
        foreach (Entry entry in set.Cars)
        {
            if (entry.Car == null) continue;
            UnhookDestroy(entry.Car);
            Unmap(entry.Car);
            cars.Add(entry.Car);
            entry.Car = null;
        }
        DeleteCars(cars);
    }

    private void DeleteCars(List<TrainCar> cars)
    {
        if (cars.Count == 0) return;
        _deletingOurs = true; // our own deletions must not read as DV stream-outs
        try
        {
            CarSpawner spawner = CarSpawner.Instance;
            if (spawner == null) return; // world is going down; Unity is destroying them anyway
            foreach (TrainCar car in cars)
            {
                if (car != null) car.preventDelete = false;
            }
            spawner.DeleteTrainCarsInstant(cars);
        }
        catch (Exception e)
        {
            _log($"[trains] remote car despawn failed ({e.Message}) — world teardown?");
        }
        finally
        {
            _deletingOurs = false;
        }
    }

    private void Map(TrainCar car, int serverCarId)
    {
        _serverIdByCar[car] = serverCarId;
        _carByServerId[serverCarId] = car;
    }

    private void Unmap(TrainCar car)
    {
        if (_serverIdByCar.TryGetValue(car, out int id))
        {
            _serverIdByCar.Remove(car);
            _carByServerId.Remove(id);
        }
    }
}
