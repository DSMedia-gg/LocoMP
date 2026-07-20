using System.Diagnostics;
using LocoMP.Core.Career;
using LocoMP.Core.Persistence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
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

// Career board. A real game-exported career is a later slice; --config is reserved for it.
if (opts.ConfigPath is not null)
    Console.WriteLine($"[server] --config is not supported yet ({opts.ConfigPath}); using the built-in default career.");
CareerConfig career = DefaultCareer.Build(opts.Preset);
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

var stopwatch = Stopwatch.StartNew();
string Status() =>
    $"[status] up {stopwatch.Elapsed:hh\\:mm\\:ss} | {server.PlayerCount}/{opts.MaxPlayers} player(s) | " +
    $"{server.Career.Registry.Jobs.Count} job(s) on board | {autosaver.SavesWritten} save(s) written";

bool stopping = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };

long tickMs = (long)(1000 / opts.Hz);
long lastTimeSync = 0;
while (!stopping)
{
    long now = stopwatch.ElapsedMilliseconds;

    server.Poll();                 // pumps the transport + Career.Tick() (board refill, TTL/grace)
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
