namespace LocoMP;

/// <summary>
/// The observable session lifecycle (M5.0). This is the old SessionController.Mode promoted to a
/// public surface, split where a UI needs to render differently: a joined-but-not-yet-admitted
/// link shows as <see cref="Connecting"/>, and a dead host shows as <see cref="SessionLost"/>
/// (the fail-safe state the panel's "Leave to restore" prompt lives in). The controller computes
/// it from its internal state in one place and raises PhaseChanged only on real transitions, so
/// retained-mode screens can rebuild on push instead of polling every frame.
/// </summary>
public enum SessionPhase
{
    Idle,
    Connecting,
    Hosting,
    Joined,
    SessionLost,
}
