using System;
using System.Collections.Generic;

namespace LocoMP.Core.Session;

/// <summary>
/// Session-scoped moderation state for M5.2's host utilities — admin roles, session ban list, and the
/// pause-new-joins gate. Everything here is IN-MEMORY and dies with the session (U3, Cody 2026-08-04:
/// pre-Steam there is deliberately no persistent ban schema — that arrives keyed on Steam ID at M5.5,
/// with nothing to migrate). State is keyed by the stable player KEY (the M3 reconnect identity), the
/// same client-held secret the credentialed-takeover path trusts (F7) — so a ban or admin grant follows
/// the player across a reconnect within the session.
///
/// <para><see cref="NetServer"/> owns one of these and consults it in the join gate and the admin-action
/// handler; the game-touching host UI (the Shim) only ever reads snapshots and sends action requests.
/// This class is pure policy so the rules — who may be evicted, who is immune, what a ban means — are
/// pinned headlessly.</para>
///
/// <para><b>Owner model.</b> The first admitted key becomes the session <see cref="Owner"/> (the host, in
/// host-mode): auto-admin, and immune to kick / ban / demote by anyone else. Admins may kick, ban, pause
/// joins, and (owner only) promote/demote. A dedicated server with no natural "first host" gives its
/// FIRST joiner ownership — acceptable for the friends-only pre-Steam scope; an operator-console admin
/// path and Steam-ID ownership are the post-Steam refinement.</para>
/// </summary>
public sealed class ServerModeration
{
    private readonly HashSet<string> _admins = new(StringComparer.Ordinal);
    // key → its ban entry. The entry (opaque id + display name) is what surfaces (R4-A) — the key
    // itself never leaves the server: it is a credential (F7 takeover, D3 wallet identity).
    private readonly Dictionary<string, Protocol.SessionBan> _banned = new(StringComparer.Ordinal);
    private int _nextBanId = 1;
    private string? _owner;

    /// <summary>The session owner's key — the first player admitted, auto-admin and immune to
    /// kick/ban/demote. Null until the first admit.</summary>
    public string? Owner => _owner;

    /// <summary>While true, brand-new joins are refused (<see cref="Protocol.RejectKind.JoinsPaused"/>) —
    /// a host "hold the door" toggle. It does NOT block a reconnect of a player already represented in the
    /// session: those run the takeover / same-key-queued paths, which resolve before the join gate reaches
    /// this check.</summary>
    public bool JoinsPaused { get; set; }

    /// <summary>Record the first admitted key as owner + admin; idempotent on every later admit.</summary>
    public void EnsureOwner(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (_owner is null)
        {
            _owner = key;
            _admins.Add(key);
        }
    }

    public bool IsOwner(string key) =>
        _owner != null && !string.IsNullOrEmpty(key) && string.Equals(_owner, key, StringComparison.Ordinal);

    public bool IsAdmin(string key) => !string.IsNullOrEmpty(key) && _admins.Contains(key);

    public bool IsBanned(string key) => !string.IsNullOrEmpty(key) && _banned.ContainsKey(key);

    /// <summary>Grant admin to a key (promote-to-admin). Returns true if it changed anything.</summary>
    public bool Promote(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return _admins.Add(key);
    }

    /// <summary>Revoke admin. The owner can never be demoted. Returns true if it changed anything.</summary>
    public bool Demote(string key)
    {
        if (IsOwner(key)) return false;
        return _admins.Remove(key);
    }

    /// <summary>Add a key to the session ban list, minting a surfaced entry (opaque id + the display
    /// name as of the ban, R4-A). The owner can never be banned. Returns true if it was newly banned.
    /// (Disconnecting the live peer is <see cref="NetServer"/>'s job — this only records the ban so a
    /// rejoin is refused; a ban also implies eviction, done by the caller.)</summary>
    public bool Ban(string key, string name = "")
    {
        if (string.IsNullOrEmpty(key) || IsOwner(key) || _banned.ContainsKey(key)) return false;
        _banned[key] = new Protocol.SessionBan(_nextBanId++, string.IsNullOrEmpty(name) ? "(unknown)" : name);
        return true;
    }

    /// <summary>Lift a session ban by key. Returns true if the key was banned.</summary>
    public bool Unban(string key) => !string.IsNullOrEmpty(key) && _banned.Remove(key);

    /// <summary>Lift a session ban by its surfaced entry id (the Host Menu / console / remote-admin
    /// unban target — those views never hold keys). Returns true if an entry matched.</summary>
    public bool UnbanById(int id)
    {
        foreach (KeyValuePair<string, Protocol.SessionBan> kv in _banned)
        {
            if (kv.Value.Id != id) continue;
            _banned.Remove(kv.Key);
            return true;
        }
        return false;
    }

    /// <summary>The current admin keys — host-UI / diagnostics snapshot.</summary>
    public IReadOnlyCollection<string> Admins => _admins;

    /// <summary>The current session-ban keys — the JOIN GATE's view (server-internal only).</summary>
    public IReadOnlyCollection<string> BannedKeys => _banned.Keys;

    /// <summary>The surfaced ban entries (id + name, never keys) — what every UI lists (R4-A).</summary>
    public IReadOnlyCollection<Protocol.SessionBan> Bans => _banned.Values;
}
