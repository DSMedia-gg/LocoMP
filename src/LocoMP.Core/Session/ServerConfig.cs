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
        CareerConfig? career = null, ItemConfig? items = null, InterestConfig? interest = null,
        CommsFeeTable? commsFees = null)
    {
        Expected = expected ?? throw new ArgumentNullException(nameof(expected));
        Password = password;
        if (maxPlayers < 1) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
        MaxPlayers = maxPlayers;
        Career = career ?? new CareerConfig();
        Items = items ?? new ItemConfig();
        Interest = interest ?? new InterestConfig();
        CommsFees = commsFees ?? new CommsFeeTable();
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

    /// <summary>Session password; null or empty means open. Checked after version compatibility.
    /// Live-mutable via <see cref="NetServer.SetSessionPassword"/> (M5.2 session control) — present
    /// players are untouched, only future joins check it.</summary>
    public string? Password { get; private set; }

    /// <summary>Player cap (D10 design ceiling ~32). Join is rejected when the roster is full.
    /// Live-mutable via <see cref="NetServer.SetMaxPlayers"/> (M5.2 session control).</summary>
    public int MaxPlayers { get; private set; }

    /// <summary>Server-authoritative comms-radio fees (R4-M / dedicated economy): what the SERVER
    /// bills for parked-set actions it commits or claims itself. Executor-reported fees (the world
    /// source's native prices, D14) still cover self-executed actions; this table covers the paths
    /// that previously logged "fee waived".</summary>
    public CommsFeeTable CommsFees { get; }

    internal void OverridePassword(string? password) => Password = password;

    internal void OverrideMaxPlayers(int maxPlayers)
    {
        if (maxPlayers < 1) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
        MaxPlayers = maxPlayers;
    }
}

/// <summary>
/// The server's own price list for comms-radio actions on PARKED sets (R4-M): the server executes
/// (delete-as-retire) or claims-then-routes (rerail) these itself, so no native executor price
/// exists and a client-supplied one would be a client-named economy delta (03 §9). Flat first-slice
/// numbers: delete matches DV's observed $100; rerail approximates DV's base rate (its native
/// formula ~500 + 150/m needs world distance the server does not compute). Fees burn (ledger), so
/// conservation stays exact; an unaffordable fee refuses the ACTION, never overdrafts.
/// </summary>
public sealed class CommsFeeTable
{
    /// <summary>Fee for retiring one parked car via the radio, in cents. DV charges $100.</summary>
    public long DeleteCarCents { get; set; } = 100_00;

    /// <summary>Flat fee for the parked-rerail claim-then-execute, in cents (billed at claim; the
    /// executor skips its own report for self-initiated commands so nothing double-bills).</summary>
    public long RerailFlatCents { get; set; } = 500_00;
}
