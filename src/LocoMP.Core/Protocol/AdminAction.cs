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

    /// <summary>Ask the server to save the world now (M5.2 "Save now"). The server authorises it and
    /// raises its SaveRequested hook; the host / dedicated-server process does the actual write (the file
    /// IO is deliberately outside game-free Core).</summary>
    SaveNow = 9,

    // ── M5.2 session-control settings (v18 arc). OWNER-only — admins moderate players, the owner
    // reconfigures the session. The new value rides the targetKey string (parsed server-side). ──

    /// <summary>Change the session password mid-session (owner only). Value = the new password;
    /// empty = open. Present players are untouched — only future joins check it.</summary>
    SetPassword = 10,

    /// <summary>Change the player cap live (owner only). Value = the new cap as decimal text.
    /// Raising it pumps the admission queue immediately; lowering it never evicts anyone — present
    /// players stay, only new admissions are held.</summary>
    SetMaxPlayers = 11,

    /// <summary>Change the autosave interval live (owner only). Value = seconds as decimal text.
    /// The server authorises + parses and raises AutosaveIntervalRequested; the host / dedicated
    /// process retunes its Autosaver (the file IO layer stays outside game-free Core).</summary>
    SetAutosaveInterval = 12,
}

/// <summary>
/// One session-ban entry as the admin channel carries it (v20, R4-A): an opaque per-session ban id
/// plus the banned player's display NAME. The banned player's KEY deliberately never leaves the
/// server — a key is a credential (F7 credentialed takeover, D3 wallet identity), and the v19 ban
/// list leaked it to every admin. Unban targets the id.
/// </summary>
public readonly struct SessionBan
{
    public SessionBan(int id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>Session-scoped opaque id, minted at ban time — the unban target.</summary>
    public int Id { get; }

    /// <summary>The banned player's display name as it was when they were banned.</summary>
    public string Name { get; }

    public override string ToString() => $"{Id}: {Name}";
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

    /// <summary>To every admitted AND queued peer, immediately before a clean session end (M5.2
    /// Save &amp; Stop, dedicated-server shutdown): the session is over ON PURPOSE — the UI should say
    /// "the host ended the session" instead of inferring a dead link. Arg = reason (may be empty).
    /// The link still drops moments later; this only re-words what the drop means.</summary>
    SessionEnded = 6,

    /// <summary>Broadcast: a session setting changed (v18 arc — max players, autosave interval, or
    /// "the session password changed/was removed"; never the password itself). Arg = display text.</summary>
    SettingChanged = 7,
}
