using System.Globalization;
using System.Reflection;
using LocoMP.Core.Career;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;

namespace LocoMP.Server;

/// <summary>
/// Parsed command line for the headless dedicated server. Hand-rolled parsing — no dependency for a
/// handful of flags (supply-chain posture, hard rule 4 spirit), mirroring <c>LocoMP.Bot</c>'s BotOptions.
/// Defaults make <c>LocoMP.Server</c> run out-of-the-box: it binds the standard port, generates a small
/// built-in career board, and persists to <c>locomp-server.save</c> beside the exe.
/// </summary>
public sealed class ServerOptions
{
    public int Port = NetDefaults.Port;
    public string Key = NetDefaults.ConnectKey;
    public string SavePath = "locomp-server.save";
    public string? WorldFile;              // extracted .lmpw — parsed now; used by the later train slice
    public string? ConfigPath;             // optional career JSON — deferred; warns + uses the default
    public string? Password;
    public int MaxPlayers = 32;
    public string GameBuild = "99-build2702"; // what B99.7 reports at runtime; must match every joiner
    public string ModVersion = DefaultModVersion();
    public string ModListHash = "";        // "" matches a bot; paste the game's hash to join from DV
    public string Name = "LocoMP Dedicated";
    public long AutosaveSeconds = 60;
    public ProgressionPreset Preset = ProgressionPreset.PerPlayer;
    public double Hz = 30;                  // server tick rate (03 §5)

    public HandshakeRequest ToIdentity() => new(ProtocolVersion.Current, GameBuild, ModVersion, ModListHash);

    /// <summary>The server ships in the same tree as the mod, so the single version source
    /// (Directory.Build.props) already stamps this assembly — strip the SDK's "+sha" suffix so the
    /// version a client must match is clean.</summary>
    private static string DefaultModVersion()
    {
        string v = typeof(ServerOptions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    public static ServerOptions? Parse(string[] args)
    {
        var o = new ServerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i]
                : throw new ArgumentException($"{args[i]} needs a value");
            try
            {
                switch (args[i])
                {
                    case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--key": o.Key = Next(); break;
                    case "--save": o.SavePath = Next(); break;
                    case "--world": o.WorldFile = Next(); break;
                    case "--config": o.ConfigPath = Next(); break;
                    case "--password": o.Password = Next(); break;
                    case "--max-players": o.MaxPlayers = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--build": o.GameBuild = Next(); break;
                    case "--mod-version": o.ModVersion = Next(); break;
                    case "--modlist-hash": o.ModListHash = Next(); break;
                    case "--name": o.Name = Next(); break;
                    case "--autosave-seconds": o.AutosaveSeconds = Math.Max(1, long.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--tick-hz": o.Hz = Math.Clamp(double.Parse(Next(), CultureInfo.InvariantCulture), 1, 60); break;
                    case "--preset":
                    {
                        string p = Next().ToLowerInvariant();
                        o.Preset = p switch
                        {
                            "perplayer" or "per-player" => ProgressionPreset.PerPlayer,
                            "shared" or "sharedcareer" => ProgressionPreset.SharedCareer,
                            _ => throw new ArgumentException($"unknown preset '{p}' — use perplayer or shared"),
                        };
                        break;
                    }
                    case "--help" or "-h" or "/?": PrintUsage(); return null;
                    default:
                        Console.Error.WriteLine($"Unknown option: {args[i]} (try --help)");
                        return null;
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
            {
                Console.Error.WriteLine($"Bad value for {args[i]}: {e.Message}");
                return null;
            }
        }
        return o;
    }

    public static void PrintUsage()
    {
        // Verbatim (not raw) string: the repo pins C# 10 for the net48/Unity ceiling; raw literals are C# 11.
        Console.WriteLine($@"LocoMP.Server — headless dedicated server for a LocoMP session (protocol v{ProtocolVersion.Current}).

Usage: LocoMP.Server [options]
  --port <port>          UDP port to bind            (default {NetDefaults.Port})
  --key <key>            transport connect key       (default {NetDefaults.ConnectKey})
  --save <path>          world save file             (default locomp-server.save)
  --world <path>         extracted .lmpw topology    (default none; reserved for server-owned trains)
  --config <path>        career config JSON          (not yet — falls back to the built-in default)
  --password <pw>        session password            (default none)
  --max-players <n>      player cap                  (default 32)
  --build <s>            game build clients must match(default 99-build2702)
  --mod-version <s>      mod version clients must match (default: this assembly's version)
  --modlist-hash <s>     mod manifest hash clients must match (default empty — set to join from DV)
  --name <s>             server name (log/banner)    (default 'LocoMP Dedicated')
  --autosave-seconds <n> autosave interval           (default 60)
  --preset <p>           perplayer | shared          (default perplayer)
  --tick-hz <n>          server tick rate            (default 30)
  --help                 this text

Console commands (type at the prompt while running): status | save | stop | help

Solo-test recipe (no second player needed):
  1) LocoMP.Server --port {NetDefaults.Port}
  2) LocoMP.Bot --host 127.0.0.1 --consist 3 --livery LocoDiesel,BoxcarBrown,BoxcarBrown
     (the bot joins first, becomes the world source, and registers a real consist)
  3) Join from Derail Valley (Direct connect 127.0.0.1) as a second client — you see the bot's train,
     the job board, and presence. Restart the game or the server and rejoin: the world persists.");
    }
}
