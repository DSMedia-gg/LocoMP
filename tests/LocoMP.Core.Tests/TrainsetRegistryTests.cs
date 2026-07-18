using System.Linq;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// Unit tests for the server's transaction authority — the epoch rules of 03 §4 in isolation.
/// The registry is where "the race is eliminated by construction" is enforced, so every commit
/// shape (all four merge end-combinations, splits, derail/rerail) and every refusal path is
/// pinned down here before the wire gets involved.
/// </summary>
public class TrainsetRegistryTests
{
    private static readonly CarDef[] TwoCars = { new(0, "loco"), new(0, "boxcar") };

    private static TrainsetRegistry NewRegistry(out ManualClock clock)
    {
        clock = new ManualClock();
        return new TrainsetRegistry(clock);
    }

    private static CarDef[] Specs(params string[] kinds) => kinds.Select(k => new CarDef(0, k)).ToArray();

    [Fact]
    public void Register_assigns_ids_and_starts_at_epoch_1()
    {
        TrainsetRegistry reg = NewRegistry(out _);

        TrainsetDef a = reg.Register(1, Specs("loco", "boxcar"));
        TrainsetDef b = reg.Register(2, Specs("flatbed"));

        Assert.Equal(1u, a.Epoch);
        Assert.Equal(1, a.OwnerId);
        Assert.NotEqual(a.Id, b.Id);
        // Car ids are globally unique across trainsets.
        Assert.Equal(3, a.Cars.Concat(b.Cars).Select(c => c.Id).Distinct().Count());
        Assert.True(reg.TryFindCar(a.Cars[0].Id, out TrainsetDef found));
        Assert.Equal(a.Id, found.Id);
    }

    [Theory]
    [InlineData(CoupleEnd.Rear, CoupleEnd.Front, "a1,a2,b1,b2")]  // tail-to-head: A then B
    [InlineData(CoupleEnd.Rear, CoupleEnd.Rear, "a1,a2,b2,b1")]   // tail-to-tail: A then reversed B
    [InlineData(CoupleEnd.Front, CoupleEnd.Rear, "b1,b2,a1,a2")]  // head-to-tail: B then A
    [InlineData(CoupleEnd.Front, CoupleEnd.Front, "b2,b1,a1,a2")] // head-to-head: reversed B then A
    public void Couple_orders_the_merged_cars_by_the_named_ends(CoupleEnd endA, CoupleEnd endB, string expected)
    {
        TrainsetRegistry reg = NewRegistry(out ManualClock clock);
        TrainsetDef a = reg.Register(1, Specs("a1", "a2"));
        TrainsetDef b = reg.Register(2, Specs("b1", "b2"));
        clock.Advance(3000);

        int carA = endA == CoupleEnd.Front ? a.Cars[0].Id : a.Cars[1].Id;
        int carB = endB == CoupleEnd.Front ? b.Cars[0].Id : b.Cars[1].Id;

        Assert.True(reg.TryCouple(1, carA, endA, carB, endB, relV: 1f, out TrainsetTransaction? txn, out _));
        TrainsetDef merged = txn!.Products.Single();

        Assert.Equal(expected, string.Join(",", merged.Cars.Select(c => c.Kind)));
        Assert.Equal(2u, merged.Epoch);                   // max(1, 1) + 1
        Assert.Equal(1, merged.OwnerId);                  // the proposer simulates the product
        Assert.Equal(new[] { a.Id, b.Id }, txn.RetiredIds);
        Assert.False(reg.Sets.ContainsKey(a.Id));         // parents are gone
        Assert.False(reg.Sets.ContainsKey(b.Id));
        Assert.True(reg.Sets.ContainsKey(merged.Id));
    }

    [Fact]
    public void Couple_refuses_a_mid_train_car()
    {
        TrainsetRegistry reg = NewRegistry(out ManualClock clock);
        TrainsetDef a = reg.Register(1, Specs("a1", "a2", "a3"));
        TrainsetDef b = reg.Register(1, Specs("b1"));
        clock.Advance(3000);

        // a2 is in the middle — it is not the Rear end of A.
        Assert.False(reg.TryCouple(1, a.Cars[1].Id, CoupleEnd.Rear, b.Cars[0].Id, CoupleEnd.Front, 1f, out _, out string? reason));
        Assert.Contains("not the Rear end", reason);
    }

    [Fact]
    public void Couple_refuses_a_proposer_who_owns_neither_set()
    {
        TrainsetRegistry reg = NewRegistry(out ManualClock clock);
        TrainsetDef a = reg.Register(1, TwoCars);
        TrainsetDef b = reg.Register(2, Specs("b1"));
        clock.Advance(3000);

        Assert.False(reg.TryCouple(3, a.Cars[1].Id, CoupleEnd.Rear, b.Cars[0].Id, CoupleEnd.Front, 1f, out _, out string? reason));
        Assert.Contains("owns neither", reason);
    }

    [Fact]
    public void Couple_refuses_implausible_closing_speed_and_settling_sets()
    {
        TrainsetRegistry reg = NewRegistry(out ManualClock clock);
        TrainsetDef a = reg.Register(1, TwoCars);
        TrainsetDef b = reg.Register(1, Specs("b1"));

        // Still inside the settle window (Open Rails guard).
        Assert.False(reg.TryCouple(1, a.Cars[1].Id, CoupleEnd.Rear, b.Cars[0].Id, CoupleEnd.Front, 1f, out _, out string? reason));
        Assert.Contains("settling", reason);

        clock.Advance(3000);
        Assert.False(reg.TryCouple(1, a.Cars[1].Id, CoupleEnd.Rear, b.Cars[0].Id, CoupleEnd.Front, 50f, out _, out reason));
        Assert.Contains("closing speed", reason);
    }

    [Fact]
    public void Couple_refuses_derailed_cars()
    {
        TrainsetRegistry reg = NewRegistry(out ManualClock clock);
        TrainsetDef a = reg.Register(1, TwoCars);
        TrainsetDef b = reg.Register(1, Specs("b1"));
        clock.Advance(3000);
        Assert.True(reg.TryDerail(1, b.Id, new[] { b.Cars[0].Id }, out _, out _));

        Assert.False(reg.TryCouple(1, a.Cars[1].Id, CoupleEnd.Rear, b.Cars[0].Id, CoupleEnd.Front, 1f, out _, out string? reason));
        Assert.Contains("derailed", reason);
    }

    [Fact]
    public void Uncouple_splits_into_two_fresh_trainsets_with_bumped_epochs()
    {
        TrainsetRegistry reg = NewRegistry(out ManualClock clock);
        TrainsetDef set = reg.Register(1, Specs("c1", "c2", "c3"));
        clock.Advance(3000);

        Assert.True(reg.TryUncouple(1, set.Id, gapIndex: 0, out TrainsetTransaction? txn, out _));

        Assert.Equal(TrainsetTransactionType.Split, txn!.Type);
        Assert.Equal(new[] { set.Id }, txn.RetiredIds);
        Assert.Equal(2, txn.Products.Length);
        Assert.Equal("c1", txn.Products[0].Cars.Single().Kind);
        Assert.Equal("c2,c3", string.Join(",", txn.Products[1].Cars.Select(c => c.Kind)));
        Assert.All(txn.Products, p => Assert.Equal(2u, p.Epoch)); // parent 1 + 1
        Assert.False(reg.Sets.ContainsKey(set.Id));
    }

    [Fact]
    public void Uncouple_refuses_out_of_range_gaps_and_non_owners()
    {
        TrainsetRegistry reg = NewRegistry(out _);
        TrainsetDef set = reg.Register(1, TwoCars);

        Assert.False(reg.TryUncouple(1, set.Id, gapIndex: 1, out _, out string? reason)); // only gap 0 exists
        Assert.Contains("out of range", reason);
        Assert.False(reg.TryUncouple(2, set.Id, gapIndex: 0, out _, out reason));
        Assert.Contains("does not own", reason);
    }

    [Fact]
    public void Derail_and_rerail_keep_the_id_and_bump_the_epoch_each_time()
    {
        TrainsetRegistry reg = NewRegistry(out _);
        TrainsetDef set = reg.Register(1, Specs("c1", "c2"));

        Assert.True(reg.TryDerail(1, set.Id, new[] { set.Cars[1].Id }, out TrainsetTransaction? derail, out _));
        TrainsetDef derailed = derail!.Products.Single();
        Assert.Equal(set.Id, derailed.Id);
        Assert.Equal(2u, derailed.Epoch);
        Assert.False(derailed.Cars[0].Derailed);
        Assert.True(derailed.Cars[1].Derailed);
        Assert.Empty(derail.RetiredIds);

        // Rerail may come from ANY player (comms-radio path) — requester 9 owns nothing.
        Assert.True(reg.TryRerail(9, set.Id, out TrainsetTransaction? rerail, out _));
        TrainsetDef railed = rerail!.Products.Single();
        Assert.Equal(set.Id, railed.Id);
        Assert.Equal(3u, railed.Epoch);
        Assert.All(railed.Cars, c => Assert.False(c.Derailed));

        Assert.False(reg.TryRerail(9, set.Id, out _, out string? reason)); // nothing left to rerail
        Assert.Contains("nothing derailed", reason);
    }

    [Fact]
    public void Park_frees_ownership_and_claim_takes_it()
    {
        TrainsetRegistry reg = NewRegistry(out _);
        TrainsetDef mine = reg.Register(1, TwoCars);
        TrainsetDef theirs = reg.Register(2, Specs("b1"));

        var parked = reg.Park(1);
        Assert.Equal(new[] { mine.Id }, parked);
        Assert.Equal(0, reg.Sets[mine.Id].OwnerId);
        Assert.Equal(2, reg.Sets[theirs.Id].OwnerId); // untouched

        Assert.False(reg.TryClaim(3, theirs.Id, out _, out string? reason)); // owned by 2
        Assert.Contains("owned by player 2", reason);
        Assert.True(reg.TryClaim(3, mine.Id, out TrainsetDef? claimed, out _));
        Assert.Equal(3, claimed!.OwnerId);
    }

    [Fact]
    public void Snapshot_admission_requires_current_epoch_from_the_owner()
    {
        TrainsetRegistry reg = NewRegistry(out _);
        TrainsetDef set = reg.Register(1, TwoCars);

        Assert.True(reg.IsCurrentFromOwner(1, set.Id, epoch: 1));
        Assert.False(reg.IsCurrentFromOwner(2, set.Id, epoch: 1));   // not the owner
        Assert.False(reg.IsCurrentFromOwner(1, set.Id, epoch: 2));   // wrong epoch
        Assert.False(reg.IsCurrentFromOwner(1, set.Id + 99, epoch: 1)); // unknown trainset

        // After a derail bumps the epoch, the old stamp is dead.
        Assert.True(reg.TryDerail(1, set.Id, new[] { set.Cars[0].Id }, out _, out _));
        Assert.False(reg.IsCurrentFromOwner(1, set.Id, epoch: 1));
        Assert.True(reg.IsCurrentFromOwner(1, set.Id, epoch: 2));
    }
}
