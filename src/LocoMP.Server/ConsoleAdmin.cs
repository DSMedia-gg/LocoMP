using System.Collections.Concurrent;

namespace LocoMP.Server;

/// <summary>
/// Minimal console admin. A background thread reads stdin (blocking) and enqueues each line; the main
/// loop drains the queue and executes commands on the LOOP thread — the NetServer, its registries, and
/// the transport are single-threaded and must never be touched off the tick thread.
/// This slice: <c>status</c>, <c>save</c>, <c>stop</c>/<c>quit</c>, <c>help</c>. (<c>kick</c> waits on a
/// public NetServer removal API — a separate small Core change.)
/// </summary>
public sealed class ConsoleAdmin
{
    private readonly ConcurrentQueue<string> _lines = new();

    public ConsoleAdmin()
    {
        var t = new Thread(ReadLoop) { IsBackground = true, Name = "console-admin" };
        t.Start();
    }

    private void ReadLoop()
    {
        // ReadLine returns null when stdin is closed/redirected-and-exhausted — stop reading, don't spin.
        string? line;
        while ((line = Console.ReadLine()) != null)
            _lines.Enqueue(line.Trim());
    }

    /// <summary>A restore command ran: the world on disk is now the chosen backup, so the shutdown
    /// path must SKIP its final save (it would overwrite the restore with the live state).</summary>
    public bool RestorePerformed { get; private set; }

    /// <summary>Drain queued commands on the calling (main-loop) thread. Returns true if a stop/quit
    /// command was seen, so the loop can exit.</summary>
    public bool Drain(Func<string> status, Action saveNow,
                      Func<string>? listBackups = null, Func<int, (bool ok, string message)>? restore = null,
                      Action<string>? say = null,
                      Func<string>? bans = null, Func<int, bool>? unban = null)
    {
        bool stop = false;
        while (_lines.TryDequeue(out string? cmd))
        {
            string lower = cmd.ToLowerInvariant();
            switch (lower)
            {
                case "": break;
                case "status": Console.WriteLine(status()); break;
                case "save": saveNow(); Console.WriteLine("[admin] saved."); break;
                case "stop" or "quit" or "exit": stop = true; break;
                case "backups":
                    Console.WriteLine(listBackups is null ? "[admin] no backup storage attached" : listBackups());
                    break;
                case "bans":
                    // R4-A: the dedicated operator's ban surface — entries are (id, name); keys never print.
                    Console.WriteLine(bans is null ? "[admin] no moderation attached" : bans());
                    break;
                case "help" or "?":
                    Console.WriteLine("commands: status | save | say <text> | bans | unban <id> | backups | restore <n> | stop | help");
                    break;
                default:
                    if (lower.StartsWith("unban ", StringComparison.Ordinal) && unban != null &&
                        int.TryParse(lower.Substring("unban ".Length).Trim(), out int banId))
                    {
                        Console.WriteLine(unban(banId)
                            ? $"[admin] ban {banId} lifted — they can rejoin."
                            : $"[admin] no session ban with id {banId} (try 'bans').");
                        break;
                    }
                    // M5.4: talk to the session as THE SERVER (players see a [server] chat line).
                    // Case-preserving — the original cmd is sliced, not the lowered copy.
                    if (lower.StartsWith("say ", StringComparison.Ordinal) && say != null)
                    {
                        say(cmd.Substring("say ".Length));
                        break;
                    }
                    // M5.2 restore: roll the world back to backup n, then STOP without the final
                    // save — the next start loads the restored world. Everything is rotation-safe
                    // (the displaced current became .1), so a mistaken restore is itself undoable.
                    if (lower.StartsWith("restore ", StringComparison.Ordinal) && restore != null &&
                        int.TryParse(lower.Substring("restore ".Length).Trim(), out int index))
                    {
                        (bool ok, string message) = restore(index);
                        Console.WriteLine("[admin] " + message);
                        if (ok)
                        {
                            RestorePerformed = true;
                            stop = true;
                        }
                        break;
                    }
                    Console.WriteLine($"[admin] unknown command '{cmd}' — try: status | save | say <text> | bans | unban <id> | backups | restore <n> | stop | help");
                    break;
            }
        }
        return stop;
    }
}
