using System;

namespace LocoMP.Core.Protocol;

/// <summary>What a joining client presents to the server (or vice-versa) during the handshake.</summary>
/// <remarks>
/// M0 covers the first two links of the pre-play chain from 03 §10 (protocol version → exact game
/// build). The mod-manifest / progression-mode / channel negotiation links are added in M1.
/// </remarks>
public sealed class HandshakeRequest
{
    public HandshakeRequest(int protocolVersion, string gameBuild, string modVersion, string modListHash = "")
    {
        ProtocolVersion = protocolVersion;
        GameBuild = gameBuild ?? throw new ArgumentNullException(nameof(gameBuild));
        ModVersion = modVersion ?? throw new ArgumentNullException(nameof(modVersion));
        ModListHash = modListHash ?? throw new ArgumentNullException(nameof(modListHash));
    }

    public int ProtocolVersion { get; }
    public string GameBuild { get; }
    public string ModVersion { get; }

    /// <summary>
    /// Hash of the client's installed mod manifest (03 §10 — the mod-manifest link). Empty string =
    /// "not advertised". A mismatch means the two sides run different mods; LocoMP refuses rather than
    /// risk divergent state. The hashing scheme is defined by the frontend (M1.3); Core only compares.
    /// </summary>
    public string ModListHash { get; }
}

/// <summary>Outcome of a handshake check. Rejections always name the exact mismatch (have/need).</summary>
public readonly struct HandshakeResult
{
    private HandshakeResult(bool compatible, string? reason)
    {
        Compatible = compatible;
        Reason = reason;
    }

    public bool Compatible { get; }
    public string? Reason { get; }

    public static HandshakeResult Ok { get; } = new(true, null);
    public static HandshakeResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// Pure, game-free compatibility check run before a client is admitted. This is the seed of the
/// full pre-play handshake (03 §10); keeping it in Core means it is fuzzed and unit-tested with no
/// game running (03 §11, hard rule 8).
/// </summary>
public static class VersionHandshake
{
    public static HandshakeResult Check(HandshakeRequest client, HandshakeRequest server)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (server is null) throw new ArgumentNullException(nameof(server));

        if (client.ProtocolVersion != server.ProtocolVersion)
        {
            return HandshakeResult.Reject(
                $"protocol mismatch: client has v{client.ProtocolVersion}, server needs v{server.ProtocolVersion}");
        }

        // Exact game-build match: LocoMP refuses cross-build sessions rather than corrupting a world (R3).
        if (!string.Equals(client.GameBuild, server.GameBuild, StringComparison.Ordinal))
        {
            return HandshakeResult.Reject(
                $"game build mismatch: client has {client.GameBuild}, server needs {server.GameBuild}");
        }

        // Exact mod version: a client on a different LocoMP build may speak a subtly different protocol.
        if (!string.Equals(client.ModVersion, server.ModVersion, StringComparison.Ordinal))
        {
            return HandshakeResult.Reject(
                $"mod version mismatch: client has {client.ModVersion}, server needs {server.ModVersion}");
        }

        // Mod-manifest match (03 §10): different installed mods ⇒ potentially divergent state.
        if (!string.Equals(client.ModListHash, server.ModListHash, StringComparison.Ordinal))
        {
            return HandshakeResult.Reject(
                "mod list mismatch: client and server have different installed mods");
        }

        return HandshakeResult.Ok;
    }
}
