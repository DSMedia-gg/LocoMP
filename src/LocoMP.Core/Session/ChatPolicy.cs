using System;
using System.Collections.Generic;
using System.Text;

namespace LocoMP.Core.Session;

/// <summary>
/// Chat admission policy (M5.4): sanitisation + a per-peer token bucket — the first concrete slice
/// of the per-client rate limits 03 §9 promises (chat is where a limit is cheapest and the abuse is
/// most visible). Pure and clock-injected, so every rule — what survives sanitisation, when the
/// bucket refuses, when the warning fires — is pinned headlessly.
///
/// <para>Bucket shape: burst of <see cref="BurstSize"/> lines, refilling one line per
/// <see cref="RefillMs"/> — enough for conversation, a wall for a paste-bomb. A refusal is silent
/// to everyone else; the sender gets at most one warning per <see cref="WarnCooldownMs"/> so the
/// warning itself can't be farmed into spam.</para>
/// </summary>
public sealed class ChatPolicy
{
    /// <summary>Longest committed line in chars — anything longer is truncated, not refused (the
    /// sender sees the truncation in their own echo, which is the honest feedback).</summary>
    public const int MaxLength = 256;

    /// <summary>Lines a peer may burst before the bucket gates them.</summary>
    public const int BurstSize = 5;

    /// <summary>One token (= one line) refills per this many ms — sustained rate 1 line/s.</summary>
    public const int RefillMs = 1000;

    /// <summary>Minimum gap between rate-limit warnings to one peer.</summary>
    public const int WarnCooldownMs = 3000;

    private sealed class Bucket
    {
        public double Tokens = BurstSize;
        public long LastRefillMs;
        public long? LastWarnMs; // null = never warned (a MinValue sentinel would overflow the delta)
    }

    private readonly IClock _clock;
    private readonly Dictionary<int, Bucket> _buckets = new();

    public ChatPolicy(IClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Normalise a raw line: control characters (newlines included — chat lines are one line by
    /// construction) become spaces, surrounding whitespace is trimmed, and the result is capped at
    /// <see cref="MaxLength"/>. Returns the clean text; empty means "nothing worth committing".
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder(Math.Min(raw!.Length, MaxLength));
        foreach (char c in raw)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
            if (sb.Length >= MaxLength) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Charge one line against a peer's bucket. True = committed (send it); false = rate-limited,
    /// with <paramref name="warn"/> true if the sender deserves a (cooldown-gated) warning line.
    /// </summary>
    public bool TryCharge(int peerId, out bool warn)
    {
        warn = false;
        long now = _clock.NowMs;
        if (!_buckets.TryGetValue(peerId, out Bucket? b))
        {
            b = new Bucket { LastRefillMs = now };
            _buckets[peerId] = b;
        }

        // Continuous refill from the last accounting point, capped at the burst size.
        if (now > b.LastRefillMs)
        {
            b.Tokens = Math.Min(BurstSize, b.Tokens + (now - b.LastRefillMs) / (double)RefillMs);
            b.LastRefillMs = now;
        }

        if (b.Tokens >= 1)
        {
            b.Tokens -= 1;
            return true;
        }

        if (b.LastWarnMs is null || now - b.LastWarnMs.Value >= WarnCooldownMs)
        {
            b.LastWarnMs = now;
            warn = true;
        }
        return false;
    }

    /// <summary>Drop a departed peer's bucket (peer ids are transport-scoped and may be reused).</summary>
    public void Forget(int peerId) => _buckets.Remove(peerId);
}
