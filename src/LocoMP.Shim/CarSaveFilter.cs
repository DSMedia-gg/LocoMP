using System;
using System.Collections.Generic;
using HarmonyLib;
using LocoMP.Core.Trains;

namespace LocoMP.Shim;

/// <summary>
/// Host-save leak fix (2026-08-06). While HOSTING, the game renders every remote player's (and every
/// bot's) consist as real <c>TrainCar</c>s in the host's own world, and DV's save walks
/// <c>CarSpawner.AllCars</c> and serializes ALL of them (its only filter is <c>logicCar != null</c>,
/// which a spawned replica satisfies). So an autosave mid-session bakes those foreign replicas into the
/// host's single-player save, and a reload restores them as genuine cars — the save grows every re-host
/// (24 sets / 73 cars vs a clean 22 / 67). <see cref="SaveSuppressor"/> only guards CLIENTS; the host's
/// world is genuinely his and must keep saving, so the fix is surgical: for the duration of the one
/// call that captures cars, HIDE the replicas from the list the serializer reads.
///
/// This patches <c>CarsSaveManager.GetCarsSaveData</c>, NOT <c>SaveGameManager.AboutToSave</c> — the
/// latter fires AFTER <c>UpdateInternalData</c> has already captured the car list, so it is too late.
/// The prefix removes the replica cars from <c>CarSpawner.AllCars</c> (the exact live list
/// <c>GetCarsSaveData</c> reads) via <see cref="ReplicaSaveExclusion"/>; the postfix restores them —
/// reversibly, so no host car is ever lost or duplicated. The replica GameObjects are never despawned,
/// so there is no visible blink and no physics disruption; they simply do not appear in the save.
/// </summary>
public static class CarSaveFilter
{
    /// <summary>Set by the session controller for the lifetime of a session (host and client alike —
    /// on a client <see cref="SaveSuppressor"/> blocks the save before this ever runs, but keeping the
    /// arm symmetric means the guard is correct if that policy ever changes). Points at
    /// <c>RealCarSync.IsRemoteCar</c>: true for a car this session spawned as another player's replica.
    /// Null outside a session, so single-player saves are never filtered.</summary>
    public static Func<TrainCar, bool>? IsReplica;

    private static Action<string>? _log;

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;
        harmony.Patch(
            AccessTools.Method(typeof(CarsSaveManager), nameof(CarsSaveManager.GetCarsSaveData)),
            prefix: new HarmonyMethod(typeof(CarSaveFilter), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(CarSaveFilter), nameof(Postfix)));
        log("[session] car-save filter installed (excludes remote replicas from the host's save)");
    }

    private static void Prefix(out List<TrainCar>? __state)
    {
        __state = null;
        Func<TrainCar, bool>? isReplica = IsReplica;
        if (isReplica == null) return;
        CarSpawner? spawner = CarSpawner.Instance;
        if (spawner == null) return;

        List<TrainCar> removed = ReplicaSaveExclusion.Extract(spawner.AllCars, isReplica);
        if (removed.Count == 0) return;
        __state = removed;
        _log?.Invoke($"[trains] excluding {removed.Count} remote replica car(s) from this save");
    }

    private static void Postfix(List<TrainCar>? __state)
    {
        if (__state == null) return;
        CarSpawner? spawner = CarSpawner.Instance;
        if (spawner == null) return; // world torn down mid-save — the live list is gone; nothing to restore into
        ReplicaSaveExclusion.Restore(spawner.AllCars, __state);
    }
}
