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
    /// The build string both sides present in the handshake. Hard-coded for now: the game is frozen
    /// on B99.7 until B100 (~2027), and the bot harness presents the same default. TODO(M2): read
    /// the live build at runtime + supported-build gate (03 §10); the in-game log line below is the
    /// discovery step for what the runtime actually reports.
    /// </summary>
    public const string GameBuild = "B99.7";

    /// <summary>What the running game calls itself — logged at session start to inform the TODO above.</summary>
    public static string ReportedGameVersion => Application.version;

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
