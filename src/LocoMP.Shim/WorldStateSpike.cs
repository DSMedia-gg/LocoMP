using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// M0 game-adapter spike (07 §M0). Proves the Shim seam: it reads live game world state (train car
/// positions) and observes a game event (junction throws), logging both. This is the ONLY layer that
/// names game types — everything above it will consume shim-neutral events instead (03 §2).
/// Types/signatures here were verified against Derail Valley B99.7 by reflection-only inspection
/// (TrainCar/Bogie/CarSpawner in Assembly-CSharp; Junction in DV.RailTrack) — no game code was copied.
/// </summary>
public static class WorldStateSpike
{
    private static Action<string>? _log;

    /// <summary>Wire the game-event hooks. <paramref name="log"/> keeps the Shim free of any UMM dependency.</summary>
    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // Junction throws: postfix every Junction.Switch overload. Fires on any junction change,
        // however triggered (player lever, comms radio, remote).
        var patched = 0;
        foreach (var method in typeof(Junction).GetMethods().Where(m => m.Name == "Switch" && !m.IsStatic))
        {
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(WorldStateSpike), nameof(JunctionSwitchedPostfix)));
            patched++;
        }
        log($"[shim] junction hook installed ({patched} Switch overload(s) patched).");
    }

    private static void JunctionSwitchedPostfix(Junction __instance)
    {
        _log?.Invoke($"[world] junction switched (id={__instance.GetHashCode():x8}).");
    }

    /// <summary>Snapshot every live train car and log its world position + speed. Throttled by the caller.</summary>
    public static void LogCars(Action<string> log)
    {
        var cars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
        log($"[world] {cars.Length} live train car(s):");
        foreach (var car in cars.Take(12))
        {
            Vector3 p = car.transform.position;
            float kmh = 0f;
            try { kmh = car.GetForwardSpeed() * 3.6f; } catch { /* speed unavailable before bogies init */ }
            log($"    {car.name} @ ({p.x:F1}, {p.y:F1}, {p.z:F1})  {kmh:F1} km/h{(car.derailed ? "  [DERAILED]" : string.Empty)}");
        }
        if (cars.Length > 12) log($"    … (+{cars.Length - 12} more)");
    }
}
