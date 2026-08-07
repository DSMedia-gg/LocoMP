using System;
using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;

namespace LocoMP.UI;

/// <summary>
/// The seam's UI half (M5.0 step 2): the ONLY thing LocoMP screens bind to. Wraps
/// <see cref="SessionController"/>, republishes its state as simple properties, and coalesces
/// every backend event into one <see cref="Changed"/> so a retained-mode screen rebuilds once per
/// change instead of polling per frame. Command methods route back to the controller — screens
/// never touch it (or Core) directly, which is what keeps the dev IMGUI panel and the uGUI
/// screens interchangeable frontends over one backend (10-M5-UIUX-PLAN §2).
///
/// Threading: the controller raises everything from UMM's OnUpdate pump on the Unity main
/// thread, so <see cref="Changed"/> handlers may touch GameObjects directly.
/// </summary>
public sealed class SessionViewModel
{
    private readonly SessionController _c;

    public SessionViewModel(SessionController c)
    {
        _c = c;
        c.PhaseChanged += p => { if (p == SessionPhase.Idle) JoinInitiatedFromUi = false; Raise(); };
        c.PlayersChanged += Raise;
        c.ErrorRaised += e => { Error = e; Raise(); };
        c.CareerToast += t => { Toast = t; Raise(); };
        c.JoinStageChanged += s => { JoinStageChanged?.Invoke(s); Raise(); };
        c.JoinRejected += r => { JoinRejected?.Invoke(r); Raise(); };
        c.QueueChanged += (position, total) => { QueueChanged?.Invoke(position, total); Raise(); };
        c.ChatReceived += e => { ChatReceived?.Invoke(e); Raise(); };
        c.BanListChanged += Raise; // R4-A: the Host Menu's ban list repaints on refresh/unban
    }

    public SessionPhase Phase => _c.Phase;
    public bool IsHost => Phase == SessionPhase.Hosting;
    public bool InSession => Phase is SessionPhase.Hosting or SessionPhase.Joined or SessionPhase.SessionLost;

    /// <summary>Last error surfaced by the backend; sticky until the next one (screens decide
    /// their own dismissal — the backend's <see cref="SessionController.LastError"/> is the
    /// canonical "current" value).</summary>
    public string Error { get; private set; } = "";

    /// <summary>Last career/items toast (refusals, economy events, grants).</summary>
    public string Toast { get; private set; } = "";

    /// <summary>Roster snapshot (empty when not in a session). Allocates — call on change, not per frame.</summary>
    public PlayerState[] Players => _c.Client?.Players.Values.ToArray() ?? Array.Empty<PlayerState>();

    /// <summary>Career surface once admitted (board/wallet/licenses); null before that. Board
    /// screens (M5.1+) subscribe to its own events directly — it is already a push surface.</summary>
    public ClientCareer? Career => _c.Client is { Joined: true } client ? client.Career : null;

    /// <summary>Items surface once admitted (inventory/shop catalog); null before that.</summary>
    public ClientItems? Items => _c.Client is { Joined: true } client ? client.Items : null;

    /// <summary>Host-only: live player count as the server sees it (0 otherwise).</summary>
    public int ServerPlayerCount => _c.Server?.PlayerCount ?? 0;

    // ── M5.1 join progress + structured refusal (the loading + mismatch screens' feed) ────────

    /// <summary>Where the join burst is; <see cref="JoinStage.None"/> outside a join.</summary>
    public JoinStage JoinStage => _c.JoinStage;

    /// <summary>The join burst is fully delivered — the ONLY predicate the join gate clears on.</summary>
    public bool JoinSettled => _c.JoinSettled;

    /// <summary>The last structured join refusal; null until one arrives, cleared by the next
    /// Host/Join command (the controller owns that lifecycle).</summary>
    public RejectInfo? Reject => _c.LastReject;

    /// <summary>Our admission-queue place (D18): 1-based while waiting for a slot, 0 otherwise.</summary>
    public int QueuePosition => _c.QueuePosition;

    /// <summary>Stage transitions for the loading interstitial (also coalesced into Changed).</summary>
    public event Action<JoinStage>? JoinStageChanged;

    /// <summary>Queue transitions (D18): (position, total); (0, 0) = no longer queued. The
    /// interstitial shows the live place in line and treats each update as server progress.</summary>
    public event Action<int, int>? QueueChanged;

    /// <summary>A join was refused; the mismatch screen decides how to render it.</summary>
    public event Action<RejectInfo>? JoinRejected;

    /// <summary>True while the current join attempt was started from the uGUI screens. The
    /// loading cover and the reject auto-leave are scoped to this path — a dev-panel join keeps
    /// its legacy flow untouched (Cody's call, 2026-08-04). Set by <see cref="Join"/>, cleared
    /// when the session returns to Idle.</summary>
    public bool JoinInitiatedFromUi { get; private set; }

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    // Commands — screens call these, never the controller directly.
    public void Host(HostOptions options) => _c.HostSession(options);

    public void Join(JoinOptions options)
    {
        JoinInitiatedFromUi = true; // before the call — JoinSession raises PhaseChanged synchronously
        _c.JoinSession(options);
    }
    public void Leave() => _c.Leave();

    // ── M5.2 host-menu surface (the overlay's four utility groups bind ONLY these) ────────────

    /// <summary>Our own player id (0 before admission) — the roster's self row.</summary>
    public int LocalId => _c.Client?.LocalId ?? 0;

    /// <summary>A player's session role from the roster status (Player when unknown).</summary>
    public PlayerRole RoleOf(int id) => _c.Client?.RoleOf(id) ?? PlayerRole.Player;

    /// <summary>A player's measured server round-trip in ms (null = unknown).</summary>
    public int? PingOf(int id) => _c.Client?.PingOf(id);

    /// <summary>Is the door held (pause-new-joins)? Host-read, so it is always current.</summary>
    public bool JoinsPaused => _c.JoinsPausedByHost;

    /// <summary>Hosting career preset (panel display).</summary>
    public string PresetName => _c.HostPresetForDisplay.ToString();

    /// <summary>Autosave cadence in seconds (panel display).</summary>
    public int AutosaveSeconds => _c.AutosaveSeconds;

    // Moderation — the client verbs; the host's own client is the session owner, so these are
    // authorised server-side exactly like a remote admin's (one path, no host special case).
    public void Kick(int id) => _c.Client?.Kick(id);
    public void Ban(int id) => _c.Client?.Ban(id);
    public void Promote(int id) => _c.Client?.Promote(id);
    public void Demote(int id) => _c.Client?.Demote(id);

    /// <summary>R4-A: the session-ban entries (id + name — keys never reach the UI). Refresh with
    /// <see cref="RequestBanList"/>; repaints via Changed when the reply/unban push lands.</summary>
    public IReadOnlyList<SessionBan> Bans => _c.SessionBans;
    public void RequestBanList() => _c.RequestBanList();
    public void Unban(int banId) => _c.Unban(banId);
    public void SetJoinsPaused(bool paused)
    {
        if (paused) _c.Client?.PauseJoins();
        else _c.Client?.ResumeJoins();
    }

    // Session-control settings (owner-only server-side).
    public void SetPassword(string password) => _c.Client?.SetSessionPassword(password);
    public void SetMaxPlayers(int cap) => _c.Client?.SetMaxPlayers(cap);
    public void SetAutosaveInterval(int seconds) => _c.Client?.SetAutosaveInterval(seconds);

    // World/save tools.
    public void SaveNow() => _c.Client?.SaveNow();
    public void SaveAndStop() => _c.SaveAndStop();
    public IReadOnlyList<SaveBackupInfo> Backups => _c.ListBackups();
    public bool RestoreBackupAndStop(int index, out string? reason) => _c.RestoreBackupAndStop(index, out reason);

    /// <summary>Diagnostics snapshot (host only; null otherwise) — the panel renders it read-only.</summary>
    public ServerDiagnostics? Diagnostics => _c.Server?.CaptureDiagnostics();

    // ── M5.4 chat (the overlay binds these) ───────────────────────────────────────────────────

    /// <summary>A committed chat line arrived (also coalesced into Changed). The overlay appends
    /// rather than rebinding the whole backlog.</summary>
    public event Action<ChatEntry>? ChatReceived;

    /// <summary>The session's chat backlog, oldest first (empty outside a session).</summary>
    public IReadOnlyList<ChatEntry> ChatLog =>
        _c.Client?.ChatLog ?? (IReadOnlyList<ChatEntry>)Array.Empty<ChatEntry>();

    /// <summary>Send a chat line. The server echoes the committed form back to everyone including
    /// us — the overlay renders the echo, never the local draft.</summary>
    public void SendChat(string text) => _c.Client?.SendChat(text);
}
