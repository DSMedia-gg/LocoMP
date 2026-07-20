using System;
using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>
/// Host-chosen settings the server enforces during the handshake (03 §10). The order of checks —
/// compatibility (protocol/build/mod) THEN password THEN capacity THEN player key — is fixed in
/// <see cref="NetServer"/>.
/// </summary>
public sealed class ServerConfig
{
    public ServerConfig(HandshakeRequest expected, string? password = null, int maxPlayers = 32,
        CareerConfig? career = null, ItemConfig? items = null, InterestConfig? interest = null)
    {
        Expected = expected ?? throw new ArgumentNullException(nameof(expected));
        Password = password;
        if (maxPlayers < 1) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
        MaxPlayers = maxPlayers;
        Career = career ?? new CareerConfig();
        Items = items ?? new ItemConfig();
        Interest = interest ?? new InterestConfig();
    }

    /// <summary>Career knobs (M3): preset, starting grant, claim rules, generator data. The default
    /// has no stations, so a host that doesn't configure jobs simply runs an empty board.</summary>
    public CareerConfig Career { get; }

    /// <summary>Item/shop knobs (M4): shop catalog + prices, pickup radius, host-capture gate. The
    /// default sells nothing and gates external items, so an unconfigured host has an inert item
    /// layer until purchases or world drops appear.</summary>
    public ItemConfig Items { get; }

    /// <summary>Spatial interest-management knobs (D10): per-client relevance radii + which entity
    /// kinds to gate. The default is disabled, so an unconfigured server broadcasts to everyone exactly
    /// as before; a host/dedicated server opts in.</summary>
    public InterestConfig Interest { get; }

    /// <summary>The protocol/build/mod fingerprint a joining client must match exactly.</summary>
    public HandshakeRequest Expected { get; }

    /// <summary>Session password; null or empty means open. Checked after version compatibility.</summary>
    public string? Password { get; }

    /// <summary>Player cap (D10 design ceiling ~32). Join is rejected when the roster is full.</summary>
    public int MaxPlayers { get; }
}
