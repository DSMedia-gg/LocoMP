using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocoMP.Core.World;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The M2.2 exit criterion: Core loads the REAL extractor output — the same load path the headless
/// dedicated server uses (03 §6). The file comes from an in-game run of the Shim extractor and is
/// dropped into tests/data/ (git-ignored: it's per-build derived data, re-extracted per game build).
/// On machines without a dump (CI is game-free by design, hard rule 8) the facts pass vacuously —
/// TopologyCodecTests cover the codec itself with a synthetic world.
/// </summary>
public class RealWorldTopologyTests
{
    private static string? FindRealWorldFile()
    {
        string? overridePath = Environment.GetEnvironmentVariable("LOCOMP_WORLD_FILE");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;

        // Walk up from the test bin dir to the repo root, then look in tests/data/.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "LocoMP.sln"))) continue;
            string dataDir = Path.Combine(dir.FullName, "tests", "data");
            if (!Directory.Exists(dataDir)) return null;
            return Directory.EnumerateFiles(dataDir, "world-*.lmpw").OrderBy(f => f).FirstOrDefault();
        }
        return null;
    }

    [Fact]
    public void The_real_extracted_world_loads_and_is_one_sane_graph()
    {
        string? path = FindRealWorldFile();
        if (path is null) return; // no extracted dump on this machine — see class doc

        WorldTopology w = TopologyCodec.Read(File.ReadAllBytes(path));

        // Stamp + scale: the build string is embedded in the file name by the extractor, and the
        // DV map is a big place — hundreds of edges, tens of kilometres, dozens of switches.
        Assert.False(string.IsNullOrWhiteSpace(w.GameBuild));
        Assert.Contains(w.GameBuild.Replace(':', '_'), Path.GetFileName(path));
        Assert.True(w.Edges.Count > 500, $"only {w.Edges.Count} edges — extraction looks truncated");
        double totalKm = w.Edges.Sum(e => (double)e.LengthM) / 1000;
        Assert.True(totalKm > 50, $"only {totalKm:F1} km of track — lengths look wrong");
        Assert.True(w.Junctions.Count > 30, $"only {w.Junctions.Count} junctions");

        // Edge ids are the registry indices: sequential from 0, no gaps, no duplicates.
        Assert.Equal(Enumerable.Range(0, w.Edges.Count).Select(i => (uint)i), w.Edges.Select(e => e.Id));

        // Junction ids are unique, and every referenced edge exists.
        var edgeById = w.Edges.ToDictionary(e => e.Id);
        Assert.Equal(w.Junctions.Count, w.Junctions.Select(j => j.Id).Distinct().Count());
        foreach (JunctionDef j in w.Junctions)
        {
            Assert.True(edgeById.ContainsKey(j.EntryEdgeId), $"junction {j.Id}: entry edge {j.EntryEdgeId} missing");
            foreach (uint b in j.BranchEdgeIds)
                Assert.True(edgeById.ContainsKey(b), $"junction {j.Id}: branch edge {b} missing");

            // The junction fuses its entry end and all branch ends into ONE node, so a single node
            // must be common to the entry edge and every branch edge.
            IEnumerable<uint> common = new[] { edgeById[j.EntryEdgeId].NodeA, edgeById[j.EntryEdgeId].NodeB };
            common = j.BranchEdgeIds.Aggregate(common, (acc, b) => acc.Intersect(new[] { edgeById[b].NodeA, edgeById[b].NodeB }));
            Assert.True(common.Any(), $"junction {j.Id}: entry and branches share no node — graph is inconsistent");
        }

        // The valley is one rail network: the overwhelming share of edges must sit in a single
        // connected component. (Turntable-served stubs may legitimately be islands — the turntable
        // link is session state, not topology — hence the margin.)
        int largest = LargestComponentSize(w);
        Assert.True(largest >= w.Edges.Count / 2,
            $"largest connected component is {largest}/{w.Edges.Count} edges — the graph is shattered");
    }

    private static int LargestComponentSize(WorldTopology w)
    {
        var edgesAtNode = new Dictionary<uint, List<int>>();
        for (int i = 0; i < w.Edges.Count; i++)
        {
            foreach (uint node in new[] { w.Edges[i].NodeA, w.Edges[i].NodeB })
            {
                if (!edgesAtNode.TryGetValue(node, out List<int>? list)) edgesAtNode[node] = list = new List<int>();
                list.Add(i);
            }
        }

        var seen = new bool[w.Edges.Count];
        int best = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < w.Edges.Count; start++)
        {
            if (seen[start]) continue;
            int size = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int e = stack.Pop();
                size++;
                foreach (uint node in new[] { w.Edges[e].NodeA, w.Edges[e].NodeB })
                    foreach (int next in edgesAtNode[node])
                        if (!seen[next]) { seen[next] = true; stack.Push(next); }
            }
            best = Math.Max(best, size);
        }
        return best;
    }
}
