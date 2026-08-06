namespace LocoMP.Core.Protocol;

/// <summary>
/// The moderation verbs a host/admin can request (M5.2). Sent as the first byte of an
/// <see cref="MessageType.AdminAction"/> packet. Wire values are STABLE — only append (a new value is a
/// protocol change). Targeting: Kick/Ban/Promote/Demote name a live peer id (chosen from the roster);
/// Unban names an offline key (chosen from the ban-list view); Pause/Resume take no target.
/// </summary>
public enum AdminActionKind : byte
{
    /// <summary>Disconnect a player now. They may reconnect immediately (this is not a ban).</summary>
    Kick = 0,

    /// <summary>Disconnect a player AND session-ban their key so a rejoin is refused until unbanned or
    /// the session ends (U3: session-scoped, in-memory).</summary>
    Ban = 1,

    /// <summary>Grant admin to a player's key (owner only).</summary>
    Promote = 2,

    /// <summary>Revoke admin from a player's key (owner only; the owner can't be demoted).</summary>
    Demote = 3,

    /// <summary>Stop admitting new players (reconnects of present players still resolve).</summary>
    PauseJoins = 4,

    /// <summary>Resume admitting new players.</summary>
    ResumeJoins = 5,

    /// <summary>Lift a session ban on a key.</summary>
    Unban = 6,

    /// <summary>Ask the server for a fresh diagnostics snapshot (reply: AdminDiagnostics). Admin-only, so
    /// it rides the same authorised channel as the mutating verbs.</summary>
    RequestDiagnostics = 7,

    /// <summary>Ask the server for the current session ban list (reply: AdminBanList).</summary>
    RequestBanList = 8,
}

/// <summary>
/// The server → client consequences of moderation (M5.2). First byte of an
/// <see cref="MessageType.AdminNotice"/> packet, followed by a string argument whose meaning depends on
/// the kind. Display-only: the authoritative effect (disconnect, role change, pause state) has already
/// happened server-side — the notice just lets the UI explain it.
/// </summary>
public enum AdminNoticeKind : byte
{
    /// <summary>To the target, immediately before disconnect: you were kicked. Arg = reason (may be empty).</summary>
    Kicked = 0,

    /// <summary>To the target, immediately before disconnect: you were banned. Arg = reason (may be empty).</summary>
    Banned = 1,

    /// <summary>Broadcast: new joins are now paused. Arg = empty.</summary>
    JoinsPaused = 2,

    /// <summary>Broadcast: new joins are open again. Arg = empty.</summary>
    JoinsResumed = 3,

    /// <summary>To a player whose admin role changed. Arg = "admin" or "player".</summary>
    RoleChanged = 4,

    /// <summary>To the acting admin: the action could not be performed. Arg = reason (e.g. "not allowed",
    /// "no such player", "cannot target the owner").</summary>
    Rejected = 5,
}
