using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocoMP.Core.Trains;
using LocoMP.Core.World;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The ghost-train engine: a seeded kinematic traveller over extracted topology. Invariants under
/// test — the head never leaves a valid edge, points resolved behind the head are exact, junction
/// crossings report real (junction, branch) pairs, dead-ends reverse instead of throwing, and equal
/// seeds replay equal paths. The last test soaks over the REAL extracted map when a dump is present.
/// </summary>
public class TopologyWalkerTests
{
    /// <summary>Triangle loop n1-n2-n3 plus a dead-end spur at n2, entered via junction 7:
    /// edge0 n1→n2 (100 m), edge1 n2→n3 (100 m), edge2 n3→n1 (100 m), edge3 n2→n4 (80 m, spur).</summary>
    private static WorldTopology Synthetic() => new(
        "test",
        new[]
        {
            new TrackEdge(0, 100f, nodeA: 1, nodeB: 2),
            new TrackEdge(1, 100f, nodeA: 2, nodeB: 3),
            new TrackEdge(2, 100f, nodeA: 3, nodeB: 1),
            new TrackEdge(3, 80f, nodeA: 2, nodeB: 4),
        },
        new[]
        {
            new JunctionDef(7, entryEdgeId: 0, branchEdgeIds: new uint[] { 1, 3 }),
        });

    private static void AssertValid(WorldTopology world, BogieState state)
    {
        TrackEdge edge = Assert.Single(world.Edges, e => e.Id == state.EdgeId);
        Assert.InRange(state.S, 0f, edge.LengthM);
    }

    [Fact]
    public void The_head_stays_on_valid_edges_across_thousands_of_small_steps()
    {
        WorldTopology world = Synthetic();
        var walker = new TopologyWalker(world, seed: 1);
        for (int i = 0; i < 5000; i++)
        {
            walker.Advance(0.7);
            AssertValid(world, walker.HeadState(5f));
        }
    }

    [Fact]
    public void Behind_zero_is_the_head_and_behind_grows_monotonically_along_the_path()
    {
        WorldTopology world = Synthetic();
        var walker = new TopologyWalker(world, seed: 2);
        walker.Advance(180); // enough trail history for a 60 m consist

        BogieState head = walker.HeadState(5f);
        BogieState atZero = walker.Behind(0, 5f)!.Value;
        Assert.Equal(head.EdgeId, atZero.EdgeId);
        Assert.Equal(head.S, atZero.S, precision: 3);

        // Successive behind-points must each sit on a valid edge; when two share an edge and
        // direction, the one farther behind must sit farther from the direction of travel.
        BogieState? prev = null;
        for (double d = 0; d <= 60; d += 5)
        {
            BogieState? p = walker.Behind(d, 5f);
            Assert.NotNull(p);
            AssertValid(world, p!.Value);
            if (prev.HasValue && prev.Value.EdgeId == p.Value.EdgeId)
            {
                if (p.Value.V > 0) Assert.True(p.Value.S <= prev.Value.S);
                else Assert.True(p.Value.S >= prev.Value.S);
            }
            prev = p;
        }
    }

    [Fact]
    public void Junction_crossings_report_real_junction_and_branch_pairs()
    {
        WorldTopology world = Synthetic();
        var walker = new TopologyWalker(world, seed: 3);
        var crossings = new List<(uint junctionId, byte branch)>();
        walker.JunctionCrossed += (id, b) => crossings.Add((id, b));

        walker.Advance(5000); // plenty of laps
        Assert.NotEmpty(crossings);
        Assert.All(crossings, c =>
        {
            Assert.Equal(7u, c.junctionId);
            Assert.InRange(c.branch, (byte)0, (byte)1);
        });
    }

    [Fact]
    public void A_dead_end_reverses_the_walker_instead_of_throwing()
    {
        // One isolated edge: both ends are dead-ends, so the walker must ping-pong forever.
        var world = new WorldTopology("test",
            new[] { new TrackEdge(0, 50f, nodeA: 1, nodeB: 2) },
            Array.Empty<JunctionDef>());
        var walker = new TopologyWalker(world, seed: 4);
        for (int i = 0; i < 100; i++)
        {
            walker.Advance(17);
            AssertValid(world, walker.HeadState(3f));
        }
    }

    [Fact]
    public void An_explicit_start_edge_is_honored_and_an_unknown_one_falls_back()
    {
        WorldTopology world = Synthetic();
        var walker = new TopologyWalker(world, seed: 5, startEdgeId: 3);
        Assert.Equal(3u, walker.HeadState(1f).EdgeId); // starts mid-spur, exactly where asked

        var fallback = new TopologyWalker(world, seed: 5, startEdgeId: 999); // no such edge
        AssertValid(world, fallback.HeadState(1f));
    }

    [Fact]
    public void Equal_seeds_replay_the_exact_same_path()
    {
        WorldTopology world = Synthetic();
        var a = new TopologyWalker(world, seed: 99);
        var b = new TopologyWalker(world, seed: 99);
        for (int i = 0; i < 500; i++)
        {
            a.Advance(3.1);
            b.Advance(3.1);
            Assert.Equal(a.HeadState(5f), b.HeadState(5f));
        }
    }

    [Fact]
    public void The_walker_survives_a_long_run_over_the_real_extracted_map()
    {
        string? path = FindRealWorldFile();
        if (path is null) return; // no dump on this machine — synthetic tests cover the mechanics

        WorldTopology world = TopologyCodec.Read(File.ReadAllBytes(path));
        var edgeById = world.Edges.ToDictionary(e => e.Id);
        var junctionById = world.Junctions.ToDictionary(j => j.Id);
        var walker = new TopologyWalker(world, seed: 7, tailCapacityM: 200);
        walker.JunctionCrossed += (id, branch) =>
        {
            Assert.True(junctionById.TryGetValue(id, out JunctionDef? j), $"unknown junction {id}");
            Assert.InRange(branch, (byte)0, (byte)(j!.BranchEdgeIds.Length - 1));
        };

        for (int i = 0; i < 10_000; i++) // 10 km in 1 m steps across the whole valley
        {
            walker.Advance(1);
            BogieState head = walker.HeadState(8f);
            Assert.True(edgeById.TryGetValue(head.EdgeId, out TrackEdge? edge));
            Assert.InRange(head.S, 0f, edge!.LengthM);
        }
        BogieState? tail = walker.Behind(150, 8f);
        Assert.NotNull(tail); // a 150 m consist resolves after 10 km of history
    }

    private static string? FindRealWorldFile()
    {
        string? overridePath = Environment.GetEnvironmentVariable("LOCOMP_WORLD_FILE");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "LocoMP.sln"))) continue;
            string dataDir = Path.Combine(dir.FullName, "tests", "data");
            if (!Directory.Exists(dataDir)) return null;
            return Directory.EnumerateFiles(dataDir, "world-*.lmpw").OrderBy(f => f).FirstOrDefault();
        }
        return null;
    }
}
