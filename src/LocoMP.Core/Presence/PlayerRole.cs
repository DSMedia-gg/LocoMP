namespace LocoMP.Core.Presence;

/// <summary>
/// A player's session role (M5.2 moderation, surfaced on the roster). Carried as a byte in
/// <see cref="Protocol.MessageType.RosterStatus"/>, so wire values are STABLE — only append.
/// Authority lives in <see cref="Session.ServerModeration"/> (keyed by the secret player key);
/// this is the shareable projection of it, keyed by peer id.
/// </summary>
public enum PlayerRole : byte
{
    Player = 0,

    /// <summary>May kick/ban players and pause joins (promoted by the owner).</summary>
    Admin = 1,

    /// <summary>The session owner (the host — or a dedicated server's first joiner): auto-admin,
    /// immune to kick/ban/demote.</summary>
    Owner = 2,
}
