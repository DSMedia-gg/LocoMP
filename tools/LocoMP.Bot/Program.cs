using System.Diagnostics;
using LocoMP.Bot;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Session;
using LocoMP.Core.World;
using LocoMP.Transport;

// Headless test player swarm (03 §11, hard rule 8). See BotOptions.PrintUsage for the workflow.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected output — fine as-is */ }

BotOptions? opts = BotOptions.Parse(args);
if (opts is null) return 1;

Console.WriteLine($"LocoMP.Bot → {opts.Host}:{opts.Port} — {opts.Count} × {opts.Behavior}, " +
                  $"build {opts.GameBuild}, mod {opts.ModVersion}, {opts.Hz:F0} Hz" +
                  (opts.ChurnSeconds > 0 ? $", churn {opts.ChurnSeconds}s" : ""));

// The ghost train (M2) needs the extracted world topology to drive on.
WorldTopology? world = null;
if (opts.ConsistCars > 0)
{
    string? worldPath = opts.WorldFile ?? FindWorldFile();
    if (worldPath is null || !File.Exists(worldPath))
    {
        Console.Error.WriteLine("--consist needs an extracted topology (.lmpw). Pass --world <path>, " +
                                "set LOCOMP_WORLD_FILE, or extract one in-game first (mod panel).");
        return 1;
    }
    world = TopologyCodec.Read(File.ReadAllBytes(worldPath));
    Console.WriteLine($"Loaded world '{world.GameBuild}': {world.Edges.Count} edges, {world.Junctions.Count} junctions ({Path.GetFileName(worldPath)})");
}

var clock = new SystemClock();

// M3.5b listen mode: the bot IS the server (plus its own first client over the loopback hub) so
// the game can JOIN as a client — the one-PC rig for the client-side real-car path. Mirrors the
// in-game host wiring: LoopbackNetwork for self, LiteNetLib UDP for the game, one composite.
NetServer? server = null;
CompositeTransport? serverTransport = null;
LoopbackNetwork? hub = null;
if (opts.Listen)
{
    hub = new LoopbackNetwork();
    var udp = LiteNetLibTransport.StartServer(opts.Port, opts.Key);
    serverTransport = new CompositeTransport(hub.Server, udp);
    server = new NetServer(serverTransport, new ServerConfig(opts.ToIdentity(), opts.Password), clock);
    server.PlayerAdmitted += p => Console.WriteLine($"[server] admitted {p.Name} (id {p.Id}) — {server!.PlayerCount} player(s)");
    server.PlayerRemoved += id => Console.WriteLine($"[server] removed id {id} — {server!.PlayerCount} player(s)");
    Console.WriteLine($"[server] hosting on UDP {opts.Port} — join from the game (address 127.0.0.1)");
}

var bots = new List<BotClient>(opts.Count);
for (int i = 0; i < opts.Count; i++)
{
    string name = opts.Count == 1 ? opts.Name : $"{opts.Name}-{i + 1}";
    // Spread bots around the circle / seed space so a swarm doesn't stack into one spot.
    IBotBehavior behavior = opts.Behavior switch
    {
        "idle" => new IdleBehavior(OffsetIdle(opts.Center, i)),
        "wander" => new WanderBehavior(opts.Center, opts.Radius, opts.Speed, opts.Seed + i),
        _ => new OrbitBehavior(opts.Center, opts.Radius, opts.Speed, startAngle: i * Math.PI * 2 / opts.Count),
    };
    Func<ITransport> connect = hub is { } h
        ? () => h.Connect(out _)
        : () => LiteNetLibTransport.ConnectClient(opts.Host, opts.Port, opts.Key);
    var bot = new BotClient(name, connect,
        opts.ToIdentity(), behavior, clock, Console.WriteLine,
        opts.Password, opts.ChurnSeconds);
    if (world != null)
    {
        // Multiple ghosts share a start hint; stagger them a seed apart so they diverge at junctions.
        uint? startEdge = opts.StartEdge >= 0 ? (uint)opts.StartEdge : (uint?)null;
        var driver = new ConsistDriver(world, opts.ConsistCars, opts.ConsistSpeed, opts.Seed + i, name, Console.WriteLine,
            startEdge, opts.Liveries, opts.CargoId, opts.CargoAmount);
        bot.SessionTick = driver.Tick;
    }
    bots.Add(bot);
}

// Ctrl+C leaves gracefully instead of ghosting the server (it would still evict on timeout, but a
// clean Leave keeps host-side logs readable during avatar bring-up).
bool stopping = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };

var sw = Stopwatch.StartNew();
long tickMs = (long)(1000 / opts.Hz);
long lastTick = 0, lastStats = 0, lastTimeSync = 0;

while (!stopping && (opts.DurationSeconds <= 0 || sw.Elapsed.TotalSeconds < opts.DurationSeconds))
{
    long now = sw.ElapsedMilliseconds;
    double dt = (now - lastTick) / 1000.0;
    lastTick = now;

    server?.Poll();
    if (server != null && now - lastTimeSync >= 5_000)
    {
        lastTimeSync = now;
        server.BroadcastTime();
    }
    foreach (BotClient bot in bots) bot.Tick(dt);

    if (bots.All(b => b.RejectReason != null))
    {
        Console.Error.WriteLine("All bots were rejected — nothing left to do.");
        break;
    }

    if (now - lastStats >= 10_000)
    {
        lastStats = now;
        Console.WriteLine($"[stats] up {sw.Elapsed:hh\\:mm\\:ss} | joined {bots.Count(b => b.Joined)}/{bots.Count} " +
                          $"| poses {bots.Sum(b => b.PosesSent)} | joins {bots.Sum(b => b.JoinCount)} " +
                          $"| sees {(bots.FirstOrDefault(b => b.Joined)?.VisiblePlayers ?? 0)} other player(s)");
    }

    long elapsed = sw.ElapsedMilliseconds - now;
    if (elapsed < tickMs) Thread.Sleep((int)(tickMs - elapsed));
}

Console.WriteLine("Shutting down — leaving session(s)…");
foreach (BotClient bot in bots) bot.Dispose();
server?.Dispose();
serverTransport?.Dispose();
Thread.Sleep(150); // let LiteNetLib flush the disconnects
Console.WriteLine($"Done. {bots.Sum(b => b.PosesSent)} poses sent across {bots.Sum(b => b.JoinCount)} join(s).");
return 0;

// Idle bots line up a metre apart so --count with idle doesn't put everyone inside everyone.
static Pose OffsetIdle(Pose c, int i) => new(c.Px + i, c.Py, c.Pz, c.Rx, c.Ry, c.Rz, c.Rw);

// Same probe order as the Core test: explicit env override, then tests/data/ walking up from here
// (covers both `dotnet run` from the repo and the built exe under tools/.../bin).
static string? FindWorldFile()
{
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
