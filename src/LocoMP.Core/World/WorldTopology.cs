using System;
using System.Collections.Generic;

namespace LocoMP.Core.World;

/// <summary>
/// One rail spline segment in the extracted track graph: its stable id (the same numbering the Shim
/// uses in <see cref="Trains.BogieState.EdgeId"/>), its arc length, and the graph nodes at each end.
/// Two edges are connected when they share a node id.
///
/// <para><b>Geometry (codec v2, D10 Burst 2).</b> Optionally carries the edge's two endpoints in
/// ABSOLUTE world coordinates — the same frame player poses use on the wire (DV's floating origin
/// means a raw <c>Transform.position</c> is shift-relative and would drift; the extractor subtracts
/// <c>OriginShift.currentMove</c>). This is what lets the server place a spline-space bogie in the
/// world and distance-test a railed train for interest management. A v1 file has no geometry
/// (<see cref="HasGeometry"/> false) and the train filter fails open.</para>
/// </summary>
public sealed class TrackEdge
{
    /// <summary>A geometry-free edge (the v1 shape): a pure graph segment.</summary>
    public TrackEdge(uint id, float lengthM, uint nodeA, uint nodeB)
    {
        Id = id;
        LengthM = lengthM;
        NodeA = nodeA;
        NodeB = nodeB;
    }

    /// <summary>An edge with absolute world endpoints (v2): <paramref name="a"/> at s = 0,
    /// <paramref name="b"/> at s = <paramref name="lengthM"/>.</summary>
    public TrackEdge(uint id, float lengthM, uint nodeA, uint nodeB, WorldPoint a, WorldPoint b)
        : this(id, lengthM, nodeA, nodeB)
    {
        A = a;
        B = b;
        HasGeometry = true;
    }

    public uint Id { get; }

    /// <summary>Arc length in metres — the denominator for spline-space s and the coaster's ruler.</summary>
    public float LengthM { get; }

    /// <summary>Node at the edge's logical start (s = 0).</summary>
    public uint NodeA { get; }

    /// <summary>Node at the edge's logical end (s = LengthM).</summary>
    public uint NodeB { get; }

    /// <summary>True when <see cref="A"/>/<see cref="B"/> carry real world positions (a v2 file).</summary>
    public bool HasGeometry { get; }

    /// <summary>Absolute world position of the edge's logical start. Meaningful only when
    /// <see cref="HasGeometry"/>.</summary>
    public WorldPoint A { get; }

    /// <summary>Absolute world position of the edge's logical end. Meaningful only when
    /// <see cref="HasGeometry"/>.</summary>
    public WorldPoint B { get; }
}

/// <summary>A position in the session's absolute world frame (metres). Deliberately not a Unity
/// Vector3 — Core is game-free (hard rule 3).</summary>
public readonly struct WorldPoint
{
    public WorldPoint(float x, float y, float z) { X = x; Y = y; Z = z; }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
}

/// <summary>A switch in the track graph: the entry edge and the branch edges it can select between.</summary>
public sealed class JunctionDef
{
    public JunctionDef(uint id, uint entryEdgeId, uint[] branchEdgeIds)
    {
        if (branchEdgeIds is null) throw new ArgumentNullException(nameof(branchEdgeIds));
        if (branchEdgeIds.Length < 2) throw new ArgumentException("a junction selects between at least two branches", nameof(branchEdgeIds));
        Id = id;
        EntryEdgeId = entryEdgeId;
        BranchEdgeIds = branchEdgeIds;
    }

    public uint Id { get; }
    public uint EntryEdgeId { get; }
    public uint[] BranchEdgeIds { get; }
}

/// <summary>
/// The extractor's product (03 §6): everything the dedicated server needs to know about the rail
/// network without a game install — the edge graph (for the kinematic coaster and spline-space
/// validation) and the junction map. Stamped with the game build it was extracted from, because
/// edge ids are only stable within one build; the server refuses topology from a different build
/// than its session's.
/// </summary>
public sealed class WorldTopology
{
    private readonly Dictionary<uint, TrackEdge> _byId;

    public WorldTopology(string gameBuild, IReadOnlyList<TrackEdge> edges, IReadOnlyList<JunctionDef> junctions)
    {
        GameBuild = gameBuild ?? throw new ArgumentNullException(nameof(gameBuild));
        Edges = edges ?? throw new ArgumentNullException(nameof(edges));
        Junctions = junctions ?? throw new ArgumentNullException(nameof(junctions));

        _byId = new Dictionary<uint, TrackEdge>(edges.Count);
        int geometryEdges = 0;
        foreach (TrackEdge e in edges)
        {
            _byId[e.Id] = e; // a duplicate id would already have broken edge lookup everywhere else
            if (e.HasGeometry) geometryEdges++;
        }
        GeometryEdgeCount = geometryEdges;
    }

    /// <summary>The exact build string this topology was extracted from (e.g. "99-build2702").</summary>
    public string GameBuild { get; }

    public IReadOnlyList<TrackEdge> Edges { get; }
    public IReadOnlyList<JunctionDef> Junctions { get; }

    /// <summary>How many edges carry world geometry. B99.7 deterministically has a set of special
    /// tracks (139 of 2073 — turntables and the like) whose node transforms never resolve, so
    /// "every edge or nothing" meant NEVER filtering (the 2026-08-04 gauntlet's F4). Partial
    /// geometry is safe because placement already fails open PER ENTITY: a train on a bare edge
    /// can't be placed, lands in nobody's <c>_placed</c> set, and is relevant to everyone.</summary>
    public int GeometryEdgeCount { get; }

    /// <summary>True when this topology can place bogies at all — i.e. ANY edge carries geometry
    /// (was all-or-nothing before F4; see <see cref="GeometryEdgeCount"/> for the honest count).
    /// The server suppresses train interest outright when this is false.</summary>
    public bool HasGeometry => GeometryEdgeCount > 0;

    /// <summary>The edge with this id, or null. O(1).</summary>
    public TrackEdge? Edge(uint edgeId) => _byId.TryGetValue(edgeId, out TrackEdge? e) ? e : null;

    /// <summary>
    /// Place a spline-space position (edge + metres along it) in the absolute world — the bridge
    /// between <see cref="Trains.BogieState"/> and the world-space distance test.
    ///
    /// <para><b>Coarse by design:</b> a curved edge is approximated by the straight chord between its
    /// endpoints. DV's edges are short relative to an interest radius of hundreds of metres, so the
    /// chord error is far inside the hysteresis band — and the alternative (baking whole bezier
    /// control sets) would multiply the file size for a decision that only asks "roughly where".</para>
    /// </summary>
    /// <returns>False when the edge is unknown or geometry-free — the caller must fail open.</returns>
    public bool TryEdgeWorldPoint(uint edgeId, float s, out WorldPoint point)
    {
        point = default;
        TrackEdge? e = Edge(edgeId);
        if (e is null || !e.HasGeometry) return false;

        // Clamp rather than reject: a bogie a few centimetres past an edge end (float drift, or a
        // snapshot caught mid-transition) is a real train at a real place, not bad data.
        float t = e.LengthM > 0f ? s / e.LengthM : 0f;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;

        point = new WorldPoint(
            e.A.X + (e.B.X - e.A.X) * t,
            e.A.Y + (e.B.Y - e.A.Y) * t,
            e.A.Z + (e.B.Z - e.A.Z) * t);
        return true;
    }
}
