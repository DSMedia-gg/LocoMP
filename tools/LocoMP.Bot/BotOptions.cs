using System.Globalization;
using System.Reflection;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;

namespace LocoMP.Bot;

/// <summary>
/// Parsed command line. Hand-rolled parsing — no dependency for a dozen flags (supply-chain posture,
/// hard rule 4 spirit). Defaults are tuned for the M1.3 one-PC workflow: host a session in-game,
/// run <c>LocoMP.Bot --at &lt;your coords&gt;</c>, watch the avatar orbit you.
/// </summary>
public sealed class BotOptions
{
    public string Host = "127.0.0.1";
    public int Port = NetDefaults.Port;
    public string Key = NetDefaults.ConnectKey;
    public string? Password;
    public int Count = 1;
    public string Name = "Bot";
    public string Behavior = "orbit";
    public Pose Center = Pose.Identity;
    public double Radius = 5;
    public double Speed = 1.4;          // human walking pace, m/s
    public double Hz = 20;              // pose sends per second
    public string GameBuild = "99-build2702"; // what B99.7 reports at runtime; must match the host's
    public string ModVersion = DefaultModVersion();
    public string ModListHash = "";
    public double ChurnSeconds = 0;     // 0 = stay connected
    public double DurationSeconds = 0;  // 0 = run until Ctrl+C
    public int Seed = 12345;            // wander is seeded so soak failures can be replayed
    public int ConsistCars = 0;         // 0 = no ghost train
    public double ConsistSpeed = 8;     // m/s ≈ 29 km/h, a sedate freight roll
    public string? WorldFile;           // extracted .lmpw; null = probe the usual spots
    public long StartEdge = -1;         // ghost start edge (host logs the nearest one); -1 = walker's pick

    public HandshakeRequest ToIdentity() => new(ProtocolVersion.Current, GameBuild, ModVersion, ModListHash);

    /// <summary>The bot ships in the same tree as the mod, so the single version source
    /// (Directory.Build.props) already stamps this assembly — strip the SDK's "+sha" suffix.</summary>
    private static string DefaultModVersion()
    {
        string v = typeof(BotOptions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    public static BotOptions? Parse(string[] args)
    {
        var o = new BotOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i]
                : throw new ArgumentException($"{args[i]} needs a value");
            try
            {
                switch (args[i])
                {
                    case "--host": o.Host = Next(); break;
                    case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--key": o.Key = Next(); break;
                    case "--password": o.Password = Next(); break;
                    case "--count": o.Count = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--name": o.Name = Next(); break;
                    case "--behavior": o.Behavior = Next().ToLowerInvariant(); break;
                    case "--at": o.Center = ParsePoint(Next()); break;
                    case "--radius": o.Radius = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--speed": o.Speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--hz": o.Hz = Math.Clamp(double.Parse(Next(), CultureInfo.InvariantCulture), 1, 60); break;
                    case "--build": o.GameBuild = Next(); break;
                    case "--mod-version": o.ModVersion = Next(); break;
                    case "--modlist-hash": o.ModListHash = Next(); break;
                    case "--churn": o.ChurnSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--duration": o.DurationSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--consist": o.ConsistCars = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--consist-speed": o.ConsistSpeed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--world": o.WorldFile = Next(); break;
                    case "--start-edge": o.StartEdge = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--help" or "-h" or "/?": PrintUsage(); return null;
                    default:
                        Console.Error.WriteLine($"Unknown option: {args[i]} (try --help)");
                        return null;
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                Console.Error.WriteLine($"Bad value for {args[i]}: {e.Message}");
                return null;
            }
        }

        if (o.Behavior is not ("orbit" or "wander" or "idle"))
        {
            Console.Error.WriteLine($"Unknown behavior '{o.Behavior}' — use orbit, wander, or idle.");
            return null;
        }
        return o;
    }

    private static Pose ParsePoint(string s)
    {
        string[] parts = s.Split(',');
        if (parts.Length != 3) throw new ArgumentException("expected x,y,z");
        return new Pose(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture),
            0f, 0f, 0f, 1f);
    }

    public static void PrintUsage()
    {
        // Verbatim (not raw) string: the repo pins C# 10 for the net48/Unity ceiling; raw literals are C# 11.
        Console.WriteLine($@"LocoMP.Bot — headless test player(s) for a LocoMP session (protocol v{ProtocolVersion.Current}).

Usage: LocoMP.Bot [options]
  --host <addr>          server address           (default 127.0.0.1)
  --port <port>          server UDP port          (default {NetDefaults.Port})
  --key <key>            transport connect key    (default {NetDefaults.ConnectKey})
  --password <pw>        session password         (default none)
  --count <n>            number of bots           (default 1)
  --name <prefix>        bot name prefix          (default Bot -> Bot-1, Bot-2, ...)
  --behavior <b>         orbit | wander | idle    (default orbit)
  --at <x,y,z>           centre in world coords   (default 0,0,0 — use YOUR position
                         from the LocoMP host log so avatars appear next to you)
  --radius <m>           orbit/wander radius      (default 5)
  --speed <m/s>          movement speed           (default 1.4)
  --hz <n>               pose sends per second    (default 20)
  --build <s>            game build to present    (default 99-build2702)
  --mod-version <s>      mod version to present   (default: this assembly's version)
  --modlist-hash <s>     mod manifest hash        (default empty)
  --churn <s>            leave + rejoin every N seconds (default 0 = stay)
  --duration <s>         total run time           (default 0 = until Ctrl+C)
  --seed <n>             wander RNG seed          (default 12345)
  --consist <n>          drive an n-car ghost train along the extracted topology
  --consist-speed <m/s>  ghost train speed        (default 8)
  --start-edge <id>      edge to start the ghost on — paste the host log's
                         'ghost-train hint' so it spawns near the player
  --world <path>         extracted .lmpw topology (default: LOCOMP_WORLD_FILE env,
                         then tests/data/world-*.lmpw upward from the working dir)

Examples:
  LocoMP.Bot --at 671,132,591                        one bot orbiting those coords
  LocoMP.Bot --count 8 --behavior wander --radius 30 eight wanderers (join/leave storm: add --churn 15)
  LocoMP.Bot --build WRONG                           verify the host's mismatch screen
  LocoMP.Bot --consist 3 --at 671,132,591            a 3-car ghost train + an orbiting avatar");
    }
}
