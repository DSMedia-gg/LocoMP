namespace LocoMP.Core.Career;

/// <summary>The two shipped progression presets (D3; O2 resolved: per-player is the default).</summary>
public enum ProgressionPreset : byte
{
    /// <summary>Each player has their own wallet and licenses; payouts go to the claimant and fees
    /// to the acting player (02 §6 — the reference config for testing).</summary>
    PerPlayer = 0,

    /// <summary>"Classic co-op": one shared wallet and one shared license set.</summary>
    SharedCareer = 1,
}

/// <summary>
/// The policy layer (02 §6, D3): every wallet and license touch asks THIS where to route, so
/// per-player vs shared is one switch flipped in one place, never scattered ifs. Wallet accounts
/// live in the <see cref="EconomyLedger"/> keyed by player key (per-player preset) or by the one
/// shared account.
/// </summary>
public sealed class ProgressionPolicy
{
    /// <summary>The ledger account holding the shared-career wallet. The '@' prefix keeps it out
    /// of the player-key namespace (keys starting with '@' are rejected at the handshake).</summary>
    public const string SharedAccount = "@shared";

    public ProgressionPolicy(ProgressionPreset preset) => Preset = preset;

    public ProgressionPreset Preset { get; }

    /// <summary>Where money lands or is charged for this player. Payouts AND fees route to the
    /// same place in both presets (02 §6: to claimant/actor, or to the shared wallet).</summary>
    public string WalletAccountFor(string playerKey) =>
        Preset == ProgressionPreset.SharedCareer ? SharedAccount : playerKey;

    /// <summary>Whether licenses live in the one shared set instead of per-player profiles.</summary>
    public bool LicensesShared => Preset == ProgressionPreset.SharedCareer;

    /// <summary>Which scope owns an item this player picks up or buys (M4). Per-player mode keeps
    /// each player's inventory to themselves; shared career pools items in the one shared scope so
    /// they are "freely shared" (02 §6 inventory row) — the same one-switch routing as the wallet.</summary>
    public string InventoryScopeFor(string playerKey) =>
        Preset == ProgressionPreset.SharedCareer ? SharedAccount : playerKey;
}
