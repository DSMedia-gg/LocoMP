using DV.OriginShift;
using LocoMP.Core.Presence;
using UnityEngine;

// UnityEngine defines its own Pose struct; the alias pins the unqualified name to Core's wire type
// (same idiom as the DeliveryMethod alias in LiteNetLibTransport).
using Pose = LocoMP.Core.Presence.Pose;

namespace LocoMP.Shim;

/// <summary>
/// The presence boundary between the game and Core (hard rule 3): reads the local player's pose in
/// ABSOLUTE world coordinates and converts synced poses back into this instance's shifted space.
/// DV floating-origin background: the 256 km² map far exceeds float precision, so the engine
/// periodically teleports the whole world around the player. Raw transform.position therefore
/// differs between two game instances standing in the same physical spot — every pose that crosses
/// the wire MUST be absolute (position + OriginShift.currentMove), and every applied pose must be
/// re-localized with the RECEIVER's own shift. DV.OriginShift.OriginShift provides exactly this
/// math (verified against B99.7 by reflection-only inspection; DV.OriginShiftInfo.dll).
/// </summary>
public static class PresenceShim
{
    /// <summary>
    /// Builds this mod release is known to work on (03 §10 supported-build gate). B99.7 reports
    /// itself as "99-build2702" at runtime (discovered M1.3). Extend per verified build; on an
    /// unknown build the mod refuses to start a session with a friendly message instead of
    /// crashing mid-play — and the exact-match handshake keeps mixed-build sessions out anyway.
    /// </summary>
    public static readonly string[] SupportedBuilds = { "99-build2702" };

    /// <summary>
    /// The build string both sides present in the handshake — the RUNTIME version, so two patched
    /// installs can never quietly session together across builds. The bot presents the same default.
    /// </summary>
    public static string GameBuild => Application.version;

    /// <summary>Alias kept for log readability at session start.</summary>
    public static string ReportedGameVersion => Application.version;

    /// <summary>False on a build this release has not been verified against (B100 one day).</summary>
    public static bool IsSupportedBuild => System.Array.IndexOf(SupportedBuilds, Application.version) >= 0;

    // Sign convention, verified from DV.OriginShiftInfo IL (AbsolutePosition = position − currentMove):
    //   absolute = local − currentMove      local = absolute + currentMove
    // We do the arithmetic with currentMove directly instead of calling AbsolutePosition(Transform):
    // that method's overload set drags Unity.Mathematics/Unity.Transforms (DOTS) into compile-time
    // overload resolution, and one Vector3 subtraction doesn't justify two extra assembly references.

    /// <summary>
    /// Capture the local player's pose in absolute coordinates. False when no player exists yet
    /// (loading screens, main menu) — callers just skip that tick.
    /// </summary>
    public static bool TryCaptureLocalPose(out Pose pose)
    {
        Transform? player = PlayerManager.PlayerTransform;
        if (player == null) // Unity's destroyed-object == overload, not a plain null check
        {
            pose = Pose.Identity;
            return false;
        }

        Vector3 abs = player.position - OriginShift.currentMove;
        Quaternion rot = player.rotation;
        pose = new Pose(abs.x, abs.y, abs.z, rot.x, rot.y, rot.z, rot.w);
        return true;
    }

    /// <summary>An absolute synced position, re-localized into THIS instance's shifted world space.</summary>
    public static Vector3 ToLocalPosition(Pose pose) =>
        new Vector3(pose.Px, pose.Py, pose.Pz) + OriginShift.currentMove;

    /// <summary>The camera remote name tags should face; null on loading screens.</summary>
    public static Camera? ActiveCamera => PlayerManager.ActiveCamera;
}
