using System;
using System.Collections.Generic;

namespace LocoMP.Core.World;

/// <summary>
/// One rail spline segment in the extracted track graph: its stable id (the same numbering the Shim
/// uses in <see cref="Trains.BogieState.EdgeId"/>), its arc length, and the graph nodes at each end.
/// Two edges are connected when they share a node id.
/// </summary>
public sealed class TrackEdge
{
    public TrackEdge(uint id, float lengthM, uint nodeA, uint nodeB)
    {
        Id = id;
        LengthM = lengthM;
        NodeA = nodeA;
        NodeB = nodeB;
    }

    public uint Id { get; }

    /// <summary>Arc length in metres — the denominator for spline-space s and the coaster's ruler.</summary>
    public float LengthM { get; }

    /// <summary>Node at the edge's logical start (s = 0).</summary>
    public uint NodeA { get; }

    /// <summary>Node at the edge's logical end (s = LengthM).</summary>
    public uint NodeB { get; }
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
    public WorldTopology(string gameBuild, IReadOnlyList<TrackEdge> edges, IReadOnlyList<JunctionDef> junctions)
    {
        GameBuild = gameBuild ?? throw new ArgumentNullException(nameof(gameBuild));
        Edges = edges ?? throw new ArgumentNullException(nameof(edges));
        Junctions = junctions ?? throw new ArgumentNullException(nameof(junctions));
    }

    /// <summary>The exact build string this topology was extracted from (e.g. "99-build2702").</summary>
    public string GameBuild { get; }

    public IReadOnlyList<TrackEdge> Edges { get; }
    public IReadOnlyList<JunctionDef> Junctions { get; }
}
