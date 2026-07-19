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
    /// message changes shape, so this is incompatible by construction.</remarks>
    public const int Current = 4;
}
