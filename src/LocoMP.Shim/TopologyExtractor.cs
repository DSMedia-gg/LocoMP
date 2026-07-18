using System;
using System.Collections.Generic;
using System.IO;
using LocoMP.Core.World;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// M2.2 world extractor (03 §6): walks the live rail network and writes it as an LMPW topology file —
/// the data the headless dedicated server loads instead of a game install. Edge ids are indices into
/// <c>RailTrackRegistryBase.OrderedRailtracks</c> (the game's own stable ordering; its TracksHash is
/// logged so two extractions of the same build can be compared), and the SAME numbering the Shim uses
/// for <c>BogieState.EdgeId</c> in M2.3. Junction ids are the game's <c>junctionData.junctionId</c>
/// (the ids the save format uses). API verified against B99.7 by reflection-only inspection
/// (RailTrack/Junction/RailTrackRegistryBase in DV.RailTrack; BezierCurve.length in BezierCurves).
/// </summary>
public static class TopologyExtractor
{
    /// <summary>
    /// Extract the loaded world's track graph and write <c>world-&lt;build&gt;.lmpw</c> into
    /// <paramref name="outputDir"/>. Returns the written file's full path. Throws with a clear
    /// message when no world is loaded.
    /// </summary>
    public static string Extract(string outputDir, Action<string> log)
    {
        if (outputDir is null) throw new ArgumentNullException(nameof(outputDir));
        if (log is null) throw new ArgumentNullException(nameof(log));

        RailTrackRegistryBase registry = RailTrackRegistryBase.Instance;
        if (registry == null) throw new InvalidOperationException("RailTrackRegistry is not alive — load a world first.");

        RailTrack[] tracks = registry.OrderedRailtracks;
        Junction[] junctions = registry.OrderedJunctions;
        if (tracks == null || tracks.Length == 0) throw new InvalidOperationException("registry has no tracks — world still loading?");

        log($"[extract] {tracks.Length} track(s), {junctions?.Length ?? 0} junction(s); " +
            $"TracksHash={SafeHash(() => registry.TracksHash)} JunctionsHash={SafeHash(() => registry.JunctionsHash)}");

        var indexOf = new Dictionary<RailTrack, int>(tracks.Length);
        for (int i = 0; i < tracks.Length; i++) indexOf[tracks[i]] = i;

        // Node discovery: union-find over the 2N track endpoints (even = in end, odd = out end).
        // Connections come from the game's own Branch pointers, never from position clustering —
        // but every union is positionally verified below, which also proves the Branch.first
        // convention (first ⇒ the branch lands on the target track's IN end) against the live game.
        var union = new EndpointUnion(tracks.Length * 2);
        int missingTracks = 0, positionMismatches = 0;
        string? firstMismatch = null;

        int EndpointOf(Junction.Branch b) => indexOf[b.track] * 2 + (b.first ? 0 : 1);

        Vector3? EndPosition(int endpoint)
        {
            RailTrack t = tracks[endpoint / 2];
            try
            {
                Transform node = endpoint % 2 == 0 ? t.GetInNodeT() : t.GetOutNodeT();
                return node != null ? node.position : (Vector3?)null;
            }
            catch { return null; }
        }

        void Connect(int a, int b)
        {
            union.Union(a, b);
            Vector3? pa = EndPosition(a), pb = EndPosition(b);
            if (pa.HasValue && pb.HasValue && Vector3.Distance(pa.Value, pb.Value) > 1f)
            {
                positionMismatches++;
                firstMismatch ??= $"edge {a / 2}/{(a % 2 == 0 ? "in" : "out")} vs edge {b / 2}/{(b % 2 == 0 ? "in" : "out")}: {Vector3.Distance(pa.Value, pb.Value):F1} m apart";
            }
        }

        bool Valid(Junction.Branch? b)
        {
            if (b?.track == null) return false;
            if (indexOf.ContainsKey(b.track)) return true;
            missingTracks++;
            return false;
        }

        for (int i = 0; i < tracks.Length; i++)
        {
            RailTrack t = tracks[i];
            if (Valid(t.inBranch)) Connect(i * 2, EndpointOf(t.inBranch));
            if (Valid(t.outBranch)) Connect(i * 2 + 1, EndpointOf(t.outBranch));
        }

        // A junction fuses its entry end and every branch end into ONE graph node; which branch is
        // routable at a given moment is session state (JunctionState), not topology.
        var junctionDefs = new List<JunctionDef>(junctions?.Length ?? 0);
        var seenJunctionIds = new HashSet<uint>();
        int skippedJunctions = 0, duplicateJunctionIds = 0;
        if (junctions != null)
        {
            foreach (Junction j in junctions)
            {
                if (j == null || j.outBranches == null || !Valid(j.inBranch)) { skippedJunctions++; continue; }
                int entry = EndpointOf(j.inBranch);

                // outBranches order is load-bearing: Junction.selectedBranch indexes it, and the
                // wire protocol's branch byte must mean the same thing on every peer.
                var branchEdges = new List<uint>();
                foreach (Junction.Branch b in j.outBranches)
                {
                    if (!Valid(b)) continue;
                    Connect(entry, EndpointOf(b));
                    branchEdges.Add((uint)indexOf[b.track]);
                }
                if (branchEdges.Count < 2) { skippedJunctions++; continue; }

                uint id = (uint)j.junctionData.junctionId;
                if (!seenJunctionIds.Add(id)) { duplicateJunctionIds++; continue; }
                junctionDefs.Add(new JunctionDef(id, (uint)indexOf[j.inBranch.track], branchEdges.ToArray()));
            }
        }

        // Compact node numbering, deterministic because it scans endpoints in registry order.
        var nodeIds = new Dictionary<int, uint>();
        var edges = new TrackEdge[tracks.Length];
        int zeroLength = 0;
        double totalMetres = 0;
        for (int i = 0; i < tracks.Length; i++)
        {
            float length = 0f;
            try { var curve = tracks[i].curve; if (curve != null) length = curve.length; } catch { }
            if (length <= 0f) zeroLength++;
            totalMetres += length;
            edges[i] = new TrackEdge((uint)i, length, NodeId(i * 2), NodeId(i * 2 + 1));
        }

        uint NodeId(int endpoint)
        {
            int root = union.Find(endpoint);
            if (!nodeIds.TryGetValue(root, out uint id)) nodeIds[root] = id = (uint)nodeIds.Count;
            return id;
        }

        string build = Application.version;
        var topology = new WorldTopology(build, edges, junctionDefs);
        byte[] bytes = TopologyCodec.Write(topology);

        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, $"world-{Sanitize(build)}.lmpw");
        File.WriteAllBytes(path, bytes);

        log($"[extract] wrote {bytes.Length:N0} bytes → {path}");
        log($"[extract] build '{build}': {edges.Length} edge(s) ({totalMetres / 1000:F1} km), {nodeIds.Count} node(s), " +
            $"{junctionDefs.Count} junction(s) ({skippedJunctions} skipped, {duplicateJunctionIds} duplicate id(s))");
        log($"[extract] health: {positionMismatches} position mismatch(es) > 1 m" +
            (firstMismatch != null ? $" — first: {firstMismatch}" : "") +
            $", {zeroLength} zero-length edge(s), {missingTracks} branch(es) to unregistered tracks");
        if (positionMismatches > 0)
            log("[extract] WARNING: position mismatches mean the Branch.first convention or the graph is off — do NOT trust this file.");

        return path;
    }

    private static string SafeHash(Func<string> get)
    {
        try { return get() ?? "<null>"; } catch (Exception e) { return $"<{e.GetType().Name}>"; }
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    /// <summary>Plain union-find with path halving; endpoints are ints, roots become node ids.</summary>
    private sealed class EndpointUnion
    {
        private readonly int[] _parent;

        public EndpointUnion(int size)
        {
            _parent = new int[size];
            for (int i = 0; i < size; i++) _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }
    }
}
