using System;

namespace LocoMP.Core.Trains;

/// <summary>Which membership/mode change a committed transaction describes (03 §4).</summary>
public enum TrainsetTransactionType : byte
{
    Merge = 1,
    Split = 2,
    Derail = 3,
    Rerail = 4,
}

/// <summary>
/// Which end of a TRAINSET a coupling contact involves: Front = the car-index-0 side, Rear = the
/// last-car side. Defined against the trainset (not the car's own couplers) so Core can validate
/// and order the merge without knowing car orientations — the Shim translates a physical coupler
/// contact into the trainset-end form.
/// </summary>
public enum CoupleEnd : byte
{
    Front = 0,
    Rear = 1,
}

/// <summary>
/// A committed, server-authoritative membership/mode change: the parent trainset ids it retires and
/// the full product definitions (fresh ids for merges/splits, bumped epochs always). Broadcast
/// reliable-ordered; every client applies it atomically, after which snapshots stamped with any
/// retired id or stale epoch cannot match anything (03 §4 — the race is gone by construction).
/// </summary>
public sealed class TrainsetTransaction
{
    public TrainsetTransaction(TrainsetTransactionType type, int[] retiredIds, TrainsetDef[] products)
    {
        Type = type;
        RetiredIds = retiredIds ?? throw new ArgumentNullException(nameof(retiredIds));
        Products = products ?? throw new ArgumentNullException(nameof(products));
        if (products.Length == 0) throw new ArgumentException("a transaction has at least one product", nameof(products));
    }

    public TrainsetTransactionType Type { get; }

    /// <summary>Trainset ids that cease to exist when this commits (empty for derail/rerail).</summary>
    public int[] RetiredIds { get; }

    /// <summary>The post-transaction trainsets, complete (id, epoch, owner, cars).</summary>
    public TrainsetDef[] Products { get; }
}
