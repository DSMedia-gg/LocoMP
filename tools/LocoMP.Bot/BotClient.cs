using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;

namespace LocoMP.Bot;

/// <summary>
/// One simulated player: owns a NetClient + transport, drives an <see cref="IBotBehavior"/>, and
/// manages the connection lifecycle — connect timeout, reconnect with backoff (a soak run must
/// survive a server restart, 03 §11/M6), and optional churn (leave + rejoin every N seconds, the
/// live-fire version of the join/leave storm). Transport and clock are injected so the whole
/// lifecycle runs deterministically over Loopback in unit tests; Program.cs injects real UDP.
/// </summary>
public sealed class BotClient : IDisposable
{
    /// <summary>Give up on an unanswered connect attempt after this long, then back off and retry.</summary>
    public const long ConnectTimeoutMs = 10_000;

    /// <summary>Wait between failed attempts. Deliberately gentle — a down server isn't hammered.</summary>
    public const long RetryBackoffMs = 5_000;

    private readonly string _name;
    private readonly Func<ITransport> _connect;
    private readonly HandshakeRequest _identity;
    private readonly string? _password;
    private readonly IBotBehavior _behavior;
    private readonly IClock _clock;
    private readonly Action<string> _log;
    private readonly long _churnMs;

    private ITransport? _transport;
    private NetClient? _client;
    private long _attemptStartedMs;
    private long _joinedAtMs;
    private long _nextAttemptMs;

    public BotClient(string name, Func<ITransport> connectFactory, HandshakeRequest identity,
                     IBotBehavior behavior, IClock clock, Action<string> log,
                     string? password = null, double churnSeconds = 0)
    {
        _name = name;
        _connect = connectFactory;
        _identity = identity;
        _behavior = behavior;
        _clock = clock;
        _log = log;
        _password = password;
        _churnMs = (long)(churnSeconds * 1000);
    }

    public bool Joined => _client?.Joined == true;

    /// <summary>Set (and the bot permanently stopped) when the server refuses the handshake —
    /// a rejection means misconfiguration, and retrying would just spam the exact same refusal.</summary>
    public string? RejectReason { get; private set; }

    public long PosesSent { get; private set; }
    public long JoinCount { get; private set; }

    /// <summary>How many OTHER players this bot currently sees (mirrors the host's roster).</summary>
    public int VisiblePlayers => _client?.Players.Count ?? 0;

    /// <summary>Advance the bot by one tick. Call at the pose rate (Program) or per test step.</summary>
    public void Tick(double dtSeconds)
    {
        if (RejectReason != null) { Teardown(); return; } // release the socket the tick after a rejection
        long now = _clock.NowMs;

        if (_client is null)
        {
            if (now < _nextAttemptMs) return;
            StartAttempt(now);
        }

        _client!.Poll();
        if (RejectReason != null) return; // Poll may have delivered a rejection

        if (_client.Joined)
        {
            if (_joinedAtMs == 0)
            {
                _joinedAtMs = now;
                JoinCount++;
            }

            _client.SendPose(_behavior.Tick(dtSeconds));
            PosesSent++;

            if (_churnMs > 0 && now - _joinedAtMs >= _churnMs)
            {
                _log($"[{_name}] churn: leaving after {(now - _joinedAtMs) / 1000.0:F1}s");
                _client.Leave();
                _client.Poll(); // give the leave message a pump before the socket drops
                Teardown();
                _nextAttemptMs = now + 500; // brief pause, then rejoin
            }
        }
        else if (now - _attemptStartedMs > ConnectTimeoutMs)
        {
            _log($"[{_name}] connect timed out after {ConnectTimeoutMs / 1000}s; retrying in {RetryBackoffMs / 1000}s");
            Teardown();
            _nextAttemptMs = now + RetryBackoffMs;
        }
    }

    private void StartAttempt(long now)
    {
        _attemptStartedMs = now;
        _joinedAtMs = 0;
        _transport = _connect();
        _client = new NetClient(_transport, _identity, _name, _clock, _password);
        _client.Accepted += id => _log($"[{_name}] joined as id {id} (server offset {_client!.ServerTimeOffsetMs} ms, sees {_client.Players.Count} other player(s))");
        _client.Rejected += reason =>
        {
            RejectReason = reason;
            _log($"[{_name}] REJECTED: {reason} — stopping (fix the mismatch and rerun)");
        };
        _client.PlayerJoined += p => _log($"[{_name}] sees player join: {p.Name} (id {p.Id})");
        _client.PlayerLeft += id => _log($"[{_name}] sees player leave: id {id}");
    }

    private void Teardown()
    {
        _client?.Dispose();
        _transport?.Dispose();
        _client = null;
        _transport = null;
    }

    /// <summary>Leave gracefully (best-effort) and release the socket.</summary>
    public void Dispose()
    {
        if (_client?.Joined == true)
        {
            _client.Leave();
            _client.Poll();
        }
        Teardown();
    }
}
