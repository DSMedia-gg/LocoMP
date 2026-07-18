namespace LocoMP.Api;

/// <summary>
/// Entry point for the public Mod API (04). Third-party mods reference only this assembly.
/// The full event/message/SyncedVar surface plus compat negotiation is designed at M6; for now this
/// pins the API version constant and demonstrates the DTO-only rule.
/// </summary>
public static class LocoMpApi
{
    /// <summary>Mod API surface version, negotiated per channel in the handshake (04 §stability).</summary>
    public const int ApiVersion = 1;
}

/// <summary>
/// Example DTO. Everything the API exposes is a plain data object like this — never a UnityEngine or
/// DV.* type — so the public surface stays stable across game builds (hard rule 3).
/// </summary>
public sealed class PlayerRef
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
