using System;

namespace LocoMP.Core.Presence;

/// <summary>
/// A player's world pose in game-free primitives (hard rule 3 — Core never names UnityEngine).
/// Position in metres; rotation as a quaternion. The Shim maps this to/from UnityEngine.Vector3 /
/// Quaternion at the boundary. A readonly struct so a roster of poses is a flat array with no
/// per-entry heap allocation on the snapshot path (03 §5 budget).
/// </summary>
public readonly struct Pose : IEquatable<Pose>
{
    public Pose(float px, float py, float pz, float rx, float ry, float rz, float rw)
    {
        Px = px; Py = py; Pz = pz;
        Rx = rx; Ry = ry; Rz = rz; Rw = rw;
    }

    public float Px { get; }
    public float Py { get; }
    public float Pz { get; }
    public float Rx { get; }
    public float Ry { get; }
    public float Rz { get; }
    public float Rw { get; }

    /// <summary>Origin with an identity rotation (w=1) — the roster default before a first pose update.</summary>
    public static Pose Identity { get; } = new(0f, 0f, 0f, 0f, 0f, 0f, 1f);

    public bool Equals(Pose other) =>
        Px == other.Px && Py == other.Py && Pz == other.Pz &&
        Rx == other.Rx && Ry == other.Ry && Rz == other.Rz && Rw == other.Rw;

    public override bool Equals(object? obj) => obj is Pose p && Equals(p);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Px.GetHashCode();
            h = h * 31 + Py.GetHashCode();
            h = h * 31 + Pz.GetHashCode();
            h = h * 31 + Rx.GetHashCode();
            h = h * 31 + Ry.GetHashCode();
            h = h * 31 + Rz.GetHashCode();
            h = h * 31 + Rw.GetHashCode();
            return h;
        }
    }

    public static bool operator ==(Pose a, Pose b) => a.Equals(b);
    public static bool operator !=(Pose a, Pose b) => !a.Equals(b);

    public override string ToString() => $"({Px:F1}, {Py:F1}, {Pz:F1})";
}
