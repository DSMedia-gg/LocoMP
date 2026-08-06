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
    private readonly HashSet<string> _banned = new(StringComparer.Ordinal);
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

    public bool IsBanned(string key) => !string.IsNullOrEmpty(key) && _banned.Contains(key);

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

    /// <summary>Add a key to the session ban list. The owner can never be banned. Returns true if it was
    /// newly banned. (Disconnecting the live peer is <see cref="NetServer"/>'s job — this only records the
    /// ban so a rejoin is refused; a ban also implies eviction, done by the caller.)</summary>
    public bool Ban(string key)
    {
        if (string.IsNullOrEmpty(key) || IsOwner(key)) return false;
        return _banned.Add(key);
    }

    /// <summary>Lift a session ban. Returns true if the key was banned.</summary>
    public bool Unban(string key) => !string.IsNullOrEmpty(key) && _banned.Remove(key);

    /// <summary>The current admin keys — host-UI / diagnostics snapshot.</summary>
    public IReadOnlyCollection<string> Admins => _admins;

    /// <summary>The current session-ban keys — host-UI / diagnostics snapshot.</summary>
    public IReadOnlyCollection<string> BannedKeys => _banned;
}
