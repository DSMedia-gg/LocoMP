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
    /// <remarks>v12 (M4 orphan cleanup, item half): ItemSpawned gains a provenance byte after the def
    /// (host-native vs LocoMP-minted — the involuntary-release schedule when a holder departs). The
    /// layout change makes this incompatible by construction.</remarks>
    /// <remarks>v13 (M5.1): the join burst ends with a JoinBurstComplete sentinel (MessageType 65) —
    /// the real completion signal the join interstitial's readiness gate clears on (a timer may only
    /// FAIL a gate, never clear it). JoinRejected also gains structured detail (kind/have/need) APPENDED
    /// after the reason string, so the mismatch screen renders exact have/need without parsing prose;
    /// the append keeps the reject readable across versions (either side reads the reason, extra fields
    /// are read defensively). The new sentinel is a deliberate incompatible bump, per v7/v11 precedent.</remarks>
    /// <remarks>v14 (D18 + F7/F8): join admission — a full server now QUEUES a validated joiner and
    /// reports their live position via JoinQueued (MessageType 66) instead of an instant ServerFull
    /// reject; a v13 peer wouldn't know the message, so this is a deliberate incompatible bump.
    /// UncoupleRequest (49) also gains the physical PARTNER car id appended after the coupler end —
    /// the requester's world knows which car the chain connects to, and it is what lets the server
    /// resolve the def gap for a PARKED set it must reclaim itself (F8: an ownerless set has no owner
    /// to route to, so the server commits the split/merge directly — parked truth is server truth).</remarks>
    /// <remarks>v15 (M5.2, host moderation): admin utilities — AdminAction (MessageType 67) carries a
    /// host/admin's kick / ban / promote / demote / pause-joins / unban request, and AdminNotice (68)
    /// returns the consequence (kicked/banned reason, joins paused/resumed broadcast, role change, or an
    /// authorisation rejection). JoinRejected gains two kinds — Banned (session-scoped) and JoinsPaused.
    /// AdminAction also carries admin-only QUERIES (RequestDiagnostics/RequestBanList) answered by
    /// AdminDiagnostics (69) / AdminBanList (70), so a remote admin's panel can populate. A v14 peer
    /// wouldn't know the new messages, so this is a deliberate incompatible bump.</remarks>
    /// <remarks>v16 (M5.2 diagnostics net stats): the AdminDiagnostics payload gains four cumulative
    /// traffic counters (bytes/messages sent+received) appended after the existing fields, so a v15 peer
    /// would misparse it — a deliberate incompatible bump. Per-peer RTT is served live via NetServer.RttMs
    /// (roster ping), not in this snapshot.</remarks>
    /// <remarks>v17 (M5.2 roster status): RosterStatus (MessageType 71) pushes every admitted player's
    /// role and measured ping to all clients (join burst + role changes + a slow cadence). A v16 peer
    /// wouldn't know the new message, so this is a deliberate incompatible bump, per v7/v11 precedent.</remarks>
    public const int Current = 17;
}
