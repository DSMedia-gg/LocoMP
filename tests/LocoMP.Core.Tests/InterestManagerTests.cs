using System.Collections.Generic;
using LocoMP.Core.Session;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The interest-management state machine (D10), exercised in isolation — no transport, no session.
/// Synthetic observers and positions drive <see cref="InterestManager"/> directly and record the
/// enter/leave callbacks, so every transition (enter, stay, leave, the hysteresis band, fail-open,
/// forget, disabled) is asserted deterministically.
/// </summary>
public class InterestManagerTests
{
    private sealed class Harness
    {
        public readonly InterestConfig Config = new()
        {
            Enabled = true,
            FilterPlayers = true,
            EnterRadiusM = 100f,
            LeaveRadiusM = 150f,
        };
        public readonly List<int> Observers = new();
        public readonly Dictionary<int, (float X, float Z)?> Pos = new();
        public readonly List<EntityKey> Enters = new();
        public readonly List<EntityKey> Leaves = new();

        public InterestManager Build() => new(
            Config,
            () => Observers,
            id => Pos.TryGetValue(id, out (float X, float Z)? p) ? p : null,
            (peer, key) => Enters.Add(key),
            (peer, key) => Leaves.Add(key));

        public void Add(InterestManager m, int id, float x, float z)
        {
            if (!Observers.Contains(id)) Observers.Add(id);
            Pos[id] = (x, z);
            m.AddClient(id);
        }

        public void MoveTo(int id, float x, float z) => Pos[id] = (x, z);
    }

    private static EntityKey Player(int id) => new(EntityKind.Player, id);

    [Fact]
    public void Nearby_players_are_seeded_in_scope_on_first_anchor_without_an_enter_packet()
    {
        // A client's first pose seeds it with everything it was over-subscribed to (fail-open relayed
        // their streams already), so nearby entities are relevant with NO redundant enter — the enter
        // callback is reserved for a genuine later crossing (see the approach test below).
        var h = new Harness();
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);
        h.Add(m, 2, 50f, 0f); // within the 100 m enter radius

        m.Recompute();

        Assert.True(m.IsRelevant(1, Player(2)));
        Assert.True(m.IsRelevant(2, Player(1)));
        Assert.Empty(h.Enters);
        Assert.Empty(h.Leaves);
    }

    [Fact]
    public void Players_enter_scope_on_approach_and_never_their_own()
    {
        var h = new Harness();
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);
        h.Add(m, 2, 9999f, 0f); // start far → first anchor seeds-then-trims them apart
        m.Recompute();
        Assert.False(m.IsRelevant(1, Player(2)));
        h.Enters.Clear();
        h.Leaves.Clear();

        h.MoveTo(2, 50f, 0f);   // 2 walks into range → a real enter, both directions
        m.Recompute();

        // Exactly two enters — Player1 (into 2's scope) and Player2 (into 1's scope). A third would mean
        // a player entered its OWN scope (the self-exclusion guard failed).
        Assert.Equal(2, h.Enters.Count);
        Assert.Contains(Player(1), h.Enters);
        Assert.Contains(Player(2), h.Enters);
        Assert.True(m.IsRelevant(1, Player(2)));
    }

    [Fact]
    public void A_far_player_past_the_leave_radius_leaves_scope()
    {
        var h = new Harness();
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);
        h.Add(m, 2, 50f, 0f);
        m.Recompute();               // both seeded in scope
        h.Enters.Clear();

        h.MoveTo(2, 200f, 0f);       // 200 m > 150 m leave radius
        m.Recompute();

        Assert.Contains(Player(2), h.Leaves);
        Assert.False(m.IsRelevant(1, Player(2)));
    }

    [Fact]
    public void The_hysteresis_band_neither_enters_nor_drops()
    {
        var h = new Harness();
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);
        h.Add(m, 2, 9999f, 0f);
        m.Recompute();               // first anchor trims 2 out of 1's scope
        Assert.False(m.IsRelevant(1, Player(2)));

        h.MoveTo(2, 120f, 0f);       // between enter (100) and leave (150), coming from OUT: must NOT enter
        m.Recompute();
        Assert.DoesNotContain(Player(2), h.Enters);
        Assert.False(m.IsRelevant(1, Player(2)));

        h.MoveTo(2, 50f, 0f);        // close → enters
        m.Recompute();
        Assert.True(m.IsRelevant(1, Player(2)));
        h.Leaves.Clear();

        h.MoveTo(2, 120f, 0f);       // back into the band while IN scope: must NOT drop
        m.Recompute();
        Assert.DoesNotContain(Player(2), h.Leaves);
        Assert.True(m.IsRelevant(1, Player(2)));

        h.MoveTo(2, 200f, 0f);       // past leave → drops
        m.Recompute();
        Assert.Contains(Player(2), h.Leaves);
        Assert.False(m.IsRelevant(1, Player(2)));
    }

    [Fact]
    public void An_unposed_observer_is_fail_open_and_untrimmed()
    {
        var h = new Harness();
        InterestManager m = h.Build();
        // Observer 1 has no pose (over-subscribed); 2 is far away.
        h.Observers.Add(1);
        m.AddClient(1);
        h.Add(m, 2, 9999f, 0f);

        m.Recompute();

        Assert.True(m.IsRelevant(1, Player(2))); // no anchor yet → everything relevant
        Assert.Empty(h.Leaves);                  // and nothing was trimmed
    }

    [Fact]
    public void First_pose_seeds_then_trims_the_over_subscribed_scope()
    {
        var h = new Harness();
        InterestManager m = h.Build();
        // 1 unposed (fail-open, over-subscribed); 2 posed and FAR.
        h.Observers.Add(1);
        m.AddClient(1);
        h.Add(m, 2, 500f, 0f);
        m.Recompute();                     // 1 still unposed → no trim
        Assert.Empty(h.Leaves);

        h.MoveTo(1, 0f, 0f);               // 1's first real pose
        m.Recompute();

        // The over-subscribed far entity is now hidden via a LEAVE (not silently stranded as a ghost).
        Assert.Contains(Player(2), h.Leaves);
        Assert.False(m.IsRelevant(1, Player(2)));
    }

    [Fact]
    public void ForgetEntity_clears_scope_without_firing_a_leave()
    {
        var h = new Harness();
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);
        h.Add(m, 2, 50f, 0f);
        m.Recompute();
        Assert.True(m.IsRelevant(1, Player(2)));
        h.Leaves.Clear();

        m.ForgetEntity(Player(2));

        Assert.False(m.IsRelevant(1, Player(2)));
        Assert.Empty(h.Leaves); // the authoritative removal (PlayerLeft) does the client cleanup
    }

    [Fact]
    public void Disabled_is_fully_inert()
    {
        var h = new Harness();
        h.Config.Enabled = false;
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);
        h.Add(m, 2, 9999f, 0f);

        m.Recompute();

        Assert.True(m.IsRelevant(1, Player(2))); // everything relevant
        Assert.Empty(h.Enters);
        Assert.Empty(h.Leaves);
    }

    [Fact]
    public void FilterPlayers_off_leaves_players_unfiltered_even_when_enabled()
    {
        var h = new Harness();
        h.Config.FilterPlayers = false;
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);
        h.Add(m, 2, 9999f, 0f);

        m.Recompute();

        Assert.True(m.IsRelevant(1, Player(2)));
        Assert.Empty(h.Enters); // players aren't collected as entities, so nothing tracks
    }

    [Fact]
    public void An_unknown_observer_is_relevant_to_everything()
    {
        var h = new Harness();
        InterestManager m = h.Build();
        h.Add(m, 1, 0f, 0f);

        Assert.True(m.IsRelevant(999, Player(1))); // never AddClient'd → fail-open
    }
}
