using System;
using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Trains;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The host-save leak fix's reversible list surgery (2026-08-06): the Shim hides remote replica cars
/// from DV's <c>CarsSaveManager.GetCarsSaveData</c> by removing them from the live <c>AllCars</c> for
/// the duration of the save, then restoring. The property that MUST hold — because a slip corrupts the
/// host's world, not just the save — is that Extract+Restore reverses to the exact original membership:
/// nothing lost (a lost car = a host car deleted from the world), nothing duplicated (a doubled car =
/// world corruption). A reference-typed token stands in for TrainCar (Core stays game-free).
/// </summary>
public class ReplicaSaveExclusionTests
{
    private sealed class Car
    {
        public Car(string id, bool replica) { Id = id; Replica = replica; }
        public string Id { get; }
        public bool Replica { get; }
        public override string ToString() => Id;
    }

    private static List<Car> World() => new()
    {
        new Car("host-A", replica: false),
        new Car("remote-1", replica: true),
        new Car("host-B", replica: false),
        new Car("remote-2", replica: true),
        new Car("host-C", replica: false),
    };

    [Fact]
    public void Extract_removes_exactly_the_replicas_in_order_and_leaves_the_host_cars()
    {
        List<Car> all = World();
        List<Car> removed = ReplicaSaveExclusion.Extract(all, c => c.Replica);

        Assert.Equal(new[] { "remote-1", "remote-2" }, removed.Select(c => c.Id));
        Assert.Equal(new[] { "host-A", "host-B", "host-C" }, all.Select(c => c.Id));
    }

    [Fact]
    public void Extract_then_Restore_reverses_to_the_exact_original_membership()
    {
        List<Car> all = World();
        var original = all.ToList();

        List<Car> removed = ReplicaSaveExclusion.Extract(all, c => c.Replica);
        ReplicaSaveExclusion.Restore(all, removed);

        // Same set, same references, no loss and no duplication (order may differ — the save doesn't care).
        Assert.Equal(original.Count, all.Count);
        Assert.Equal(original.OrderBy(c => c.Id), all.OrderBy(c => c.Id));
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Restore_never_double_adds_a_car_that_is_already_present()
    {
        // Defensive: if something re-registered a "removed" car during the save window, Restore must
        // not add a second copy — a doubled TrainCar in AllCars is world corruption.
        List<Car> all = World();
        List<Car> removed = ReplicaSaveExclusion.Extract(all, c => c.Replica);
        all.Add(removed[0]);                          // simulate a concurrent re-add of remote-1

        ReplicaSaveExclusion.Restore(all, removed);

        Assert.Equal(1, all.Count(c => c.Id == "remote-1"));
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void A_predicate_that_matches_nothing_leaves_the_list_untouched()
    {
        List<Car> all = World();
        var original = all.ToList();

        List<Car> removed = ReplicaSaveExclusion.Extract(all, _ => false);

        Assert.Empty(removed);
        Assert.Equal(original, all);
    }

    [Fact]
    public void Null_arguments_are_rejected_rather_than_silently_corrupting()
    {
        Assert.Throws<ArgumentNullException>(() => ReplicaSaveExclusion.Extract<Car>(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => ReplicaSaveExclusion.Extract(World(), null!));
        // Restore tolerates a null removed-list (nothing to put back) — no throw.
        List<Car> all = World();
        ReplicaSaveExclusion.Restore(all, null!);
        Assert.Equal(5, all.Count);
    }
}
