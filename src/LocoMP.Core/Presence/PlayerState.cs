using System;

namespace LocoMP.Core.Presence;

/// <summary>
/// A connected player: the server's roster entry and each client's mirror of a peer. Identity
/// (<see cref="Id"/>, <see cref="Name"/>) is fixed for the connection's life; <see cref="Pose"/> is
/// the last-known value, overwritten by every pose packet. Kept deliberately game-free.
/// </summary>
public sealed class PlayerState
{
    public PlayerState(int id, string name, Pose pose)
    {
        Id = id;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Pose = pose;
    }

    /// <summary>Server-assigned id, unique for the session. The player learns its own from JoinAccepted.</summary>
    public int Id { get; }

    public string Name { get; }

    public Pose Pose { get; set; }
}
