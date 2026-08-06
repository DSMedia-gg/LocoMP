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

/// <summary>
/// Why a join was refused, as a machine-readable discriminator beside the human-readable reason
/// (M5.1: the mismatch screen branches on this instead of parsing prose). Wire values are STABLE —
/// append only, never renumber (same discipline as <see cref="MessageType"/>).
/// </summary>
public enum RejectKind : byte
{
    /// <summary>Anything without a dedicated kind; the reason string is the whole story.</summary>
    Other = 0,
    /// <summary>Wire protocol version differs (have/need carry the two versions).</summary>
    Protocol = 1,
    /// <summary>Exact game-build mismatch (have/need carry the two builds).</summary>
    GameBuild = 2,
    /// <summary>LocoMP mod version differs (have/need carry the two versions).</summary>
    ModVersion = 3,
    /// <summary>Installed-mod manifest differs; the hashes are opaque, so no have/need.</summary>
    ModList = 4,
    /// <summary>Wrong or missing server password.</summary>
    Password = 5,
    /// <summary>The server is at MaxPlayers AND its admission queue is full (v14/D18: capacity
    /// alone now queues a validated joiner instead of rejecting).</summary>
    ServerFull = 6,
    /// <summary>Malformed/reserved player key.</summary>
    InvalidKey = 7,
    /// <summary>This player key is already online in the session. v14 (F7): presenting the key IS
    /// the credential, so a live duplicate normally EVICTS the old peer (takeover) — this reject
    /// survives only for the race where the key came online while its holder waited in the queue.</summary>
    DuplicateKey = 8,
    /// <summary>The player key is session-banned by a host/admin (M5.2, v15). Session-scoped: the ban
    /// dies with the session (U3) — no have/need fields.</summary>
    Banned = 9,
    /// <summary>The host has paused new joins (M5.2, v15). A transient door-closed state, not a
    /// rejection of the player — retrying once joins resume succeeds. No have/need fields.</summary>
    JoinsPaused = 10,
}

/// <summary>Outcome of a handshake check. Rejections always name the exact mismatch (have/need).</summary>
public readonly struct HandshakeResult
{
    private HandshakeResult(bool compatible, string? reason, RejectKind kind, string? clientHas, string? serverNeeds)
    {
        Compatible = compatible;
        Reason = reason;
        Kind = kind;
        ClientHas = clientHas;
        ServerNeeds = serverNeeds;
    }

    public bool Compatible { get; }
    public string? Reason { get; }

    /// <summary>The mismatch discriminator (M5.1). <see cref="RejectKind.Other"/> when compatible.</summary>
    public RejectKind Kind { get; }

    /// <summary>The client's value of the mismatched field, when one exists (null otherwise).</summary>
    public string? ClientHas { get; }

    /// <summary>The server's required value of the mismatched field, when one exists (null otherwise).</summary>
    public string? ServerNeeds { get; }

    public static HandshakeResult Ok { get; } = new(true, null, RejectKind.Other, null, null);
    public static HandshakeResult Reject(string reason) => new(false, reason, RejectKind.Other, null, null);

    public static HandshakeResult Reject(RejectKind kind, string reason, string? clientHas = null, string? serverNeeds = null)
        => new(false, reason, kind, clientHas, serverNeeds);
}

/// <summary>
/// A join refusal as the CLIENT received it (M5.1): the discriminator plus the exact have/need pair
/// when the mismatch has one. <see cref="ClientHas"/>/<see cref="ServerNeeds"/> are null for kinds
/// with nothing to compare (password, server full) and when talking to a pre-v13 server, in which
/// case <see cref="Kind"/> is <see cref="RejectKind.Other"/> and the reason string is the story.
/// </summary>
public readonly struct RejectInfo
{
    public RejectInfo(RejectKind kind, string reason, string? clientHas, string? serverNeeds)
    {
        Kind = kind;
        Reason = reason;
        ClientHas = clientHas;
        ServerNeeds = serverNeeds;
    }

    public RejectKind Kind { get; }
    public string Reason { get; }
    public string? ClientHas { get; }
    public string? ServerNeeds { get; }

    /// <summary>True for the version-shaped kinds a mismatch screen renders as have/need rows.</summary>
    public bool IsVersionMismatch => Kind is RejectKind.Protocol or RejectKind.GameBuild
        or RejectKind.ModVersion or RejectKind.ModList;
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
            return HandshakeResult.Reject(RejectKind.Protocol,
                $"protocol mismatch: client has v{client.ProtocolVersion}, server needs v{server.ProtocolVersion}",
                $"v{client.ProtocolVersion}", $"v{server.ProtocolVersion}");
        }

        // Exact game-build match: LocoMP refuses cross-build sessions rather than corrupting a world (R3).
        if (!string.Equals(client.GameBuild, server.GameBuild, StringComparison.Ordinal))
        {
            return HandshakeResult.Reject(RejectKind.GameBuild,
                $"game build mismatch: client has {client.GameBuild}, server needs {server.GameBuild}",
                client.GameBuild, server.GameBuild);
        }

        // Exact mod version: a client on a different LocoMP build may speak a subtly different protocol.
        if (!string.Equals(client.ModVersion, server.ModVersion, StringComparison.Ordinal))
        {
            return HandshakeResult.Reject(RejectKind.ModVersion,
                $"mod version mismatch: client has {client.ModVersion}, server needs {server.ModVersion}",
                client.ModVersion, server.ModVersion);
        }

        // Mod-manifest match (03 §10): different installed mods ⇒ potentially divergent state.
        // The hashes are opaque, so there is no meaningful have/need to display — kind only.
        if (!string.Equals(client.ModListHash, server.ModListHash, StringComparison.Ordinal))
        {
            return HandshakeResult.Reject(RejectKind.ModList,
                "mod list mismatch: client and server have different installed mods");
        }

        return HandshakeResult.Ok;
    }
}
