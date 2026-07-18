using System;
using LocoMP.Core.Presence;

namespace LocoMP.Core.Trains;

/// <summary>
/// One car's kinematic state inside a snapshot. Railed cars carry two spline-space bogies; derailed
/// cars carry a 6-DOF <see cref="Presence.Pose"/> instead (03 §4 — derail switches the car to pose
/// streaming). The car this describes is identified by position: snapshot car order equals the
/// trainset's car order at the stamped epoch.
/// </summary>
public readonly struct CarSnapshot
{
    private CarSnapshot(bool derailed, BogieState front, BogieState rear, Pose pose)
    {
        Derailed = derailed;
        Front = front;
        Rear = rear;
        Pose = pose;
    }

    public bool Derailed { get; }

    /// <summary>Leading bogie (in car-forward order). Meaningful only when railed.</summary>
    public BogieState Front { get; }

    /// <summary>Trailing bogie. Meaningful only when railed.</summary>
    public BogieState Rear { get; }

    /// <summary>Free 6-DOF pose. Meaningful only when derailed.</summary>
    public Pose Pose { get; }

    public static CarSnapshot Railed(BogieState front, BogieState rear) =>
        new(false, front, rear, Pose.Identity);

    public static CarSnapshot OffRail(Pose pose) =>
        new(true, default, default, pose);
}

/// <summary>
/// The sim owner's authoritative kinematic frame for one consist, stamped with the (trainsetId,
/// epoch) it describes and the owner's estimate of server time (for interpolation delay, 03 §5).
/// Sent sequenced-unreliable — latest wins, losses are filled by interpolation.
/// </summary>
public sealed class TrainsetSnapshot
{
    public TrainsetSnapshot(int trainsetId, uint epoch, long serverTimeMs, CarSnapshot[] cars)
    {
        if (cars is null) throw new ArgumentNullException(nameof(cars));
        if (cars.Length == 0) throw new ArgumentException("a snapshot carries at least one car", nameof(cars));
        TrainsetId = trainsetId;
        Epoch = epoch;
        ServerTimeMs = serverTimeMs;
        Cars = cars;
    }

    public int TrainsetId { get; }
    public uint Epoch { get; }
    public long ServerTimeMs { get; }
    public CarSnapshot[] Cars { get; }
}
