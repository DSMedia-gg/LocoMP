using System.IO;
using LocoMP.Core.Protocol;
using LocoMP.Core.World;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The Core half of the M2 world-extractor exit criterion: the dedicated server must load track
/// topology with no game install (03 §6). The Shim-side extractor writes this exact format; here a
/// synthetic world proves the load path and the refusal edges.
/// </summary>
public class TopologyCodecTests
{
    private static WorldTopology Synthetic() => new(
        "99-build2702",
        new[]
        {
            new TrackEdge(0, 250.5f, nodeA: 1, nodeB: 2),
            new TrackEdge(1, 90.25f, nodeA: 2, nodeB: 3),
            new TrackEdge(2, 1200f, nodeA: 2, nodeB: 4),
        },
        new[]
        {
            new JunctionDef(7, entryEdgeId: 0, branchEdgeIds: new uint[] { 1, 2 }),
        });

    [Fact]
    public void A_topology_survives_the_write_read_round_trip()
    {
        byte[] data = TopologyCodec.Write(Synthetic());
        WorldTopology read = TopologyCodec.Read(data);

        Assert.Equal("99-build2702", read.GameBuild);
        Assert.Equal(3, read.Edges.Count);
        Assert.Equal(250.5f, read.Edges[0].LengthM);
        Assert.Equal(2u, read.Edges[0].NodeB);
        Assert.Equal(2u, read.Edges[2].NodeA);      // edges 0/1/2 share node 2 — connectivity intact

        JunctionDef j = Assert.Single(read.Junctions);
        Assert.Equal(7u, j.Id);
        Assert.Equal(0u, j.EntryEdgeId);
        Assert.Equal(new uint[] { 1, 2 }, j.BranchEdgeIds);
    }

    /// <summary>The v2 shape: every edge carries absolute world endpoints (D10 Burst 2).</summary>
    private static WorldTopology WithGeometry() => new(
        "99-build2702",
        new[]
        {
            // A 100 m edge running due east from the origin, then one continuing north.
            new TrackEdge(0, 100f, nodeA: 1, nodeB: 2, new WorldPoint(0, 10, 0), new WorldPoint(100, 10, 0)),
            new TrackEdge(1, 200f, nodeA: 2, nodeB: 3, new WorldPoint(100, 10, 0), new WorldPoint(100, 30, 200)),
        },
        new JunctionDef[0]);

    [Fact]
    public void Edge_geometry_survives_the_write_read_round_trip()
    {
        WorldTopology read = TopologyCodec.Read(TopologyCodec.Write(WithGeometry()));

        Assert.True(read.HasGeometry);
        Assert.Equal(2, read.GeometryEdgeCount);
        Assert.Equal(100f, read.Edges[0].B.X);
        Assert.Equal(200f, read.Edges[1].B.Z);
        Assert.Equal(30f, read.Edges[1].B.Y);
    }

    /// <summary>
    /// The back-compat contract that makes the v2 bump safe: a <c>.lmpw</c> costs a running game and a
    /// human to produce, so an existing v1 extraction must keep working. It loads, it is still a valid
    /// graph, and it simply reports no geometry — which is the server's signal to fail open on train
    /// interest rather than to refuse the file.
    /// </summary>
    [Fact]
    public void A_v1_file_still_loads_and_reports_no_geometry()
    {
        byte[] v1 = WriteV1(Synthetic());
        WorldTopology read = TopologyCodec.Read(v1);

        Assert.False(read.HasGeometry);
        Assert.Equal(3, read.Edges.Count);
        Assert.Equal(250.5f, read.Edges[0].LengthM);
        Assert.Equal(2u, read.Edges[2].NodeA);              // the graph is fully intact
        Assert.Single(read.Junctions);
        Assert.False(read.TryEdgeWorldPoint(0, 10f, out _)); // ...it just can't place anything
    }

    /// <summary>A geometry-free topology written by THIS build is v3-with-the-flag-clear, and still
    /// round-trips as geometry-free — the flag is not a proxy for the version.</summary>
    [Fact]
    public void A_geometry_free_topology_round_trips_without_inventing_positions()
    {
        WorldTopology read = TopologyCodec.Read(TopologyCodec.Write(Synthetic()));

        Assert.False(read.HasGeometry);
        Assert.False(read.Edges[0].HasGeometry);
    }

    /// <summary>Geometry is PER EDGE since v3 (the 2026-08-04 gauntlet's F4): B99.7 deterministically
    /// withholds node transforms on its special tracks, so all-or-nothing meant the filter never ran
    /// on a real extract. A bare edge stays unplaceable — its trains fail open per entity — while its
    /// neighbours keep their geometry through the round trip.</summary>
    [Fact]
    public void A_bare_edge_keeps_its_neighbours_geometry_through_the_round_trip()
    {
        var mixed = new WorldTopology("99-build2702",
            new[]
            {
                new TrackEdge(0, 100f, 1, 2, new WorldPoint(0, 0, 0), new WorldPoint(100, 0, 0)),
                new TrackEdge(1, 100f, 2, 3), // no geometry — a turntable-style special track
            },
            new JunctionDef[0]);

        Assert.True(mixed.HasGeometry);          // usable: SOME edges place
        Assert.Equal(1, mixed.GeometryEdgeCount);
        Assert.True(mixed.TryEdgeWorldPoint(0, 50f, out WorldPoint mid));
        Assert.Equal(50f, mid.X, 3);
        Assert.False(mixed.TryEdgeWorldPoint(1, 50f, out _)); // the caller fails open per train

        WorldTopology read = TopologyCodec.Read(TopologyCodec.Write(mixed));
        Assert.True(read.HasGeometry);
        Assert.Equal(1, read.GeometryEdgeCount);
        Assert.True(read.Edges[0].HasGeometry);
        Assert.False(read.Edges[1].HasGeometry);
        Assert.Equal(100f, read.Edges[0].B.X);
    }

    /// <summary>The v2 back-compat contract: an old file's single flag byte meant ALL edges carry
    /// geometry (its own all-or-nothing invariant), with no per-edge flags in the stream. Real v2
    /// bytes, hand-written, must read back fully placeable.</summary>
    [Fact]
    public void A_v2_file_still_loads_with_geometry_on_every_edge()
    {
        WorldTopology t = WithGeometry();
        var w = new PacketWriter(256);
        foreach (byte m in new[] { (byte)'L', (byte)'M', (byte)'P', (byte)'W' }) w.WriteByte(m);
        w.WriteByte(2);
        w.WriteString(t.GameBuild);
        w.WriteByte(1); // v2: one file-level flag, no per-edge flags
        w.WriteVarUInt((uint)t.Edges.Count);
        foreach (TrackEdge e in t.Edges)
        {
            w.WriteVarUInt(e.Id);
            w.WriteSingle(e.LengthM);
            w.WriteVarUInt(e.NodeA);
            w.WriteVarUInt(e.NodeB);
            w.WriteSingle(e.A.X); w.WriteSingle(e.A.Y); w.WriteSingle(e.A.Z);
            w.WriteSingle(e.B.X); w.WriteSingle(e.B.Y); w.WriteSingle(e.B.Z);
        }
        w.WriteVarUInt(0); // no junctions

        WorldTopology read = TopologyCodec.Read(w.ToArray());
        Assert.True(read.HasGeometry);
        Assert.Equal(read.Edges.Count, read.GeometryEdgeCount);
        Assert.Equal(100f, read.Edges[0].B.X);
    }

    /// <summary>The spline→world bridge: s metres along an edge lands proportionally along its chord.
    /// This is what lets the server distance-test a railed train against a player pose.</summary>
    [Fact]
    public void A_spline_position_resolves_to_a_world_point_along_the_edge()
    {
        WorldTopology t = WithGeometry();

        Assert.True(t.TryEdgeWorldPoint(0, 0f, out WorldPoint start));
        Assert.Equal(0f, start.X);

        Assert.True(t.TryEdgeWorldPoint(0, 25f, out WorldPoint quarter));
        Assert.Equal(25f, quarter.X, 3);
        Assert.Equal(0f, quarter.Z, 3);

        Assert.True(t.TryEdgeWorldPoint(1, 100f, out WorldPoint mid)); // halfway along the 200 m edge
        Assert.Equal(100f, mid.X, 3);
        Assert.Equal(100f, mid.Z, 3);
        Assert.Equal(20f, mid.Y, 3);
    }

    /// <summary>An out-of-range s is clamped to the edge, not rejected: a bogie a few centimetres past
    /// an end (float drift, or a snapshot caught mid-transition) is a real train at a real place.</summary>
    [Fact]
    public void An_out_of_range_s_clamps_to_the_edge_ends()
    {
        WorldTopology t = WithGeometry();

        Assert.True(t.TryEdgeWorldPoint(0, -5f, out WorldPoint before));
        Assert.Equal(0f, before.X, 3);

        Assert.True(t.TryEdgeWorldPoint(0, 100.4f, out WorldPoint after));
        Assert.Equal(100f, after.X, 3);
    }

    /// <summary>An unknown edge id resolves to nothing — the caller must fail open rather than guess.</summary>
    [Fact]
    public void An_unknown_edge_cannot_be_placed()
    {
        Assert.False(WithGeometry().TryEdgeWorldPoint(999, 0f, out _));
        Assert.Null(WithGeometry().Edge(999));
    }

    /// <summary>Hand-writes the pre-geometry layout (magic, version 1, build, then edges with no
    /// geometry flag and no endpoints) so the back-compat test exercises real v1 bytes rather than
    /// a v2 file with a doctored version byte.</summary>
    private static byte[] WriteV1(WorldTopology t)
    {
        var w = new PacketWriter(256);
        foreach (byte m in new[] { (byte)'L', (byte)'M', (byte)'P', (byte)'W' }) w.WriteByte(m);
        w.WriteByte(1);
        w.WriteString(t.GameBuild);
        w.WriteVarUInt((uint)t.Edges.Count);
        foreach (TrackEdge e in t.Edges)
        {
            w.WriteVarUInt(e.Id);
            w.WriteSingle(e.LengthM);
            w.WriteVarUInt(e.NodeA);
            w.WriteVarUInt(e.NodeB);
        }
        w.WriteVarUInt((uint)t.Junctions.Count);
        foreach (JunctionDef j in t.Junctions)
        {
            w.WriteVarUInt(j.Id);
            w.WriteVarUInt(j.EntryEdgeId);
            w.WriteVarUInt((uint)j.BranchEdgeIds.Length);
            foreach (uint b in j.BranchEdgeIds) w.WriteVarUInt(b);
        }
        return w.ToArray();
    }

    [Fact]
    public void An_arbitrary_file_is_refused_by_the_magic_check()
    {
        byte[] junk = { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 }; // a zip header, say
        Assert.Throws<InvalidDataException>(() => TopologyCodec.Read(junk));
    }

    [Fact]
    public void A_future_format_version_is_refused_not_misread()
    {
        byte[] data = TopologyCodec.Write(Synthetic());
        // The version byte sits right after the varint magic; bump it to something unknown.
        data[4] = 99;
        var ex = Assert.Throws<InvalidDataException>(() => TopologyCodec.Read(data));
        Assert.Contains("v99", ex.Message);
    }
}
