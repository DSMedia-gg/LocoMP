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
    /// is a deliberate incompatible bump (hard rule 6).</remarks>
    public const int Current = 2;
}
