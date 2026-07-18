using System;

namespace LocoMP.Core.Trains;

/// <summary>
/// One bogie's position on the rail network in SPLINE SPACE (03 §5): which track edge it sits on,
/// how far along it, and how fast it is moving along it. ~13 bytes on the wire vs ~28 for a 6-DOF
/// pose — and, unlike a world-space pose, it cannot describe an off-rail position, so replicated
/// railed cars stay railed by construction. Edge ids come from the world topology (the extractor's
/// output and the Shim agree on the same numbering per game build).
/// </summary>
public readonly struct BogieState : IEquatable<BogieState>
{
    public BogieState(uint edgeId, float s, float v)
    {
        EdgeId = edgeId;
        S = s;
        V = v;
    }

    /// <summary>Track edge the bogie is on (stable per game build; 0 is a valid id).</summary>
    public uint EdgeId { get; }

    /// <summary>Distance along the edge from its logical start, in metres.</summary>
    public float S { get; }

    /// <summary>Signed speed along the edge in m/s (positive = toward the edge's end).</summary>
    public float V { get; }

    public bool Equals(BogieState other) => EdgeId == other.EdgeId && S == other.S && V == other.V;
    public override bool Equals(object? obj) => obj is BogieState b && Equals(b);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (int)EdgeId;
            h = h * 31 + S.GetHashCode();
            h = h * 31 + V.GetHashCode();
            return h;
        }
    }

    public override string ToString() => $"edge {EdgeId} s={S:F1} v={V:F1}";
}
