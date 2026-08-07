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
    public string? ConfigPath;             // optional career config (.lmpc) — real yards/jobs/licenses
    public string? DumpConfigPath;         // write the built-in default career to this .lmpc path and exit
    public string? Password;
    public int MaxPlayers = 32;
    public string GameBuild = "99-build2702"; // what B99.7 reports at runtime; must match every joiner
    public string ModVersion = DefaultModVersion();
    public string ModListHash = "";        // "" matches a bot; paste the game's hash to join from DV
    public string Name = "LocoMP Dedicated";
    public long AutosaveSeconds = 60;
    public ProgressionPreset Preset = ProgressionPreset.PerPlayer;
    public double Hz = 30;                  // server tick rate (03 §5)

    // Server-owned kinematic trains (M6-B.2) — the server drives its own consists so a fresh server has
    // moving trains with no bot. Needs an extracted topology (.lmpw) to walk.
    public int SpawnTrains = 0;             // 0 = none
    public int TrainCars = 3;
    public double TrainSpeed = 10;          // m/s ≈ 36 km/h
    public string[] TrainLiveries = System.Array.Empty<string>(); // real livery ids (else generic kinds)
    public uint? TrainStartEdge = null;     // start all server trains on this edge (else seed-derived, scattered)
    public double TimeOfDayHours = 8.0;     // world clock at startup, 0-24 (v18 — the server is its own time source)
    public double DayLengthMinutes = 30;    // real minutes per world day (DV stock: 30)

    // Interest management (D10). Off by default — a friend-scale session is comfortably inside the
    // bandwidth budget, and filtering is only worth its complexity as player/train counts climb
    // (docs/PERF-BASELINE.md §3). Gating railed trains is the big win and needs a geometry-carrying
    // (codec v2) .lmpw via --world; without one the server says so and fails open.
    public bool Interest = false;
    public double InterestRadius = 500;     // metres; the leave radius is 1.5× this (hysteresis band)
    public bool InterestPlayers = false;    // poses are only ~4% of the bandwidth — opt in separately

    // Soak / unattended-run controls (M6-B soak exit).
    public double SoakReportSeconds = 0;    // 0 = off; else emit a health/leak line every N seconds
    public double DurationSeconds = 0;      // 0 = run until Ctrl+C/stop; else self-terminate after N s

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
                    case "--dump-config": o.DumpConfigPath = Next(); break;
                    case "--password": o.Password = Next(); break;
                    case "--max-players": o.MaxPlayers = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--build": o.GameBuild = Next(); break;
                    case "--mod-version": o.ModVersion = Next(); break;
                    case "--modlist-hash": o.ModListHash = Next(); break;
                    case "--name": o.Name = Next(); break;
                    case "--autosave-seconds": o.AutosaveSeconds = Math.Max(1, long.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--tick-hz": o.Hz = Math.Clamp(double.Parse(Next(), CultureInfo.InvariantCulture), 1, 60); break;
                    case "--interest": o.Interest = true; break;
                    case "--interest-players": o.InterestPlayers = true; break;
                    case "--interest-radius": o.InterestRadius = Math.Max(1, double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--spawn-trains": o.SpawnTrains = Math.Max(0, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--train-cars": o.TrainCars = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--train-speed": o.TrainSpeed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--train-livery": o.TrainLiveries = Next().Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                    case "--train-start-edge": o.TrainStartEdge = uint.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--time-of-day":
                    {
                        // "HH:MM" or a plain hour ("8", "14.5") — the shared sun's start position.
                        string t = Next();
                        int colon = t.IndexOf(':');
                        o.TimeOfDayHours = colon >= 0
                            ? int.Parse(t.Substring(0, colon), CultureInfo.InvariantCulture)
                              + int.Parse(t.Substring(colon + 1), CultureInfo.InvariantCulture) / 60.0
                            : double.Parse(t, CultureInfo.InvariantCulture);
                        if (o.TimeOfDayHours < 0 || o.TimeOfDayHours >= 24)
                            throw new ArgumentException("--time-of-day must be within 0-24");
                        break;
                    }
                    case "--day-length": o.DayLengthMinutes = Math.Max(1, double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--soak-report": o.SoakReportSeconds = Math.Max(0, double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--duration": o.DurationSeconds = Math.Max(0, double.Parse(Next(), CultureInfo.InvariantCulture)); break;
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

    /// <summary>Resolve the topology file for --spawn-trains: explicit --world, then the
    /// LOCOMP_WORLD_FILE env var, then tests/data/world-*.lmpw walking up from the exe (repo runs). Null
    /// if none found. Same probe order as the bot, so `--spawn-trains` works out-of-the-box from the repo.</summary>
    public string? ResolveWorldFile()
    {
        if (!string.IsNullOrEmpty(WorldFile)) return File.Exists(WorldFile) ? WorldFile : null;

        string? env = Environment.GetEnvironmentVariable("LOCOMP_WORLD_FILE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string dataDir = Path.Combine(dir.FullName, "tests", "data");
            if (!Directory.Exists(dataDir)) continue;
            string? found = Directory.EnumerateFiles(dataDir, "world-*.lmpw").OrderBy(f => f).FirstOrDefault();
            if (found != null) return found;
        }
        return null;
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
  --config <path>        career config (.lmpc): real yards/jobs/licenses (else the built-in default)
  --dump-config <path>   write the built-in default career to a .lmpc file and exit (a seed to edit)
  --password <pw>        session password            (default none)
  --max-players <n>      player cap                  (default 32)
  --build <s>            game build clients must match(default 99-build2702)
  --mod-version <s>      mod version clients must match (default: this assembly's version)
  --modlist-hash <s>     mod manifest hash clients must match (default empty — set to join from DV)
  --name <s>             server name (log/banner)    (default 'LocoMP Dedicated')
  --autosave-seconds <n> autosave interval           (default 60)
  --preset <p>           perplayer | shared          (default perplayer)
  --tick-hz <n>          server tick rate            (default 30)
  --interest             filter streams by spatial relevance (needs a v2 --world for trains)
  --interest-radius <m>  interest enter radius        (default 500; leave radius is 1.5x)
  --interest-players     also filter player poses     (default off — poses are ~4% of bandwidth)
  --spawn-trains <n>     server drives n kinematic trains itself (needs a topology; no bot needed)
  --train-cars <n>       cars per server train       (default 3)
  --train-speed <m/s>    server train speed          (default 10)
  --train-livery <a,b,c> real livery ids for server trains (else generic kinds)
  --train-start-edge <id> start server trains on this edge (else seed-derived, scattered) — paste a
                         host/join log's 'ghost-train hint' to spawn them beside a player
  --time-of-day <h>      world clock at startup, HH:MM or a plain hour (default 8:00) — the server
                         is the session's time source; every client's sky follows it
  --day-length <min>     real minutes per full world day (default 30 — DV stock)
  --soak-report <s>      emit a health/leak line every s seconds (players/sets/jobs/items/heap +
                         the money & item conservation oracles). 0 = off. Unhealthy → non-zero exit
  --duration <s>         self-terminate after s seconds (0 = run until Ctrl+C/stop). Use for an
                         unattended soak — a bounded run stops + saves cleanly on its own
  --help                 this text

Console commands (type at the prompt while running): status | save | stop | help

Unattended soak (M6-B): the server + a bot swarm run for hours, leaks surface in the health line:
  LocoMP.Server --spawn-trains 4 --soak-report 30 --duration 86400   (24 h; exit code 2 if unhealthy)
  LocoMP.Bot --count 8 --behavior wander --churn 15 --duration 86400  (join/leave storm + movement)

Solo-test recipe (no second player needed):
  1) LocoMP.Server --port {NetDefaults.Port} --spawn-trains 3
     (the server drives its own trains along the extracted topology — no bot required)
  2) Join from Derail Valley (Direct connect 127.0.0.1) — you see the server's trains rolling, the
     job board, and presence. Restart the game or the server and rejoin: the world persists.

--spawn-trains needs an extracted topology (.lmpw): pass --world <path>, set LOCOMP_WORLD_FILE, or run
from the repo (it finds tests/data/world-*.lmpw). Extract one in-game via the mod panel for the real map.");
    }
}
