namespace LocoMP.Core.Presence;

/// <summary>
/// One roster-status entry (M5.2): a player's session role and measured server round-trip time —
/// the per-player line the player list renders beside the name. Unlike <see cref="PlayerState"/>
/// (identity + pose, fixed name), this is live status: the server re-pushes it on membership/role
/// changes and on a slow cadence for the ping refresh.
/// </summary>
public readonly struct PlayerStatus
{
    public PlayerStatus(int id, PlayerRole role, int? pingMs)
    {
        Id = id;
        Role = role;
        PingMs = pingMs;
    }

    /// <summary>The server-assigned player id this status describes.</summary>
    public int Id { get; }

    public PlayerRole Role { get; }

    /// <summary>Measured round-trip time to the server in ms; null = not known/measured (a peer the
    /// transport has no ping for). A live in-process link legitimately reads 0.</summary>
    public int? PingMs { get; }
}
