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

    // ── M3: career (02 §4/§6, 03 §3 state authority). Everything economic is server-computed —
    // clients only ever propose (claim/report/abandon/purchase); wallets, payouts, and fees are
    // committed by the server and broadcast as state. All reliable-ordered. ──

    /// <summary>server → client: your career (policy preset, wallet, licenses, active claims).
    /// Sent in the join burst and again on rejoin — this is what makes reconnect restore exact.</summary>
    CareerState = 29,

    /// <summary>server → clients: a job was added to the board (full definition).</summary>
    JobCreated = 30,

    /// <summary>server → clients: a job's lifecycle changed (claimed/progressed/completed/…).</summary>
    JobState = 31,

    /// <summary>client → server: claim an available job (license + claim-limit gated).</summary>
    JobClaimRequest = 32,

    /// <summary>claimant → server: the next task step is done (server validates it is in order).</summary>
    JobTaskReport = 33,

    /// <summary>claimant → server: give the job up; it returns to the board.</summary>
    JobAbandonRequest = 34,

    /// <summary>server → client(s): a wallet balance changed (scope: yours or the shared one).</summary>
    WalletState = 35,

    /// <summary>client → server: buy a license (fee charged through the policy layer).</summary>
    LicensePurchaseRequest = 36,

    /// <summary>server → client(s): a license was granted (scope: yours or the shared set).</summary>
    LicenseState = 37,

    /// <summary>server → client(s): one committed money movement, for UI/log (payout, fee, …).</summary>
    EconomyEvent = 38,

    /// <summary>server → requester: a career proposal was refused, with the exact reason (unlike
    /// train proposals, a refusal here is first-class UX — "missing license: hazmat") and the job
    /// id it concerned (0 = none), so an optimistic native claim can roll itself back (D13).</summary>
    CareerRejected = 39,

    // ── M3.5 (D13): host-native job capture. In host-embedded mode the game's own generator runs
    // on the host and the results are mirrored into the server career; the deterministic core
    // generator remains the dedicated server's source. ──

    /// <summary>world source → server: a game-generated job exists; register it on the board.</summary>
    JobRegister = 40,

    /// <summary>world source → server: an unclaimed game job expired natively; drop it.</summary>
    JobRetract = 41,

    // ── D14: native economy unification. On the host, native money is a live VIEW of the LocoMP
    // wallet and the game's own career manager is the shop: native license grants and finalized
    // register purchases are mirrored into the policy layer instead of running beside it. ──

    /// <summary>world source → server: the game granted a license natively (career manager);
    /// mirror it into the policy scope. No ledger charge rides with the grant — the money side
    /// arrives separately as <see cref="FeeExternal"/> from the register that took the payment.</summary>
    LicenseGrantExternal = 42,

    /// <summary>world source → server: a native register finalized a purchase against the mirrored
    /// wallet (license, fee, shop); burn the amount from the sender's policy wallet.</summary>
    FeeExternal = 43,
}
