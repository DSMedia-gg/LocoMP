using LocoMP.Core.Protocol;

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
    /// LiteNetLib connect key. Embeds the protocol version so a client on an old protocol is refused
    /// at the socket layer — before any handshake traffic — with the app-level handshake (03 §10)
    /// remaining the authoritative check that names the exact mismatch.
    /// </summary>
    public static string ConnectKey => "LocoMP:" + ProtocolVersion.Current;
}
