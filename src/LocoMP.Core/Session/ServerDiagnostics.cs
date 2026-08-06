namespace LocoMP.Core.Session;

/// <summary>
/// A point-in-time snapshot of the session's health counters (M5.2 diagnostics group) — the numbers the
/// host's Diagnostics panel reads. Aggregated from state the server already tracks, so it is cheap and
/// game-free: <see cref="NetServer.CaptureDiagnostics"/> fills it, and the host UI (or a dedicated-server
/// console) renders it.
///
/// <para>Deliberately NOT here yet: live bandwidth and per-peer latency / connection quality. Those come
/// from the concrete transport (LiteNetLib's <c>NetPeer.Ping</c> / byte counters), not from the minimal
/// game-free <c>ITransport</c> — surfacing them needs an <c>ITransport</c> stats extension across all
/// three adapters, a separate slice. This snapshot is the headless half that needs none of that.</para>
/// </summary>
public readonly struct ServerDiagnostics
{
    public ServerDiagnostics(int players, int queued, int trainsets, int jobs, int items,
        long staleSnapshotsDropped, bool moneyConservationHolds, bool itemConservationHolds,
        bool joinsPaused, int admins, int bannedKeys, bool interestEnabled,
        long bytesSent, long bytesReceived, long messagesSent, long messagesReceived)
    {
        Players = players;
        Queued = queued;
        Trainsets = trainsets;
        Jobs = jobs;
        Items = items;
        StaleSnapshotsDropped = staleSnapshotsDropped;
        MoneyConservationHolds = moneyConservationHolds;
        ItemConservationHolds = itemConservationHolds;
        JoinsPaused = joinsPaused;
        Admins = admins;
        BannedKeys = bannedKeys;
        InterestEnabled = interestEnabled;
        BytesSent = bytesSent;
        BytesReceived = bytesReceived;
        MessagesSent = messagesSent;
        MessagesReceived = messagesReceived;
    }

    /// <summary>Admitted players (excludes queued joiners).</summary>
    public int Players { get; }

    /// <summary>Validated joiners waiting for a slot (D18).</summary>
    public int Queued { get; }

    /// <summary>Registered trainsets (all owners, incl. parked and server-owned).</summary>
    public int Trainsets { get; }

    /// <summary>Jobs currently on the board.</summary>
    public int Jobs { get; }

    /// <summary>World + carried items the server tracks.</summary>
    public int Items { get; }

    /// <summary>Lifetime count of stale-epoch snapshots rejected (the consist-sync invariant's health
    /// gauge — the soak oracle watches this too). A non-zero value is normal churn, not an error.</summary>
    public long StaleSnapshotsDropped { get; }

    /// <summary>The economy ledger still balances (Σ balances == minted − burned). A live FALSE is a bug.</summary>
    public bool MoneyConservationHolds { get; }

    /// <summary>Item accounting still balances. A live FALSE is a bug.</summary>
    public bool ItemConservationHolds { get; }

    /// <summary>New joins are currently paused by a host/admin (M5.2).</summary>
    public bool JoinsPaused { get; }

    /// <summary>Keys with admin rights this session (includes the owner).</summary>
    public int Admins { get; }

    /// <summary>Keys on the session ban list (M5.2).</summary>
    public int BannedKeys { get; }

    /// <summary>Spatial interest management is active (D10) — relevance-gated snapshot relay is on.</summary>
    public bool InterestEnabled { get; }

    /// <summary>Cumulative application-payload bytes the server has sent since it started (M5.2).</summary>
    public long BytesSent { get; }

    /// <summary>Cumulative application-payload bytes the server has received since it started.</summary>
    public long BytesReceived { get; }

    /// <summary>Cumulative messages the server has sent.</summary>
    public long MessagesSent { get; }

    /// <summary>Cumulative messages the server has received.</summary>
    public long MessagesReceived { get; }
}
