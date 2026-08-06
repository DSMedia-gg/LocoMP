using System;
using System.Collections.Generic;

namespace LocoMP.Core.Trains;

/// <summary>
/// The reversible half of the host-save leak fix (2026-08-06). When the local player HOSTS, the game
/// materialises every REMOTE player's (and every bot's) consist as real <c>TrainCar</c>s in the host's
/// own world — and DV's save walks <c>CarSpawner.AllCars</c> and serializes ALL of them, so an autosave
/// during a session bakes those foreign replicas into the host's single-player save. A reload then
/// restores them as genuine cars, so the save grows every re-host (observed: 24 sets / 73 cars vs a
/// clean 22 / 67). <see cref="SaveSuppressor"/> can't help — that blanket-blocks CLIENT saves, but the
/// host's world is genuinely his and must keep saving; only the foreign replicas must be excluded.
///
/// The Shim's <c>CarSaveFilter</c> patches <c>CarsSaveManager.GetCarsSaveData</c> and, for the duration
/// of that one call, hides the replica cars from the very list the serializer reads. The GameObjects are
/// NEVER despawned (no visible blink, no physics disruption) — they are only removed from the list and
/// then put back. This class is that list surgery, kept in game-free Core so the property that actually
/// matters can be pinned headlessly: the mutation must reverse EXACTLY — every removed car comes back,
/// none is lost (that would delete a host car from the world) and none is duplicated (that would corrupt
/// the world with a doubled car). It must NOT hook <c>SaveGameManager.AboutToSave</c>: that event fires
/// AFTER <c>UpdateInternalData</c> has already captured the car list, so it is too late to influence the
/// save — the exclusion has to happen inside <c>GetCarsSaveData</c> itself.
/// </summary>
public static class ReplicaSaveExclusion
{
    /// <summary>Remove every element of <paramref name="all"/> matching <paramref name="isReplica"/>,
    /// in list order, and return them so the caller can restore them after the save is captured. The
    /// list is mutated in place (it is the game's own live <c>AllCars</c>); a null/empty predicate
    /// result leaves the list untouched and returns an empty list.</summary>
    public static List<T> Extract<T>(IList<T> all, Func<T, bool> isReplica) where T : class
    {
        if (all is null) throw new ArgumentNullException(nameof(all));
        if (isReplica is null) throw new ArgumentNullException(nameof(isReplica));

        var removed = new List<T>();
        // Walk once, front to back, collecting matches; then remove by reference. Collect-then-remove
        // (rather than removing while scanning) keeps this correct regardless of the list type's
        // in-place removal semantics.
        foreach (T item in all)
            if (item != null && isReplica(item)) removed.Add(item);
        foreach (T item in removed) all.Remove(item);
        return removed;
    }

    /// <summary>Put back exactly what <see cref="Extract"/> took out. Idempotent per element: a car
    /// already present (nothing else should re-add it, but be defensive — a double-add would corrupt
    /// the world) is not added again, so the net effect is precisely the original membership.</summary>
    public static void Restore<T>(IList<T> all, IReadOnlyList<T> removed) where T : class
    {
        if (all is null) throw new ArgumentNullException(nameof(all));
        if (removed is null) return;
        foreach (T item in removed)
            if (item != null && !all.Contains(item)) all.Add(item);
    }
}
