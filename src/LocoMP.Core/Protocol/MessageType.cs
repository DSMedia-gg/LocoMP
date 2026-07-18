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

    // ── M2: trains (03 §3–§5). Server-committed transactions are reliable-ordered; snapshots are
    // sequenced-unreliable and epoch-stamped so stale ones are discarded by construction (03 §4). ──

    /// <summary>client (world source) → server: register an existing consist; server assigns ids.</summary>
    TrainsetRegister = 9,

    /// <summary>server → client(s): full trainset definition (registration commit, join burst, resync).</summary>
    TrainsetCreate = 10,

    /// <summary>server → clients: a trainset was deleted outright (not split/merged).</summary>
    TrainsetRemove = 11,

    /// <summary>server → clients: committed membership/mode change (merge/split/derail/rerail) — retires
    /// parent ids and carries the full product definitions with bumped epochs (03 §4).</summary>
    TrainsetTransaction = 12,

    /// <summary>sim owner → server (then relayed): epoch-stamped spline-space bogie snapshot (03 §5).</summary>
    TrainsetSnapshot = 13,

    /// <summary>sim owner → server: coupling contact proposal (carA/endA/carB/endB/relV).</summary>
    CoupleProposal = 14,

    /// <summary>sim owner → server: split proposal (trainset, gap index).</summary>
    UncoupleProposal = 15,

    /// <summary>sim owner → server: cars left the rails; server commits a Derail transaction.</summary>
    DerailReport = 16,

    /// <summary>any client → server: rerail a trainset (comms-radio path); commits a Rerail transaction.</summary>
    RerailRequest = 17,

    /// <summary>client → server: manual escape hatch — resend the current definition (03 §4).</summary>
    ResyncRequest = 18,

    /// <summary>client → server: request simulation ownership of an unowned trainset.</summary>
    OwnershipRequest = 19,

    /// <summary>server → clients: simulation owner changed (0 = parked/server-held).</summary>
    TrainsetOwner = 20,

    /// <summary>client → server: junction throw proposal.</summary>
    JunctionThrow = 21,

    /// <summary>server → clients: committed junction state (also sent as a join burst).</summary>
    JunctionState = 22,

    /// <summary>client → server: turntable rotation stream (last-writer-wins for M2).</summary>
    TurntableRotate = 23,

    /// <summary>server → clients: relayed turntable angle.</summary>
    TurntableState = 24,

    /// <summary>client → server: request the control grant for a cab/car (03 §3 GRANT).</summary>
    ControlGrantRequest = 25,

    /// <summary>client → server: release a held control grant.</summary>
    ControlGrantRelease = 26,

    /// <summary>server → clients: grant holder changed (0 = free).</summary>
    ControlGrantState = 27,

    /// <summary>grant holder → server → sim owner: one control moved (controlId, value).</summary>
    ControlInput = 28,
}
