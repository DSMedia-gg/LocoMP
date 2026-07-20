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

    /// <summary>world source → server: grant a license into a policy scope, charge-free. Two uses:
    /// mirroring a NATIVE grant on the host (career manager purchase — the money side arrives
    /// separately as <see cref="FeeExternal"/>), and the HOST-ADMIN grant to a connected player
    /// (M3.5c: the interim answer for fresh guests on a mature world). Carries a target peer id;
    /// 0 = the sender's own scope.</summary>
    LicenseGrantExternal = 42,

    /// <summary>world source → server: a native register finalized a purchase against the mirrored
    /// wallet (license, fee, shop); burn the amount from the sender's policy wallet.</summary>
    FeeExternal = 43,

    // ── M3.5c: remote claim parity + multi-crew input. Controls and cargo are OWNER-authoritative
    // (the sim owner's world is where they physically live); couple/uncouple requests route remote
    // player intent TO the owner, whose native events then drive the normal proposal path — one
    // authority chain, no second commit path (03 §3). ──

    /// <summary>sim owner → server → others: a cab control's committed value (controlId, value).
    /// The server keeps the latest per (car, control) and replays them in the join burst, so a
    /// newcomer's replica levers match reality before anyone touches them.</summary>
    ControlState = 44,

    /// <summary>sim owner → server → others: a car's cargo changed (cargoId, amount; empty id =
    /// unloaded). The server folds it into the stored CarDef so late joiners and saves carry the
    /// current load — cargo is not membership, so the epoch does not move.</summary>
    CargoState = 45,

    /// <summary>server → world source: a remote claimant reported an externally captured job done —
    /// validate it against the game's own task tree and answer with JobCompleteReply.</summary>
    JobCompleteRequest = 46,

    /// <summary>world source → server: the native verdict on a JobCompleteRequest (ok + reason).
    /// On ok the server commits the deferred task report and mints the claimant's payout.</summary>
    JobCompleteReply = 47,

    /// <summary>any client → server → sim owner: a player physically chained two cars together —
    /// the owner performs the real couple and its native event proposes the merge.</summary>
    CoupleRequest = 48,

    /// <summary>any client → server → sim owner: a player physically uncoupled a car's coupler —
    /// the owner performs the real uncouple and its native event proposes the split.</summary>
    UncoupleRequest = 49,

    // ── items (M4, protocol v6) ──

    /// <summary>world source → server: register a WORLD item the host owns (host capture / content
    /// spawning). The server mints the id and echoes a correlation token so the Shim maps its local
    /// GameObject; gated behind <see cref="Items.ItemConfig.AcceptExternalItems"/>.</summary>
    ItemRegister = 50,

    /// <summary>client → server: pick up a world item into my possession (proximity-gated, exclusive).</summary>
    ItemPickupRequest = 51,

    /// <summary>client → server: drop an item I hold into the world at a pose.</summary>
    ItemDropRequest = 52,

    /// <summary>client → server: buy a prefab from a shop — money burns from the policy wallet and
    /// the item mints into my possession (02 §4 win condition: a CLIENT purchase lands in the right
    /// wallet).</summary>
    ItemPurchaseRequest = 53,

    /// <summary>world source → server: a world item is gone in the native world (consumed) — despawn it.</summary>
    ItemDespawnRequest = 54,

    /// <summary>server → clients: an item exists — full identity/state + location. Sent on create and
    /// in the join burst. Leads with a correlation token (0 except the echo to a registrant/buyer).</summary>
    ItemSpawned = 55,

    /// <summary>server → clients: a known item changed location (picked up, dropped, or its holder
    /// went offline/rejoined). Lightweight — the def is already mirrored.</summary>
    ItemMoved = 56,

    /// <summary>server → clients: an item was removed from the world.</summary>
    ItemDespawned = 57,

    /// <summary>server → requester: an item proposal failed — carries the reason and the item id
    /// (0 for a purchase, which has no id yet), the mirror of CareerRejected.</summary>
    ItemRejected = 58,

    // ── M4 shops (protocol v7) ──

    /// <summary>server → client: the shop catalog (itemPrefabName → price in cents) so the client can
    /// render its Buy UI. Sent once in the join burst, mirroring how <see cref="CareerState"/> carries
    /// the license price catalog. The purchase transaction itself is the v6 <see cref="ItemPurchaseRequest"/>;
    /// this only feeds the front-end what is for sale.</summary>
    ItemShopCatalog = 59,

    // ── M4 comms radio (protocol v8): rerail / delete / summon for all players. A remote player's
    // comms-radio action on a host-owned car is intercepted and ROUTED to the car's sim owner (the
    // M3.5c couple/uncouple-request pattern); the owner performs the real action and its native event
    // drives the normal path. Fees burn from the INITIATOR via FeeExternal's new target peer. ──

    /// <summary>client → server: a comms-radio action (rerail/delete) on a car I don't simulate —
    /// the server routes it to the car's sim owner (the world source) as a CommsActionCommand.
    /// Carries the action kind, the target car id, and a destination pose (rerail only).</summary>
    CommsActionRequest = 60,

    /// <summary>server → world source: perform this comms-radio action natively (the initiator can't,
    /// the car is yours). Same payload plus the initiator peer id, so the owner charges the right
    /// wallet via <see cref="FeeExternal"/> with a target.</summary>
    CommsActionCommand = 61,

    /// <summary>world source → server: a car was deleted natively (comms-radio Clear) — remove it from
    /// the registry and broadcast the removal so every client despawns its replica (a deleted car
    /// otherwise lingers as a ghost, since the destroy hook can't tell delete from distance-streaming).</summary>
    CarDeleteNotice = 62,

    // ── M6-B.3: drivable server trains. A player may CLAIM an ambient server-owned train (the
    // sentinel-owner check in TryClaim now admits it, never another player's) and drive it via the
    // ordinary owner-snapshot path; releasing it — or disconnecting — hands it back to the server,
    // which resumes its kinematic drive. ──

    /// <summary>client → server: hand back a trainset I currently simulate. A borrowed server train
    /// returns to the server (it resumes driving); a self-registered consist parks (owner 0). The
    /// counterpart to <see cref="OwnershipRequest"/>; only the current owner may release.</summary>
    OwnershipRelease = 63,

    // ── D10: spatial interest management. The server relays a spatial entity's stream only to clients
    // whose player is near it; when an entity leaves a client's relevance set the server tells that one
    // client to HIDE the replica — a presence hint, NOT an authoritative removal (the entity still
    // exists; TrainsetRemove/ItemDespawned/PlayerLeft remain the "it's gone everywhere" messages, the
    // same distinction as CarDeleteNotice vs a distance stream-out). ──

    /// <summary>server → ONE client: hide a replica that left your relevance set. Wire:
    /// [kind:byte (<see cref="Session.EntityKind"/>)][id:varuint]. The client hides but keeps the
    /// object (cheap re-show on re-enter); a re-enter re-sends the entity's full state (a pose for a
    /// player, TrainsetCreate/ItemSpawned for a world object).</summary>
    InterestHide = 64,
}
