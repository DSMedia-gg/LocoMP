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

    /// <summary>Drain queued commands on the calling (main-loop) thread. Returns true if a stop/quit
    /// command was seen, so the loop can exit.</summary>
    public bool Drain(Func<string> status, Action saveNow)
    {
        bool stop = false;
        while (_lines.TryDequeue(out string? cmd))
        {
            switch (cmd.ToLowerInvariant())
            {
                case "": break;
                case "status": Console.WriteLine(status()); break;
                case "save": saveNow(); Console.WriteLine("[admin] saved."); break;
                case "stop" or "quit" or "exit": stop = true; break;
                case "help" or "?":
                    Console.WriteLine("commands: status | save | stop | help");
                    break;
                default:
                    Console.WriteLine($"[admin] unknown command '{cmd}' — try: status | save | stop | help");
                    break;
            }
        }
        return stop;
    }
}
