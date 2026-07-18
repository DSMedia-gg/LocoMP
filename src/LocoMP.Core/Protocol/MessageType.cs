namespace LocoMP.Core.Protocol;

/// <summary>
/// The 1-byte discriminator prefixing every LocoMP control-plane packet. Wire values are STABLE:
/// only ever append a new member; never renumber or reuse a value (that is a protocol break — bump
/// <see cref="ProtocolVersion"/>). M1 covers presence; trains/economy/items add members in M2+.
/// </summary>
public enum MessageType : byte
{
    /// <summary>client → server: handshake + credentials + display name (join attempt).</summary>
    JoinRequest = 1,

    /// <summary>server → client: admitted; carries your assigned id, server time, and the current roster.</summary>
    JoinAccepted = 2,

    /// <summary>server → client: refused; carries the exact have/need reason (03 §10).</summary>
    JoinRejected = 3,

    /// <summary>server → others: a new player was admitted.</summary>
    PlayerJoined = 4,

    /// <summary>server → others: a player left (graceful or dropped).</summary>
    PlayerLeft = 5,

    /// <summary>client → server (own pose) and server → others (relayed with authoritative id).</summary>
    PlayerPose = 6,

    /// <summary>server → client: authoritative monotonic clock for NTP-style offset (03 §5).</summary>
    TimeSync = 7,

    /// <summary>client → server: graceful disconnect.</summary>
    Leave = 8,
}
