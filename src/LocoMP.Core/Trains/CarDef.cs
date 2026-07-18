using System;

namespace LocoMP.Core.Trains;

/// <summary>
/// One car's membership record inside a <see cref="TrainsetDef"/>: server-assigned identity, the
/// game-free kind string the Shim maps to a spawnable car type, and whether it is currently
/// derailed (derailed cars stream a 6-DOF pose instead of bogies, 03 §4). Instances are treated as
/// immutable once broadcast — transactions carry fresh defs rather than mutating shared ones.
/// </summary>
public sealed class CarDef
{
    public CarDef(int id, string kind, bool derailed = false)
    {
        Id = id;
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        Derailed = derailed;
    }

    /// <summary>Server-assigned car id, unique for the session (survives merges/splits).</summary>
    public int Id { get; }

    /// <summary>Opaque car-type token (e.g. a livery id). Core never interprets it; the Shim does.</summary>
    public string Kind { get; }

    public bool Derailed { get; }

    /// <summary>Copy with a different derailed flag (used by derail/rerail commits).</summary>
    public CarDef WithDerailed(bool derailed) => derailed == Derailed ? this : new CarDef(Id, Kind, derailed);
}
