using System;
using System.Linq;
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
        c.PhaseChanged += _ => Raise();
        c.PlayersChanged += Raise;
        c.ErrorRaised += e => { Error = e; Raise(); };
        c.CareerToast += t => { Toast = t; Raise(); };
        c.JoinStageChanged += s => { JoinStageChanged?.Invoke(s); Raise(); };
        c.JoinRejected += r => { JoinRejected?.Invoke(r); Raise(); };
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

    /// <summary>Stage transitions for the loading interstitial (also coalesced into Changed).</summary>
    public event Action<JoinStage>? JoinStageChanged;

    /// <summary>A join was refused; the mismatch screen decides how to render it.</summary>
    public event Action<RejectInfo>? JoinRejected;

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    // Commands — screens call these, never the controller directly.
    public void Host(HostOptions options) => _c.HostSession(options);
    public void Join(JoinOptions options) => _c.JoinSession(options);
    public void Leave() => _c.Leave();
}
