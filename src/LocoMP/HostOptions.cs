using LocoMP.Core.Career;
using LocoMP.Core.Session;

namespace LocoMP;

/// <summary>
/// Everything <see cref="SessionController.HostSession"/> needs, lifted out of the IMGUI field
/// state (M5.0 step 1) so the dev panel and the uGUI host screen build the same object and share
/// one code path. Plain mutable properties by design: an options object is filled field-by-field
/// by whichever UI owns the form (net48 + no IsExternalInit polyfill rules out init-only setters,
/// and the house style avoids records).
/// </summary>
public sealed class HostOptions
{
    public string PlayerName { get; set; } = "";
    public int Port { get; set; } = NetDefaults.Port;
    /// <summary>Session password; null or empty = open session.</summary>
    public string? Password { get; set; }
    /// <summary>D3/O2: per-player careers is the default; shared career is the classic-co-op preset.</summary>
    public ProgressionPreset Preset { get; set; } = ProgressionPreset.PerPlayer;
    /// <summary>Ignore any saved career and start fresh (also re-mints the D14 starting grant).</summary>
    public bool FreshCareer { get; set; }
    /// <summary>D15: joining players inherit the host's held licenses, live while enabled.</summary>
    public bool AutoGrantLicenses { get; set; }
    /// <summary>D10: only stream nearby trains/players (needs live world geometry; fails open).</summary>
    public bool InterestFiltering { get; set; }

    public static HostOptions Defaults(string playerName) => new() { PlayerName = playerName };
}
