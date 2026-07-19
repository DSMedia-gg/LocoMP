using System;

namespace LocoMP.Core.Trains;

/// <summary>
/// One car's membership record inside a <see cref="TrainsetDef"/>: server-assigned identity, the
/// game-free kind string the Shim maps to a spawnable car type, and whether it is currently
/// derailed (derailed cars stream a 6-DOF pose instead of bogies, 03 §4). Instances are treated as
/// immutable once broadcast — transactions carry fresh defs rather than mutating shared ones.
/// M3.5b adds the world identity + cargo fields real-car replication needs: remote clients spawn a
/// REAL TrainCar carrying the source world's car id/guid (job task trees and booklets name cars by
/// these), plus the cargo it held at registration. Core never interprets any of them.
/// </summary>
public sealed class CarDef
{
    public CarDef(int id, string kind, bool derailed = false,
        string gameId = "", string gameGuid = "", string cargoId = "", float cargoAmount = 0f)
    {
        Id = id;
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        Derailed = derailed;
        GameId = gameId ?? "";
        GameGuid = gameGuid ?? "";
        CargoId = cargoId ?? "";
        CargoAmount = cargoAmount;
    }

    /// <summary>Server-assigned car id, unique for the session (survives merges/splits).</summary>
    public int Id { get; }

    /// <summary>Opaque car-type token (e.g. a livery id). Core never interprets it; the Shim does.</summary>
    public string Kind { get; }

    public bool Derailed { get; }

    /// <summary>The source world's human-visible car id (e.g. "G-12345"; empty when unknown).</summary>
    public string GameId { get; }

    /// <summary>The source world's stable car guid (savegame identity; empty when unknown).</summary>
    public string GameGuid { get; }

    /// <summary>Cargo type token held at registration (empty = no cargo). Opaque to Core.</summary>
    public string CargoId { get; }

    /// <summary>Cargo amount held at registration (game units; 0 when <see cref="CargoId"/> is empty).</summary>
    public float CargoAmount { get; }

    /// <summary>Copy with a different derailed flag (used by derail/rerail commits).</summary>
    public CarDef WithDerailed(bool derailed) => derailed == Derailed
        ? this
        : new CarDef(Id, Kind, derailed, GameId, GameGuid, CargoId, CargoAmount);

    /// <summary>Copy with a different load (live cargo sync, M3.5c). Empty id = unloaded.</summary>
    public CarDef WithCargo(string cargoId, float cargoAmount) =>
        cargoId == CargoId && cargoAmount == CargoAmount
            ? this
            : new CarDef(Id, Kind, Derailed, GameId, GameGuid, cargoId, cargoAmount);
}
