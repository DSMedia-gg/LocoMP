using System;
using System.Collections.Generic;
using LocoMP.Core.Trains;
using UnityEngine;

// System is imported for Action; pin the unqualified Object back to Unity's (Destroy lives there).
using Object = UnityEngine.Object;

namespace LocoMP.Shim;

/// <summary>
/// Placeholder visuals for consists simulated by OTHER players (the bot's ghost train today): one
/// collider-free box per car, positioned from spline-space snapshots via <see cref="TrackIndexMap"/>
/// and smoothed with the same 12/s lerp + snap the avatars use. M2-level rendering — real spawned
/// TrainCars arrive with world/state sync (M3+); the point here is proving remote train motion
/// through the full pipeline.
/// </summary>
public sealed class GhostConsists
{
    private const float LerpRate = 12f;
    private const float SnapDistance = 80f;
    private const float BodyHeight = 2.1f; // rail-head to body-centre; close enough for a ghost

    private sealed class GhostCar
    {
        public GhostCar(int trainsetId, int index, bool isLoco)
        {
            Root = new GameObject($"LocoMP Ghost (set {trainsetId} car {index})");
            Root.SetActive(false); // invisible until the first snapshot places it — never a box at origin
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(Root.transform, worldPositionStays: false);
            body.transform.localPosition = new Vector3(0f, BodyHeight, 0f);
            body.transform.localScale = new Vector3(2.8f, 3.2f, 14.5f);
            Object.Destroy(body.GetComponent<Collider>()); // visual only
            body.GetComponent<Renderer>().material.color = isLoco
                ? new Color(0.95f, 0.65f, 0.15f)   // amber loco
                : new Color(0.45f, 0.55f, 0.70f);  // slate wagons
        }

        public GameObject Root { get; }
        public Vector3 TargetPos;
        public Quaternion TargetRot = Quaternion.identity;
        public bool HasTarget;
    }

    private readonly Dictionary<int, GhostCar[]> _sets = new();
    private readonly HashSet<int> _unresolvedWarned = new();
    private readonly Action<string> _log;

    public GhostConsists(Action<string> log) => _log = log;

    /// <summary>Create (or resize after a transaction) the ghost visuals for a remote trainset.</summary>
    public void EnsureSet(TrainsetDef def)
    {
        if (_sets.TryGetValue(def.Id, out GhostCar[] existing) && existing.Length == def.Cars.Count) return;
        Remove(def.Id);
        var cars = new GhostCar[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
            cars[i] = new GhostCar(def.Id, i, def.Cars[i].Kind.Contains("loco"));
        _sets[def.Id] = cars;
    }

    public void Remove(int trainsetId)
    {
        if (!_sets.TryGetValue(trainsetId, out GhostCar[] cars)) return;
        foreach (GhostCar car in cars) Object.Destroy(car.Root);
        _sets.Remove(trainsetId);
    }

    public void Clear()
    {
        foreach (GhostCar[] cars in _sets.Values)
            foreach (GhostCar car in cars)
                Object.Destroy(car.Root);
        _sets.Clear();
    }

    /// <summary>Feed one admitted snapshot: every railed car is placed from its two bogies on the
    /// spline; derailed cars use their free 6-DOF pose (absolute coords, re-localized).</summary>
    public void Apply(TrainsetSnapshot snap, TrackIndexMap map)
    {
        if (!_sets.TryGetValue(snap.TrainsetId, out GhostCar[] cars) || cars.Length != snap.Cars.Length) return;

        int unresolved = 0;
        for (int i = 0; i < cars.Length; i++)
        {
            CarSnapshot state = snap.Cars[i];
            GhostCar car = cars[i];
            if (state.Derailed)
            {
                car.TargetPos = PresenceShim.ToLocalPosition(state.Pose);
                car.TargetRot = new Quaternion(state.Pose.Rx, state.Pose.Ry, state.Pose.Rz, state.Pose.Rw);
            }
            else
            {
                if (!map.TryGetLocalPoint(state.Front.EdgeId, state.Front.S, out Vector3 front, out Vector3 fwd) ||
                    !map.TryGetLocalPoint(state.Rear.EdgeId, state.Rear.S, out Vector3 rear, out _))
                {
                    unresolved++;
                    continue;
                }
                Vector3 axis = front - rear;
                car.TargetPos = (front + rear) * 0.5f;
                car.TargetRot = Quaternion.LookRotation(axis.sqrMagnitude > 0.01f ? axis : fwd);
            }

            if (!car.HasTarget)
            {
                car.HasTarget = true;
                car.Root.SetActive(true);
                car.Root.transform.SetPositionAndRotation(car.TargetPos, car.TargetRot);
                if (i == 0) _log($"[trains] ghost consist {snap.TrainsetId} is on the rails (edge {state.Front.EdgeId})");
            }
        }

        // One loud line instead of an invisible train: every car unresolved means the map and the
        // snapshot disagree about the world (the exact failure mode of the first exit run).
        if (unresolved == cars.Length && _unresolvedWarned.Add(snap.TrainsetId))
            _log($"[trains] WARNING: ghost consist {snap.TrainsetId} cannot be placed — " +
                 "track points unresolvable (stale world map?)");
    }

    /// <summary>Advance smoothing. Call once per frame.</summary>
    public void Tick(float dt)
    {
        float t = Mathf.Clamp01(LerpRate * dt);
        foreach (GhostCar[] cars in _sets.Values)
        {
            foreach (GhostCar car in cars)
            {
                if (!car.HasTarget) continue;
                Transform tr = car.Root.transform;
                if ((car.TargetPos - tr.position).sqrMagnitude > SnapDistance * SnapDistance)
                {
                    tr.SetPositionAndRotation(car.TargetPos, car.TargetRot);
                    continue;
                }
                tr.SetPositionAndRotation(
                    Vector3.Lerp(tr.position, car.TargetPos, t),
                    Quaternion.Slerp(tr.rotation, car.TargetRot, t));
            }
        }
    }
}
