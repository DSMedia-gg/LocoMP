using System;
using System.Collections.Generic;

namespace LocoMP.Core.Items;

/// <summary>
/// Host-chosen item/shop knobs (02 §4 shop stock + purchases, the win-condition surface). Like
/// <see cref="Career.CareerConfig"/> everything is plain DATA so Core stays game-free: the host/
/// extractor feeds the real shop catalog in a later slice, tests and the dedicated server feed
/// synthetic prices. Set before constructing the server — read once, never watched.
/// </summary>
public sealed class ItemConfig
{
    /// <summary>What the shops sell: <c>itemPrefabName</c> → price in cents. A prefab absent here is
    /// simply "not for sale" (the purchase is refused). Fees burn from the policy wallet, exactly
    /// like license purchases — money is never moved, only minted or burned (03 §9).</summary>
    public IReadOnlyDictionary<string, long> ShopPrices { get; set; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>How close (metres, horizontal) a player must be to a world item to pick it up. 0
    /// (default) disables the check — the same posture the career task-proximity gate takes.</summary>
    public float PickupRadiusM { get; set; }

    /// <summary>D13-style host capture: accept world items registered by the session's world source
    /// (the host's game owns real items) instead of only server-spawned ones. Off on the dedicated
    /// server, where purchases + world drops are the only item sources until content spawning lands.</summary>
    public bool AcceptExternalItems { get; set; }
}
