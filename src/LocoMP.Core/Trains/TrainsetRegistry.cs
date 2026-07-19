using System;
using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Session;

namespace LocoMP.Core.Trains;

/// <summary>
/// The server's authoritative record of every consist (03 §3 — state authority). All membership and
/// mode changes go through the Try* methods, which validate a client proposal and either commit —
/// returning the <see cref="TrainsetTransaction"/> to broadcast — or refuse with an exact reason.
/// The epoch rules live here and ONLY here (hard rule 5): merges and splits retire the parent ids
/// and mint fresh ones with epoch = max(parents)+1; derail/rerail keep the id and bump the epoch.
/// Game-free by design — the whole transaction space is fuzzed headless (03 §11).
/// </summary>
public sealed class TrainsetRegistry
{
    private readonly IClock _clock;
    private readonly Dictionary<int, TrainsetDef> _sets = new();
    private readonly Dictionary<int, int> _carToSet = new();
    private readonly Dictionary<int, long> _createdAtMs = new();
    private int _nextTrainsetId = 1;
    private int _nextCarId = 1;

    public TrainsetRegistry(IClock clock, long settleMs = 2000, float maxCoupleRelV = 10f)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        SettleMs = settleMs;
        MaxCoupleRelV = maxCoupleRelV;
    }

    /// <summary>No coupling involving a trainset younger than this (Open Rails settling guard, 03 §4).</summary>
    public long SettleMs { get; }

    /// <summary>Reject couplings reported above this closing speed (m/s) — physically implausible.</summary>
    public float MaxCoupleRelV { get; }

    /// <summary>All live trainsets, keyed by id.</summary>
    public IReadOnlyDictionary<int, TrainsetDef> Sets => _sets;

    /// <summary>Look up the trainset currently containing a car.</summary>
    public bool TryFindCar(int carId, out TrainsetDef set)
    {
        if (_carToSet.TryGetValue(carId, out int setId) && _sets.TryGetValue(setId, out TrainsetDef? found))
        {
            set = found;
            return true;
        }
        set = null!;
        return false;
    }

    /// <summary>
    /// Admit an existing consist into the session (world source registering its cars, or a mid-
    /// session spawn). The server assigns both the trainset id and every car id (any ids on the
    /// specs are ignored); everything else on the spec — kind, derailed, world identity, cargo —
    /// is preserved (M3.5c: job paperwork and rebinding name cars by their game identity).
    /// </summary>
    public TrainsetDef Register(int ownerId, IReadOnlyList<CarDef> carSpecs)
    {
        if (carSpecs is null) throw new ArgumentNullException(nameof(carSpecs));
        if (carSpecs.Count == 0) throw new ArgumentException("a trainset has at least one car", nameof(carSpecs));

        var cars = new CarDef[carSpecs.Count];
        for (int i = 0; i < carSpecs.Count; i++)
        {
            CarDef spec = carSpecs[i];
            cars[i] = new CarDef(_nextCarId++, spec.Kind, spec.Derailed,
                spec.GameId, spec.GameGuid, spec.CargoId, spec.CargoAmount);
        }

        var def = new TrainsetDef(_nextTrainsetId++, epoch: 1, ownerId, cars);
        Commit(def);
        return def;
    }

    /// <summary>Commit a live cargo change on one car (owner-reported, M3.5c). Cargo is NOT
    /// membership, so the epoch does not move — snapshots stay admissible across a load. The
    /// updated def is what late joiners and saves will carry.</summary>
    public bool TryUpdateCargo(int carId, string cargoId, float cargoAmount,
        out TrainsetDef? updated, out string? reason)
    {
        updated = null;
        if (!TryFindCar(carId, out TrainsetDef set)) { reason = $"unknown car {carId}"; return false; }

        var cars = set.Cars.Select(c => c.Id == carId ? c.WithCargo(cargoId, cargoAmount) : c).ToArray();
        updated = new TrainsetDef(set.Id, set.Epoch, set.OwnerId, cars);
        _sets[set.Id] = updated;
        reason = null;
        return true;
    }

    /// <summary>Validate + commit a coupling contact (03 §4 step 2). Product owner = the proposer.</summary>
    public bool TryCouple(int proposerId, int carA, CoupleEnd endA, int carB, CoupleEnd endB, float relV,
        out TrainsetTransaction? txn, out string? reason)
    {
        txn = null;
        if (!TryFindCar(carA, out TrainsetDef a)) { reason = $"unknown car {carA}"; return false; }
        if (!TryFindCar(carB, out TrainsetDef b)) { reason = $"unknown car {carB}"; return false; }
        if (a.Id == b.Id) { reason = "cars are already in the same trainset"; return false; }
        if (proposerId != a.OwnerId && proposerId != b.OwnerId)
        {
            reason = $"player {proposerId} owns neither trainset {a.Id} nor {b.Id}";
            return false;
        }
        if (!IsAtEnd(a, carA, endA)) { reason = $"car {carA} is not the {endA} end of trainset {a.Id}"; return false; }
        if (!IsAtEnd(b, carB, endB)) { reason = $"car {carB} is not the {endB} end of trainset {b.Id}"; return false; }
        if (Math.Abs(relV) > MaxCoupleRelV) { reason = $"closing speed {relV:F1} m/s exceeds {MaxCoupleRelV:F1}"; return false; }
        if (a.Cars.Concat(b.Cars).Any(c => c.Derailed)) { reason = "cannot couple derailed cars"; return false; }

        long now = _clock.NowMs;
        if (now - _createdAtMs[a.Id] < SettleMs || now - _createdAtMs[b.Id] < SettleMs)
        {
            reason = "trainset still settling";
            return false;
        }

        // Order the merged car list so the two named trainset ends meet in the middle.
        IEnumerable<CarDef> merged = (endA, endB) switch
        {
            (CoupleEnd.Rear, CoupleEnd.Front) => a.Cars.Concat(b.Cars),
            (CoupleEnd.Rear, CoupleEnd.Rear) => a.Cars.Concat(b.Cars.Reverse()),
            (CoupleEnd.Front, CoupleEnd.Rear) => b.Cars.Concat(a.Cars),
            _ => b.Cars.Reverse().Concat(a.Cars), // (Front, Front)
        };

        uint epoch = Math.Max(a.Epoch, b.Epoch) + 1;
        var product = new TrainsetDef(_nextTrainsetId++, epoch, proposerId, merged.ToArray());

        Retire(a.Id);
        Retire(b.Id);
        Commit(product);

        txn = new TrainsetTransaction(TrainsetTransactionType.Merge, new[] { a.Id, b.Id }, new[] { product });
        reason = null;
        return true;
    }

    /// <summary>Validate + commit an uncouple: split between gapIndex and gapIndex+1 (owner-only).</summary>
    public bool TryUncouple(int proposerId, int trainsetId, int gapIndex,
        out TrainsetTransaction? txn, out string? reason)
    {
        txn = null;
        if (!_sets.TryGetValue(trainsetId, out TrainsetDef? set)) { reason = $"unknown trainset {trainsetId}"; return false; }
        if (proposerId != set.OwnerId) { reason = $"player {proposerId} does not own trainset {trainsetId}"; return false; }
        if (gapIndex < 0 || gapIndex >= set.Cars.Count - 1)
        {
            reason = $"gap index {gapIndex} out of range for {set.Cars.Count} cars";
            return false;
        }

        uint epoch = set.Epoch + 1;
        var head = new TrainsetDef(_nextTrainsetId++, epoch, proposerId, set.Cars.Take(gapIndex + 1).ToArray());
        var tail = new TrainsetDef(_nextTrainsetId++, epoch, proposerId, set.Cars.Skip(gapIndex + 1).ToArray());

        Retire(set.Id);
        Commit(head);
        Commit(tail);

        txn = new TrainsetTransaction(TrainsetTransactionType.Split, new[] { set.Id }, new[] { head, tail });
        reason = null;
        return true;
    }

    /// <summary>Commit a derail reported by the sim owner: flag cars, bump epoch, keep the id.</summary>
    public bool TryDerail(int proposerId, int trainsetId, IReadOnlyList<int> carIds,
        out TrainsetTransaction? txn, out string? reason)
    {
        txn = null;
        if (carIds is null || carIds.Count == 0) { reason = "no cars named"; return false; }
        if (!_sets.TryGetValue(trainsetId, out TrainsetDef? set)) { reason = $"unknown trainset {trainsetId}"; return false; }
        if (proposerId != set.OwnerId) { reason = $"player {proposerId} does not own trainset {trainsetId}"; return false; }

        var targets = new HashSet<int>(carIds);
        foreach (int id in targets)
        {
            if (set.Cars.All(c => c.Id != id)) { reason = $"car {id} not in trainset {trainsetId}"; return false; }
        }
        if (set.Cars.Where(c => targets.Contains(c.Id)).All(c => c.Derailed)) { reason = "cars already derailed"; return false; }

        var cars = set.Cars.Select(c => targets.Contains(c.Id) ? c.WithDerailed(true) : c).ToArray();
        var product = new TrainsetDef(set.Id, set.Epoch + 1, set.OwnerId, cars);
        _sets[set.Id] = product;

        txn = new TrainsetTransaction(TrainsetTransactionType.Derail, Array.Empty<int>(), new[] { product });
        reason = null;
        return true;
    }

    /// <summary>
    /// Commit a rerail (comms-radio path — ANY player may request it, 03 §4): clear all derailed
    /// flags, bump the epoch. Placement is delegated to the owner's first snapshot at the new epoch,
    /// which re-baselines everyone; a server-chosen spline pose needs topology and lands with the
    /// extractor work.
    /// </summary>
    public bool TryRerail(int requesterId, int trainsetId, out TrainsetTransaction? txn, out string? reason)
    {
        txn = null;
        if (!_sets.TryGetValue(trainsetId, out TrainsetDef? set)) { reason = $"unknown trainset {trainsetId}"; return false; }
        if (set.Cars.All(c => !c.Derailed)) { reason = "nothing derailed"; return false; }

        var cars = set.Cars.Select(c => c.WithDerailed(false)).ToArray();
        var product = new TrainsetDef(set.Id, set.Epoch + 1, set.OwnerId, cars);
        _sets[set.Id] = product;

        txn = new TrainsetTransaction(TrainsetTransactionType.Rerail, Array.Empty<int>(), new[] { product });
        reason = null;
        return true;
    }

    /// <summary>Park every trainset a departing player owns (owner → 0, positions freeze, 03 §3).
    /// Returns the affected trainset ids for the ownership broadcast.</summary>
    public List<int> Park(int ownerId)
    {
        var affected = new List<int>();
        foreach (int id in _sets.Keys.ToList())
        {
            if (_sets[id].OwnerId != ownerId) continue;
            _sets[id] = _sets[id].WithOwner(0);
            affected.Add(id);
        }
        return affected;
    }

    /// <summary>Grant simulation ownership of an unowned (parked) trainset to a player.</summary>
    public bool TryClaim(int playerId, int trainsetId, out TrainsetDef? claimed, out string? reason)
    {
        claimed = null;
        if (!_sets.TryGetValue(trainsetId, out TrainsetDef? set)) { reason = $"unknown trainset {trainsetId}"; return false; }
        if (set.OwnerId != 0 && set.OwnerId != playerId)
        {
            reason = $"trainset {trainsetId} is owned by player {set.OwnerId}";
            return false;
        }

        claimed = set.WithOwner(playerId);
        _sets[trainsetId] = claimed;
        reason = null;
        return true;
    }

    /// <summary>Delete a trainset outright (admin/despawn path; not a split or merge).</summary>
    public bool Remove(int trainsetId)
    {
        if (!_sets.TryGetValue(trainsetId, out TrainsetDef? set)) return false;
        foreach (CarDef car in set.Cars) _carToSet.Remove(car.Id);
        _sets.Remove(trainsetId);
        _createdAtMs.Remove(trainsetId);
        return true;
    }

    /// <summary>
    /// The snapshot admission check (03 §4 step 3): the trainset must exist, the epoch must be
    /// CURRENT, and the sender must be the simulation owner. Anything else is stale or spoofed and
    /// must be dropped, never applied.
    /// </summary>
    public bool IsCurrentFromOwner(int senderId, int trainsetId, uint epoch) =>
        _sets.TryGetValue(trainsetId, out TrainsetDef? set) && set.Epoch == epoch && set.OwnerId == senderId;

    /// <summary>Snapshot every def + the id counters for the world save (persistence v1). Counters
    /// travel with the save so a restored world never re-mints an id a saved job or log referenced.</summary>
    internal (List<TrainsetDef> sets, int nextTrainsetId, int nextCarId) CaptureState() =>
        (_sets.Values.ToList(), _nextTrainsetId, _nextCarId);

    /// <summary>Rebuild from a save (cold restart, before any peer connects). Ids and epochs are
    /// preserved exactly; every set is parked — everyone is offline after a restart (03 §3).</summary>
    internal void RestoreState(IEnumerable<TrainsetDef> sets, int nextTrainsetId, int nextCarId)
    {
        _sets.Clear();
        _carToSet.Clear();
        _createdAtMs.Clear();
        foreach (TrainsetDef def in sets) Commit(def.WithOwner(0));
        _nextTrainsetId = nextTrainsetId;
        _nextCarId = nextCarId;
    }

    private static bool IsAtEnd(TrainsetDef set, int carId, CoupleEnd end)
    {
        CarDef edge = end == CoupleEnd.Front ? set.Cars[0] : set.Cars[set.Cars.Count - 1];
        return edge.Id == carId;
    }

    private void Commit(TrainsetDef def)
    {
        _sets[def.Id] = def;
        _createdAtMs[def.Id] = _clock.NowMs;
        foreach (CarDef car in def.Cars) _carToSet[car.Id] = def.Id;
    }

    private void Retire(int trainsetId)
    {
        _sets.Remove(trainsetId);
        _createdAtMs.Remove(trainsetId);
        // Car → set entries are overwritten by the product's Commit; nothing dangles because every
        // retired set's cars appear in exactly one product.
    }
}
