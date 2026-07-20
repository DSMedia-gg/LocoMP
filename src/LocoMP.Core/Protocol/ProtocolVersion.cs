namespace LocoMP.Core.Protocol;

/// <summary>
/// The network protocol version. Deliberately SEPARATE from the release version in
/// Directory.Build.props (03 §10, 05 §2): the wire format is bumped on its own cadence, only when
/// the protocol actually changes in an incompatible way.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>Current wire protocol version. Bump on any incompatible protocol change.</summary>
    /// <remarks>v2 (M2): train sync — trainset transactions/snapshots, junctions, turntables,
    /// ownership, control grants (MessageType 9–28). A v1 peer cannot interpret train state, so this
    /// is a deliberate incompatible bump (hard rule 6).
    /// v3 (M3): career — JoinRequest gains the stable player key (profiles/reconnect need identity
    /// that outlives peer ids), career messages (MessageType 29–38). The JoinRequest format change
    /// makes this incompatible by construction.
    /// v4 (M3.5b): real-car replication — CarDef carries world identity (game car id/guid) and
    /// registration cargo, so remote clients spawn REAL cars instead of ghosts. Every def-bearing
    /// message changes shape, so this is incompatible by construction.
    /// v5 (M3.5c): remote claim parity + multi-crew — TrainsetRegister now carries the FULL car
    /// spec (v4 added identity/cargo to defs but the registration message still stripped them — a
    /// real bug: every remote spawn fell back to synthetic identity), plus messages 44–49
    /// (control/cargo state, native job-completion verification, couple/uncouple requests) and a
    /// target peer id on LicenseGrantExternal (host-admin grants). The registration format change
    /// makes this incompatible by construction.
    /// v6 (M4): items — server-authoritative inventory/world items/shops (MessageType 50–58). A v5
    /// peer cannot interpret item state, so the new message family is a deliberate incompatible bump.
    /// v7 (M4 shops): the join burst now carries the shop catalog (prefab→price, MessageType 59) so a
    /// client can render its Buy UI — a v6 peer would desync on the unknown message, so it is a
    /// deliberate bump (the purchase transaction itself was already v6; this is only its front-end
    /// feed).
    /// v8 (M4 comms radio): comms-radio actions for all players — CommsActionRequest/Command
    /// (MessageType 60/61) route a remote player's rerail/delete to the car's sim owner, CarDeleteNotice
    /// (62) removes a deleted car everywhere, and FeeExternal (43) gains a target peer so a comms fee
    /// can burn the INITIATOR's wallet. The FeeExternal format change makes this incompatible by
    /// construction.</remarks>
    /// <remarks>v9: a WORLD item's location gains a "locked" byte and the item-register request carries
    /// one, so a personal essential (map/radio/wallet) set down syncs as look-but-don't-touch — visible
    /// to all, pickup refused for everyone but its owner.</remarks>
    /// <remarks>v10 (M6-B.3): drivable server trains — a player may claim an ambient server-owned
    /// consist and drive it, then hand it back via the new OwnershipRelease message (MessageType 63);
    /// a v9 peer wouldn't know the new message, so this is a deliberate incompatible bump.</remarks>
    /// <remarks>v11 (D10, interest management): the server may send InterestHide (MessageType 64) to
    /// tell one client to hide a replica that left its spatial relevance set; a v10 peer wouldn't know
    /// the message, so it is a deliberate incompatible bump (the filtering itself is off by default).</remarks>
    public const int Current = 11;
}
