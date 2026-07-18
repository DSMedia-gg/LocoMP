using LocoMP.Core.Presence;

namespace LocoMP.Bot;

/// <summary>
/// A bot's movement brain: called once per tick while joined, returns the pose to stream. Behaviors
/// are pure pose math over game-free primitives — connection lifecycle (join/churn/reconnect) is
/// BotClient's job, so new behaviors never re-implement networking.
/// </summary>
public interface IBotBehavior
{
    /// <summary>Advance by <paramref name="dtSeconds"/> and return the current pose.</summary>
    Pose Tick(double dtSeconds);
}

/// <summary>Stands still at a fixed pose. The baseline for "does an avatar appear at all".</summary>
public sealed class IdleBehavior : IBotBehavior
{
    private readonly Pose _pose;
    public IdleBehavior(Pose pose) => _pose = pose;
    public Pose Tick(double dtSeconds) => _pose;
}

/// <summary>
/// Walks a circle of <paramref name="radius"/> metres around a centre at walking speed, facing the
/// direction of travel (yaw-only rotation, Unity convention: yaw measured from +Z toward +X). The
/// visually obvious "it's alive" behavior for in-game avatar bring-up.
/// </summary>
public sealed class OrbitBehavior : IBotBehavior
{
    private readonly Pose _center;
    private readonly double _radius;
    private readonly double _speed;
    private double _angle;

    public OrbitBehavior(Pose center, double radius, double speedMetresPerSecond, double startAngle = 0)
    {
        _center = center;
        _radius = Math.Max(0.1, radius);
        _speed = speedMetresPerSecond;
        _angle = startAngle;
    }

    public Pose Tick(double dtSeconds)
    {
        _angle += (_speed / _radius) * dtSeconds;
        double x = _center.Px + _radius * Math.Cos(_angle);
        double z = _center.Pz + _radius * Math.Sin(_angle);
        // Travel direction is the circle tangent; convert to a yaw-only quaternion (rotation about Y).
        double yaw = Math.Atan2(Math.Cos(_angle), -Math.Sin(_angle));
        double half = yaw * 0.5;
        return new Pose((float)x, _center.Py, (float)z,
                        0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
    }
}

/// <summary>
/// Picks random points within <paramref name="radius"/> of the centre and walks between them —
/// less predictable coverage than orbiting, useful for interest-management and soak runs (M6).
/// Seedable so a soak failure can be replayed.
/// </summary>
public sealed class WanderBehavior : IBotBehavior
{
    private readonly Pose _center;
    private readonly double _radius;
    private readonly double _speed;
    private readonly Random _rng;
    private double _x, _z, _targetX, _targetZ;

    public WanderBehavior(Pose center, double radius, double speedMetresPerSecond, int seed)
    {
        _center = center;
        _radius = Math.Max(0.1, radius);
        _speed = speedMetresPerSecond;
        _rng = new Random(seed);
        _x = center.Px;
        _z = center.Pz;
        PickTarget();
    }

    public Pose Tick(double dtSeconds)
    {
        double dx = _targetX - _x, dz = _targetZ - _z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        double step = _speed * dtSeconds;
        if (dist <= step) { _x = _targetX; _z = _targetZ; PickTarget(); }
        else { _x += dx / dist * step; _z += dz / dist * step; }

        double yaw = Math.Atan2(dx, dz);
        double half = yaw * 0.5;
        return new Pose((float)_x, _center.Py, (float)_z,
                        0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
    }

    private void PickTarget()
    {
        double angle = _rng.NextDouble() * Math.PI * 2;
        double r = Math.Sqrt(_rng.NextDouble()) * _radius; // sqrt → uniform over the disc, not clumped at centre
        _targetX = _center.Px + r * Math.Cos(angle);
        _targetZ = _center.Pz + r * Math.Sin(angle);
    }
}
