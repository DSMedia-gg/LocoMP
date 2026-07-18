using System;
using System.Collections.Generic;

namespace LocoMP.Core.Career;

/// <summary>
/// One player's persistent career record, keyed by their stable player key (02 §5 — survives
/// leave/rejoin and server restarts; the SteamID becomes the key when the Steam transport lands in
/// M5). The wallet balance is deliberately NOT here — it lives in the <see cref="EconomyLedger"/>
/// under whatever account the policy layer names, so both presets share one money path.
/// </summary>
public sealed class PlayerProfile
{
    public PlayerProfile(string key, string name)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>Stable identity, presented in the handshake. NEVER broadcast to other clients:
    /// within the reconnect grace window it doubles as the reclaim credential (07 §M3), so the
    /// wire only ever carries session peer ids and display names for other players.</summary>
    public string Key { get; }

    /// <summary>Last display name seen for this key (refreshed on every connect).</summary>
    public string Name { get; internal set; }

    /// <summary>Per-player license scope. Stays empty under the shared preset, where the one
    /// shared set on <see cref="CareerRegistry"/> is the scope instead (02 §6).</summary>
    public HashSet<string> Licenses { get; } = new(StringComparer.Ordinal);
}
