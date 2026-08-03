using System.Diagnostics;
using System.Runtime.InteropServices;
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
var interest = new InterestConfig
{
    Enabled = opts.Interest,
    FilterPlayers = opts.InterestPlayers,
    EnterRadiusM = (float)opts.InterestRadius,
    LeaveRadiusM = (float)(opts.InterestRadius * 1.5), // hysteresis band — must exceed the enter radius
};
var config = new ServerConfig(opts.ToIdentity(), opts.Password, opts.MaxPlayers, career, interest: interest);

// The extracted rail network. Loaded up front (not just for --spawn-trains) because interest
// management needs it too: without per-edge world geometry the server cannot place a spline-space
// bogie, so it can't distance-test a railed train and fails open (D10 Burst 2).
WorldTopology? topology = null;
string? worldFile = opts.ResolveWorldFile();
if (worldFile is not null)
{
    try
    {
        topology = TopologyCodec.Read(File.ReadAllBytes(worldFile));
        Console.WriteLine($"[server] topology {Path.GetFileName(worldFile)}: {topology.Edges.Count} edge(s), " +
                          $"build {topology.GameBuild}, world geometry " +
                          (topology.HasGeometry
                              ? topology.GeometryEdgeCount == topology.Edges.Count
                                  ? "present."
                                  : $"on {topology.GeometryEdgeCount}/{topology.Edges.Count} edge(s) (bare edges broadcast per-train)."
                              : "ABSENT (v1 file)."));
    }
    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        Console.WriteLine($"[server] topology unreadable ({e.Message}) — running without one.");
    }
}

// A topology from a DIFFERENT game build has different edge ids, so its geometry would place trains
// at arbitrary wrong places — worse than having none. Refuse it for interest (the walker's own build
// check is separate and unchanged).
if (topology is not null && topology.GameBuild != opts.GameBuild)
{
    Console.WriteLine($"[server] topology build '{topology.GameBuild}' != session build '{opts.GameBuild}' — " +
                      "edge ids are only stable within a build, so it will NOT be used for interest management.");
}

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

WorldTopology? interestTopology = topology is not null && topology.GameBuild == opts.GameBuild ? topology : null;
using var server = new NetServer(udp, config, clock, restore, interestTopology);
server.PlayerAdmitted += p => Console.WriteLine($"[server] admitted {p.Name} (id {p.Id}) — {server.PlayerCount} player(s)");
server.PlayerRemoved += id => Console.WriteLine($"[server] removed id {id} — {server.PlayerCount} player(s)");

var autosaver = new Autosaver(clock, opts.AutosaveSeconds * 1000L, storage, () => SaveCodec.Write(server.CaptureSave()));
autosaver.SaveFailed += e => Console.WriteLine(
    $"[server] WARNING: save failed — {e.Message} (world changes since the last good save are not on disk)");
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
    if (topology is null)
    {
        Console.Error.WriteLine("[server] --spawn-trains needs an extracted topology (.lmpw): pass --world <path>, " +
                                "set LOCOMP_WORLD_FILE, or run from the repo. No server trains spawned.");
    }
    else
    {
        for (int i = 0; i < opts.SpawnTrains; i++)
            kinematicTrains.Add(new ServerKinematicTrain(server.Trains, topology, opts.TrainCars, opts.TrainSpeed,
                                                         seed: 1000 + i, liveries: opts.TrainLiveries));
        Console.WriteLine($"[server] driving {kinematicTrains.Count} server-owned train(s) of {opts.TrainCars} car(s) " +
                          $"at {opts.TrainSpeed:F0} m/s along {Path.GetFileName(worldFile)} ({topology.Edges.Count} edges).");
    }
}

// Say plainly what interest management will actually do — an operator who passes --interest and sees
// no bandwidth change deserves to know the train filter fell back to broadcast, and why.
if (opts.Interest)
{
    var suppressed = new List<string>();
    foreach (EntityKind k in server.Interest.Suppressed) suppressed.Add(k.ToString());
    Console.WriteLine($"[server] interest management ON — enter {interest.EnterRadiusM:F0} m / leave {interest.LeaveRadiusM:F0} m, " +
                      $"players {(interest.FilterPlayers ? "filtered" : "broadcast")}, " +
                      $"trains {(suppressed.Contains("Trainset") ? "BROADCAST (no world geometry — extract a fresh .lmpw)" : "filtered")}.");
}

var stopwatch = Stopwatch.StartNew();
string Status() =>
    $"[status] up {stopwatch.Elapsed:hh\\:mm\\:ss} | {server.PlayerCount}/{opts.MaxPlayers} player(s) | " +
    $"{server.Career.Registry.Jobs.Count} job(s) on board | {autosaver.SavesWritten} save(s) written";

// Soak/health watch (M6-B soak exit): a periodic leak + conservation line for an unattended run, and
// an optional self-terminating duration so a bounded soak stops + saves cleanly on its own (which also
// dodges the env trap where a detached server exe outlives its shell and locks LocoMP.Core.dll).
SoakReporter? soak = opts.SoakReportSeconds > 0
    ? new SoakReporter(clock, (long)(opts.SoakReportSeconds * 1000))
    : null;
long durationMs = (long)(opts.DurationSeconds * 1000);
if (soak is not null)
{
    Console.WriteLine($"[server] soak health report every {opts.SoakReportSeconds:F0}s" +
                      (durationMs > 0 ? $"; self-terminating after {opts.DurationSeconds:F0}s." : "."));
    // Each report forces a full collection to read the retained set, which briefly stalls the loop. That
    // cannot produce a false FAIL (the verdict is the conservation flags plus the leak test), but at a
    // short interval the stalls are frequent enough that the run stops representing unattended behaviour.
    if (opts.SoakReportSeconds < 30)
        Console.WriteLine($"[server] NOTE: a {opts.SoakReportSeconds:F0}s report interval stalls the loop often enough to " +
                          "skew a long soak — 30s or more is the representative cadence.");
}

bool stopping = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };
// docker stop / systemd stop send SIGTERM, not Ctrl+C — handle it too so a containerized server saves
// the world on the way down instead of being hard-killed after the grace timer (losing up to one
// autosave interval). Cancel the default terminate so the loop below can drain + SaveNow. No-op on
// platforms without POSIX signals.
using PosixSignalRegistration? sigterm =
    PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stopping = true; });

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
    if (admin.Drain(Status, () => autosaver.SaveNow())) stopping = true; // failure already logs via SaveFailed

    // Only gather the sample when a report is actually due — it queries process memory and forces a
    // full collection, so Due() keeps that cost on the report cadence rather than on every tick.
    if (soak is not null && soak.Due())
    {
        long workingSet; using (var proc = Process.GetCurrentProcess()) workingSet = proc.WorkingSet64;
        string? line = soak.Poll(new SoakSample(
            server.PlayerCount, server.Trains.Registry.Sets.Count, server.Career.Registry.Jobs.Count,
            server.Items.Registry.Items.Count, server.Trains.StaleSnapshotsDropped,
            SoakReporter.ReadRetainedBytes(), workingSet,
            server.Career.Registry.Ledger.ConservationHolds, server.Items.Registry.ItemConservationHolds));
        if (line is not null) Console.WriteLine(line);
    }

    if (durationMs > 0 && stopwatch.ElapsedMilliseconds >= durationMs) stopping = true;

    long elapsed = stopwatch.ElapsedMilliseconds - now;
    if (elapsed < tickMs) Thread.Sleep((int)(tickMs - elapsed));
}

Console.WriteLine("[server] shutting down — saving world…");
bool finalSaved = autosaver.SaveNow(); // capture reads the live registries, so save BEFORE disposing
server.Dispose();
udp.Dispose();
Thread.Sleep(150);                 // let LiteNetLib flush the disconnects
if (soak is not null) Console.WriteLine(soak.Summary());
Console.WriteLine(finalSaved
    ? $"[server] saved to {opts.SavePath}. Bye."
    : $"[server] FINAL SAVE FAILED — {opts.SavePath} was not written (see the warning above). Bye.");
// Exit codes: 2 = soak latched unhealthy (the unattended-run contract, checked first); 1 = the
// world's final save never reached disk; 0 = clean. Both non-zero cases are for scripts to catch.
return soak?.EverUnhealthy == true ? 2 : finalSaved ? 0 : 1;
