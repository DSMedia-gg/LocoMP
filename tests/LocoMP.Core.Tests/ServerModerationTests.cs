using LocoMP.Core.Session;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The session-scoped moderation policy (M5.2): owner election, admin roles, session bans, pause-joins.
/// Pure state — the wire and eviction are NetServer's job; here the RULES are pinned (who is immune, what
/// a ban records, that everything is session-local).
/// </summary>
public class ServerModerationTests
{
    private const string Host = "host-key";
    private const string Alice = "alice-key";
    private const string Bob = "bob-key";

    [Fact]
    public void The_first_admitted_key_becomes_owner_and_admin()
    {
        var m = new ServerModeration();
        Assert.Null(m.Owner);
        m.EnsureOwner(Host);
        Assert.Equal(Host, m.Owner);
        Assert.True(m.IsOwner(Host));
        Assert.True(m.IsAdmin(Host));
    }

    [Fact]
    public void Owner_election_is_idempotent_and_later_joiners_are_not_owner()
    {
        var m = new ServerModeration();
        m.EnsureOwner(Host);
        m.EnsureOwner(Alice); // second admit does not steal ownership
        Assert.Equal(Host, m.Owner);
        Assert.False(m.IsOwner(Alice));
        Assert.False(m.IsAdmin(Alice));
    }

    [Fact]
    public void A_plain_player_is_neither_admin_nor_banned()
    {
        var m = new ServerModeration();
        m.EnsureOwner(Host);
        Assert.False(m.IsAdmin(Alice));
        Assert.False(m.IsBanned(Alice));
    }

    [Fact]
    public void Promote_grants_admin_and_demote_revokes_it()
    {
        var m = new ServerModeration();
        m.EnsureOwner(Host);
        Assert.True(m.Promote(Alice));
        Assert.True(m.IsAdmin(Alice));
        Assert.False(m.Promote(Alice)); // already admin — no change
        Assert.True(m.Demote(Alice));
        Assert.False(m.IsAdmin(Alice));
    }

    [Fact]
    public void The_owner_can_never_be_demoted()
    {
        var m = new ServerModeration();
        m.EnsureOwner(Host);
        Assert.False(m.Demote(Host));
        Assert.True(m.IsAdmin(Host));
    }

    [Fact]
    public void Ban_records_the_key_and_unban_lifts_it()
    {
        var m = new ServerModeration();
        m.EnsureOwner(Host);
        Assert.True(m.Ban(Bob));
        Assert.True(m.IsBanned(Bob));
        Assert.False(m.Ban(Bob));  // already banned — no change
        Assert.Contains(Bob, m.BannedKeys);
        Assert.True(m.Unban(Bob));
        Assert.False(m.IsBanned(Bob));
    }

    [Fact]
    public void The_owner_can_never_be_banned()
    {
        var m = new ServerModeration();
        m.EnsureOwner(Host);
        Assert.False(m.Ban(Host));
        Assert.False(m.IsBanned(Host));
    }

    [Fact]
    public void Pause_joins_is_a_plain_toggle_defaulting_off()
    {
        var m = new ServerModeration();
        Assert.False(m.JoinsPaused);
        m.JoinsPaused = true;
        Assert.True(m.JoinsPaused);
    }

    [Fact]
    public void Empty_or_null_keys_are_inert()
    {
        var m = new ServerModeration();
        m.EnsureOwner("");            // no owner elected from an empty key
        Assert.Null(m.Owner);
        Assert.False(m.IsAdmin(""));
        Assert.False(m.IsBanned(""));
        Assert.False(m.Ban(""));
        Assert.False(m.Promote(""));
    }
}
