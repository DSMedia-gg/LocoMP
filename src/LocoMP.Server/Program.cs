using System.Diagnostics;
using LocoMP.Core.Career;
using LocoMP.Core.Persistence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.World;
using LocoMP.Server;
using LocoMP.Transport;

// Headless dedicated server (03 §6, M6 Track B — pulled forward so multiplayer is solo-testable).
// It wires the game-free Core stack (NetServer + persistence + the deterministic career generator) over
// real LiteNetLib UDP — the same stack the bot's --listen mode proves — into a standalone process with a
// persistent world. No game, no Unity, no game assemblies.

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected output — fine as-is */ }

ServerOptions? opts = ServerOptions.Parse(args);
if (opts is null) return 1;

var clock = new SystemClock();

// --dump-config: write the built-in default career to a .lmpc file and exit — a seed to edit, a way to
// produce a config for testing --config without the game, and the reference shape the Shim exporter fills.
if (opts.DumpConfigPath is not null)
{
    byte[] seed = CareerConfigCodec.Write(DefaultCareer.Build(opts.Preset));
    File.WriteAllBytes(opts.DumpConfigPath, seed);
    Console.WriteLine($"[server] wrote the built-in default career to {opts.DumpConfigPath} ({seed.Length} bytes) — " +
                      "replace it with a game-exported career and pass it with --config.");
    return 0;
}

// Career board. --config loads a real career (.lmpc — a Shim export or a --dump-config seed); the file is
// authoritative (including the preset). Without it — or if the file is missing/foreign/corrupt — fall back
// to the built-in synthetic default so the server always runs.
CareerConfig career = DefaultCareer.Build(opts.Preset);
if (opts.ConfigPath is not null)
{
    try
    {
        career = CareerConfigCodec.Read(File.ReadAllBytes(opts.ConfigPath));
        opts.Preset = career.Preset; // keep the rest of the run (save-preset guard, banner) consistent
        Console.WriteLine($"[server] loaded career config from {opts.ConfigPath}: preset {career.Preset}, " +
                          $"{career.Stations.Count} station(s), {career.JobTypes.Count} job type(s), " +
                          $"{career.LicensePrices.Count} purchasable license(s).");
    }
    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        Console.WriteLine($"[server] career config unreadable ({e.Message}) — using the built-in default career.");
    }
}
var config = new ServerConfig(opts.ToIdentity(), opts.Password, opts.MaxPlayers, career);

// Restore a saved world if one exists. A corrupt/foreign/older save is refused cleanly — start fresh,
// the rotated backups keep the old bytes.
var storage = new FileSaveStorage(opts.SavePath);
ServerSaveData? restore = null;
byte[]? saved = storage.TryLoad();
if (saved is not null)
{
    try
    {
        restore = SaveCodec.Read(saved);
        Console.WriteLine($"[server] loaded world from {opts.SavePath}.");
    }
    catch (InvalidDataException e)
    {
        Console.WriteLine($"[server] save unreadable ({e.Message}) — starting fresh (backups kept).");
    }
}

// A preset switch between runs would throw from the CareerRegistry restore inside the NetServer ctor
// (wallet migration between presets is undefined) — drop the restore and start fresh instead of crashing.
if (restore is not null && restore.Career.Preset != opts.Preset)
{
    Console.WriteLine($"[server] saved preset {restore.Career.Preset} != requested {opts.Preset} — starting a fresh world for this preset.");
    restore = null;
}

LiteNetLibTransport udp;
try
{
    udp = LiteNetLibTransport.StartServer(opts.Port, opts.Key);
}
catch (Exception e)
{
    Console.Error.WriteLine($"[server] could not bind UDP {opts.Port}: {e.Message}");
    return 1;
}

using var server = new NetServer(udp, config, clock, restore);
server.PlayerAdmitted += p => Console.WriteLine($"[server] admitted {p.Name} (id {p.Id}) — {server.PlayerCount} player(s)");
server.PlayerRemoved += id => Console.WriteLine($"[server] removed id {id} — {server.PlayerCount} player(s)");

var autosaver = new Autosaver(clock, opts.AutosaveSeconds * 1000L, storage, () => SaveCodec.Write(server.CaptureSave()));
var admin = new ConsoleAdmin();

server.Poll(); // prime the deterministic board (Career.Tick fills it) so the banner's job count is real

Console.WriteLine($"LocoMP.Server '{opts.Name}' — protocol v{ProtocolVersion.Current}, build {opts.GameBuild}, mod {opts.ModVersion}, preset {opts.Preset}.");
Console.WriteLine($"[server] listening on UDP {udp.Port} — join from the game (Direct connect 127.0.0.1:{udp.Port}).");
Console.WriteLine($"[server] world save: {opts.SavePath} (autosave every {opts.AutosaveSeconds}s). Board: {server.Career.Registry.Jobs.Count} job(s). Type 'help' for commands.");

// Server-owned kinematic trains (M6-B.2): the server drives its own consists along the extracted
// topology, so a fresh server has moving trains with no bot. Needs a .lmpw to walk.
var kinematicTrains = new List<ServerKinematicTrain>();
if (opts.SpawnTrains > 0)
{
    string? worldPath = opts.ResolveWorldFile();
    if (worldPath is null)
    {
        Console.Error.WriteLine("[server] --spawn-trains needs an extracted topology (.lmpw): pass --world <path>, " +
                                "set LOCOMP_WORLD_FILE, or run from the repo. No server trains spawned.");
    }
    else
    {
        WorldTopology topo = TopologyCodec.Read(File.ReadAllBytes(worldPath));
        for (int i = 0; i < opts.SpawnTrains; i++)
            kinematicTrains.Add(new ServerKinematicTrain(server.Trains, topo, opts.TrainCars, opts.TrainSpeed,
                                                         seed: 1000 + i, liveries: opts.TrainLiveries));
        Console.WriteLine($"[server] driving {kinematicTrains.Count} server-owned train(s) of {opts.TrainCars} car(s) " +
                          $"at {opts.TrainSpeed:F0} m/s along {Path.GetFileName(worldPath)} ({topo.Edges.Count} edges).");
    }
}

var stopwatch = Stopwatch.StartNew();
string Status() =>
    $"[status] up {stopwatch.Elapsed:hh\\:mm\\:ss} | {server.PlayerCount}/{opts.MaxPlayers} player(s) | " +
    $"{server.Career.Registry.Jobs.Count} job(s) on board | {autosaver.SavesWritten} save(s) written";

bool stopping = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };

long tickMs = (long)(1000 / opts.Hz);
long lastTimeSync = 0, lastTick = 0;
while (!stopping)
{
    long now = stopwatch.ElapsedMilliseconds;
    double dt = lastTick == 0 ? 1.0 / opts.Hz : (now - lastTick) / 1000.0;
    lastTick = now;

    server.Poll();                 // pumps the transport + Career.Tick() (board refill, TTL/grace)
    foreach (ServerKinematicTrain t in kinematicTrains) t.Tick(dt); // advance + publish server trains
    if (now - lastTimeSync >= 5_000)
    {
        lastTimeSync = now;
        server.BroadcastTime();
    }
    autosaver.Tick();
    if (admin.Drain(Status, autosaver.SaveNow)) stopping = true;

    long elapsed = stopwatch.ElapsedMilliseconds - now;
    if (elapsed < tickMs) Thread.Sleep((int)(tickMs - elapsed));
}

Console.WriteLine("[server] shutting down — saving world…");
autosaver.SaveNow();               // capture reads the live registries, so save BEFORE disposing
server.Dispose();
udp.Dispose();
Thread.Sleep(150);                 // let LiteNetLib flush the disconnects
Console.WriteLine($"[server] saved to {opts.SavePath}. Bye.");
return 0;
