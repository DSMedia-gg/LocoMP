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
    public bool ClaimServerTrain;       // M6-B.3: claim + drive a dedicated server's own train, then release
    public string? WorldFile;           // extracted .lmpw; null = probe the usual spots
    public long StartEdge = -1;         // ghost start edge (host logs the nearest one); -1 = walker's pick
    public bool Listen;                 // M3.5b: HOST the session (bot = server + world source)
    public string[] Liveries = Array.Empty<string>(); // real livery ids for the consist (host logs a hint)
    public string CargoId = "";         // cargo id loaded onto the consist's wagons
    public float CargoAmount = 0f;      // 0 = the car's capacity
    public int DerailCar = 0;           // 1-based consist car streamed as DERAILED at --at (0 = none)

    // M3.5c remote-parity rig: exercise career + cab-input flows as the "remote player".
    public bool ClaimFirst;             // claim the first available job after the career burst
    public double ReportIntervalSeconds; // retry "Report delivery" every N s (0 = never)
    public double AbandonAfterSeconds;  // abandon the held claim after N s (0 = never)
    public bool Drive;                  // grab a grant on a host loco and push its throttle
    public string DriveCarId = "";      // target car by its game plate (e.g. L-013); "" = first loco
    public float DriveValue = 0.35f;    // throttle to send while driving
    public double DriveSeconds = 15;    // how long before throttle-to-zero + grant release

    // M4.2 item rig: pick up world items the host drops, then re-drop them elsewhere.
    public bool GrabItems;              // pick up world items as they appear, then drop them again
    public double DropAfterSeconds = 20; // hold a picked-up/bought item this long before dropping it

    // M4 shops: buy a prefab from the shop as the "remote client" — the win condition on one PC.
    public string BuyPrefab = "";       // itemPrefabName to buy once joined ("" = don't buy)

    // M4 comms radio: drive the remote-action wire path (the host executes + charges YOUR wallet).
    public string RerailCar = "";       // game plate (e.g. L-014) to rerail once joined (dest = --at)
    public string ClearCar = "";        // game plate to delete (clear) once joined

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
                    case "--claim-server-train": o.ClaimServerTrain = true; break;
                    case "--world": o.WorldFile = Next(); break;
                    case "--start-edge": o.StartEdge = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--listen": o.Listen = true; break;
                    case "--livery": o.Liveries = Next().Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                    case "--cargo":
                    {
                        string[] parts = Next().Split(':');
                        o.CargoId = parts[0];
                        if (parts.Length > 1) o.CargoAmount = float.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    }
                    case "--derail-car": o.DerailCar = Math.Max(0, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--claim-first": o.ClaimFirst = true; break;
                    case "--report-interval": o.ReportIntervalSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--abandon-after": o.AbandonAfterSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--drive": o.Drive = true; break;
                    case "--drive-car": o.DriveCarId = Next(); o.Drive = true; break;
                    case "--drive-value": o.DriveValue = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--drive-seconds": o.DriveSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--grab-items": o.GrabItems = true; break;
                    case "--drop-after": o.DropAfterSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--buy": o.BuyPrefab = Next(); break;
                    case "--rerail": o.RerailCar = Next(); break;
                    case "--clear": o.ClearCar = Next(); break;
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
  --consist-speed <m/s>  ghost train speed        (default 8; also the claim-drive speed)
  --claim-server-train   join a dedicated server running its own trains (--spawn-trains N),
                         CLAIM one and drive it along the topology, then release it after
                         --drive-seconds (the server resumes). Needs --world; drives at
                         --consist-speed. Ctrl+C also hands it back (reclaim-on-disconnect)
  --start-edge <id>      edge to start the ghost on — paste the host log's
                         'ghost-train hint' so it spawns near the player
  --world <path>         extracted .lmpw topology (default: LOCOMP_WORLD_FILE env,
                         then tests/data/world-*.lmpw upward from the working dir)
  --listen               HOST the session instead of joining one (bot = server; join
                         it from the game to test the CLIENT side on one PC)
  --livery <a,b,c>       real livery ids for the consist (first = car 1, rest cycle) —
                         paste the host log's 'bot livery hint' so the consist spawns
                         as REAL cars in the game instead of ghost boxes
  --cargo <id[:amt]>     load this cargo onto the consist's wagons (amt default: full)
  --derail-car <n>       stream consist car n (1-based) as DERAILED at the --at point —
                         a joining client then exercises the null-track spawn path
  --claim-first          claim the first available board job (the remote-claim rig)
  --report-interval <s>  retry 'Report delivery' on the held claim every N seconds —
                         refusals log until the host world says the cars are delivered
  --abandon-after <s>    abandon the held claim after N seconds (external jobs DIE)
  --drive                request a control grant on a host loco and push its throttle
  --drive-car <plate>    drive THIS car (game plate, e.g. L-013; implies --drive) —
                         without it the bot picks the first loco it sees, which may
                         not be the one you're standing next to
  --drive-value <v>      throttle value to send   (default 0.35)
  --drive-seconds <s>    driving time before throttle 0 + release (default 15)
  --grab-items           pick up world items as the host drops them, then re-drop them —
                         watch the item vanish from the host's world and reappear
  --buy <prefabName>     buy this item from the shop once joined (the client-buys-a-lantern
                         win condition: YOUR wallet is debited, the host's is not), then drop
                         it after --drop-after so the host sees it materialize
  --drop-after <s>       hold a grabbed/bought item this long before dropping it (default 20)
  --rerail <plate>       rerail the host's car with this game plate (e.g. L-014) to the --at
                         point once joined — the host does it, YOUR wallet pays the fee
  --clear <plate>        delete (comms-radio Clear) the host's car with this plate; the host
                         removes it everywhere and charges YOUR wallet

Examples:
  LocoMP.Bot --at 671,132,591                        one bot orbiting those coords
  LocoMP.Bot --count 8 --behavior wander --radius 30 eight wanderers (join/leave storm: add --churn 15)
  LocoMP.Bot --build WRONG                           verify the host's mismatch screen
  LocoMP.Bot --consist 3 --at 671,132,591            a 3-car ghost train + an orbiting avatar
  LocoMP.Bot --consist 3 --livery LocoDE2,Boxcar     the same train as REAL spawned cars
  LocoMP.Bot --listen --consist 3 --livery ...       host a session; join from the game
  LocoMP.Bot --claim-server-train --world w.lmpw      borrow + drive a dedicated server's train
  LocoMP.Bot --claim-first --report-interval 30      claim a captured job; report until paid
  LocoMP.Bot --drive --drive-seconds 20              drive the host's loco from outside
  LocoMP.Bot --grab-items --at 671,132,591           pick up items you drop, re-drop them near you
  LocoMP.Bot --buy Lantern --at 671,132,591          buy a lantern (your wallet), drop it near you");
    }
}
