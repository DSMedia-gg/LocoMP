namespace LocoMP.Core.Trains;

/// <summary>
/// Decides when a client should ask the server to replay an unspawned consist's baseline (03 §4's
/// ResyncRequest — the server re-sends the def AND its last known position). This is the pure kernel
/// of <c>RealCarSync</c>'s reconcile poll, kept game-free so the state machine can be pinned headlessly.
///
/// <para><b>Why it exists (2026-08-06 finding #3).</b> The poll did two jobs at once: recover a set
/// whose live snapshot stream was lost, AND re-run the materialise-distance check for a parked, far
/// consist as the player approaches (<c>Apply</c> only ever runs on an arriving snapshot, and <c>Tick</c>
/// skips unspawned sets). But every replay reply is itself a snapshot, and <c>Apply</c> reset the
/// "already logged" flag on every snapshot — so a PARKED set (owner 0, no organic stream) re-logged
/// "requesting a baseline replay" and re-pulled a full def+snapshot on every retry, forever. That was
/// the observed set-34 loop: a genuinely-baselined DE2 parked beside the player, resyncing endlessly.</para>
///
/// <para><b>The fix</b> splits the two jobs by whether the client already holds position data. A set we
/// have NEVER seen a snapshot for needs the baseline urgently (fast cadence, logged once — the real
/// lost-stream / late-join recovery). A set we HAVE baselined but cannot yet materialise (parked / far /
/// spawn-point occupied) needs only a gentle, SILENT keep-alive: re-requesting the identical static
/// baseline changes nothing but the side-effect of re-checking distance, and the D10 relevance replay is
/// the primary approach trigger anyway, so this backstop can be slow and quiet.</para>
/// </summary>
public static class BaselineReplayPolicy
{
    /// <summary>Cadence for a set with no position data yet — recover the missing baseline promptly.</summary>
    public const float FreshRetrySeconds = 10f;

    /// <summary>Cadence for a baselined-but-unspawned set — a quiet keep-alive that re-checks materialise
    /// distance on approach. Slower than <see cref="FreshRetrySeconds"/> because the server's relevance
    /// replay (D10) is the primary approach path; this poll is only the backstop.</summary>
    public const float BaselinedPollSeconds = 20f;

    /// <summary>What the reconcile loop should do with one unspawned set this tick.</summary>
    public readonly struct Decision
    {
        public Decision(bool request, bool log, float nextAllowed)
        {
            Request = request;
            Log = log;
            NextAllowed = nextAllowed;
        }

        /// <summary>Send a ResyncRequest for this set this tick.</summary>
        public bool Request { get; }

        /// <summary>Emit the human "requesting a baseline replay" line — the first fresh request only.</summary>
        public bool Log { get; }

        /// <summary>The per-set throttle to store back; meaningful only when <see cref="Request"/> is true.</summary>
        public float NextAllowed { get; }
    }

    private static readonly Decision Skip = new(false, false, 0f);

    /// <summary>Decide whether to (re)request a baseline replay for one unspawned set.</summary>
    /// <param name="spawned">The set is materialised locally (live and placed) — nothing to heal.</param>
    /// <param name="everBaselined">At least one real snapshot has been applied — position data is in hand.</param>
    /// <param name="secondsSinceSnapshot">now − LastSnapshotAt.</param>
    /// <param name="staleAfter">A stream quiet at least this long counts as gone.</param>
    /// <param name="now">Current unscaled time.</param>
    /// <param name="nextAllowed">The set's stored throttle — no request until <paramref name="now"/> ≥ this.</param>
    /// <param name="alreadyLogged">This set has already logged a fresh request.</param>
    public static Decision Evaluate(
        bool spawned, bool everBaselined, float secondsSinceSnapshot,
        float staleAfter, float now, float nextAllowed, bool alreadyLogged)
    {
        if (spawned) return Skip;                           // live and placed — nothing to heal
        if (secondsSinceSnapshot < staleAfter) return Skip; // stream is alive; Apply decides
        if (now < nextAllowed) return Skip;                 // throttled

        float interval = everBaselined ? BaselinedPollSeconds : FreshRetrySeconds;
        // Log only the FIRST request for a set we have no data for. A baselined set polls silently: its
        // retries are a keep-alive, not a new problem — logging them on every retry was the finding-#3 spam.
        bool log = !everBaselined && !alreadyLogged;
        return new Decision(request: true, log: log, nextAllowed: now + interval);
    }
}
