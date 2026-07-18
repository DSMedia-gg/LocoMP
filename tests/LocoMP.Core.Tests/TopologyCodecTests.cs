using System.IO;
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
