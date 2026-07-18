using System;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>
/// Host-chosen settings the server enforces during the handshake (03 §10). The order of checks —
/// compatibility (protocol/build/mod) THEN password THEN capacity — is fixed in <see cref="NetServer"/>.
/// </summary>
public sealed class ServerConfig
{
    public ServerConfig(HandshakeRequest expected, string? password = null, int maxPlayers = 32)
    {
        Expected = expected ?? throw new ArgumentNullException(nameof(expected));
        Password = password;
        if (maxPlayers < 1) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
        MaxPlayers = maxPlayers;
    }

    /// <summary>The protocol/build/mod fingerprint a joining client must match exactly.</summary>
    public HandshakeRequest Expected { get; }

    /// <summary>Session password; null or empty means open. Checked after version compatibility.</summary>
    public string? Password { get; }

    /// <summary>Player cap (D10 design ceiling ~32). Join is rejected when the roster is full.</summary>
    public int MaxPlayers { get; }
}
