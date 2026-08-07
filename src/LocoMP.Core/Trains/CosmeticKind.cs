namespace LocoMP.Core.Trains;

/// <summary>
/// The coarse cosmetic scalars a car's sim owner streams (M6-A1, 11 §5) — the visible OUTPUTS of
/// the loco sim, never the sim itself. Each rides the CosmeticState wire as (kind, value) with the
/// value quantised 0–255 over the scalar's natural 0–1 range. Core is deliberately blind to what
/// renders a kind: the Shim maps each one onto the matching sim PORT on both sides (read on the
/// owner, ExternalValueUpdate on the replica — DV's own port readers do the rendering), so a kind
/// a particular car doesn't have is simply never sent for it, and an unknown kind received from a
/// newer owner is skipped, not an error (forward room within the same protocol).
/// </summary>
public enum CosmeticKind : byte
{
    None = 0,

    /// <summary>Exhaust/steam plume intensity, normalised — a steam loco's chimney flow. Drives
    /// the exhaust particle readers on the replica (A1.1).</summary>
    SmokeIntensity = 1,

    /// <summary>Sand flow, normalised — the sander's computed output (not the cab control, which
    /// already mirrors via ControlState; a kinematic replica never runs the Tick that turns the
    /// control into flow). Drives the sand plume particles (A1.1).</summary>
    SandFlow = 2,

    /// <summary>Engine speed, normalised — diesel/electric prime-mover RPM. Drives the exhaust
    /// plume on diesels now (A1.1's smoke row for that family) and feeds the A1.3 engine-audio
    /// slice later without a wire change.</summary>
    EngineRpm = 3,

    /// <summary>Prime mover running, 0/255 — the gate diesel exhaust and engine effects hang off.
    /// A replica whose owner shut down reads dead, not idling.</summary>
    EngineOn = 4,
}
