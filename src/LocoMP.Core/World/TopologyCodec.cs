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

    /// <summary>Bump when the topology layout changes; old files are refused, re-extract per build.</summary>
    public const byte FormatVersion = 1;

    public const int MaxEdges = 500_000;
    public const int MaxJunctions = 50_000;
    public const int MaxBranches = 8;

    public static byte[] Write(WorldTopology topology)
    {
        if (topology is null) throw new ArgumentNullException(nameof(topology));

        var w = new PacketWriter(topology.Edges.Count * 16 + 64);
        foreach (byte m in Magic) w.WriteByte(m);
        w.WriteByte(FormatVersion);
        w.WriteString(topology.GameBuild);

        w.WriteVarUInt((uint)topology.Edges.Count);
        foreach (TrackEdge e in topology.Edges)
        {
            w.WriteVarUInt(e.Id);
            w.WriteSingle(e.LengthM);
            w.WriteVarUInt(e.NodeA);
            w.WriteVarUInt(e.NodeB);
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
        if (version != FormatVersion) throw new InvalidDataException($"topology format v{version}, this build reads v{FormatVersion}");
        string gameBuild = r.ReadString();

        int edgeCount = (int)r.ReadVarUInt();
        if (edgeCount > MaxEdges) throw new InvalidDataException($"edge count {edgeCount} out of range");
        var edges = new TrackEdge[edgeCount];
        for (int i = 0; i < edgeCount; i++)
            edges[i] = new TrackEdge(r.ReadVarUInt(), r.ReadSingle(), r.ReadVarUInt(), r.ReadVarUInt());

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
