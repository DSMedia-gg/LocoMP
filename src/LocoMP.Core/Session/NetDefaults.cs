namespace LocoMP.Core.Session;

/// <summary>
/// Canonical connection defaults shared by every frontend — host UI (M1.3), dedicated server (M6),
/// and the dev bot harness. One source so "what port is LocoMP?" always has exactly one answer.
/// </summary>
public static class NetDefaults
{
    /// <summary>Default UDP port for LocoMP sessions (host-embedded and dedicated).</summary>
    public const int Port = 8877;

    /// <summary>
    /// LiteNetLib connect key — FIXED, deliberately version-free (F3, 2026-08-04 gauntlet). The key
    /// used to embed the protocol version, which refused cross-protocol peers at the SOCKET: a
    /// silent 10 s connect timeout, before the handshake could speak — so the structured
    /// protocol-mismatch reject (and M5.1's have/need MismatchScreen) could never fire for the one
    /// mismatch it most needed to name. Now every LocoMP peer connects at the socket and the
    /// app-level handshake (03 §10) names the exact mismatch. One final incompatible hop: peers on
    /// builds with the old versioned key still time out against this build — accepted, pre-release.
    /// The version-check authority is <see cref="VersionHandshake"/>; the JoinRequest wire PREFIX
    /// (varuint protocol first) is frozen so any future server can always read it far enough to
    /// reject legibly.
    /// </summary>
    public const string ConnectKey = "LocoMP";
}
