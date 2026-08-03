using System;
using System.IO;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.World;

/// <summary>
/// Versioned binary format for extracted world topology — the contract between the Shim-side
/// extractor (writes it from a running game, M2) and the headless dedicated server (loads it with
/// no game install, 03 §6). Hand-rolled on the proven PacketWriter/Reader primitives: zero new
/// dependencies for the prototype, and the read side inherits their untrusted-input posture. The
/// caller owns file IO; Core only sees bytes.
/// </summary>
public static class TopologyCodec
{
    /// <summary>Literal "LMPW" bytes — refuses arbitrary files early with a clear error.</summary>
    private static readonly byte[] Magic = { (byte)'L', (byte)'M', (byte)'P', (byte)'W' };

    /// <summary>
    /// Current layout. <b>v2</b> (D10 Burst 2) appends a per-file geometry flag and, when set, two
    /// absolute world endpoints per edge — what the server needs to distance-test a railed train.
    ///
    /// <para><b>v1 files still load.</b> Unlike a wire protocol (where both ends upgrade together), a
    /// <c>.lmpw</c> is an expensive artifact: re-extracting one requires the game, a loaded world, and
    /// a human. Refusing v1 would brick every existing extraction to gain nothing — a v1 topology is
    /// still perfectly valid for everything it always did (the walker, spline validation). It simply
    /// reads back with <see cref="WorldTopology.HasGeometry"/> false, and the server fails open on
    /// train interest. So <see cref="Read"/> accepts v1..v3 and <see cref="Write"/> always emits v3.</para>
    ///
    /// <para><b>v3 carries geometry PER EDGE.</b> v2 had one file-level flag because geometry was
    /// all-or-nothing — and the 2026-08-04 gauntlet (F4) proved B99.7 deterministically withholds
    /// node transforms on 139 special-track edges, so an all-or-nothing extract can never complete
    /// and the filter never runs. v3 spends one flag byte per edge (a few KB) so the 93% of the
    /// network that IS placeable filters, and trains on bare edges fail open per entity. A v2 file
    /// with its flag set still reads as all-edges-geometry (its own invariant guarantees that).</para>
    /// </summary>
    public const byte FormatVersion = 3;

    /// <summary>Oldest layout <see cref="Read"/> still accepts.</summary>
    public const byte MinReadableVersion = 1;

    public const int MaxEdges = 500_000;
    public const int MaxJunctions = 50_000;
    public const int MaxBranches = 8;

    public static byte[] Write(WorldTopology topology)
    {
        if (topology is null) throw new ArgumentNullException(nameof(topology));

        bool geometry = topology.HasGeometry;
        var w = new PacketWriter(topology.Edges.Count * (geometry ? 41 : 16) + 64);
        foreach (byte m in Magic) w.WriteByte(m);
        w.WriteByte(FormatVersion);
        w.WriteString(topology.GameBuild);

        // File-level flag first (any geometry at all?) so a bare topology skips the per-edge
        // bytes entirely; when set, each edge carries its own flag (F4: partial geometry is real).
        w.WriteByte(geometry ? (byte)1 : (byte)0);

        w.WriteVarUInt((uint)topology.Edges.Count);
        foreach (TrackEdge e in topology.Edges)
        {
            w.WriteVarUInt(e.Id);
            w.WriteSingle(e.LengthM);
            w.WriteVarUInt(e.NodeA);
            w.WriteVarUInt(e.NodeB);
            if (!geometry) continue;
            w.WriteByte(e.HasGeometry ? (byte)1 : (byte)0);
            if (!e.HasGeometry) continue;
            w.WriteSingle(e.A.X); w.WriteSingle(e.A.Y); w.WriteSingle(e.A.Z);
            w.WriteSingle(e.B.X); w.WriteSingle(e.B.Y); w.WriteSingle(e.B.Z);
        }

        w.WriteVarUInt((uint)topology.Junctions.Count);
        foreach (JunctionDef j in topology.Junctions)
        {
            w.WriteVarUInt(j.Id);
            w.WriteVarUInt(j.EntryEdgeId);
            w.WriteVarUInt((uint)j.BranchEdgeIds.Length);
            foreach (uint b in j.BranchEdgeIds) w.WriteVarUInt(b);
        }

        return w.ToArray();
    }

    public static WorldTopology Read(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        var r = new PacketReader(data);
        foreach (byte m in Magic)
            if (r.ReadByte() != m) throw new InvalidDataException("not a LocoMP topology file");
        byte version = r.ReadByte();
        if (version < MinReadableVersion || version > FormatVersion)
            throw new InvalidDataException($"topology format v{version}, this build reads v{MinReadableVersion}–v{FormatVersion}");
        string gameBuild = r.ReadString();

        // v1 predates geometry entirely — no flag byte in the stream to read. v2's file-level
        // flag means ALL edges carry geometry (its all-or-nothing invariant); v3 flags per edge.
        bool geometry = version >= 2 && r.ReadByte() != 0;

        int edgeCount = (int)r.ReadVarUInt();
        if (edgeCount > MaxEdges) throw new InvalidDataException($"edge count {edgeCount} out of range");
        var edges = new TrackEdge[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            uint id = r.ReadVarUInt();
            float length = r.ReadSingle();
            uint nodeA = r.ReadVarUInt();
            uint nodeB = r.ReadVarUInt();
            bool edgeGeometry = geometry && (version < 3 || r.ReadByte() != 0);
            if (!edgeGeometry)
            {
                edges[i] = new TrackEdge(id, length, nodeA, nodeB);
                continue;
            }
            var a = new WorldPoint(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var b = new WorldPoint(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            edges[i] = new TrackEdge(id, length, nodeA, nodeB, a, b);
        }

        int junctionCount = (int)r.ReadVarUInt();
        if (junctionCount > MaxJunctions) throw new InvalidDataException($"junction count {junctionCount} out of range");
        var junctions = new JunctionDef[junctionCount];
        for (int i = 0; i < junctionCount; i++)
        {
            uint id = r.ReadVarUInt();
            uint entry = r.ReadVarUInt();
            int branchCount = (int)r.ReadVarUInt();
            if (branchCount < 2 || branchCount > MaxBranches) throw new InvalidDataException($"branch count {branchCount} out of range");
            var branches = new uint[branchCount];
            for (int b = 0; b < branchCount; b++) branches[b] = r.ReadVarUInt();
            junctions[i] = new JunctionDef(id, entry, branches);
        }

        return new WorldTopology(gameBuild, edges, junctions);
    }
}
