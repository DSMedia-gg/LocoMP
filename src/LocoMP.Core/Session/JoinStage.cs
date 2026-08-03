namespace LocoMP.Core.Session;

/// <summary>
/// Where a joining client is in the join burst (M5.1). Drives the loading interstitial's staged
/// display. The intermediate stages are INFERRED from ordered delivery — the server sends the burst
/// as roster → world → career → items, so the first message of each family proves every earlier
/// family fully arrived. Only <see cref="Complete"/> is authoritative: it is set by the server's
/// JoinBurstComplete sentinel, and it is the only stage a readiness gate may clear on (a stage
/// inferred from traffic could false-positive on an empty burst family; the sentinel cannot).
/// Stages only ever advance (monotonic) and reset on disconnect.
/// </summary>
public enum JoinStage : byte
{
    /// <summary>No join in flight (also the post-disconnect state).</summary>
    None = 0,

    /// <summary>Transport connected; handshake sent, no acceptance yet.</summary>
    Connecting = 1,

    /// <summary>Admitted (id + roster received); the world burst (trainsets/junctions/grants) is arriving.</summary>
    World = 2,

    /// <summary>First career-family message seen — the world burst is fully delivered.</summary>
    Career = 3,

    /// <summary>First item-family message seen — the career burst is fully delivered.</summary>
    Items = 4,

    /// <summary>The server's JoinBurstComplete sentinel arrived: the whole burst is delivered.</summary>
    Complete = 5,
}
