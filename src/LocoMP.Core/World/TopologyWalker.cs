using System;
using System.Collections.Generic;
using LocoMP.Core.Trains;

namespace LocoMP.Core.World;

/// <summary>
/// A kinematic traveller over an extracted <see cref="WorldTopology"/>: advances a head point along
/// connected edges, picks branches at nodes (seeded — runs replay), and can resolve points a given
/// distance BEHIND the head across edge boundaries, which is exactly what a multi-car consist needs
/// for its trailing bogies. Game-free by design: this is the bot's ghost-train engine today and the
/// seed of the dedicated server's kinematic coaster (03 §3) tomorrow — both move something along
/// the graph without PhysX.
/// </summary>
public sealed class TopologyWalker
{
    private readonly Dictionary<uint, TrackEdge> _edges = new();
    private readonly Dictionary<uint, List<TrackEdge>> _edgesAtNode = new();
    private readonly Dictionary<uint, JunctionDef> _junctionByEntryEdge = new();
    private readonly Dictionary<uint, (JunctionDef junction, byte branch)> _junctionByBranchEdge = new();
    private readonly Random _rng;
    private readonly double _tailCapacity;

    // Traversal history, newest last. Each segment knows where on its edge it was entered, so the
    // first segment (entered mid-edge at the start of a run, or after a reversal) needs no special
    // casing anywhere: available run length is always |exit − entry|.
    private readonly List<Segment> _trail = new();

    private readonly struct Segment
    {
        public Segment(TrackEdge edge, bool forward, double entryS)
        {
            Edge = edge;
            Forward = forward;
            EntryS = entryS;
        }

        public TrackEdge Edge { get; }

        /// <summary>True = travelling NodeA→NodeB (s increasing).</summary>
        public bool Forward { get; }

        /// <summary>Spline position where this segment was entered.</summary>
        public double EntryS { get; }

        /// <summary>Distance from entry to the exit end of the edge.</summary>
        public double Available => Forward ? Edge.LengthM - EntryS : EntryS;
    }

    /// <summary>(junctionId, branchIndex) chosen while crossing a junction node — facing moves pick
    /// a branch, trailing moves force the switch to the branch they came from (like the game).</summary>
    public event Action<uint, byte>? JunctionCrossed;

    public TopologyWalker(WorldTopology topology, int seed, double tailCapacityM = 400, uint? startEdgeId = null)
    {
        if (topology is null) throw new ArgumentNullException(nameof(topology));
        if (topology.Edges.Count == 0) throw new ArgumentException("topology has no edges", nameof(topology));
        _rng = new Random(seed);
        _tailCapacity = tailCapacityM;

        foreach (TrackEdge e in topology.Edges)
        {
            _edges[e.Id] = e;
            AddAtNode(e.NodeA, e);
            if (e.NodeB != e.NodeA) AddAtNode(e.NodeB, e);
        }
        foreach (JunctionDef j in topology.Junctions)
        {
            _junctionByEntryEdge[j.EntryEdgeId] = j;
            for (int b = 0; b < j.BranchEdgeIds.Length; b++)
                _junctionByBranchEdge[j.BranchEdgeIds[b]] = (j, (byte)b);
        }

        // Explicit start (e.g. the host's nearest-edge hint), else mid-edge on something long
        // enough to look like open track rather than a junction shard.
        TrackEdge? start = null;
        if (startEdgeId.HasValue && _edges.TryGetValue(startEdgeId.Value, out TrackEdge chosen)) start = chosen;
        if (start is null)
        {
            start = topology.Edges[_rng.Next(topology.Edges.Count)];
            foreach (TrackEdge e in topology.Edges)
                if (e.LengthM > start.LengthM) { start = e; break; }
        }
        _trail.Add(new Segment(start, forward: true, entryS: start.LengthM * 0.5));
        HeadTraversed = 0;
    }

    private void AddAtNode(uint node, TrackEdge e)
    {
        if (!_edgesAtNode.TryGetValue(node, out List<TrackEdge> list)) _edgesAtNode[node] = list = new List<TrackEdge>();
        list.Add(e);
    }

    private Segment Head => _trail[_trail.Count - 1];

    /// <summary>Distance travelled on the head segment since entering it (metres).</summary>
    public double HeadTraversed { get; private set; }

    /// <summary>The head point as a spline-space bogie state moving at <paramref name="speed"/>.</summary>
    public BogieState HeadState(float speed) => StateAt(Head, HeadTraversed, speed);

    /// <summary>
    /// Resolve the point <paramref name="distance"/> metres behind the head, or null when the trail
    /// history does not reach that far back yet (start of a run — callers wait a tick or two).
    /// </summary>
    public BogieState? Behind(double distance, float speed)
    {
        double d = distance;
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            double traversed = i == _trail.Count - 1 ? HeadTraversed : _trail[i].Available;
            if (d <= traversed) return StateAt(_trail[i], traversed - d, speed);
            d -= traversed;
        }
        return null;
    }

    private static BogieState StateAt(Segment seg, double traversedOnSegment, float speed)
    {
        double s = seg.Forward ? seg.EntryS + traversedOnSegment : seg.EntryS - traversedOnSegment;
        float v = seg.Forward ? speed : -speed;
        return new BogieState(seg.Edge.Id, (float)Clamp(s, 0, seg.Edge.LengthM), v);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>
    /// Advance the head by <paramref name="distance"/> metres, crossing nodes as needed. At a
    /// dead-end the walker reverses (the trail resets — a real consist would stop; the ghost just
    /// turns around, which is fine for a test rig).
    /// </summary>
    public void Advance(double distance)
    {
        double remaining = distance;
        while (remaining > 0)
        {
            Segment head = Head;
            double room = head.Available - HeadTraversed;
            if (remaining < room)
            {
                HeadTraversed += remaining;
                return;
            }

            remaining -= room;
            uint node = head.Forward ? head.Edge.NodeB : head.Edge.NodeA;
            TrackEdge next = PickNext(head.Edge, node);

            if (ReferenceEquals(next, head.Edge))
            {
                // Dead end: reverse in place. What was behind the head is now ahead of it, so the
                // trail history is void — reset to a fresh segment entered at the buffer end.
                _trail.Clear();
                _trail.Add(new Segment(head.Edge, !head.Forward, head.Forward ? head.Edge.LengthM : 0));
                HeadTraversed = 0;
                continue;
            }

            _trail.Add(new Segment(next, forward: next.NodeA == node, entryS: next.NodeA == node ? 0 : next.LengthM));
            HeadTraversed = 0;
            TrimTrail();
        }
    }

    private TrackEdge PickNext(TrackEdge current, uint node)
    {
        // Facing a junction from its entry edge: pick a branch and throw the switch to it.
        if (_junctionByEntryEdge.TryGetValue(current.Id, out JunctionDef junction))
        {
            var candidates = new List<(TrackEdge edge, byte branch)>();
            for (int b = 0; b < junction.BranchEdgeIds.Length; b++)
            {
                if (_edges.TryGetValue(junction.BranchEdgeIds[b], out TrackEdge e) && Touches(e, node))
                    candidates.Add((e, (byte)b));
            }
            if (candidates.Count > 0)
            {
                (TrackEdge edge, byte branch) = candidates[_rng.Next(candidates.Count)];
                JunctionCrossed?.Invoke(junction.Id, branch);
                return edge;
            }
        }

        // Trailing into a junction from one of its branches: continue onto the entry edge and force
        // the switch to the branch we came from (what a real trailing move does to a point).
        if (_junctionByBranchEdge.TryGetValue(current.Id, out (JunctionDef junction, byte branch) trailing) &&
            _edges.TryGetValue(trailing.junction.EntryEdgeId, out TrackEdge entry) && Touches(entry, node))
        {
            JunctionCrossed?.Invoke(trailing.junction.Id, trailing.branch);
            return entry;
        }

        // Plain node: any other edge touching it (seeded pick keeps runs replayable).
        List<TrackEdge> at = _edgesAtNode[node];
        var others = new List<TrackEdge>();
        foreach (TrackEdge e in at)
            if (!ReferenceEquals(e, current)) others.Add(e);
        return others.Count == 0 ? current : others[_rng.Next(others.Count)];
    }

    private static bool Touches(TrackEdge e, uint node) => e.NodeA == node || e.NodeB == node;

    private void TrimTrail()
    {
        // Keep enough history behind the head for the longest consist, drop the rest.
        double behindHead = HeadTraversed;
        for (int i = _trail.Count - 2; i > 0; i--)
        {
            behindHead += _trail[i].Available;
            if (behindHead > _tailCapacity)
            {
                _trail.RemoveRange(0, i);
                return;
            }
        }
    }
}
