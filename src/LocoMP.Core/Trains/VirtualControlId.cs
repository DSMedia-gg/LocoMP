namespace LocoMP.Core.Trains;

/// <summary>
/// LocoMP-defined control ids riding the ControlState/ControlInput machinery (v18 parity floor).
/// DV's own cab controls are the 42-value ControlType enum (1..~42, captured uniformly by
/// CabControlSync); ids from <see cref="ExteriorFloor"/> up are LocoMP's EXTERIOR-HARDWARE ids —
/// per-car state that DV keeps outside the cab-control rig but that syncs perfectly as
/// (car, control, value):
///
/// <list type="bullet">
/// <item><description>They are GRANT-EXEMPT on the input path: control grants govern cab occupancy,
/// but exterior hardware (a handbrake wheel on the car's body) is operated from the ground, and the
/// game's own physical-reach requirement on the acting client is the proximity gate — the same
/// trust model as the F8 couple/uncouple requests and the comms radio.</description></item>
/// <item><description>On a PARKED set (owner 0) an exterior input is server-committed directly
/// (stored + broadcast) — securing an ownerless cut's handbrakes is ordinary yard work and D21's
/// parked-authority spirit; a cab input on a parked set stays dropped (no sim to apply it).</description></item>
/// </list>
///
/// Wire-stable like MessageType: only append, never renumber.
/// </summary>
public static class VirtualControlId
{
    /// <summary>Ids at or above this are LocoMP exterior-hardware ids, never DV ControlTypes.</summary>
    public const byte ExteriorFloor = 200;

    /// <summary>The per-car handbrake position, 0..1 (02 §1 P0 — BrakeSystem.handbrakePosition).</summary>
    public const byte Handbrake = 200;
}
