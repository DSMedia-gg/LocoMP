using System;
using System.Collections.Generic;

namespace LocoMP.Core.Trains;

/// <summary>
/// The replication atom (03 §1 tenet 3): a consist's identity, epoch, simulation owner, and ordered
/// car list. <see cref="Epoch"/> is the heart of the consist transaction protocol (03 §4): every
/// snapshot is stamped with the epoch it describes, and any snapshot whose (id, epoch) is stale is
/// discarded — membership races are eliminated by construction. Car order is physical coupling
/// order; snapshots index cars by this order rather than repeating ids.
/// </summary>
public sealed class TrainsetDef
{
    public TrainsetDef(int id, uint epoch, int ownerId, IReadOnlyList<CarDef> cars)
    {
        if (cars is null) throw new ArgumentNullException(nameof(cars));
        if (cars.Count == 0) throw new ArgumentException("a trainset has at least one car", nameof(cars));
        Id = id;
        Epoch = epoch;
        OwnerId = ownerId;
        Cars = cars;
    }

    /// <summary>Server-assigned trainset id. Membership changes RETIRE parent ids and mint fresh
    /// ones, so a snapshot addressed to a pre-transaction grouping can never match (03 §4).</summary>
    public int Id { get; }

    /// <summary>Bumped by every committed transaction touching this lineage (merge/split/derail/rerail).</summary>
    public uint Epoch { get; }

    /// <summary>Player currently simulating this consist; 0 = parked (server holds it static, 03 §3).</summary>
    public int OwnerId { get; }

    /// <summary>Cars in physical coupling order.</summary>
    public IReadOnlyList<CarDef> Cars { get; }

    /// <summary>Copy with a different owner (ownership handoffs don't bump the epoch — membership
    /// is unchanged, and the server's sender==owner check already drops the old owner's snapshots).</summary>
    public TrainsetDef WithOwner(int ownerId) => new(Id, Epoch, ownerId, Cars);

    public override string ToString() => $"trainset {Id} e{Epoch} owner {OwnerId} ({Cars.Count} cars)";
}
