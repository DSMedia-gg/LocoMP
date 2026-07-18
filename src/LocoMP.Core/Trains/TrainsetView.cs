using System;
using System.Collections.Generic;

namespace LocoMP.Core.Trains;

/// <summary>
/// A client's live mirror of the server's trainset registry, fed by reliable-ordered definition
/// traffic (creates/transactions/ownership) and sequenced-unreliable snapshots. This is where the
/// 03 §4 discard rule executes on the receiving side: a snapshot applies ONLY if its trainset id is
/// known and its epoch EXACTLY matches the current definition. Exact match matters — snapshots ride
/// a different delivery channel than transactions, so a post-transaction snapshot can arrive
/// *before* its transaction; it is dropped and the stream resumes when the definition catches up
/// (the owner's next snapshot re-baselines). The discard counters are the fuzz harness's oracle.
/// </summary>
public sealed class TrainsetView
{
    private readonly Dictionary<int, TrainsetDef> _sets = new();
    private readonly Dictionary<int, TrainsetSnapshot> _latest = new();

    /// <summary>Mirrored trainset definitions, keyed by id.</summary>
    public IReadOnlyDictionary<int, TrainsetDef> Sets => _sets;

    /// <summary>Last applied (current-epoch) snapshot per trainset id.</summary>
    public IReadOnlyDictionary<int, TrainsetSnapshot> LatestSnapshots => _latest;

    public long AppliedSnapshots { get; private set; }

    /// <summary>Snapshots dropped for unknown id, wrong epoch, or car-count mismatch. In a healthy
    /// session this stays near zero (only the brief channel race after a transaction).</summary>
    public long StaleSnapshotsDiscarded { get; private set; }

    /// <summary>A definition arrived for a new trainset (registration commit, join burst, resync).</summary>
    public event Action<TrainsetDef>? TrainsetAdded;

    /// <summary>A committed transaction was applied atomically (retired sets gone, products live).</summary>
    public event Action<TrainsetTransaction>? TransactionApplied;

    /// <summary>A trainset was deleted outright.</summary>
    public event Action<int>? TrainsetRemoved;

    /// <summary>(trainsetId, newOwnerId) — 0 means parked.</summary>
    public event Action<int, int>? OwnerChanged;

    /// <summary>A current-epoch snapshot was accepted (drives interpolation in the Shim).</summary>
    public event Action<TrainsetSnapshot>? SnapshotApplied;

    public void ApplyCreate(TrainsetDef def)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        _sets[def.Id] = def;
        _latest.Remove(def.Id); // a (re)definition invalidates any older kinematic state
        TrainsetAdded?.Invoke(def);
    }

    public void ApplyTransaction(TrainsetTransaction txn)
    {
        if (txn is null) throw new ArgumentNullException(nameof(txn));
        foreach (int id in txn.RetiredIds)
        {
            _sets.Remove(id);
            _latest.Remove(id);
        }
        foreach (TrainsetDef def in txn.Products)
        {
            _sets[def.Id] = def;
            _latest.Remove(def.Id); // epoch changed — the next snapshot re-baselines
        }
        TransactionApplied?.Invoke(txn);
    }

    public void ApplyRemove(int trainsetId)
    {
        if (!_sets.Remove(trainsetId)) return;
        _latest.Remove(trainsetId);
        TrainsetRemoved?.Invoke(trainsetId);
    }

    public void ApplyOwner(int trainsetId, int ownerId)
    {
        if (!_sets.TryGetValue(trainsetId, out TrainsetDef? set)) return;
        _sets[trainsetId] = set.WithOwner(ownerId);
        OwnerChanged?.Invoke(trainsetId, ownerId);
    }

    /// <summary>The receiving half of the 03 §4 invariant. Returns false when the snapshot is stale
    /// (or malformed) and was discarded — it must never be partially applied.</summary>
    public bool TryApplySnapshot(TrainsetSnapshot snap)
    {
        if (snap is null) throw new ArgumentNullException(nameof(snap));
        if (!_sets.TryGetValue(snap.TrainsetId, out TrainsetDef? set) ||
            set.Epoch != snap.Epoch ||
            set.Cars.Count != snap.Cars.Length)
        {
            StaleSnapshotsDiscarded++;
            return false;
        }

        _latest[snap.TrainsetId] = snap;
        AppliedSnapshots++;
        SnapshotApplied?.Invoke(snap);
        return true;
    }
}
