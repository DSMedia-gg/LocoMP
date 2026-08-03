using LocoMP.Core.Session;

namespace LocoMP;

/// <summary>
/// Everything <see cref="SessionController.JoinSession"/> needs (M5.0 step 1) — the join half of
/// <see cref="HostOptions"/>, shared by the IMGUI dev panel and the uGUI direct-join screen.
/// </summary>
public sealed class JoinOptions
{
    public string PlayerName { get; set; } = "";
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = NetDefaults.Port;
    /// <summary>Session password; null or empty = none offered.</summary>
    public string? Password { get; set; }

    public static JoinOptions Defaults(string playerName) => new() { PlayerName = playerName };
}
