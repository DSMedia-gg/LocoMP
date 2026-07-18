using System;
using System.Collections.Generic;
using DV.OriginShift;
using DV.PointSet;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// The per-world bridge between live game objects and the wire's stable numbering: RailTrack ↔
/// edge id (index in the registry's own ordered array — the SAME numbering the M2.2 extractor wrote
/// into the topology file) and Junction ↔ the save-format junction id. Also evaluates a world-space
/// point at (edgeId, s) off the track's EquiPointSet so remote consists can be rendered.
///
/// Point-set positions are Vector3d and are assumed ABSOLUTE (double precision exists to exceed
/// float range on the 256 km² map); because that is an inference, the map SELF-CALIBRATES from the
/// first real bogie sample (comparing the evaluated point against the bogie's actual transform,
/// with and without the origin shift) and locks in whichever space fits — a wrong guess costs one
/// log line, not a broken render.
/// </summary>
public sealed class TrackIndexMap
{
    private readonly Dictionary<RailTrack, uint> _edgeOf = new();
    private readonly RailTrack[] _trackOf;
    private readonly Dictionary<Junction, uint> _idOfJunction = new();
    private readonly Dictionary<uint, Junction> _junctionOf = new();
    private readonly Action<string> _log;

    private enum PointSpace { Unknown, Absolute, Local }
    private PointSpace _space = PointSpace.Unknown;

    private TrackIndexMap(RailTrack[] tracks, Junction[] junctions, Action<string> log)
    {
        _log = log;
        _trackOf = tracks;
        for (int i = 0; i < tracks.Length; i++) _edgeOf[tracks[i]] = (uint)i;
        foreach (Junction j in junctions)
        {
            if (j == null) continue;
            uint id = (uint)j.junctionData.junctionId;
            _idOfJunction[j] = id;
            if (!_junctionOf.ContainsKey(id)) _junctionOf[id] = j;
        }
    }

    /// <summary>Null while the world is still loading — callers retry next tick. The registry's
    /// own getters NRE internally mid-load (TrackRootParent not up yet), hence the broad catch.</summary>
    public static TrackIndexMap? TryBuild(Action<string> log)
    {
        try
        {
            RailTrackRegistryBase registry = RailTrackRegistryBase.Instance;
            if (registry == null) return null;
            RailTrack[] tracks = registry.OrderedRailtracks;
            Junction[] junctions = registry.OrderedJunctions;
            if (tracks == null || tracks.Length == 0) return null;

            var map = new TrackIndexMap(tracks, junctions ?? Array.Empty<Junction>(), log);
            log($"[trains] track index built: {tracks.Length} edges, {map._junctionOf.Count} junctions " +
                $"(TracksHash {registry.TracksHash})");
            return map;
        }
        catch
        {
            return null; // still loading
        }
    }

    /// <summary>Nearest edge to a render-space position, by track-origin distance — a coarse but
    /// cheap hint (one pass over 2k transforms) good enough to start a ghost train near the player.</summary>
    public bool TryNearestEdge(Vector3 localPosition, out uint edgeId, out float distance)
    {
        edgeId = 0;
        distance = float.MaxValue;
        for (int i = 0; i < _trackOf.Length; i++)
        {
            RailTrack track = _trackOf[i];
            if (track == null) continue;
            float d = (track.transform.position - localPosition).sqrMagnitude;
            if (d < distance)
            {
                distance = d;
                edgeId = (uint)i;
            }
        }
        if (distance == float.MaxValue) return false;
        distance = (float)Math.Sqrt(distance);
        return true;
    }

    public int EdgeCount => _trackOf.Length;

    public bool TryGetEdgeId(RailTrack track, out uint edgeId) =>
        _edgeOf.TryGetValue(track, out edgeId);

    public bool TryGetJunctionId(Junction junction, out uint id) =>
        _idOfJunction.TryGetValue(junction, out id);

    public bool TryGetJunction(uint id, out Junction junction) =>
        _junctionOf.TryGetValue(id, out junction!);

    /// <summary>
    /// Evaluate the track point at (edgeId, s) in THIS instance's render space, interpolated
    /// between the equidistant samples. False for unknown edges or unbaked point sets.
    /// </summary>
    public bool TryGetLocalPoint(uint edgeId, double s, out Vector3 position, out Vector3 forward)
    {
        position = default;
        forward = Vector3.forward;
        if (edgeId >= _trackOf.Length) return false;
        RailTrack track = _trackOf[edgeId];
        if (track == null) return false;

        EquiPointSet points;
        try { points = track.GetKinkedPointSet(); } catch { return false; }
        if (points?.points == null || points.points.Length == 0) return false;

        double clamped = s < 0 ? 0 : s > points.span ? points.span : s;
        int index = points.GetPointIndexForSpan(clamped);
        if (index < 0 || index >= points.points.Length) return false;

        EquiPointSet.Point pt = points.points[index];
        double x = pt.position.x, y = pt.position.y, z = pt.position.z;
        forward = pt.forward;
        if (pt.spanToNextPoint > 1e-6 && index + 1 < points.points.Length)
        {
            double frac = (clamped - pt.span) / pt.spanToNextPoint;
            if (frac > 0)
            {
                if (frac > 1) frac = 1;
                EquiPointSet.Point next = points.points[index + 1];
                x += (next.position.x - x) * frac;
                y += (next.position.y - y) * frac;
                z += (next.position.z - z) * frac;
                forward = Vector3.Slerp(pt.forward, next.forward, (float)frac);
            }
        }

        var raw = new Vector3((float)x, (float)y, (float)z);
        // Unknown space defaults to the absolute reading until a bogie sample calibrates it.
        position = _space == PointSpace.Local ? raw : raw + OriginShift.currentMove;
        return true;
    }

    /// <summary>
    /// Lock in the point-set coordinate space from one real sample: a local bogie whose actual
    /// transform position and (edge, span) are both known. Call from the capture path; no-ops once
    /// calibrated.
    /// </summary>
    public void CalibrateFrom(Bogie bogie)
    {
        if (_space != PointSpace.Unknown || bogie == null || bogie.track == null) return;
        if (!TryGetEdgeId(bogie.track, out uint edgeId)) return;

        EquiPointSet points;
        try { points = bogie.track.GetKinkedPointSet(); } catch { return; }
        if (points?.points == null || points.points.Length == 0) return;
        double span = bogie.traveller?.Span ?? -1;
        if (span < 0) return;
        int index = points.GetPointIndexForSpan(span < points.span ? span : points.span);
        if (index < 0 || index >= points.points.Length) return;

        EquiPointSet.Point pt = points.points[index];
        var raw = new Vector3((float)pt.position.x, (float)pt.position.y, (float)pt.position.z);
        Vector3 actual = bogie.transform.position;

        float asAbsolute = (raw + OriginShift.currentMove - actual).magnitude;
        float asLocal = (raw - actual).magnitude;
        if (asAbsolute > 8f && asLocal > 8f) return; // neither fits — bad sample, try again later

        _space = asAbsolute <= asLocal ? PointSpace.Absolute : PointSpace.Local;
        _log($"[trains] point-set space calibrated: {_space} (abs err {asAbsolute:F1} m, local err {asLocal:F1} m)");
    }
}
